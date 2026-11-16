using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MySSH.Tools;

namespace MySSH.Mcp;

/// <summary>
/// Publishes <see cref="IToolCatalog"/> as MCP tools / resources / prompts through the SDK's handler API (no reflection,
/// NativeAOT clean) and routes every call to <see cref="IToolInvoker"/>, which owns validation, policy, approval,
/// truncation and audit. Resources are read through the invoker too, so policy and audit apply to them.
/// </summary>
internal sealed partial class McpToolAdapter(IToolCatalog catalog, IToolInvoker invoker, ClientTracker tracker, ILogger? logger = null)
{
    public const string HostsResourceUri = "myssh://hosts";
    public const string ScreenResourceTemplate = "myssh://sessions/{id}/screen";
    private const string ScreenPrefix = "myssh://sessions/";
    private const string ScreenSuffix = "/screen";

    // Tool names / argument names the resources and prompts depend on (design §6.2).
    internal const string ListHostsTool = "list_hosts";
    internal const string ReadTerminalTool = "read_terminal";
    internal const string ReadTerminalSessionArg = "session";

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>Server options for one MCP server: the HTTP endpoint (<paramref name="local"/> null) or one local connection.</summary>
    public void Configure(McpServerOptions options, McpHostOptions host, ClientTracker.Entry? local)
    {
        options.ServerInfo = new Implementation { Name = host.ServerName, Version = host.ServerVersion };
        options.ServerInstructions = host.Instructions;
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Tools ??= new ToolsCapability();
        options.Capabilities.Resources ??= new ResourcesCapability();
        options.Capabilities.Prompts ??= new PromptsCapability();

        var h = options.Handlers;
        h.ListToolsHandler = (ctx, ct) => { Client(ctx, local); return ValueTask.FromResult(ListTools()); };
        h.CallToolHandler = (ctx, ct) => CallToolAsync(ctx, local, ct);
        h.ListResourcesHandler = (ctx, ct) => { Client(ctx, local); return ValueTask.FromResult(ListResources()); };
        h.ListResourceTemplatesHandler = (ctx, ct) => { Client(ctx, local); return ValueTask.FromResult(ListResourceTemplates()); };
        h.ReadResourceHandler = (ctx, ct) => ReadResourceAsync(ctx, local, ct);
        h.ListPromptsHandler = (ctx, ct) => { Client(ctx, local); return ValueTask.FromResult(new ListPromptsResult { Prompts = McpPrompts.All }); };
        h.GetPromptHandler = (ctx, ct) => { Client(ctx, local); return ValueTask.FromResult(McpPrompts.Get(ctx.Params)); };
    }

    // ------------------------------------------------------------------ tools

    internal ListToolsResult ListTools() => new()
    {
        Tools = catalog.Tools.Select(t => new Tool
        {
            Name = t.Name,
            Title = t.Title,
            Description = t.Description,
            InputSchema = t.InputSchema,
            Annotations = new ToolAnnotations
            {
                Title = t.Title,
                ReadOnlyHint = t.ReadOnly,
                DestructiveHint = t.Destructive,
                IdempotentHint = t.Idempotent,
                OpenWorldHint = t.OpenWorld,
            },
        }).ToList(),
    };

