using System.Text;
using MySSH.Core;
using MySSH.Tools;

namespace MySSH.Mcp;

/// <summary>Maps an MCP client to the tool layer's <see cref="ToolActor"/> (audit label + approval display name).</summary>
internal static class McpActors
{
    public const int MaxNameLength = 64;

    /// <summary>
    /// clientInfo.name is self-asserted by the client and ends up in audit rows and the approval modal: keep it short and
    /// printable. Allowed: letters, digits and <c>. _ - / @ + : space</c>; everything else becomes '_'. Null if nothing is left.
    /// </summary>
    public static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var sb = new StringBuilder(Math.Min(name.Length, MaxNameLength));
        foreach (var c in name.Trim())
        {
            if (sb.Length == MaxNameLength) break;
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '/' or '@' or '+' or ':' or ' ' ? c : '_');
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;
    }

    public static string Label(McpClientTransport transport, string? name) =>
        "mcp:" + (name ?? (transport == McpClientTransport.Http ? "http" : "local"));

    public static string DisplayName(McpClientTransport transport, string? name) =>
        name is not null ? $"{name} (MCP)"
        : transport == McpClientTransport.Http ? "HTTP client (MCP)" : "Local client (MCP)";

    public static ToolActor Create(McpClientTransport transport, string? name) =>
        new(ConnectionOrigin.Mcp, Label(transport, name), DisplayName(transport, name));
}
