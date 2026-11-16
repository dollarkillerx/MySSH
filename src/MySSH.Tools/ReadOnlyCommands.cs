using System.Globalization;

namespace MySSH.Tools;

/// <summary>Result of checking a run_command line against a ReadOnly host's allowlist.</summary>
/// <param name="Reason">Why it was refused (null when allowed).</param>
public sealed record ReadOnlyCheck(bool Allowed, string? Reason)
{
    public static readonly ReadOnlyCheck Ok = new(true, null);
}

/// <summary>
/// Commands an AI may run on a host with <see cref="Core.AiPolicy.ReadOnly"/> (design §6.3).
/// <para>A command is allowed when ALL of these hold:</para>
/// <list type="number">
/// <item>it contains no shell metacharacters that could chain, redirect, substitute or expand (outside single quotes:
/// <c>; &amp; | &gt; &lt; ( ) ` $ \</c>; inside double quotes: <c>` $ \</c>; anywhere: newline and other control characters);</item>
/// <item>its words start with an allowlist entry, compared word by word (<c>"systemctl status"</c> allows
/// <c>systemctl status nginx</c> but not <c>systemctl stop nginx</c> or <c>systemctl status-x</c>). The host's
/// <see cref="Core.Host.AiCommandAllowlist"/> replaces <see cref="BuiltInAllowlist"/> when it is not empty;</item>
/// <item>no argument guard rejects it (never follow forever: <c>tail -f</c>, <c>journalctl -f</c>, <c>docker logs -f</c>;
/// never mutate: <c>find -delete/-exec</c>, <c>date -s</c>, <c>hostname NAME</c>, <c>ss -K</c>, <c>ip addr add</c>, …). Guards
/// apply to custom allowlists too;</item>
/// <item>no <see cref="HighRiskRules"/> rule matches.</item>
/// </list>
/// </summary>
public static class ReadOnlyCommandPolicy
{
    private sealed record Entry(string Text, string[] Words, bool Exact);

    private static Entry E(string text, bool exact = false) => new(text, text.Split(' ', StringSplitOptions.RemoveEmptyEntries), exact);

    private static readonly Entry[] s_builtIn =
    [
        // system / identity
        E("uptime"), E("uname"), E("hostname"), E("hostnamectl status"), E("whoami"), E("id"), E("groups"), E("date"),
        E("timedatectl status"), E("w"), E("who"), E("last"), E("nproc"), E("lscpu"), E("arch"), E("pwd"),
        // disks / memory
        E("df"), E("du"), E("free"), E("lsblk"), E("findmnt"), E("mount", exact: true),
        // processes
        E("ps"), E("pgrep"), E("top -b -n 1"),
        // files (read only)
        E("ls"), E("stat"), E("file"), E("wc"), E("head"), E("tail"), E("cat"), E("grep"), E("find"), E("readlink"),
        E("realpath"), E("md5sum"), E("sha256sum"),
        // services / logs
        E("systemctl status"), E("systemctl list-units"), E("systemctl list-unit-files"), E("systemctl list-timers"),
        E("systemctl is-active"), E("systemctl is-enabled"), E("systemctl is-failed"), E("systemctl show"),
        E("journalctl"), E("dmesg"),
        // containers
        E("docker ps"), E("docker logs"), E("docker images"), E("docker inspect"), E("docker version"), E("docker info"),
        // network
        E("ss"), E("netstat"), E("ip addr"), E("ip address"), E("ip route"), E("ip link"), E("ip -br addr"), E("ip -br link"),
    ];

    /// <summary>The built-in defaults used when a host has no custom allowlist (for display in the host editor).</summary>
    public static IReadOnlyList<string> BuiltInAllowlist { get; } = [.. s_builtIn.Select(e => e.Exact ? e.Text + " (no arguments)" : e.Text)];

    /// <summary>Checks <paramref name="command"/> for a ReadOnly host. <paramref name="hostAllowlist"/> empty = built-in defaults.</summary>
    public static ReadOnlyCheck Check(string command, IReadOnlyList<string>? hostAllowlist)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new(false, "The command is empty.");
        if (FindForbiddenCharacter(command) is { } bad)
            return new(false, $"The command contains {bad}, which is not allowed on a ReadOnly host (no command chaining, pipes, redirection, substitution or variable expansion). Run one plain command at a time.");

        var segments = ShellLexer.Split(command);
        if (segments.Count != 1)
            return new(false, "Only a single plain command is allowed on a ReadOnly host.");
        var words = segments[0].Words;

