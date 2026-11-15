namespace MySSH.Spike.McpHost;

/// <summary>Append-only evidence log (.work/events.log) that the probe greps for server-side facts.</summary>
public sealed class EventLog(SpikeOptions options)
{
    private readonly object _gate = new();

    public void Write(string evt, string detail)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {evt} {detail}";
        lock (_gate)
        {
            File.AppendAllText(options.EventsFile, line + Environment.NewLine);
        }
        Console.Error.WriteLine("[event] " + line);
    }
}
