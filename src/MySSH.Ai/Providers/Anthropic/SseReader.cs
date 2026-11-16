using System.Runtime.CompilerServices;
using System.Text;

namespace MySSH.Ai.Providers.Anthropic;

/// <summary>One server-sent event: the <c>event:</c> name (null when absent) and the joined <c>data:</c> lines.</summary>
internal readonly record struct SseEvent(string? Event, string Data);

/// <summary>
/// Server-sent events parser (WHATWG "event stream interpretation"): LF / CRLF / CR line endings, a leading BOM,
/// <c>:</c> comment lines, multi-line <c>data</c> (joined with LF), one optional space after the colon, fields without a
/// colon, and <c>id</c> / <c>retry</c> / unknown fields ignored. An event is dispatched on a blank line; blocks without
/// data are skipped. Unlike the spec, a final event that is not followed by a blank line is still dispatched at end of
/// stream (lenient with gateways that close right after the last line).
/// </summary>
internal static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);
        string? eventName = null;
        var data = new StringBuilder();
        var hasData = false;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                if (hasData)
                    yield return new SseEvent(eventName, data.ToString());
                eventName = null;
                data.Clear();
                hasData = false;
                continue;
            }

            if (line[0] == ':')
                continue; // comment / keep-alive

            string field, value;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                field = line;
                value = "";
            }
            else
            {
                field = line[..colon];
                value = colon + 1 < line.Length && line[colon + 1] == ' ' ? line[(colon + 2)..] : line[(colon + 1)..];
            }

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    if (hasData) data.Append('\n');
                    data.Append(value);
                    hasData = true;
                    break;
                default:
                    break; // id, retry, unknown
            }
        }

        if (hasData)
            yield return new SseEvent(eventName, data.ToString());
    }
}
