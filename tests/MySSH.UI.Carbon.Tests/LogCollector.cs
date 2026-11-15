using System.Collections.Concurrent;
using Avalonia.Logging;

namespace MySSH.UI.Carbon.Tests;

/// <summary>Collects Avalonia binding / property / resource warnings so tests can assert there are none.</summary>
public sealed class LogCollector : ILogSink
{
    private static readonly LogCollector Instance = new();
    private readonly ConcurrentQueue<string> _messages = new();

    public static void Install() => Logger.Sink = Instance;

    /// <summary>Drains and returns everything logged since the last call.</summary>
    public static IReadOnlyList<string> Drain()
    {
        var list = new List<string>();
        while (Instance._messages.TryDequeue(out var m))
        {
            list.Add(m);
        }

        return list;
    }

    public bool IsEnabled(LogEventLevel level, string area) =>
        level >= LogEventLevel.Warning && area is LogArea.Binding or LogArea.Property or LogArea.Visual or LogArea.Control;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        _messages.Enqueue($"[{level}] {area} {source}: {messageTemplate}");

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        _messages.Enqueue($"[{level}] {area} {source}: {Format(messageTemplate, propertyValues)}");

    private static string Format(string template, object?[] values)
    {
        var i = 0;
        var sb = new System.Text.StringBuilder();
        for (var p = 0; p < template.Length; p++)
        {
            if (template[p] == '{')
            {
                var end = template.IndexOf('}', p);
                if (end > p)
                {
                    sb.Append(i < values.Length ? values[i++] : "?");
                    p = end;
                    continue;
                }
            }

            sb.Append(template[p]);
        }

        return sb.ToString();
    }
}