    private async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> ctx, ClientTracker.Entry? local, CancellationToken ct)
    {
        var p = ctx.Params ?? throw new McpProtocolException("missing params", McpErrorCode.InvalidParams);
        var tool = catalog.Find(p.Name) ?? throw new McpProtocolException($"Unknown tool: '{p.Name}'", McpErrorCode.InvalidParams);
        var (entry, actor) = Client(ctx, local);
        using var _ = tracker.BeginCall(entry);
        var result = await InvokeAsync(new ToolCallRequest(tool.Name, ToObject(p.Arguments)), actor, ct).ConfigureAwait(false);
        return new CallToolResult
        {
            IsError = result.IsError,
            Content = [new TextContentBlock { Text = result.Text }],
            StructuredContent = result.Structured,
        };
    }

    private async Task<ToolCallResult> InvokeAsync(ToolCallRequest request, ToolActor actor, CancellationToken ct)
    {
        try
        {
            return await invoker.InvokeAsync(request, actor, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // the client cancelled or went away; the SDK reports it
        }
        catch (Exception ex)
        {
            // The invoker reports tool failures as IsError; anything thrown is a bug. Don't leak details to the client.
            LogInvokerFailure(_logger, ex, request.ToolName, actor.Label);
            return ToolCallResult.Error($"internal error while running {request.ToolName}");
        }
    }

    // ------------------------------------------------------------------ resources

    private static ListResourcesResult ListResources() => new()
    {
        Resources =
        [
            new Resource
            {
                Uri = HostsResourceUri,
                Name = "hosts",
                Title = "MySSH hosts",
                Description = "Hosts the user enabled for AI/MCP access (same data as the list_hosts tool; never contains credentials).",
                MimeType = "application/json",
            },
        ],
    };

    private static ListResourceTemplatesResult ListResourceTemplates() => new()
    {
        ResourceTemplates =
        [
            new ResourceTemplate
            {
                UriTemplate = ScreenResourceTemplate,
                Name = "session_screen",
                Title = "Terminal screen",
                Description = "Visible screen and recent scrollback of an open MySSH terminal tab (ids from list_sessions).",
                MimeType = "text/plain",
            },
        ],
    };

    private async ValueTask<ReadResourceResult> ReadResourceAsync(RequestContext<ReadResourceRequestParams> ctx, ClientTracker.Entry? local, CancellationToken ct)
    {
        var uri = ctx.Params?.Uri ?? throw new McpProtocolException("missing uri", McpErrorCode.InvalidParams);
        ToolCallRequest request;
        string mime;
        if (uri == HostsResourceUri)
        {
            request = new ToolCallRequest(ListHostsTool, EmptyObject);
            mime = "application/json";
        }
        else if (TryParseScreenUri(uri, out var sessionId))
        {
            request = new ToolCallRequest(ReadTerminalTool, StringObject(ReadTerminalSessionArg, sessionId));
            mime = "text/plain";
        }
        else
        {
            throw new McpProtocolException($"Unknown resource: '{uri}'", McpErrorCode.ResourceNotFound);
        }

        var (entry, actor) = Client(ctx, local);
        using var _ = tracker.BeginCall(entry);
        var result = await InvokeAsync(request, actor, ct).ConfigureAwait(false);
        if (result.IsError)
            throw new McpProtocolException(result.Text, McpErrorCode.ResourceNotFound);
        var text = mime == "application/json" && result.Structured is { } s ? s.GetRawText() : result.Text;
        return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = mime, Text = text }] };
    }

    internal static bool TryParseScreenUri(string uri, out string sessionId)
    {
        sessionId = "";
        if (!uri.StartsWith(ScreenPrefix, StringComparison.Ordinal) || !uri.EndsWith(ScreenSuffix, StringComparison.Ordinal))
            return false;
        var id = uri[ScreenPrefix.Length..^ScreenSuffix.Length];
        if (id.Length == 0 || id.Contains('/', StringComparison.Ordinal)) return false;
        sessionId = Uri.UnescapeDataString(id);
        return sessionId.Length > 0;
    }

    // ------------------------------------------------------------------ clients

    private (ClientTracker.Entry Entry, ToolActor Actor) Client<T>(RequestContext<T> ctx, ClientTracker.Entry? local)
    {
        // 2026-07-28 sends clientInfo in every request's _meta; 2025-11-25 only in initialize (absent in stateless HTTP, G4).
        var info = ctx.JsonRpcRequest.Context?.ClientInfo ?? ctx.Server.ClientInfo;
        var name = McpActors.SanitizeName(info?.Name);
        var version = McpActors.SanitizeName(info?.Version);
        ClientTracker.Entry entry;
        if (local is not null)
        {
            entry = local;
            tracker.Touch(entry, name, version);
            name = entry.Name; // keep the name learnt at initialize for later requests
        }
        else
        {
            entry = tracker.Http(name, version);
            tracker.Touch(entry, name, version);
        }
        return (entry, McpActors.Create(entry.Transport, name));
    }

    // ------------------------------------------------------------------ JSON helpers (no reflection-based serialization)

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    internal static JsonElement ToObject(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return EmptyObject;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var (key, value) in arguments)
            {
                w.WritePropertyName(key);
                value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static JsonElement StringObject(string key, string value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString(key, value);
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "MCP tool invoker threw for {Tool} ({Actor})")]
    private static partial void LogInvokerFailure(ILogger logger, Exception ex, string tool, string actor);
}
