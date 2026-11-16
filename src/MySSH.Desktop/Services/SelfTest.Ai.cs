using System.Net;
using System.Net.Sockets;
using System.Text;
using MySSH.Ai;
using MySSH.Core;
using MySSH.Storage;

namespace MySSH.Desktop.Services;

internal static partial class SelfTest
{
    /// <summary>
    /// The AI assistant under NativeAOT (JSON and generic-instantiation problems only show up at runtime): for Claude (own
    /// HTTP/SSE client) and an OpenAI-compatible server (OpenAI SDK), a loopback mock plays a tool_use turn and a final turn;
    /// the agent loop calls list_hosts through the real tool layer, and the history and settings round-trip through storage.
    /// </summary>
    static async Task AiRoundTripAsync(MySshStorage storage)
    {
        var visible = (await storage.Hosts.ListAsync()).First(h => h.AiPolicy != AiPolicy.Off);
        await using var connector = new MySSH.Ssh.SshConnector(storage.Hosts, storage.Identities, storage.Proxies, storage.KnownHosts,
            ViewModels.RejectingPrompts.Instance, ViewModels.RejectingPrompts.Instance);
        var tools = MySSH.Tools.ToolServices.Create(storage.Hosts, connector, ViewModels.RejectingPrompts.Instance, storage.AuditLog);
        using var http = AiSettingsService.CreateHttpClient();

        foreach (var (name, protocol, toolTurn, finalTurn, models) in new[]
        {
            ("Claude", AiProtocol.Anthropic, ClaudeToolTurn, ClaudeFinalTurn, ClaudeModels),
            ("OpenAI-compatible", AiProtocol.OpenAICompatible, OpenAIToolTurn, OpenAIFinalTurn, OpenAIModels),
        })
        {
            await using var mock = MockHttp.Start(models, toolTurn, finalTurn);
            var config = AiProviderConfig.Custom(protocol, protocol == AiProtocol.Anthropic ? mock.BaseUrl : mock.BaseUrl + "/v1") with
            {
                Name = name, Model = protocol == AiProtocol.Anthropic ? "claude-opus-5-5" : "gpt-test",
            };
            var provider = AiProviders.Create(config, "self-test-key", http);
            var listed = await provider.ListModelsAsync(CancellationToken.None);
            Check(listed.Count == 1, $"AI {name}: models listed");

            var agent = new AiAgent(provider, tools.Invoker);
            var turn = new AgentTurn(Guid.NewGuid(), [], "Which hosts can you see?", config.Model!, AiEffort.Medium,
                AiTools.FromCatalog(tools.Catalog), AiAgent.Actor(config, config.Model!));
            var history = new List<AiMessage>();
            AgentFinished? finished = null;
            string? toolOutput = null;
            await foreach (var ev in agent.RunAsync(turn))
            {
                if (ev is AgentHistoryAppended a) history.Add(a.Message);
                if (ev is AgentToolFinished f) toolOutput = f.Result.Content;
                if (ev is AgentFinished done) finished = done;
            }
            Check(finished?.Stop == AgentStop.Completed, $"AI {name}: agent loop completed ({finished?.Stop} {finished?.Detail})");
            Check(toolOutput?.Contains(visible.Label, StringComparison.Ordinal) == true, $"AI {name}: list_hosts ran through the tool layer");
            Check(history.Count == 4 && mock.Posts.Count == 2 && mock.Posts[1].Contains("Done", StringComparison.Ordinal) is false
                  && mock.Posts[1].Contains(visible.Label, StringComparison.Ordinal), $"AI {name}: tool result sent back");

            var conversation = new AiConversation { Title = "self-test " + name, ProviderConfigId = config.Id, Model = config.Model };
            await storage.AiConversations.SaveAsync(conversation);
            for (var i = 0; i < history.Count; i++)
                await storage.AiConversations.AppendMessageAsync(conversation.Id,
                    new AiStoredMessage(i, (int)history[i].Role, AiHistorySerializer.Serialize(history[i]), DateTimeOffset.UtcNow));
            var back = (await storage.AiConversations.GetMessagesAsync(conversation.Id)).Select(m => AiHistorySerializer.Deserialize(m.PayloadJson)).ToList();
            Check(back.Count == 4 && back[1].RawContent == history[1].RawContent && back[1].ToolCalls[0].Name == "list_hosts",
                $"AI {name}: history stored encrypted and read back");
        }

        var settings = new AiSettingsService(storage.Settings, storage.Secrets, http);
        var claude = AiProviderConfig.Claude();
        await settings.SaveAsync(new AiSettings { Providers = [claude, AiProviderConfig.DeepSeek()], DefaultProviderId = claude.Id });
        await settings.SetApiKeyAsync(claude, "sk-ant-self-test");
        var loaded = await new AiSettingsService(storage.Settings, storage.Secrets, http).LoadAsync();
        Check(loaded.Providers.Count == 2 && loaded.DefaultProvider?.Id == claude.Id && await settings.GetApiKeyAsync(claude) == "sk-ant-self-test",
            "AI settings and API key persisted");
        var audit = await storage.AuditLog.QueryAsync(new AuditQuery { ActorPrefix = "ai:" });
        Check(audit.Count(e => e.Tool == "list_hosts") == 2, "AI tool calls audited");
    }

