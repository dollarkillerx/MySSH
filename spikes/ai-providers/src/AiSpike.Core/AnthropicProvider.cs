using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Beta.Messages;

namespace AiSpike.Core;

public sealed record AnthropicProviderOptions
{
    public string BaseUrl { get; init; } = "https://api.anthropic.com";
    /// <summary>Always passed explicitly. Empty/null => explicit null (SDK then reads NO env/profile).</summary>
    public string? ApiKey { get; init; }
    /// <summary>beta server-side-fallback-2026-07-01 + fallbacks:"default". On by default (design §7.2).</summary>
    public bool ServerSideFallback { get; init; } = true;
    /// <summary>eager_input_streaming on client tools. Turn off for Anthropic-compatible proxies that reject it.</summary>
    public bool EagerInputStreaming { get; init; } = true;
    /// <summary>"summarized" (reasoning summary) or "omitted". ("updates" needs another beta; not used here.)</summary>
    public string ThinkingDisplay { get; init; } = "summarized";
    public int? MaxRetries { get; init; }
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Claude via the official Anthropic C# SDK, beta Messages endpoint (needed for fallbacks).</summary>
public sealed class AnthropicProvider : IChatProvider
{
    public const string Id = "anthropic";
    public const string DefaultModel = "claude-opus-5-5";
    public const string FallbackBeta = "server-side-fallback-2026-07-01";

    private readonly AnthropicClient _client;
    private readonly AnthropicProviderOptions _opts;

    public AnthropicProvider(AnthropicProviderOptions opts)
    {
        _opts = opts;
        _client = CreateClient(opts);
    }

    /// <summary>
    /// Env-isolated client: explicit BaseUrl (so ANTHROPIC_BASE_URL is never read), explicit ApiKey,
    /// no credential auto-resolution (ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN / `ant` profiles), and
    /// ANTHROPIC_CUSTOM_HEADERS stripped. See FINDINGS.md "env-var pitfall".
    /// </summary>
    public static AnthropicClient CreateClient(AnthropicProviderOptions opts) => new IsolatedAnthropicClient
    {
        BaseUrl = opts.BaseUrl,
        ApiKey = string.IsNullOrEmpty(opts.ApiKey) ? null : opts.ApiKey,
        MaxRetries = opts.MaxRetries,
        Timeout = opts.Timeout,
        Handlers = [new EnvIsolationHandler(hasConfiguredKey: !string.IsNullOrEmpty(opts.ApiKey))],
    };

    public string ProviderId => Id;
    public ProviderCapabilities Capabilities { get; } = new(Tools: true, Streaming: true, Images: true);

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var page = await _client.Models.List(cancellationToken: ct);
        return page.Items.Select(m => new ModelInfo(m.ID, m.DisplayName)).ToList();
    }