        var custom = hostAllowlist is { Count: > 0 };
        var entries = custom
            ? hostAllowlist!.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => new Entry(e.Trim(), [.. ShellLexer.Split(e).SelectMany(s => s.Words)], false)).ToArray()
            : s_builtIn;
        if (!entries.Any(e => Matches(e, words)))
        {
            var list = custom
                ? $"this host's allowlist ({string.Join(", ", entries.Select(e => "\"" + e.Text + "\""))})"
                : $"the built-in read-only allowlist ({string.Join(", ", BuiltInAllowlist)})";
            return new(false, $"\"{Shorten(words[0])}\" with these arguments is not on {list}. The host's AI policy is ReadOnly; ask the user to add the command to the host's allowlist or to switch the policy to Ask.");
        }

        if (Guard(words) is { } guard)
            return new(false, guard + " This is never allowed on a ReadOnly host.");

        if (HighRiskRules.Match(command) is { } risk)
            return new(false, $"The command matches the high-risk rule \"{risk.Rule}\" ({risk.Description}); ReadOnly hosts never run high-risk commands.");

        return ReadOnlyCheck.Ok;
    }

    /// <summary>Describes the first forbidden character (null when the line is a plain command).</summary>
    public static string? FindForbiddenCharacter(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var quote = '\0';
        foreach (var c in command)
        {
            if (c == '\n' || c == '\r')
                return "a line break";
            if (char.IsControl(c) && c != '\t')
                return $"the control character U+{((int)c).ToString("X4", CultureInfo.InvariantCulture)}";
            switch (quote)
            {
                case '\'':
                    if (c == '\'') quote = '\0';
                    break;
                case '"':
                    if (c == '"') quote = '\0';
                    else if (c is '`' or '$' or '\\') return $"'{c}' inside double quotes";
                    break;
                default:
                    if (c is '\'' or '"') quote = c;
                    else if (c is ';' or '&' or '|' or '>' or '<' or '(' or ')' or '`' or '$' or '\\') return $"'{c}'";
                    break;
            }
        }
        return quote != '\0' ? "an unterminated quote" : null;
    }

    private static bool Matches(Entry e, IReadOnlyList<string> words)
    {
        if (e.Words.Length == 0 || words.Count < e.Words.Length)
            return false;
        for (var i = 0; i < e.Words.Length; i++)
            if (!string.Equals(e.Words[i], words[i], StringComparison.Ordinal))
                return false;
        return !e.Exact || words.Count == e.Words.Length;
    }

    /// <summary>Argument guards per command: follow-forever and mutating options of otherwise read-only commands.</summary>
    private static string? Guard(IReadOnlyList<string> words)
    {
        var cmd = words[0];
        var args = words.Skip(1).ToList();
        bool Has(Func<string, bool> p) => args.Any(p);
        bool Cluster(string a, string letters) => ShellLexer.ShortClusterHasAny(a, letters);

        switch (cmd)
        {
            case "tail":
                if (Has(a => Cluster(a, "fF") || a.StartsWith("--follow", StringComparison.Ordinal) || a is "--retry"))
                    return "\"tail -f\" follows the file forever.";
                break;
            case "journalctl":
                if (Has(a => Cluster(a, "f") || a is "--follow"))
                    return "\"journalctl -f\" follows the journal forever.";
                if (Has(a => a.StartsWith("--vacuum", StringComparison.Ordinal) || a is "--rotate" or "--flush" or "--sync" or "--relinquish-var" or "--smart-relinquish-var" or "--setup-keys" or "--update-catalog"))
                    return "That journalctl option modifies the journal.";
                break;
            case "docker" when args.Count > 0 && args[0] == "logs":
                if (Has(a => Cluster(a, "f") || a is "--follow"))
                    return "\"docker logs -f\" follows the log forever.";
                break;
            case "find":
                if (Has(a => a is "-delete" or "-exec" or "-execdir" or "-ok" or "-okdir" or "-fprint" or "-fprint0" or "-fprintf" or "-fls"))
                    return "find with -delete, -exec, -ok or -fprint can modify files or run other commands.";
                break;
            case "date":
                for (var i = 0; i < args.Count; i++)
                {
                    var a = args[i];
                    if (a is "-d" or "--date" or "-r" or "--reference") { i++; continue; }
                    if (a.StartsWith("--set", StringComparison.Ordinal) || (Cluster(a, "s") && !a.StartsWith("-I", StringComparison.Ordinal)) || (!a.StartsWith('-') && !a.StartsWith('+')))
                        return "That date invocation sets the system clock.";
                }
                break;
            case "hostname":
                if (Has(a => !a.StartsWith('-') || a is "-F" or "--file" or "-b" or "--boot"))
                    return "That hostname invocation changes the host name.";
                break;
            case "ss":
                if (Has(a => Cluster(a, "KE") || a is "--kill" or "--events"))
                    return "\"ss -K\" kills sockets and \"ss -E\" runs forever.";
                break;
            case "netstat":
                if (Has(a => Cluster(a, "c") || a is "--continuous"))
                    return "\"netstat -c\" runs forever.";
                break;
            case "free":
                if (Has(a => Cluster(a, "sc") || a.StartsWith("--seconds", StringComparison.Ordinal) || a.StartsWith("--count", StringComparison.Ordinal)))
                    return "\"free -s\" repeats forever.";
                break;
            case "dmesg":
                if (Has(a => Cluster(a, "cCwWnDE") || a is "--clear" or "--read-clear" or "--follow" or "--follow-new" or "--console-off" or "--console-on" || a.StartsWith("--console-level", StringComparison.Ordinal)))
                    return "That dmesg option clears the buffer, changes the console level or follows forever.";
                break;
            case "top":
                if (!args.Contains("-b") || !args.Contains("-n"))
                    return "top must run in batch mode with an iteration count (top -b -n 1).";
                break;
            case "ip":
                if (Has(a => s_ipMutatingWords.Contains(a)))
                    return "That ip invocation changes the network configuration or runs forever.";
                break;
            case "systemctl" or "timedatectl" or "hostnamectl":
                // Verbs are restricted by the allowlist prefixes; nothing else to guard.
                break;
        }
        return null;
    }

    private static readonly HashSet<string> s_ipMutatingWords = new(StringComparer.Ordinal)
    {
        "add", "del", "delete", "change", "replace", "flush", "set", "append", "prepend", "save", "restore", "exec",
        "monitor", "-batch", "-b", "-force", "netns", "up", "down",
    };

    private static string Shorten(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
