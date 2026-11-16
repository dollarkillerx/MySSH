using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySSH.Core;
using MySSH.Ssh;

namespace MySSH.Tools;

/// <summary>
/// The single entry point for MCP and the AI assistant (design §6.1): validate → resolve host/session → policy →
/// approval → execute → truncate → audit. Exactly one <see cref="AuditEntry"/> is written per call, whatever happens.
/// Thread-safe and stateless between calls; any number of calls may run concurrently.
/// </summary>
public sealed partial class ToolInvoker : IToolInvoker
{
    private const int AuditStringLimit = 16 * 1024;
    private const int MaxTerminalChars = 256 * 1024;

    private readonly IHostRepository _hosts;
    private readonly ISshConnector _connector;
    private readonly IPolicyEngine _policy;
    private readonly IApprovalService _approvals;
    private readonly IAuditLogStore _audit;
    private readonly ITerminalSessionRegistry _terminals;
    private readonly ToolOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public ToolInvoker(
        IHostRepository hosts, ISshConnector connector, IPolicyEngine policy, IApprovalService approvals, IAuditLogStore audit,
        ITerminalSessionRegistry terminals, ToolOptions options, TimeProvider timeProvider, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(terminals);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _hosts = hosts;
        _connector = connector;
        _policy = policy;
        _approvals = approvals;
        _audit = audit;
        _terminals = terminals;
        _options = options;
        _time = timeProvider;
        _logger = logger ?? NullLogger.Instance;
        Catalog = new ToolCatalog(options);
    }

    /// <summary>The catalog this invoker validates against (schemas use the same <see cref="ToolOptions"/>).</summary>
    public ToolCatalog Catalog { get; }

    /// <summary>Per-call audit state.</summary>
    private sealed class Call(string tool, ToolActor actor, DateTimeOffset started, long startTimestamp)
    {
        public string Tool { get; set; } = tool;
        public ToolActor Actor { get; } = actor;
        public DateTimeOffset Started { get; } = started;
        public long StartTimestamp { get; } = startTimestamp;
        public string ArgumentsJson { get; set; } = "{}";
        public Guid? HostId { get; set; }
        public AuditDecision? Decision { get; set; }
        public int? ExitCode { get; set; }
        public string? Error { get; set; }

        public ToolCallResult Fail(AuditDecision decision, string message)
        {
            Decision = decision;
            Error ??= message;
            return ToolCallResult.Error(message);
        }

        /// <summary>The call was allowed/approved but execution failed.</summary>
        public ToolCallResult ExecutionFailed(string message)
        {
            Error = message;
            return ToolCallResult.Error(message);
        }
    }

    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled (audited as Cancelled).</exception>
    /// <exception cref="ArgumentException"><paramref name="actor"/> has <see cref="ConnectionOrigin.User"/>.</exception>
    public async Task<ToolCallResult> InvokeAsync(ToolCallRequest request, ToolActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Origin == ConnectionOrigin.User)
            throw new ArgumentException("Tool calls come from AI/MCP actors; ConnectionOrigin.User is not allowed here.", nameof(actor));

        var call = new Call(Clip(request.ToolName ?? "", 128), actor, _time.GetUtcNow(), _time.GetTimestamp());
        ToolCallResult result;
        try
        {
            result = await DispatchAsync(request, call, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            call.Decision = AuditDecision.Cancelled;
            call.Error ??= "Cancelled by the caller.";
            await WriteAuditAsync(call).ConfigureAwait(false);
            throw;
        }
#pragma warning disable CA1031 // A tool call must never crash the MCP server / agent loop; the failure is returned and audited.
        catch (Exception e)
#pragma warning restore CA1031
        {
            LogUnexpected(_logger, e, call.Tool);
            call.Decision ??= AuditDecision.Invalid;
            result = call.ExecutionFailed($"Internal error while running {call.Tool}: {e.GetType().Name}: {e.Message}");
        }
        await WriteAuditAsync(call).ConfigureAwait(false);
        return result;
    }