    public MessageCreateParams BuildParams(ChatRequest req)
    {
        // Prompt cache: tools sorted by name (byte-stable) with cache_control on the last tool;
        // system as a block with cache_control. Dynamic context belongs in the user turn.
        var tools = req.Tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var toolParams = new List<BetaToolUnion>();
        for (int i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            var schema = t.InputSchema;
            var tool = new BetaTool
            {
                Name = t.Name,
                Description = t.Description + ConstraintNote(schema),
                // The typed InputSchema has no AdditionalProperties member; strict tools need
                // additionalProperties:false, so pass the catalog schema through raw (minus keywords
                // strict mode may not accept - the full schema is still enforced client-side).
                InputSchema = InputSchema.FromRawUnchecked(WireSchema(schema)),
                Strict = true,
            };
            // Assigning null serializes as `"field": null`, so only set optional fields when used.
            if (_opts.EagerInputStreaming) tool = tool with { EagerInputStreaming = true };
            if (i == tools.Count - 1) tool = tool with { CacheControl = new BetaCacheControlEphemeral() };
            toolParams.Add(tool);
        }

        var p0 = new MessageCreateParams
        {
            Model = req.Model,
            MaxTokens = req.MaxTokens,
            System = new List<BetaTextBlockParam>
            {
                new() { Text = req.SystemPrompt, CacheControl = new BetaCacheControlEphemeral() },
            },
            Thinking = new BetaThinkingConfigAdaptive
            {
                Display = _opts.ThinkingDisplay == "omitted" ? Display.Omitted : Display.Summarized,
            },
            OutputConfig = new BetaOutputConfig
            {
                Effort = req.Effort switch { "low" => Effort.Low, "high" => Effort.High, _ => Effort.Medium },
            },
            Messages = BuildMessages(req.History),
            // No temperature / top_p / top_k: rejected on Opus 5.5.
        };
        if (toolParams.Count > 0)
        {
            p0 = p0 with { Tools = toolParams, ToolChoice = new BetaToolChoiceAuto() };
        }
        if (_opts.ServerSideFallback)
        {
            p0 = p0 with { Betas = [FallbackBeta], Fallbacks = new Default() };
        }
        return p0;
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = BuildParams(req);
        // SDK aggregator: CollectAsync(source, aggregator) passes every event through while the
        // aggregator records it; aggregator.Message() then builds the BetaMessage.
        var agg = new BetaMessageContentAggregator();
        BetaRawMessageStartEvent? startEvent = null;
        var toolBlocks = new Dictionary<long, (string Id, string Name, System.Text.StringBuilder Json)>();
        var completedCalls = new Dictionary<string, ToolCall>();
        long inTok = 0, outTok = 0, cacheRead = 0, cacheWrite = 0;
        string? stopReason = null;
        string? stopDetail = null;
        string? servedBy = null;
        Exception? error = null;

        await using var e = _client.Beta.Messages.CreateStreaming(parameters, ct).CollectAsync(agg).GetAsyncEnumerator(ct);
        while (true)
        {
            BetaRawMessageStreamEvent ev;
            try
            {
                if (!await e.MoveNextAsync()) break;
                ev = e.Current;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (AnthropicException ex) { error = ex; break; }
            catch (HttpRequestException ex) { error = ex; break; }

            if (ev.TryPickStart(out var start))
            {
                startEvent = start;
                servedBy = start.Message.Model;
                var u = start.Message.Usage;
                inTok = u.InputTokens;
                cacheRead = u.CacheReadInputTokens ?? 0;
                cacheWrite = u.CacheCreationInputTokens ?? 0;
            }
            else if (ev.TryPickContentBlockStart(out var cbs))
            {
                if (cbs.ContentBlock.TryPickBetaToolUse(out var tu))
                {
                    toolBlocks[cbs.Index] = (tu.ID, tu.Name, new System.Text.StringBuilder());
                    yield return new ToolCallStarted(tu.ID, tu.Name);
                }
            }
            else if (ev.TryPickContentBlockDelta(out var cbd))
            {
                if (cbd.Delta.TryPickText(out var td))
                    yield return new TextDelta(td.Text);
                else if (cbd.Delta.TryPickThinking(out var th))
                    yield return new ThinkingSummaryDelta(th.Thinking);
                else if (cbd.Delta.TryPickInputJson(out var ij) && toolBlocks.TryGetValue(cbd.Index, out var tb))
                {
                    tb.Json.Append(ij.PartialJson);
                    yield return new ToolCallInputDelta(tb.Id, ij.PartialJson);
                }
            }
            else if (ev.TryPickContentBlockStop(out var cbstop))
            {
                if (toolBlocks.TryGetValue(cbstop.Index, out var tb))
                {
                    // Our own strict parse of the accumulated fragments (not the SDK's tolerant parser).
                    var call = ToolInput.Build(tb.Id, tb.Name, tb.Json.ToString(), req.Tools);
                    completedCalls[tb.Id] = call;
                    yield return new ToolCallCompleted(call);
                }
            }
            else if (ev.TryPickDelta(out var md))
            {
                stopReason = md.Delta.StopReason?.Raw();
                if (md.Delta.StopDetails is { } sd)
                    stopDetail = $"category={sd.Category?.Raw() ?? "null"}; {sd.Explanation}";
                outTok = md.Usage.OutputTokens;
                if (md.Usage.InputTokens is { } it) inTok = it;
                if (md.Usage.CacheReadInputTokens is { } cr) cacheRead = cr;
                if (md.Usage.CacheCreationInputTokens is { } cw) cacheWrite = cw;
                yield return new Usage(inTok, outTok, cacheRead, cacheWrite);
            }
        }

        if (error is not null)
        {
            yield return new Stopped(StopReason.Error, error.GetType().Name + ": " + error.Message, null);
            yield break;
        }

        if (startEvent is null)
        {
            yield return new Stopped(StopReason.Error, "stream ended without message_start", null);
            yield break;
        }
        BetaMessage message = agg.Message();
        var (raw, text, calls) = ExtractAssistant(message, completedCalls);
        var aiMsg = new AiMessage
        {
            Role = AiRole.Assistant,
            Text = text,
            ToolCalls = calls,
            RawProvider = Id,
            RawContent = raw,
            ServedByModel = servedBy,
        };
        var reason = stopReason switch
        {
            "tool_use" => StopReason.ToolUse,
            "max_tokens" => StopReason.MaxTokens,
            "refusal" => StopReason.Refusal,
            "end_turn" or "stop_sequence" or "pause_turn" => StopReason.EndTurn,
            null => StopReason.Error,
            _ => StopReason.EndTurn,
        };
        yield return new Stopped(reason, stopDetail ?? (stopReason is null ? "stream ended without message_delta" : null), aiMsg);
    }

    /// <summary>Raw JSON of the aggregated content blocks, exactly as the SDK models hold them.</summary>
    private static (string Raw, string Text, IReadOnlyList<ToolCall> Calls) ExtractAssistant(
        BetaMessage message, Dictionary<string, ToolCall> completedCalls)
    {
        var raw = JsonSerializer.Serialize(message.Content);
        // Tool calls made before a mid-stream fallback boundary belong to the declined model: not executed.
        int lastFallback = -1;
        for (int i = 0; i < message.Content.Count; i++)
            if (message.Content[i].TryPickFallback(out _)) lastFallback = i;

        var text = new System.Text.StringBuilder();
        var calls = new List<ToolCall>();
        for (int i = 0; i < message.Content.Count; i++)
        {
            var b = message.Content[i];
            if (b.TryPickText(out var t)) text.Append(t.Text);
            else if (b.TryPickToolUse(out var tu) && i > lastFallback && completedCalls.TryGetValue(tu.ID, out var c))
                calls.Add(c);
        }
        return (raw, text.ToString(), calls);
    }

    private static readonly string[] StrictUnsafeKeywords =
        ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength"];

    /// <summary>Schema sent on the wire: numeric/length constraints removed (spike assumption, see FINDINGS).</summary>
    public static Dictionary<string, JsonElement> WireSchema(JsonElement schema)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(schema.GetRawText())!;
        Strip(node);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(node.ToJsonString())!;

        static void Strip(System.Text.Json.Nodes.JsonNode? n)
        {
            if (n is System.Text.Json.Nodes.JsonObject o)
            {
                foreach (var k in StrictUnsafeKeywords) o.Remove(k);
                foreach (var (_, v) in o.ToList()) Strip(v);
            }
            else if (n is System.Text.Json.Nodes.JsonArray a)
                foreach (var v in a) Strip(v);
        }
    }

