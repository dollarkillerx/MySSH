using System.Globalization;
using System.Text;
using Avalonia.Headless.XUnit;

namespace MySSH.Terminal.Tests;

/// <summary>
/// Replays byte streams recorded from real programs through a local PTY (see <see cref="FixtureRecorder"/>) and
/// asserts the final screen. The streams include the programs' reactions to the engine's DA/DSR answers, so the
/// replay is deterministic and runs anywhere (no PTY needed).
/// </summary>
public class ReplayTests
{
    internal static (byte[] Data, int Cols, int Rows) Load(string name)
    {
        var dir = Harness.FixtureDir();
        var data = File.ReadAllBytes(Path.Combine(dir, name + ".bin"));
        var meta = File.ReadAllLines(Path.Combine(dir, name + ".meta"))
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => int.Parse(p[1], CultureInfo.InvariantCulture));
        return (data, meta["cols"], meta["rows"]);
    }

    /// <summary>Feeds through a backend from a background thread in odd-sized chunks (splits UTF-8 and escape sequences).</summary>
    private static Harness Replay(string name)
    {
        var (data, cols, rows) = Load(name);
        var h = Harness.ForGrid(cols, rows);
        var backend = new FakeBackend();
        h.View.Attach(backend);
        for (var i = 0; i < data.Length; i += 977)
        {
            backend.EmitFromBackground(data[i..Math.Min(data.Length, i + 977)]);
        }

        h.Pump();
        return h;
    }

    private static string[] Lines(Harness h) => h.Screen().Split('\n');

    [AvaloniaFact]
    public void Vim_edit_session()
    {
        var h = Replay("vim");
        var lines = Lines(h);
        Assert.True(h.View.Engine.IsAlternateBufferActive);
        Assert.Contains(lines, l => l.Contains("listen 443 ssl;  # 中文注释 日本語 你好", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("1 server {", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains('~', StringComparison.Ordinal));
        Assert.DoesNotContain("-- INSERT --", h.Screen(), StringComparison.Ordinal);
        // Esc left insert mode with the cursor on the last inserted character "好" (row 1; wide chars count 2 cells)
        var row1 = h.View.Engine.Buffer.GetLine(h.View.Engine.Buffer.YBase + 1)!;
        Assert.Equal(1, h.Cursor.Y);
        Assert.Equal("好", row1[h.Cursor.X].Content);
        h.Save("20-replay-vim");
    }

    [AvaloniaFact]
    public void Less_with_cjk_log_paged_and_searched()
    {
        var h = Replay("less");
        var lines = Lines(h);
        Assert.True(h.View.Engine.IsAlternateBufferActive);
        Assert.Contains(lines, l => l.StartsWith("line 100: 中文日志 日本語ログ 第100行", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("line 1:", StringComparison.Ordinal));
        // no ghost characters: every visible log line is complete
        foreach (var l in lines.Where(l => l.StartsWith("line ", StringComparison.Ordinal)))
        {
            Assert.Matches(@"^line \d+: 中文日志 日本語ログ 第\d+行 [a-z]+$", l);
        }

        h.Save("21-replay-less");
    }

    [AvaloniaFact]
    public void Htop_header_and_meters()
    {
        var h = Replay("htop");
        var screen = h.Screen();
        Assert.True(h.View.Engine.IsAlternateBufferActive);
        Assert.Contains("PID", screen, StringComparison.Ordinal);
        Assert.Contains("Tasks:", screen, StringComparison.Ordinal);
        Assert.Contains("Load average:", screen, StringComparison.Ordinal);
        h.Save("22-replay-htop");
    }

    [AvaloniaFact]
    public void Top_summary()
    {
        var h = Replay("top");
        var screen = h.Screen();
        Assert.Contains("Processes:", screen, StringComparison.Ordinal);
        Assert.Contains("Load Avg:", screen, StringComparison.Ordinal);
        Assert.Contains("PhysMem:", screen, StringComparison.Ordinal);
        h.Save("23-replay-top");
    }

    [AvaloniaFact]
    public void Replaying_the_same_bytes_in_one_piece_gives_the_same_screen()
    {
        foreach (var name in new[] { "vim", "less", "htop", "top" })
        {
            var (data, cols, rows) = Load(name);
            var a = Harness.ForGrid(cols, rows);
            a.View.Feed(data);
            var b = Replay(name);
            Assert.Equal(a.Screen(), b.Screen());
            Assert.Equal(a.Cursor, b.Cursor);
        }
    }
}