    private async Task WriteAuditAsync(Call call)
    {
        var entry = new AuditEntry(
            Guid.NewGuid(), call.Started, call.Actor.Label, call.Tool, call.HostId, call.ArgumentsJson,
            call.Decision ?? AuditDecision.Invalid, call.ExitCode, (long)_time.GetElapsedTime(call.StartTimestamp).TotalMilliseconds,
            call.Error is null ? null : Clip(call.Error, 4096));
        try
        {
            // Never cancelled by the caller: the record of what happened must be written.
            await _audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Auditing failures are logged, they must not change the tool result.
        catch (Exception e)
#pragma warning restore CA1031
        {
            LogAuditFailed(_logger, e, call.Tool);
        }
    }

    private async Task<ToolCallResult> DispatchAsync(ToolCallRequest request, Call call, CancellationToken ct)
    {
        call.ArgumentsJson = RedactArguments(request.Arguments);
        var def = Catalog.Find(request.ToolName);
        if (def is null)
            return call.Fail(AuditDecision.Invalid,
                $"Unknown tool '{Clip(request.ToolName ?? "", 64)}'. Available tools: {string.Join(", ", Catalog.Tools.Select(t => t.Name))}.");

        var args = Catalog.SchemaOf(def.Name).Validate(request.Arguments, out var error);
        if (args is null)
            return call.Fail(AuditDecision.Invalid, $"Invalid arguments for {def.Name}: {error}");

        ct.ThrowIfCancellationRequested();
        return def.Name switch
        {
            ToolNames.ListHosts => await ListHostsAsync(call, args, ct).ConfigureAwait(false),
            ToolNames.RunCommand => await RunCommandAsync(call, def, args, ct).ConfigureAwait(false),
            ToolNames.SftpList => await SftpListAsync(call, def, args, ct).ConfigureAwait(false),
            ToolNames.SftpStat => await SftpStatAsync(call, def, args, ct).ConfigureAwait(false),
            ToolNames.SftpRead => await SftpReadAsync(call, def, args, ct).ConfigureAwait(false),
            ToolNames.SftpWrite => await SftpWriteAsync(call, def, args, ct).ConfigureAwait(false),
            ToolNames.SftpMkdir => await SftpMkdirAsync(call, def, args, ct).ConfigureAwait(false),
            ToolNames.SftpDelete => await SftpDeleteAsync(call, def, args, ct).ConfigureAwait(false),
            ToolNames.ListSessions => await ListSessionsAsync(call, ct).ConfigureAwait(false),
            ToolNames.ReadTerminal => await ReadTerminalAsync(call, def, args, ct).ConfigureAwait(false),
            ToolNames.SendToTerminal => await SendToTerminalAsync(call, def, args, ct).ConfigureAwait(false),
            _ => call.Fail(AuditDecision.Invalid, $"Tool {def.Name} is not implemented."),
        };
    }

    // ------------------------------------------------------------------ hosts

    /// <summary>AI tools are SSH-only: RDP hosts are never visible, whatever their policy says.</summary>
    private static bool Visible(Host h) => h.AiPolicy != AiPolicy.Off && Enum.IsDefined(h.AiPolicy) && h.Protocol == HostProtocol.Ssh;

    internal static string UnknownHostMessage(string host) =>
        $"Unknown host '{Clip(host, 100)}'. Call list_hosts to see the hosts you can use (by id or exact label).";

    /// <summary>Id or exact label (case-insensitive). Hosts with AiPolicy Off are treated exactly like missing ones.</summary>
    private async Task<(Host? Host, string? Error)> ResolveHostAsync(string reference, CancellationToken ct)
    {
        var text = reference.Trim();
        if (Guid.TryParse(text, out var id))
        {
            var byId = await _hosts.GetAsync(id, ct).ConfigureAwait(false);
            if (byId is not null && Visible(byId))
                return (byId, null);
            // A label could look like a GUID; fall through to the label lookup only if no host has that id.
        }
        var matches = (await _hosts.ListAsync(ct).ConfigureAwait(false))
            .Where(h => Visible(h) && string.Equals(h.Label.Trim(), text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            1 => (matches[0], null),
            0 => (null, UnknownHostMessage(reference)),
            _ => (null, $"The label '{Clip(reference, 100)}' matches {matches.Count} hosts; pass the host id instead: " +
                        string.Join("; ", matches.Select(h => $"{h.Id:D} ({h.Username ?? "?"}@{h.Hostname}:{h.Port.ToString(CultureInfo.InvariantCulture)})")) + "."),
        };
    }

    private async Task<ToolCallResult> ListHostsAsync(Call call, ValidatedArguments args, CancellationToken ct)
    {
        var all = await _hosts.ListAsync(ct).ConfigureAwait(false);
        var byId = all.ToDictionary(h => h.Id);
        var query = args.String("query")?.Trim();
        var tag = args.String("tag")?.Trim();
        var list = new List<HostInfo>();
        foreach (var h in all.Where(Visible))
        {
            if (!string.IsNullOrEmpty(tag) && !h.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!string.IsNullOrEmpty(query) &&
                !(Contains(h.Label, query) || Contains(h.Hostname, query) || Contains(h.Username, query) || h.Tags.Any(t => Contains(t, query))))
                continue;
            // A jump host that is itself hidden (Off) is not named.
            var chain = h.JumpChain.Select(j => byId.TryGetValue(j, out var jh) && Visible(jh) ? jh.Label : "(hidden jump host)").ToList();
            list.Add(new HostInfo(h.Id, h.Label, h.Hostname, h.Port, h.Username, [.. h.Tags], h.AiPolicy.ToString(), chain));
        }
        call.Decision = AuditDecision.Allowed;
        var result = new ListHostsResult(list);
        var text = list.Count == 0
            ? "No hosts are available. The user has to set a host's AI policy (ReadOnly, Ask, Trusted or FullAccess) in MySSH first" +
              (string.IsNullOrEmpty(query) && string.IsNullOrEmpty(tag) ? "." : ", or no host matches the filter.")
            : JsonSerializer.Serialize(result, ToolJsonContext.Default.ListHostsResult);
        return Ok(text, result, ToolJsonContext.Default.ListHostsResult);
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ policy + approval

    /// <summary>Returns null when the call may proceed (Decision set to Allowed/Approved), else the error result.</summary>
    private async Task<ToolCallResult?> AuthorizeAsync(
        Call call, ToolDefinition def, Host host, string? command, bool overwrite, bool execute, string summary, string? details, CancellationToken ct)
    {
        var decision = _policy.Authorize(new PolicyRequest(def, host, command, overwrite, execute));
        switch (decision.Verdict)
        {
            case PolicyVerdict.Allow:
                call.Decision = AuditDecision.Allowed;
                return null;
            case PolicyVerdict.Deny:
                return call.Fail(AuditDecision.PolicyRejected, decision.Reason ?? "Not allowed by the host's AI policy.");
        }

        var timeout = _options.ApprovalTimeout;
        var approval = new ApprovalRequest(Guid.NewGuid(), call.Actor, def, host, summary, details, decision.Reason, decision.HighRisk,
            _time.GetUtcNow() + timeout, decision.Kind, decision.RiskRule);
        using var deadline = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ApprovalOutcome outcome;
        try
        {
            // WaitAsync enforces the deadline even if the approval UI ignores its token.
            outcome = await _approvals.RequestAsync(approval, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            outcome = ApprovalOutcome.Cancelled;
        }
        ct.ThrowIfCancellationRequested();
        if (outcome == ApprovalOutcome.Cancelled && deadline.IsCancellationRequested)
            outcome = ApprovalOutcome.TimedOut;

        switch (outcome)
        {
            case ApprovalOutcome.Approved:
                call.Decision = AuditDecision.Approved;
                return null;
            case ApprovalOutcome.Denied:
                return call.Fail(AuditDecision.Denied, $"The user denied this request ({Clip(summary, 200)}). Do not retry it unchanged; ask the user what to do instead.");
            case ApprovalOutcome.TimedOut:
                return call.Fail(AuditDecision.TimedOut,
                    $"Nobody approved the request within {((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)} seconds, so it was treated as denied.");
            default:
                return call.Fail(AuditDecision.Cancelled, "The approval request was cancelled before the user answered.");
        }
    }

    // ------------------------------------------------------------------ SSH execution

    /// <summary>Connects with the actor's origin (strict host keys, never prompts), runs <paramref name="body"/>, maps failures.</summary>
    private async Task<ToolCallResult> OnHostAsync(Call call, Host host, Func<ISshConnection, Task<ToolCallResult>> body, CancellationToken ct)
    {
        try
        {
            var conn = await _connector.ConnectAsync(host.Id, call.Actor.Origin, ct).ConfigureAwait(false);
            await using (conn.ConfigureAwait(false))
                return await body(conn).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SshConnectException e)
        {
            call.Error = $"{e.Kind}: {e.Message}";
            return ToolCallResult.Error(ConnectFailureMessage(e, host));
        }
        catch (SshConnectionLostException e)
        {
            return call.ExecutionFailed($"The SSH connection to {host.Label} was lost: {e.Message}");
        }
        catch (VaultLockedException)
        {
            return call.ExecutionFailed("MySSH's credential vault is locked, so the stored credentials for this host cannot be used. Ask the user to unlock MySSH.");
        }
        catch (SecretUnavailableException)
        {
            return call.ExecutionFailed($"A stored credential for {host.Label} could not be decrypted. Ask the user to re-enter it in MySSH.");
        }
        catch (FileNotFoundException e)
        {
            return call.ExecutionFailed($"No such file or directory: {e.Message}");
        }
        catch (DirectoryNotFoundException e)
        {
            return call.ExecutionFailed($"No such file or directory: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            return call.ExecutionFailed($"Permission denied: {e.Message}");
        }
        catch (IOException e)
        {
            return call.ExecutionFailed(e.Message);
        }
        catch (TimeoutException e)
        {
            return call.ExecutionFailed($"The operation timed out: {e.Message}");
        }
    }

    /// <summary>Friendly text per <see cref="SshFailureKind"/> (always names the kind).</summary>
    internal static string ConnectFailureMessage(SshConnectException e, Host host)
    {
        var hop = e.HopLabel is { Length: > 0 } l && !string.Equals(l, host.Label, StringComparison.Ordinal) ? $"jump host '{l}'" : $"'{host.Label}'";
        var detail = e.Kind switch
        {
            SshFailureKind.HostKeyUnknown =>
                $"the host key of {hop} is not trusted yet; connect once from MySSH and accept the key. AI/MCP connections never accept unknown keys.",
            SshFailureKind.HostKeyChanged =>
                $"the host key of {hop} has CHANGED since it was trusted (possible man-in-the-middle attack). The connection was refused; the user must verify the new key in MySSH.",
            SshFailureKind.HostKeyRejected => $"the host key of {hop} was rejected.",
            SshFailureKind.AuthenticationFailed =>
                $"authentication to {hop} failed. Check the host's identity (or its separate AI identity) in MySSH.",
            SshFailureKind.PassphraseRequired =>
                "the private key is encrypted and its passphrase is not saved; AI/MCP connections cannot ask for it. Save the passphrase in MySSH or use a separate AI identity.",
            SshFailureKind.WrongPassphrase => "the saved passphrase of the private key is wrong.",
            SshFailureKind.InvalidKey => "the private key is invalid or in an unsupported format.",
            SshFailureKind.Network => $"{hop} could not be reached over the network.",
            SshFailureKind.Proxy => "the proxy refused or failed the connection.",
            SshFailureKind.Timeout => $"connecting to {hop} timed out.",
            SshFailureKind.InvalidConfiguration => "the host configuration in MySSH is incomplete or inconsistent.",
            _ => "the SSH server reported a protocol error.",
        };
        return $"Could not connect to {host.Label} [{e.Kind}]: {detail} ({Clip(e.Message, 300)})";
    }

    private async Task<ToolCallResult> RunCommandAsync(Call call, ToolDefinition def, ValidatedArguments args, CancellationToken ct)
    {
        var (host, error) = await ResolveHostAsync(args.RequiredString("host"), ct).ConfigureAwait(false);
        if (host is null)
            return call.Fail(AuditDecision.Invalid, error!);
        call.HostId = host.Id;
        var command = args.RequiredString("command");
        var cwd = args.String("cwd");
        var timeoutS = (int)(args.Integer("timeout_s") ?? (long)_options.DefaultCommandTimeout.TotalSeconds);
        var lines = CountLines(command);
        var summary = FirstLine(command, 200) + (lines > 1 ? $" (+{(lines - 1).ToString(CultureInfo.InvariantCulture)} more lines)" : "");
        var details = $"Command:\n{command}\n\nWorking directory: {cwd ?? "(home)"}\nTimeout: {timeoutS.ToString(CultureInfo.InvariantCulture)} s";
        if (await AuthorizeAsync(call, def, host, command, false, false, summary, details, ct).ConfigureAwait(false) is { } denied)
            return denied;

        return await OnHostAsync(call, host, async conn =>
        {
            var started = _time.GetTimestamp();
            var r = await conn.ExecAsync(command, new ExecOptions
            {
                Timeout = TimeSpan.FromSeconds(timeoutS),
                MaxOutputBytes = _options.MaxCommandOutputBytes,
                Cwd = string.IsNullOrEmpty(cwd) ? null : cwd,
            }, ct).ConfigureAwait(false);
            if (r.Cancelled)
                ct.ThrowIfCancellationRequested();
            call.ExitCode = r.ExitCode;
            var duration = (long)_time.GetElapsedTime(started).TotalMilliseconds;
            var result = new RunCommandResult(host.Id, r.ExitCode, r.Stdout, r.Stderr, r.StdoutTruncated, r.StderrTruncated, r.TimedOut, r.ExitSignal, duration);
            if (r.TimedOut) call.Error = $"Timed out after {timeoutS.ToString(CultureInfo.InvariantCulture)} s.";
            return Ok(FormatCommandResult(result, timeoutS, _options.MaxCommandOutputBytes), result, ToolJsonContext.Default.RunCommandResult);
        }, ct).ConfigureAwait(false);
    }

    internal static string FormatCommandResult(RunCommandResult r, int timeoutS, int cap)
    {
        var sb = new StringBuilder();
        sb.Append("exit_code: ").Append(r.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none").Append('\n');
        if (r.ExitSignal is { } sig) sb.Append("signal: ").Append(sig).Append('\n');
        if (r.TimedOut) sb.Append("timed_out: true (killed after ").Append(timeoutS.ToString(CultureInfo.InvariantCulture)).Append(" s)\n");
        if (r.StdoutTruncated || r.StderrTruncated)
        {
            sb.Append("truncated: ")
              .Append(r.StdoutTruncated && r.StderrTruncated ? "stdout and stderr" : r.StdoutTruncated ? "stdout" : "stderr")
              .Append(" reached the ").Append(cap.ToString(CultureInfo.InvariantCulture)).Append("-byte cap; the command was killed. Narrow the output (head, grep, tail -n) and run it again.\n");
        }
        sb.Append("--- stdout ---\n").Append(r.Stdout);
        if (r.Stdout.Length > 0 && r.Stdout[^1] != '\n') sb.Append('\n');
        sb.Append("--- stderr ---\n").Append(r.Stderr);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ SFTP

    private async Task<(Host? Host, ToolCallResult? Error)> HostForSftpAsync(Call call, ValidatedArguments args, CancellationToken ct)
    {
        var (host, error) = await ResolveHostAsync(args.RequiredString("host"), ct).ConfigureAwait(false);
        if (host is null)
            return (null, call.Fail(AuditDecision.Invalid, error!));
        call.HostId = host.Id;
        return (host, null);
    }

    /// <summary>Absolute normalized path; relative paths and a leading "~" / "~/" resolve against the SFTP home directory.</summary>
    internal static string RemotePath(string path, string home)
    {
        var p = path.Trim();
        if (p == "~") p = home;
        else if (p.StartsWith("~/", StringComparison.Ordinal)) p = SftpPath.Combine(home, p[2..]);
        return SftpPath.Normalize(p, home);
    }

    private static RemoteEntry Map(SftpEntry e) =>
        new(e.Name, e.FullPath, e.Kind.ToString().ToLowerInvariant(), e.Size, e.Modified, e.Permissions, e.LinkTarget);

    private async Task<ToolCallResult> SftpListAsync(Call call, ToolDefinition def, ValidatedArguments args, CancellationToken ct)
    {
        var (host, err) = await HostForSftpAsync(call, args, ct).ConfigureAwait(false);
        if (host is null) return err!;
        var path = args.RequiredString("path");
        if (await AuthorizeAsync(call, def, host, null, false, false, $"list {path}", null, ct).ConfigureAwait(false) is { } denied)
            return denied;
        return await OnHostAsync(call, host, async conn =>
        {
            var sftp = await conn.OpenSftpAsync(ct).ConfigureAwait(false);
            await using (sftp.ConfigureAwait(false))
            {
                var full = RemotePath(path, sftp.HomeDirectory);
                var entries = await sftp.ListAsync(full, ct).ConfigureAwait(false);
                var max = _options.MaxSftpListEntries;
                var shown = entries.Take(max).Select(Map).ToList();
                var result = new SftpListResult(host.Id, full, shown, entries.Count, entries.Count > max);
                var sb = new StringBuilder();
                sb.Append(full).Append(": ").Append(entries.Count.ToString(CultureInfo.InvariantCulture)).Append(" entries");
                if (result.Truncated) sb.Append(" (showing the first ").Append(max.ToString(CultureInfo.InvariantCulture)).Append(')');
                sb.Append('\n');
                foreach (var e in shown)
                {
                    sb.Append(e.Permissions).Append(' ')
                      .Append(e.Size.ToString(CultureInfo.InvariantCulture).PadLeft(10)).Append(' ')
                      .Append(e.Modified.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(' ')
                      .Append(e.Name);
                    if (e.Kind == "directory") sb.Append('/');
                    if (e.LinkTarget is { } t) sb.Append(" -> ").Append(t);
                    sb.Append('\n');
                }
                return Ok(sb.ToString(), result, ToolJsonContext.Default.SftpListResult);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task<ToolCallResult> SftpStatAsync(Call call, ToolDefinition def, ValidatedArguments args, CancellationToken ct)
    {
        var (host, err) = await HostForSftpAsync(call, args, ct).ConfigureAwait(false);
        if (host is null) return err!;
        var path = args.RequiredString("path");
        if (await AuthorizeAsync(call, def, host, null, false, false, $"stat {path}", null, ct).ConfigureAwait(false) is { } denied)
            return denied;
        return await OnHostAsync(call, host, async conn =>
        {
            var sftp = await conn.OpenSftpAsync(ct).ConfigureAwait(false);
            await using (sftp.ConfigureAwait(false))
            {
                var full = RemotePath(path, sftp.HomeDirectory);
                var entry = await sftp.StatAsync(full, ct).ConfigureAwait(false);
                var result = new SftpStatResult(host.Id, full, entry is not null, entry is null ? null : Map(entry));
                return Ok(JsonSerializer.Serialize(result, ToolJsonContext.Default.SftpStatResult), result, ToolJsonContext.Default.SftpStatResult);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task<ToolCallResult> SftpReadAsync(Call call, ToolDefinition def, ValidatedArguments args, CancellationToken ct)
    {
        var (host, err) = await HostForSftpAsync(call, args, ct).ConfigureAwait(false);
        if (host is null) return err!;
        var path = args.RequiredString("path");
        var maxBytes = (int)(args.Integer("max_bytes") ?? _options.DefaultSftpReadBytes);
        if (await AuthorizeAsync(call, def, host, null, false, false, $"read {path}", null, ct).ConfigureAwait(false) is { } denied)
            return denied;
        return await OnHostAsync(call, host, async conn =>
        {
            var sftp = await conn.OpenSftpAsync(ct).ConfigureAwait(false);
            await using (sftp.ConfigureAwait(false))
            {
                var full = RemotePath(path, sftp.HomeDirectory);
                var entry = await sftp.StatAsync(full, ct).ConfigureAwait(false);
                if (entry is null)
                    return call.ExecutionFailed($"No such file: {full}");
                if (entry.Kind == SftpEntryKind.Directory)
                    return call.ExecutionFailed($"{full} is a directory; use sftp_list to list it.");
                if (entry.Kind == SftpEntryKind.Other)
                    return call.ExecutionFailed($"{full} is not a regular file (device, socket or pipe) and cannot be read.");

                byte[] bytes;
                bool truncated;
                var stream = await sftp.OpenReadAsync(full, ct).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                    (bytes, truncated) = await ReadCappedAsync(stream, maxBytes, ct).ConfigureAwait(false);

                var text = DecodeText(bytes, truncated, ResolveEncoding(host.Encoding));
                if (text is null)
                    return call.ExecutionFailed(
                        $"{full} looks like a binary file (it contains NUL bytes or is not valid {EncodingName(host.Encoding)} text). " +
                        $"Inspect it with run_command instead, e.g. \"xxd -l 512 {full}\", \"file {full}\" or \"base64 {full}\".");
                long? size = entry.Kind == SftpEntryKind.File ? entry.Size : null;
                var result = new SftpReadResult(host.Id, full, size, bytes.Length, truncated, text);
                var body = truncated
                    ? text + $"\n[truncated: showing the first {bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes" +
                      (size is { } s ? $" of {s.ToString(CultureInfo.InvariantCulture)}" : "") + "; raise max_bytes or read a slice with run_command (sed -n, tail -c)]"
                    : text;
                return Ok(body, result, ToolJsonContext.Default.SftpReadResult);
            }
        }, ct).ConfigureAwait(false);
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadCappedAsync(Stream s, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        var truncated = total > maxBytes;
        return (buffer.AsSpan(0, Math.Min(total, maxBytes)).ToArray(), truncated);
    }

    /// <summary>Text or null when the bytes look binary (NUL bytes or invalid UTF-8 for UTF-8 hosts).</summary>
    internal static string? DecodeText(byte[] bytes, bool truncated, Encoding encoding)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.IndexOf((byte)0) >= 0)
            return null;
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            span = span[3..];
            encoding = Encoding.UTF8;
        }
        if (encoding.CodePage != Encoding.UTF8.CodePage)
            return encoding.GetString(span);
        if (System.Text.Unicode.Utf8.IsValid(span))
            return Encoding.UTF8.GetString(span);
        if (truncated)
        {
            // The cut may have split a multi-byte sequence: drop up to 3 trailing bytes.
            for (var cut = 1; cut <= 3 && cut <= span.Length; cut++)
                if (System.Text.Unicode.Utf8.IsValid(span[..^cut]))
                    return Encoding.UTF8.GetString(span[..^cut]);
        }
        return null;
    }

    /// <summary>Same resolution as the SSH layer (host encoding name, UTF-8 fallback, no BOM).</summary>
    internal static Encoding ResolveEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new UTF8Encoding(false);
        try
        {
            var e = Encoding.GetEncoding(name);
            return e.CodePage == Encoding.UTF8.CodePage ? new UTF8Encoding(false) : e;
        }
        catch (ArgumentException) { return new UTF8Encoding(false); }
    }

    private static string EncodingName(string? name) => ResolveEncoding(name).CodePage == Encoding.UTF8.CodePage ? "UTF-8" : name!;

    private async Task<ToolCallResult> SftpWriteAsync(Call call, ToolDefinition def, ValidatedArguments args, CancellationToken ct)
    {
        var (host, err) = await HostForSftpAsync(call, args, ct).ConfigureAwait(false);
        if (host is null) return err!;
        var path = args.RequiredString("path");
        var content = args.RequiredString("content");
        var mode = args.RequiredString("mode");
        var overwrite = mode == "overwrite";
        var bytes = ResolveEncoding(host.Encoding).GetBytes(content);
        if (bytes.Length > _options.MaxSftpWriteBytes)
            return call.Fail(AuditDecision.Invalid,
                $"Invalid arguments for sftp_write: content is {bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes, the maximum is {_options.MaxSftpWriteBytes.ToString(CultureInfo.InvariantCulture)}.");
        var lines = CountLines(content);
        var summary = $"write {path} ({mode}, {FormatSize(bytes.Length)}, {lines.ToString(CultureInfo.InvariantCulture)} line{(lines == 1 ? "" : "s")})";
        var details = content.Length <= 8192 ? content : content[..8192] + $"\n… ({(content.Length - 8192).ToString(CultureInfo.InvariantCulture)} more characters)";
        if (await AuthorizeAsync(call, def, host, null, overwrite, false, summary, details, ct).ConfigureAwait(false) is { } denied)
            return denied;
        return await OnHostAsync(call, host, async conn =>
        {
            var sftp = await conn.OpenSftpAsync(ct).ConfigureAwait(false);
            await using (sftp.ConfigureAwait(false))
            {
                var full = RemotePath(path, sftp.HomeDirectory);
                var existing = await sftp.StatAsync(full, ct).ConfigureAwait(false);
                if (existing is not null && !overwrite)
                    return call.ExecutionFailed($"{full} already exists; use mode \"overwrite\" to replace it.");
                if (existing is { Kind: SftpEntryKind.Directory })
                    return call.ExecutionFailed($"{full} is a directory.");
                var stream = await sftp.OpenWriteAsync(full, overwrite, ct).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
                var result = new SftpWriteResult(host.Id, full, bytes.Length, existing is null ? "created" : "overwritten");
                return Ok($"Wrote {bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes to {full} ({result.Mode}).", result, ToolJsonContext.Default.SftpWriteResult);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task<ToolCallResult> SftpMkdirAsync(Call call, ToolDefinition def, ValidatedArguments args, CancellationToken ct)
    {
        var (host, err) = await HostForSftpAsync(call, args, ct).ConfigureAwait(false);
        if (host is null) return err!;
        var path = args.RequiredString("path");
        if (await AuthorizeAsync(call, def, host, null, false, false, $"mkdir {path}", null, ct).ConfigureAwait(false) is { } denied)
            return denied;
        return await OnHostAsync(call, host, async conn =>
        {
            var sftp = await conn.OpenSftpAsync(ct).ConfigureAwait(false);
            await using (sftp.ConfigureAwait(false))
            {
                var full = RemotePath(path, sftp.HomeDirectory);
                if (await sftp.StatAsync(full, ct).ConfigureAwait(false) is not null)
                    return call.ExecutionFailed($"{full} already exists.");
                await sftp.CreateDirectoryAsync(full, ct).ConfigureAwait(false);
                var result = new SftpPathResult(host.Id, full, "created");
                return Ok($"Created directory {full}.", result, ToolJsonContext.Default.SftpPathResult);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task<ToolCallResult> SftpDeleteAsync(Call call, ToolDefinition def, ValidatedArguments args, CancellationToken ct)
    {
        var (host, err) = await HostForSftpAsync(call, args, ct).ConfigureAwait(false);
        if (host is null) return err!;
        var path = args.RequiredString("path");
        var recursive = args.Boolean("recursive") ?? false;
        var summary = $"delete {path}" + (recursive ? " (recursive: everything inside it)" : "");
        if (await AuthorizeAsync(call, def, host, null, false, false, summary, null, ct).ConfigureAwait(false) is { } denied)
            return denied;
        return await OnHostAsync(call, host, async conn =>
        {
            var sftp = await conn.OpenSftpAsync(ct).ConfigureAwait(false);
            await using (sftp.ConfigureAwait(false))
            {
                var full = RemotePath(path, sftp.HomeDirectory);
                if (full == "/" || full.TrimEnd('/') == sftp.HomeDirectory.TrimEnd('/'))
                    return call.ExecutionFailed($"Refusing to delete {full} (the root or home directory).");
                var entry = await sftp.StatAsync(full, ct).ConfigureAwait(false);
                if (entry is null)
                    return call.ExecutionFailed($"No such file or directory: {full}");
                try
                {
                    await sftp.DeleteAsync(entry, recursive, ct).ConfigureAwait(false);
                }
                catch (IOException e) when (entry.Kind == SftpEntryKind.Directory && !recursive)
                {
                    return call.ExecutionFailed($"Could not delete the directory {full} ({e.Message}). If it is not empty, pass recursive=true.");
                }
                var result = new SftpPathResult(host.Id, full, "deleted");
                return Ok($"Deleted {entry.Kind.ToString().ToLowerInvariant()} {full}.", result, ToolJsonContext.Default.SftpPathResult);
            }
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ terminal sessions

    private async Task<Dictionary<Guid, Host>> VisibleHostsAsync(CancellationToken ct) =>
        (await _hosts.ListAsync(ct).ConfigureAwait(false)).Where(Visible).ToDictionary(h => h.Id);

    private async Task<(TerminalSessionInfo? Session, Host? Host, ToolCallResult? Error)> ResolveSessionAsync(Call call, string id, CancellationToken ct)
    {
        var info = _terminals.List().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        var host = info is null ? null : await _hosts.GetAsync(info.HostId, ct).ConfigureAwait(false);
        if (info is null || host is null || !Visible(host))
            return (null, null, call.Fail(AuditDecision.Invalid, $"Unknown terminal session '{Clip(id, 100)}'. Call list_sessions to see the open sessions you can use."));
        call.HostId = host.Id;
        return (info, host, null);
    }

    private async Task<ToolCallResult> ListSessionsAsync(Call call, CancellationToken ct)
    {
        var hosts = await VisibleHostsAsync(ct).ConfigureAwait(false);
        var sessions = _terminals.List()
            .Where(s => hosts.ContainsKey(s.HostId))
            .Select(s => new SessionInfo(s.Id, s.HostId, hosts[s.HostId].Label, s.Title, s.IsConnected, s.Columns, s.Rows))
            .ToList();
        call.Decision = AuditDecision.Allowed;
        var result = new ListSessionsResult(sessions);
        var text = sessions.Count == 0
            ? "No terminal sessions are open on hosts available to AI tools."
            : JsonSerializer.Serialize(result, ToolJsonContext.Default.ListSessionsResult);
        return Ok(text, result, ToolJsonContext.Default.ListSessionsResult);
    }

    private async Task<ToolCallResult> ReadTerminalAsync(Call call, ToolDefinition def, ValidatedArguments args, CancellationToken ct)
    {
        var sessionId = args.RequiredString("session");
        var (session, host, err) = await ResolveSessionAsync(call, sessionId, ct).ConfigureAwait(false);
        if (session is null) return err!;
        var lines = (int)(args.Integer("lines") ?? 100);
        if (await AuthorizeAsync(call, def, host!, null, false, false, $"read terminal \"{session.Title}\"", null, ct).ConfigureAwait(false) is { } denied)
            return denied;
        var text = await _terminals.ReadAsync(session.Id, lines, ct).ConfigureAwait(false);
        if (text is null)
            return call.ExecutionFailed($"The terminal session '{Clip(sessionId, 100)}' was closed.");
        var truncated = text.Length > MaxTerminalChars;
        if (truncated) text = text[^MaxTerminalChars..];
        var result = new ReadTerminalResult(session.Id, host!.Id, lines, truncated, text);
        return Ok(text, result, ToolJsonContext.Default.ReadTerminalResult);
    }

    /// <summary>Control characters other than tab and line breaks (ESC sequences, Ctrl-C, ...) are never typed.</summary>
    internal static string? FindForbiddenTerminalCharacter(string text)
    {
        foreach (var c in text)
            if (char.IsControl(c) && c is not ('\t' or '\n' or '\r'))
                return $"U+{((int)c).ToString("X4", CultureInfo.InvariantCulture)}";
        return null;
    }

    private async Task<ToolCallResult> SendToTerminalAsync(Call call, ToolDefinition def, ValidatedArguments args, CancellationToken ct)
    {
        var sessionId = args.RequiredString("session");
        var text = args.RequiredString("text");
        var execute = args.Boolean("execute") ?? false;
        if (FindForbiddenTerminalCharacter(text) is { } bad)
            return call.Fail(AuditDecision.Invalid,
                $"Invalid arguments for send_to_terminal: text contains the control character {bad}. Only printable text, tabs and line breaks can be sent (no escape sequences or Ctrl keys).");
        var (session, host, err) = await ResolveSessionAsync(call, sessionId, ct).ConfigureAwait(false);
        if (session is null) return err!;

        // A line break runs the line in a shell that is not in bracketed-paste mode: treat it like execute=true.
        var hasLineBreak = text.AsSpan().IndexOfAny('\n', '\r') >= 0;
        var runs = execute || hasLineBreak;
        var lineCount = CountLines(text);
        var summary = $"type into terminal \"{Clip(session.Title, 60)}\": {FirstLine(text, 160)}" +
                      (lineCount > 1 ? $" (contains {lineCount.ToString(CultureInfo.InvariantCulture)} lines)" : "") +
                      (execute ? " and press Enter" : hasLineBreak ? " (line breaks run each line)" : " (without pressing Enter)");
        if (await AuthorizeAsync(call, def, host!, text, false, runs, summary, text, ct).ConfigureAwait(false) is { } denied)
            return denied;
        if (!await _terminals.SendAsync(session.Id, text, execute, ct).ConfigureAwait(false))
            return call.ExecutionFailed($"The terminal session '{Clip(sessionId, 100)}' is closed or not connected.");
        var result = new SendToTerminalResult(session.Id, host!.Id, text.Length, lineCount, execute);
        return Ok(execute
            ? "The text was typed into the terminal and Enter was pressed. Use read_terminal to see the output."
            : hasLineBreak
                ? "The text was typed into the terminal (its line breaks may have run lines). Use read_terminal to see the result."
                : "The text was typed on the prompt line; Enter was NOT pressed, the user decides whether to run it.",
            result, ToolJsonContext.Default.SendToTerminalResult);
    }

    // ------------------------------------------------------------------ helpers

    private static ToolCallResult Ok<T>(string text, T value, JsonTypeInfo<T> typeInfo) =>
        new(false, text, JsonSerializer.SerializeToElement(value, typeInfo));

    internal static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var n = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (text[i] is '\n' or '\r' && i + 1 < text.Length) n++;
        }
        return n;
    }

    private static string FirstLine(string text, int max)
    {
        var end = text.AsSpan().IndexOfAny('\n', '\r');
        var line = end < 0 ? text : text[..end];
        return Clip(line, max);
    }

    private static string FormatSize(int bytes) => bytes < 1024
        ? bytes.ToString(CultureInfo.InvariantCulture) + " B"
        : (bytes / 1024.0).ToString(bytes < 10 * 1024 ? "0.0" : "0", CultureInfo.InvariantCulture) + " KB";

    internal static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Arguments for the audit log: sftp_write content and send_to_terminal text (any "content"/"text" string) are
    /// replaced by "&lt;N chars&gt;"; other long strings are clipped. Non-object arguments are recorded as their kind.
    /// </summary>
    internal static string RedactArguments(JsonElement args)
    {
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return "{}";
        using var buffer = new MemoryStream();
        // Relaxed escaping keeps "<12 chars>" and CJK paths readable; the audit log is never embedded in HTML.
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            if (args.ValueKind != JsonValueKind.Object)
            {
                w.WriteStartObject();
                w.WriteString("<invalid>", args.ValueKind.ToString());
                w.WriteEndObject();
            }
            else
            {
                w.WriteStartObject();
                foreach (var p in args.EnumerateObject())
                {
                    if (p.Name is "content" or "text" && p.Value.ValueKind == JsonValueKind.String)
                    {
                        w.WriteString(p.Name, $"<{p.Value.GetString()!.Length.ToString(CultureInfo.InvariantCulture)} chars>");
                        continue;
                    }
                    switch (p.Value.ValueKind)
                    {
                        case JsonValueKind.String:
                            w.WriteString(Clip(p.Name, 128), Clip(p.Value.GetString()!, AuditStringLimit));
                            break;
                        case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null:
                            w.WritePropertyName(Clip(p.Name, 128));
                            p.Value.WriteTo(w);
                            break;
                        default:
                            w.WriteString(Clip(p.Name, 128), $"<{p.Value.ValueKind.ToString().ToLowerInvariant()}>");
                            break;
                    }
                }
                w.WriteEndObject();
            }
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Unexpected failure in tool {Tool}")]
    private static partial void LogUnexpected(ILogger logger, Exception exception, string tool);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Could not write the audit entry for tool {Tool}")]
    private static partial void LogAuditFailed(ILogger logger, Exception exception, string tool);
}