    private static string ConstraintNote(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var props)) return "";
        var notes = new List<string>();
        foreach (var p in props.EnumerateObject())
            foreach (var k in StrictUnsafeKeywords)
                if (p.Value.TryGetProperty(k, out var v)) notes.Add($"{p.Name}.{k}={v.GetRawText()}");
        return notes.Count == 0 ? "" : " Constraints: " + string.Join(", ", notes) + ".";
    }

    // ---------------- history replay ----------------

    public static List<BetaMessageParam> BuildMessages(IReadOnlyList<AiMessage> history)
    {
        var list = new List<BetaMessageParam>();
        foreach (var m in history)
        {
            switch (m.Role)
            {
                case AiRole.User:
                    list.Add(new BetaUserMessageParam { Content = m.Text ?? "" });
                    break;
                case AiRole.ToolResults:
                    list.Add(new BetaUserMessageParam
                    {
                        Content = m.ToolResults.Select(r =>
                        {
                            var tr = new BetaToolResultBlockParam { ToolUseID = r.ToolCallId, Content = r.Content };
                            return (BetaContentBlockParam)(r.IsError ? tr with { IsError = true } : tr);
                        }).ToList(),
                    });
                    break;
                case AiRole.Assistant:
                    list.Add(new BetaAssistantMessageParam { Content = AssistantContent(m) });
                    break;
            }
        }
        return list;
    }

    private static List<BetaContentBlockParam> AssistantContent(AiMessage m)
    {
        if (m.RawProvider != Id || m.RawContent is null)
        {
            // Cross-provider replay: degrade to normalized text + tool_use (no thinking).
            var degraded = new List<BetaContentBlockParam>();
            if (!string.IsNullOrEmpty(m.Text)) degraded.Add(new BetaTextBlockParam { Text = m.Text });
            foreach (var c in m.ToolCalls)
                degraded.Add(new BetaToolUseBlockParam
                {
                    ID = c.Id,
                    Name = c.Name,
                    Input = c.Input is { ValueKind: JsonValueKind.Object } o
                        ? o.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())
                        : new Dictionary<string, JsonElement>(),
                });
            return degraded;
        }

        var blocks = JsonSerializer.Deserialize<List<BetaContentBlock>>(m.RawContent)!;
        int lastFallback = blocks.FindLastIndex(b => b.TryPickFallback(out _));
        var result = new List<BetaContentBlockParam>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            bool beforeBoundary = i < lastFallback;
            if (b.TryPickText(out var t))
                result.Add(new BetaTextBlockParam { Text = t.Text });
            else if (b.TryPickThinking(out var th))
            {
                // Signature MUST be preserved. Dropped only before a fallback boundary (documented rule).
                if (!beforeBoundary)
                    result.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            }
            else if (b.TryPickRedactedThinking(out var rt))
            {
                if (!beforeBoundary) result.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
            }
            else if (b.TryPickToolUse(out var tu))
            {
                if (!beforeBoundary)
                    result.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
            }
            else if (b.TryPickFallback(out var fb))
            {
                // Echo in place (the API validates thinking around it by position); trigger echoed verbatim.
                var fbp = new BetaFallbackBlockParam { From = new() { Model = fb.From.Model }, To = new() { Model = fb.To.Model } };
                if (fb.Trigger is { } trig) fbp = fbp with { Trigger = JsonSerializer.SerializeToElement(trig) };
                result.Add(fbp);
            }
            else
            {
                throw new NotSupportedException("spike: unhandled content block type in replay: " + JsonSerializer.Serialize(b));
            }
        }
        return result;
    }
}
