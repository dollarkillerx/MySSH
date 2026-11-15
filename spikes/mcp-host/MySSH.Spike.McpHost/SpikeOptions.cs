namespace MySSH.Spike.McpHost;

/// <summary>Command line / env options for the spike host.</summary>
public sealed record SpikeOptions
{
    /// <summary>--tray : do not create the main window (emulates tray-resident mode).</summary>
    public bool Tray { get; init; }

    /// <summary>--auto-approve=allow|deny (or MYSSH_AUTO_APPROVE). null = interactive Avalonia dialog.</summary>
    public bool? AutoApprove { get; init; }

    /// <summary>--approval-timeout=SECONDS (default 120). Timeout = deny.</summary>
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>--work-dir=PATH (default ./.work). endpoint.json, myssh.sock, events.log live here.</summary>
    public string WorkDir { get; init; } = Path.GetFullPath(".work");

    /// <summary>--run-delay=SECONDS (default 5) fake execution time of run_command.</summary>
    public TimeSpan RunDelay { get; init; } = TimeSpan.FromSeconds(5);

    public string SocketPath => Path.Combine(WorkDir, "myssh.sock");
    public string EndpointFile => Path.Combine(WorkDir, "endpoint.json");
    public string EventsFile => Path.Combine(WorkDir, "events.log");

    public static SpikeOptions Parse(string[] args)
    {
        var o = new SpikeOptions();
        string? auto = Environment.GetEnvironmentVariable("MYSSH_AUTO_APPROVE");
        foreach (var a in args)
        {
            var (k, v) = a.Split('=', 2) is [var kk, var vv] ? (kk, vv) : (a, "");
            switch (k)
            {
                case "--tray": case "--headless": o = o with { Tray = true }; break;
                case "--auto-approve": auto = v; break;
                case "--approval-timeout": o = o with { ApprovalTimeout = TimeSpan.FromSeconds(double.Parse(v)) }; break;
                case "--work-dir": o = o with { WorkDir = Path.GetFullPath(v) }; break;
                case "--run-delay": o = o with { RunDelay = TimeSpan.FromSeconds(double.Parse(v)) }; break;
            }
        }
        return o with
        {
            AutoApprove = auto?.ToLowerInvariant() switch
            {
                "allow" => true,
                "deny" => false,
                null or "" or "off" => null,
                var x => throw new ArgumentException($"--auto-approve must be allow|deny, got '{x}'"),
            }
        };
    }
}
