using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MySSH.Mcp;

/// <summary>The exact snippets the MCP settings page shows for copying into MCP clients.</summary>
public static class McpClientSetup
{
    /// <summary>Server key used in every snippet (<c>claude mcp add … myssh …</c>, <c>"mcpServers": {"myssh": …}</c>).</summary>
    public const string ServerKey = "myssh";

    /// <summary><c>claude mcp add --transport http myssh http://127.0.0.1:PORT/mcp --header "Authorization: Bearer TOKEN"</c></summary>
    public static string ClaudeCodeCommand(Uri endpoint, string token)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(token);
        return $"claude mcp add --transport http {ServerKey} {endpoint.AbsoluteUri} --header \"Authorization: Bearer {token}\"";
    }

    /// <summary>
    /// Claude Desktop <c>claude_desktop_config.json</c> using the stdio bridge:
    /// <c>{"mcpServers":{"myssh":{"command":"/path/to/myssh-mcp"}}}</c> (indented). <paramref name="socketPath"/> adds
    /// <c>"args":["--socket", path]</c>; pass it only when the server does not use the default path.
    /// </summary>
    public static string ClaudeDesktopConfigJson(string bridgePath, string? socketPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(bridgePath);
        return WriteJson(w =>
        {
            w.WriteString("command", bridgePath);
            if (!string.IsNullOrEmpty(socketPath))
            {
                w.WriteStartArray("args");
                w.WriteStringValue("--socket");
                w.WriteStringValue(socketPath);
                w.WriteEndArray();
            }
        });
    }

    /// <summary>
    /// Generic config for clients that speak Streamable HTTP (Claude Code <c>.mcp.json</c>, Cursor, …):
    /// <c>{"mcpServers":{"myssh":{"type":"http","url":"…","headers":{"Authorization":"Bearer …"}}}}</c> (indented).
    /// </summary>
    public static string HttpConfigJson(Uri endpoint, string token)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(token);
        return WriteJson(w =>
        {
            w.WriteString("type", "http");
            w.WriteString("url", endpoint.AbsoluteUri);
            w.WriteStartObject("headers");
            w.WriteString("Authorization", "Bearer " + token);
            w.WriteEndObject();
        });
    }

    /// <summary>Where the app ships the bridge: next to the app executable (<c>Contents/MacOS/myssh-mcp</c> in the .app bundle).</summary>
    public static string DefaultBridgePath() =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "myssh-mcp.exe" : "myssh-mcp");

    private static string WriteJson(Action<Utf8JsonWriter> writeServer)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteStartObject("mcpServers");
            w.WriteStartObject(ServerKey);
            writeServer(w);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
