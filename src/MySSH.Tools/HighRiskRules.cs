using System.Text.RegularExpressions;

namespace MySSH.Tools;

/// <param name="Rule">Short rule name shown to the user, e.g. "rm -r".</param>
/// <param name="Description">Why it is risky.</param>
public sealed record HighRiskMatch(string Rule, string Description);

/// <summary>
/// High-risk command heuristics (design §6.3). A safety NET that makes Trusted hosts ask before obviously dangerous
/// commands, NOT a security boundary: shells are too expressive to be analysed reliably (aliases, functions, scripts,
/// encoded payloads...). Use a low-privilege <c>Host.AiIdentityId</c> for real protection.
/// <para>Matching is token based on every simple command of the line (after <c>;</c>, <c>&amp;&amp;</c>, pipes, inside
/// <c>$( )</c>, backticks, <c>sh -c</c>, <c>ssh host …</c>, <c>xargs</c>, <c>sudo</c>, <c>env</c>, …), so "format" never
/// matches "rm" and quoted text passed to <c>echo</c> is not a command.</para>
/// </summary>
public static partial class HighRiskRules
{
    /// <summary>Rule names, for documentation and the UI.</summary>
    public static IReadOnlyList<string> RuleNames { get; } =
    [
        "rm -r", "mkfs", "dd of=", "disk wipe/partition (shred, wipefs, fdisk, sfdisk, parted, blkdiscard)",
        "shutdown/reboot/halt/poweroff", "init 0/6", "systemctl stop/restart/disable/mask/kill/isolate/reboot/poweroff",
        "service stop/restart", "kill/killall/pkill", "chmod/chown/chgrp -R", "> /dev/<disk>", "fork bomb",
        "firewall flush (iptables -F/-X/-P, nft flush/delete, ufw disable/reset)",
        "account changes (userdel, usermod, passwd, chpasswd, groupdel)", "crontab -r", "curl/wget | sh",
        "DROP DATABASE/TABLE, TRUNCATE TABLE (via a database client)", "truncate", "find -delete / -exec rm",
        "git push --force", "docker/kubectl destructive (prune, rm -f, kubectl delete)", "volume destroy (zfs/zpool destroy, lvremove, vgremove, pvremove)",
        "sudo/su/doas (privilege escalation)",
    ];

    private const int MaxDepth = 8;

    /// <summary>First (most specific) match, or null.</summary>
    public static HighRiskMatch? Match(string command)
    {
        var all = MatchAll(command);
        return all.Count == 0 ? null : all[0];
    }

    /// <summary>All matches, specific rules first and "sudo" last.</summary>
    public static IReadOnlyList<HighRiskMatch> MatchAll(string command)
    {
        var found = new List<HighRiskMatch>();
        if (string.IsNullOrWhiteSpace(command))
            return found;
        Analyze(command, found, 0);
        // Dedupe by rule, sudo last.
        return [.. found.DistinctBy(m => m.Rule).OrderBy(m => m.Rule == Sudo.Rule ? 1 : 0)];
    }

    private static readonly HighRiskMatch Sudo = new("sudo", "runs with elevated privileges");

