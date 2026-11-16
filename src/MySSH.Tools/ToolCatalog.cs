using System.Globalization;

namespace MySSH.Tools;

/// <summary>Tool names (design §6.2). Stable: MCP clients and AI conversations refer to them.</summary>
public static class ToolNames
{
    public const string ListHosts = "list_hosts";
    public const string RunCommand = "run_command";
    public const string SftpList = "sftp_list";
    public const string SftpStat = "sftp_stat";
    public const string SftpRead = "sftp_read";
    public const string SftpWrite = "sftp_write";
    public const string SftpMkdir = "sftp_mkdir";
    public const string SftpDelete = "sftp_delete";
    public const string ListSessions = "list_sessions";
    public const string ReadTerminal = "read_terminal";
    public const string SendToTerminal = "send_to_terminal";
}

/// <summary>
/// The tool catalog (design §6.2): every tool defined once with its JSON Schema (<c>additionalProperties:false</c>,
/// explicit <c>required</c>) and annotations. Limits in the schemas come from <see cref="ToolOptions"/>.
/// </summary>
public sealed class ToolCatalog : IToolCatalog
{
    private readonly Dictionary<string, (ToolDefinition Definition, ObjectSchema Schema)> _byName;

    public ToolCatalog(ToolOptions? options = null)
    {
        Options = options ?? new ToolOptions();
        var o = Options;
        var maxTimeout = Math.Max(1, (long)o.MaxCommandTimeout.TotalSeconds);
        var defTimeout = Math.Clamp((long)o.DefaultCommandTimeout.TotalSeconds, 1, maxTimeout);
        var defRead = Math.Clamp(o.DefaultSftpReadBytes, 1, o.MaxSftpReadBytes);
        var defLines = Math.Min(100, o.MaxTerminalLines);
        string Kb(int bytes) => bytes % 1024 == 0 ? (bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB" : bytes.ToString(CultureInfo.InvariantCulture) + " bytes";

        StringProperty HostArg() => new("host",
            "The host to act on: its id (GUID) or its exact label (case-insensitive), as returned by list_hosts.",
            Required: true, MinLength: 1, MaxLength: 256);
        StringProperty PathArg(string what) => new("path",
            $"Remote path of the {what}. Absolute (\"/var/log\") or relative to the SSH user's home directory.",
            Required: true, MinLength: 1, MaxLength: 4096);
        StringProperty SessionArg() => new("session",
            "Terminal session id, as returned by list_sessions.", Required: true, MinLength: 1, MaxLength: 256);

        var tools = new (ToolDefinition, ObjectSchema)[]
        {
            Define(ToolNames.ListHosts, "List hosts",
                "Lists the SSH hosts saved in MySSH that you are allowed to use, with id, label, hostname, port, SSH user, tags, " +
                "AI policy (ReadOnly: read tools plus allowlisted read-only commands; Ask: every command/write needs the user's approval; " +
                "Trusted: runs without approval except risky operations; FullAccess: everything runs without approval) and jump hosts. Call this first to find the host id or label " +
                "for the other tools. Never returns credentials.",
                ToolCapability.Read, readOnly: true, destructive: false, idempotent: true, openWorld: false,
                new ObjectSchema(
                    new StringProperty("query", "Optional case-insensitive text matched against label, hostname, user and tags.", MaxLength: 256),
                    new StringProperty("tag", "Optional exact tag (case-insensitive) the host must have.", MaxLength: 256))),

            Define(ToolNames.RunCommand, "Run command",
                "Runs one shell command on a host over a non-interactive SSH exec channel (no TTY: do not start interactive programs " +
                "such as vim, top without -b, less or sudo with a password prompt) and returns the exit code, stdout and stderr. " +
                $"Each stream is capped at {Kb(o.MaxCommandOutputBytes)}; when the cap or the timeout is hit the remote process is killed " +
                "and the result says so. On ReadOnly hosts only single allowlisted read-only commands without pipes, redirection or " +
                "substitution are allowed. Depending on the host policy the user may have to approve the command first.",
                ToolCapability.Execute, readOnly: false, destructive: true, idempotent: false, openWorld: true,
                new ObjectSchema(
                    HostArg(),
                    new StringProperty("command", "The command line, run by the user's login shell on the remote host.", Required: true, MinLength: 1, MaxLength: 16 * 1024),
                    new IntegerProperty("timeout_s", $"Seconds before the command is killed (1-{maxTimeout}).", Minimum: 1, Maximum: maxTimeout, Default: defTimeout),
                    new StringProperty("cwd", "Optional working directory for the command.", MaxLength: 4096))),

            Define(ToolNames.SftpList, "List directory",
                $"Lists a remote directory over SFTP (name, type, size, modification time, permissions, symlink target), at most {o.MaxSftpListEntries} entries.",
                ToolCapability.Read, readOnly: true, destructive: false, idempotent: true, openWorld: true,
                new ObjectSchema(HostArg(), PathArg("directory"))),

            Define(ToolNames.SftpStat, "Stat path",
                "Returns type, size, modification time, permissions and symlink target of one remote path, or exists=false when it does not exist. Symlinks are not followed.",
                ToolCapability.Read, readOnly: true, destructive: false, idempotent: true, openWorld: true,
                new ObjectSchema(HostArg(), PathArg("file or directory"))),

            Define(ToolNames.SftpRead, "Read file",
                "Reads a remote text file over SFTP and returns its content. Files larger than max_bytes are cut off (the result says so). " +
                "Binary files are refused: inspect them with run_command (e.g. \"xxd -l 512 FILE\" or \"base64 FILE\") instead.",
                ToolCapability.Read, readOnly: true, destructive: false, idempotent: true, openWorld: true,
                new ObjectSchema(HostArg(), PathArg("file"),
                    new IntegerProperty("max_bytes", $"Maximum number of bytes to read (1-{o.MaxSftpReadBytes}).", Minimum: 1, Maximum: o.MaxSftpReadBytes, Default: defRead))),

            Define(ToolNames.SftpWrite, "Write file",
                $"Writes a text file over SFTP (encoded in the host's configured encoding, normally UTF-8; at most {Kb(o.MaxSftpWriteBytes)}). " +
                "mode \"create\" fails if the file already exists; mode \"overwrite\" replaces it and always needs the user's approval. " +
                "Parent directories are not created (use sftp_mkdir).",
                ToolCapability.Write, readOnly: false, destructive: true, idempotent: false, openWorld: true,
                new ObjectSchema(HostArg(), PathArg("file"),
                    new StringProperty("content", "The complete new file content (text).", Required: true, MaxLength: o.MaxSftpWriteBytes),
                    new StringProperty("mode", "\"create\" (fail if the file exists) or \"overwrite\" (replace an existing file).", Required: true, Enum: ["create", "overwrite"]))),

            Define(ToolNames.SftpMkdir, "Create directory",
                "Creates one remote directory over SFTP (the parent must exist; fails if it already exists).",
                ToolCapability.Write, readOnly: false, destructive: true, idempotent: false, openWorld: true,
                new ObjectSchema(HostArg(), PathArg("new directory"))),

            Define(ToolNames.SftpDelete, "Delete path",
                "Deletes a remote file, symlink (the link itself, never its target) or directory over SFTP. A non-empty directory " +
                "needs recursive=true. Always needs the user's approval.",
                ToolCapability.Write, readOnly: false, destructive: true, idempotent: false, openWorld: true,
                new ObjectSchema(HostArg(), PathArg("file or directory to delete"),
                    new BooleanProperty("recursive", "Delete a directory with everything in it.", Default: false))),

            Define(ToolNames.ListSessions, "List terminal sessions",
                "Lists the terminal tabs the user has open in MySSH (session id, host, title, connected, size). Use the id with read_terminal or send_to_terminal.",
                ToolCapability.TerminalRead, readOnly: true, destructive: false, idempotent: true, openWorld: false,
                new ObjectSchema()),

            Define(ToolNames.ReadTerminal, "Read terminal",
                "Returns the last lines (screen plus scrollback, plain text) of one of the user's open terminal sessions.",
                ToolCapability.TerminalRead, readOnly: true, destructive: false, idempotent: true, openWorld: false,
                new ObjectSchema(SessionArg(),
                    new IntegerProperty("lines", $"Number of lines from the bottom (1-{o.MaxTerminalLines}).", Minimum: 1, Maximum: o.MaxTerminalLines, Default: defLines))),

            Define(ToolNames.SendToTerminal, "Send to terminal",
                "Types text into one of the user's open terminal sessions. By default it is only placed on the prompt line WITHOUT " +
                "pressing Enter, so the user can review it; execute=true presses Enter and needs the user's approval. Text that " +
                "contains a line break is treated like execute=true. Control characters other than tab and line breaks are rejected.",
                ToolCapability.TerminalWrite, readOnly: false, destructive: true, idempotent: false, openWorld: false,
                new ObjectSchema(SessionArg(),
                    new StringProperty("text", "The text to type.", Required: true, MinLength: 1, MaxLength: 16 * 1024),
                    new BooleanProperty("execute", "Press Enter after typing (runs the command; needs approval).", Default: false))),
        };

        _byName = new(StringComparer.Ordinal);
        foreach (var (def, schema) in tools)
            _byName.Add(def.Name, (def, schema));
        Tools = [.. tools.Select(t => t.Item1)];
    }

    public ToolOptions Options { get; }

    public IReadOnlyList<ToolDefinition> Tools { get; }

    public ToolDefinition? Find(string name) => name is not null && _byName.TryGetValue(name, out var t) ? t.Definition : null;

    internal ObjectSchema SchemaOf(string name) => _byName[name].Schema;

    private static (ToolDefinition, ObjectSchema) Define(string name, string title, string description, ToolCapability capability,
        bool readOnly, bool destructive, bool idempotent, bool openWorld, ObjectSchema schema) =>
        (new ToolDefinition(name, title, description, schema.ToJsonElement(), capability, readOnly, destructive, idempotent, openWorld), schema);
}