    const string ClaudeModels = """{"data":[{"type":"model","id":"claude-opus-5-5","display_name":"Claude Opus 5.5","created_at":"2026-01-01T00:00:00Z"}],"has_more":false,"first_id":"claude-opus-5-5","last_id":"claude-opus-5-5"}""";

    static string Sse(params string[] events) => string.Concat(events.Select(e => e + "\n\n"));

    static readonly string ClaudeToolTurn = Sse(
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}",
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}}",
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"List the hosts.\"}}",
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"c2lnbmF0dXJl\"}}",
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}",
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"list_hosts\",\"input\":{}}}",
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{}\"}}",
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}",
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":20}}",
        "event: message_stop\ndata: {\"type\":\"message_stop\"}");

    static readonly string ClaudeFinalTurn = Sse(
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_2\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":30,\"output_tokens\":1}}}",
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Done.\"}}",
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}",
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":3}}",
        "event: message_stop\ndata: {\"type\":\"message_stop\"}");

    const string OpenAIModels = """{"object":"list","data":[{"id":"gpt-test","object":"model","created":1,"owned_by":"self-test"}]}""";
    const string Chunk = "\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-test\"";

    static readonly string OpenAIToolTurn = Sse(
        "data: {" + Chunk + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"list_hosts\",\"arguments\":\"{}\"}}]},\"finish_reason\":null}]}",
        "data: {" + Chunk + ",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}",
        "data: [DONE]");

    static readonly string OpenAIFinalTurn = Sse(
        "data: {" + Chunk + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Done.\"},\"finish_reason\":null}]}",
        "data: {" + Chunk + ",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}",
        "data: [DONE]");

    /// <summary>Minimal loopback HTTP/1.1 server: GET → models JSON, each POST → the next SSE body (Connection: close).</summary>
    sealed class MockHttp : IAsyncDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly Queue<string> _posts;
        readonly string _models;
        readonly Task _loop;
        readonly CancellationTokenSource _stop = new();
        public List<string> Posts { get; } = [];
        public string BaseUrl { get; }

        MockHttp(string models, string[] posts)
        {
            _models = models;
            _posts = new Queue<string>(posts);
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = Task.Run(LoopAsync);
        }

        public static MockHttp Start(string models, params string[] posts) => new(models, posts);

        async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                using (client)
                {
                    var stream = client.GetStream();
                    var (method, body) = await ReadRequestAsync(stream);
                    string type, content;
                    if (method == "GET") { type = "application/json"; content = _models; }
                    else
                    {
                        lock (Posts) Posts.Add(body);
                        type = "text/event-stream";
                        content = _posts.Count > 0 ? _posts.Dequeue() : "";
                    }
                    var bytes = Encoding.UTF8.GetBytes(content);
                    var head = $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(bytes);
                }
            }
        }

        static async Task<(string Method, string Body)> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new byte[1 << 20];
            var n = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(n));
                if (read == 0) break;
                n += read;
                var text = Encoding.UTF8.GetString(buffer, 0, n);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0) continue;
                var length = 0;
                foreach (var line in text[..end].Split("\r\n"))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        length = int.Parse(line[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                var bodyBytes = Encoding.UTF8.GetByteCount(text) - (end + 4);
                if (bodyBytes >= length)
                    return (text[..text.IndexOf(' ', StringComparison.Ordinal)], text[(end + 4)..]);
            }
            return ("", "");
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _loop; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}