    private static void Analyze(string command, List<HighRiskMatch> found, int depth)
    {
        if (depth > MaxDepth)
            return;

        // Text-level rules (whitespace-insensitive / redirections the lexer drops).
        if (ForkBombRegex().IsMatch(WhitespaceRegex().Replace(command, "")))
            found.Add(new("fork bomb", "spawns processes until the machine stops responding"));
        if (BlockDeviceRedirectRegex().IsMatch(command))
            found.Add(new("> /dev/<disk>", "overwrites a raw disk device"));

        var segments = ShellLexer.Split(command);
        var hasDbClient = false;
        var hasShell = false;
        var nestedDownload = false;
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var (cmd, args) = Unwrap(seg.Words, found, depth);
            if (cmd is null)
                continue;
            if (cmd.StartsWith("mkfs.", StringComparison.Ordinal) || cmd.StartsWith("mke2fs", StringComparison.Ordinal))
                cmd = "mkfs";
            if (s_dbClients.Contains(cmd)) hasDbClient = true;
            if (s_shells.Contains(cmd) || cmd is "eval" or "source" or ".") hasShell = true;
            if (cmd is "curl" or "wget" && seg.Nested) nestedDownload = true;

            // curl … | sh
            if (seg.PipedFrom && s_shells.Contains(cmd) && i > 0 && IsDownloader(segments[i - 1].Words))
                found.Add(new("curl/wget | sh", "runs a script downloaded from the network"));

            CheckCommand(cmd, args, found, depth);
        }
        if (nestedDownload && hasShell)
            found.Add(new("curl/wget | sh", "runs a script downloaded from the network"));
        if (hasDbClient && SqlDropRegex().Match(command) is { Success: true } m)
            found.Add(new(m.Value.StartsWith("t", StringComparison.OrdinalIgnoreCase) ? "TRUNCATE TABLE" : "DROP DATABASE/TABLE", "irreversibly deletes database data"));
    }

    private static bool IsDownloader(IReadOnlyList<string> words)
    {
        var (cmd, _) = Unwrap(words, null, 0);
        return cmd is "curl" or "wget" or "fetch";
    }

    private static readonly HashSet<string> s_shells = new(StringComparer.Ordinal) { "sh", "bash", "zsh", "dash", "ksh", "mksh", "fish", "ash", "busybox", "python", "python3", "perl", "ruby", "node" };
    private static readonly HashSet<string> s_dbClients = new(StringComparer.Ordinal) { "mysql", "mariadb", "psql", "sqlite3", "sqlcmd", "clickhouse-client", "clickhouse", "cqlsh", "duckdb" };

    /// <summary>Strips assignments and wrappers (sudo, env, nohup, xargs, …); recurses into <c>sh -c</c>, <c>ssh host cmd</c>, <c>eval</c>.</summary>
    private static (string? Command, List<string> Args) Unwrap(IReadOnlyList<string> words, List<HighRiskMatch>? found, int depth)
    {
        var i = 0;
        while (i < words.Count)
        {
            var w = words[i];
            if (AssignmentRegex().IsMatch(w) || w is "!" or "if" or "then" or "else" or "elif" or "do" or "while" or "until" or "time" or "{" or "}")
            {
                i++;
                continue;
            }
            var name = ShellLexer.CommandName(w);
            switch (name)
            {
                case "sudo" or "doas" or "pkexec" or "run0":
                    found?.Add(Sudo);
                    i = SkipOptions(words, i + 1, "ugUCDhprtTRg");
                    continue;
                case "su":
                    found?.Add(Sudo);
                    for (var j = i + 1; j < words.Count - 1; j++)
                    {
                        if (words[j] is "-c" or "--command" && found is not null)
                            Analyze(words[j + 1], found, depth + 1);
                    }
                    return (null, []);
                case "env":
                    i++;
                    while (i < words.Count && (words[i].StartsWith('-') || AssignmentRegex().IsMatch(words[i])))
                    {
                        if (words[i] is "-u" or "--unset" or "-C" or "--chdir" or "-S" or "--split-string") i++;
                        i++;
                    }
                    continue;
                case "nohup" or "exec" or "command" or "builtin" or "setsid" or "unbuffer" or "nocache" or "eatmydata" or "caffeinate":
                    i = SkipOptions(words, i + 1, "");
                    continue;
                case "nice":
                    i = SkipOptions(words, i + 1, "n");
                    continue;
                case "ionice":
                    i = SkipOptions(words, i + 1, "cnp");
                    continue;
                case "stdbuf":
                    i = SkipOptions(words, i + 1, "ioe");
                    continue;
                case "chrt" or "taskset":
                    i = SkipOptions(words, i + 1, "");
                    if (i < words.Count) i++; // priority / mask
                    continue;
                case "timeout":
                    i = SkipOptions(words, i + 1, "sk");
                    if (i < words.Count) i++; // duration
                    continue;
                case "chroot":
                    i = SkipOptions(words, i + 1, "");
                    if (i < words.Count) i++; // new root
                    continue;
                case "xargs":
                    i = SkipOptions(words, i + 1, "IinLPdaEs");
                    continue;
                case "watch":
                    i = SkipOptions(words, i + 1, "nqd");
                    if (found is not null && i < words.Count)
                        Analyze(string.Join(' ', words.Skip(i)), found, depth + 1);
                    return (null, []);
                case "eval":
                    if (found is not null)
                        Analyze(string.Join(' ', words.Skip(i + 1)), found, depth + 1);
                    return ("eval", [.. words.Skip(i + 1)]);
                case "ssh":
                {
                    var j = SkipOptions(words, i + 1, "bcDEeFIiJLlmOopQRSWw");
                    if (found is not null && j + 1 < words.Count)
                        Analyze(string.Join(' ', words.Skip(j + 1)), found, depth + 1);
                    return ("ssh", [.. words.Skip(i + 1)]);
                }
                default:
                    if (s_shells.Contains(name))
                    {
                        // sh -c 'cmd', bash -lc 'cmd'
                        for (var j = i + 1; j < words.Count; j++)
                        {
                            var a = words[j];
                            if (!a.StartsWith('-')) break;
                            if (ShellLexer.ShortClusterHasAny(a, "c") && j + 1 < words.Count)
                            {
                                if (found is not null) Analyze(words[j + 1], found, depth + 1);
                                break;
                            }
                        }
                    }
                    return (name, [.. words.Skip(i + 1)]);
            }
        }
        return (null, []);
    }

    /// <summary>Skips "-x", "--long[=v]" options; letters in <paramref name="withValue"/> consume the next word ("-u root").</summary>
    private static int SkipOptions(IReadOnlyList<string> words, int i, string withValue)
    {
        while (i < words.Count)
        {
            var w = words[i];
            if (w == "--") return i + 1;
            if (!w.StartsWith('-') || w == "-") return i;
            if (ShellLexer.IsShortCluster(w) && w.Length == 2 && withValue.Contains(w[1], StringComparison.Ordinal))
                i++;
            i++;
        }
        return i;
    }

    private static void CheckCommand(string cmd, List<string> args, List<HighRiskMatch> found, int depth)
    {
        bool Any(Func<string, bool> p) => args.Any(p);
        string? FirstVerb() => args.FirstOrDefault(a => !a.StartsWith('-'));

        switch (cmd)
        {
            case "rm":
                if (Any(a => a is "--recursive" or "--no-preserve-root" || ShellLexer.ShortClusterHasAny(a, "rR")))
                    found.Add(new("rm -r", "recursively deletes files"));
                break;
            case "mkfs" or "mke2fs" or "mkswap" or "newfs":
                found.Add(new("mkfs", "formats a filesystem"));
                break;
            case "dd":
                if (Any(a => a.StartsWith("of=", StringComparison.Ordinal)))
                    found.Add(new("dd of=", "writes raw data to a file or device"));
                break;
            case "shred" or "wipefs" or "fdisk" or "sfdisk" or "cfdisk" or "gdisk" or "sgdisk" or "parted" or "blkdiscard":
                found.Add(new(cmd, "wipes or repartitions a disk"));
                break;
            case "shutdown" or "reboot" or "halt" or "poweroff":
                found.Add(new(cmd, "stops or restarts the machine"));
                break;
            case "init" or "telinit":
                if (Any(a => a is "0" or "6"))
                    found.Add(new("init 0/6", "stops or restarts the machine"));
                break;
            case "systemctl":
                if (FirstVerb() is { } verb && s_systemctlVerbs.Contains(verb))
                    found.Add(new("systemctl " + verb, "stops, restarts or disables services or the machine"));
                break;
            case "service":
                if (args.Count >= 2 && args[1] is "stop" or "restart" or "force-reload")
                    found.Add(new("service " + args[1], "stops or restarts a service"));
                break;
            case "kill" or "killall" or "pkill" or "skill" or "xkill" or "killall5":
                found.Add(new(cmd + (Any(a => a is "-9" or "-KILL" or "-SIGKILL" or "--signal=KILL" or "--signal=9") ? " -9" : ""), "terminates processes"));
                break;
            case "chmod" or "chown" or "chgrp" or "setfacl":
                if (Any(a => a is "--recursive" || ShellLexer.ShortClusterHasAny(a, "R")))
                    found.Add(new(cmd + " -R", "recursively changes permissions or ownership"));
                break;
            case "iptables" or "ip6tables" or "iptables-legacy" or "iptables-nft" or "ip6tables-legacy" or "ip6tables-nft" or "ebtables" or "arptables":
                if (Any(a => a is "-F" or "--flush" or "-X" or "--delete-chain" or "-P" or "--policy"))
                    found.Add(new("iptables flush", "flushes firewall rules or changes the default policy"));
                break;
            case "nft":
                if (Any(a => a is "flush" or "delete"))
                    found.Add(new("nft flush", "flushes or deletes firewall rules"));
                break;
            case "ufw":
                if (Any(a => a is "disable" or "reset"))
                    found.Add(new("ufw disable/reset", "disables the firewall"));
                break;
            case "userdel" or "deluser" or "groupdel" or "delgroup" or "usermod" or "passwd" or "chpasswd" or "chage" or "vipw":
                found.Add(new(cmd, "changes user accounts or passwords"));
                break;
            case "crontab":
                if (Any(a => ShellLexer.ShortClusterHasAny(a, "r")))
                    found.Add(new("crontab -r", "deletes the crontab"));
                break;
            case "truncate":
                found.Add(new("truncate", "shrinks or empties files"));
                break;
            case "find":
                if (Any(a => a is "-delete"))
                    found.Add(new("find -delete", "deletes every matching file"));
                for (var j = 0; j < args.Count; j++)
                {
                    if (args[j] is not ("-exec" or "-execdir" or "-ok" or "-okdir")) continue;
                    var end = j + 1;
                    while (end < args.Count && args[end] is not (";" or "+")) end++;
                    var inner = args.Skip(j + 1).Take(end - j - 1).ToList();
                    if (inner.Count > 0)
                    {
                        var before = found.Count;
                        CheckCommand(ShellLexer.CommandName(inner[0]), inner.Skip(1).ToList(), found, depth);
                        if (inner[0] is "rm" or "shred" or "unlink" && found.Count == before)
                            found.Add(new("find -exec rm", "deletes every matching file"));
                    }
                    j = end;
                }
                break;
            case "git":
                if (args.Contains("push") && Any(a => a is "-f" or "--force" or "--mirror" || a.StartsWith("--force-with-lease", StringComparison.Ordinal) || (a.StartsWith('+') && a.Length > 1)))
                    found.Add(new("git push --force", "rewrites remote history"));
                break;
            case "docker" or "podman":
                if ((args.Contains("prune") && Any(a => a is "system" or "volume" or "image" or "container" or "network" or "builder")) ||
                    (FirstVerb() is "rm" or "rmi" && Any(a => a is "-f" or "--force" || ShellLexer.ShortClusterHasAny(a, "f"))) ||
                    (args.Contains("volume") && args.Contains("rm")))
                    found.Add(new(cmd + " prune/rm -f", "removes containers, images or volumes"));
                break;
            case "kubectl" or "oc":
                if (FirstVerb() is "delete" or "drain")
                    found.Add(new("kubectl " + FirstVerb(), "deletes cluster resources"));
                break;
            case "zfs" or "zpool":
                if (args.Contains("destroy"))
                    found.Add(new(cmd + " destroy", "destroys a pool, dataset or snapshot"));
                break;
            case "lvremove" or "vgremove" or "pvremove":
                found.Add(new(cmd, "removes an LVM volume"));
                break;
        }
    }

    private static readonly HashSet<string> s_systemctlVerbs = new(StringComparer.Ordinal)
    {
        "stop", "restart", "try-restart", "reload-or-restart", "try-reload-or-restart", "disable", "mask", "kill", "isolate",
        "reboot", "poweroff", "halt", "kexec", "rescue", "emergency", "suspend", "hibernate", "hybrid-sleep", "soft-reboot",
    };

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\+?=", RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    // :(){ :|:& };:  and named variants  f(){ f|f& };f
    [GeneratedRegex(@"(?<f>[^\s(){};|&]+)\(\)\{\k<f>\|\k<f>&\};?\k<f>", RegexOptions.CultureInvariant)]
    private static partial Regex ForkBombRegex();

    [GeneratedRegex(@">\s*/dev/(sd[a-z]|hd[a-z]|vd[a-z]|xvd[a-z]|nvme\d|mmcblk\d|disk\d|rdisk\d|md\d|dm-\d|mapper/)", RegexOptions.CultureInvariant)]
    private static partial Regex BlockDeviceRedirectRegex();

    [GeneratedRegex(@"\b(drop\s+(database|schema|table)|truncate\s+table)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SqlDropRegex();
}
