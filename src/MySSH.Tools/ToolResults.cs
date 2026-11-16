using System.Text.Json.Serialization;

namespace MySSH.Tools;

// Structured results (MCP structuredContent). camelCase JSON via the source-generated ToolJsonContext (NativeAOT).

public sealed record HostInfo(
    Guid Id, string Label, string Hostname, int Port, string? Username, IReadOnlyList<string> Tags, string AiPolicy,
    IReadOnlyList<string> JumpChain);

public sealed record ListHostsResult(IReadOnlyList<HostInfo> Hosts);

public sealed record RunCommandResult(
    Guid HostId, int? ExitCode, string Stdout, string Stderr, bool StdoutTruncated, bool StderrTruncated, bool TimedOut,
    string? ExitSignal, long DurationMs);

public sealed record RemoteEntry(
    string Name, string Path, string Kind, long Size, DateTimeOffset Modified, string Permissions, string? LinkTarget);

public sealed record SftpListResult(Guid HostId, string Path, IReadOnlyList<RemoteEntry> Entries, int TotalEntries, bool Truncated);

public sealed record SftpStatResult(Guid HostId, string Path, bool Exists, RemoteEntry? Entry);

public sealed record SftpReadResult(Guid HostId, string Path, long? Size, int BytesRead, bool Truncated, string Content);

public sealed record SftpWriteResult(Guid HostId, string Path, int Bytes, string Mode);

public sealed record SftpPathResult(Guid HostId, string Path, string Action);

public sealed record SessionInfo(string Id, Guid HostId, string HostLabel, string Title, bool Connected, int Columns, int Rows);

public sealed record ListSessionsResult(IReadOnlyList<SessionInfo> Sessions);

public sealed record ReadTerminalResult(string Session, Guid HostId, int Lines, bool Truncated, string Text);

public sealed record SendToTerminalResult(string Session, Guid HostId, int Characters, int LineCount, bool Executed);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(ListHostsResult))]
[JsonSerializable(typeof(RunCommandResult))]
[JsonSerializable(typeof(SftpListResult))]
[JsonSerializable(typeof(SftpStatResult))]
[JsonSerializable(typeof(SftpReadResult))]
[JsonSerializable(typeof(SftpWriteResult))]
[JsonSerializable(typeof(SftpPathResult))]
[JsonSerializable(typeof(ListSessionsResult))]
[JsonSerializable(typeof(ReadTerminalResult))]
[JsonSerializable(typeof(SendToTerminalResult))]
public sealed partial class ToolJsonContext : JsonSerializerContext;
