using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using MySSH.Core;

namespace MySSH.Terminal.Tests;

public class BackendTests
{
    private const string E = Harness.E;

    private static (Harness H, FakeBackend B) Attached(int width = 900, int height = 420)
    {
        var h = new Harness(width, height);
        var b = new FakeBackend();
        h.View.Attach(b);
        return (h, b);
    }

    [AvaloniaFact]
    public void Attach_sends_current_size_and_resizes_follow_the_control()
    {
        var (h, b) = Attached();
        Assert.Equal((h.View.Columns, h.View.Rows), b.Resizes[^1]);
        var raised = 0;
        h.View.GridSizeChanged += (_, _) => raised++;
        h.Window.Width = 500;
        h.Window.Height = 300;
        h.Pump();
        Assert.Equal(1, raised);
        Assert.Equal((h.View.Columns, h.View.Rows), b.Resizes[^1]);
        Assert.True(h.View.Columns < 70);
    }

    [AvaloniaFact]
    public void Output_from_a_background_thread_is_parsed_on_the_ui_thread()
    {
        var (h, b) = Attached();
        b.EmitFromBackground(Encoding.UTF8.GetBytes("hello "));
        // a UTF-8 sequence split across reads arrives whole
        var cjk = Encoding.UTF8.GetBytes("世界");
        b.EmitFromBackground(cjk[..2]);
        b.EmitFromBackground(cjk[2..]);
        h.Pump();
        Assert.Equal("hello 世界", h.Screen());
    }

    [AvaloniaFact]
    public void Engine_answers_are_written_back()
    {
        var (h, b) = Attached();
        b.Emit($"{E}[c{E}[6n");
        h.Pump();
        Assert.StartsWith($"{E}[?", b.WrittenText, StringComparison.Ordinal);
        Assert.EndsWith($"{E}[1;1R", b.WrittenText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Typed_text_is_sent_as_utf8()
    {
        var (h, b) = Attached();
        h.Window.KeyTextInput("ls 日本");
        Assert.Equal("ls 日本", b.WrittenText);
    }

    [AvaloniaFact]
    public void Session_end_drains_output_raises_event_and_stops_input()
    {
        var (h, b) = Attached();
        TerminalExit? ended = null;
        h.View.SessionEnded += (_, e) => ended = e;
        b.EmitFromBackground(Encoding.UTF8.GetBytes("logout\r\n"));
        b.Exit(new TerminalExit(0, false));
        h.Pump();
        Assert.Equal(new TerminalExit(0, false), ended);
        Assert.Contains("logout", h.Screen(), StringComparison.Ordinal);
        Assert.True(h.View.IsSessionEnded);
        b.ClearWritten();
        h.Window.KeyTextInput("x");
        b.Emit($"{E}[c");
        h.Pump();
        Assert.Equal(string.Empty, b.WrittenText);
    }

    [AvaloniaFact]
    public void Title_cwd_and_bell_events()
    {
        var (h, b) = Attached();
        string? title = null, cwd = null;
        var bells = 0;
        h.View.TitleChanged += (_, t) => title = t;
        h.View.WorkingDirectoryChanged += (_, d) => cwd = d;
        h.View.Bell += (_, _) => bells++;
        b.Emit($"{E}]0;deploy@web-01: ~/app\u0007{E}]7;file://web-01/home/deploy/app\u0007\u0007");
        h.Pump();
        Assert.Equal("deploy@web-01: ~/app", title);
        Assert.Equal("deploy@web-01: ~/app", h.View.Title);
        Assert.Equal("/home/deploy/app", cwd);
        Assert.Equal("/home/deploy/app", h.View.WorkingDirectory);
        Assert.Equal(1, bells);
    }

    [AvaloniaFact]
    public void Detach_stops_the_data_flow()
    {
        var (h, b) = Attached();
        h.View.Detach();
        b.Emit("ignored");
        h.Pump();
        Assert.Equal(string.Empty, h.Screen());
        h.Window.KeyTextInput("x");
        Assert.Equal(string.Empty, b.WrittenText);
        Assert.Null(h.View.Backend);
    }

    [AvaloniaFact]
    public void Clear_and_reset()
    {
        var (h, _) = Attached();
        h.Feed("one\r\ntwo\r\n" + $"{E}[?1h{E}[31mthree");
        h.View.Clear();
        h.Pump();
        Assert.DoesNotContain("one", h.Screen(), StringComparison.Ordinal);
        h.View.Reset();
        h.Pump();
        Assert.Equal(string.Empty, h.Screen());
        Assert.False(h.View.Engine.ApplicationCursorKeys);
    }

    [AvaloniaFact]
    public void Flood_is_parsed_in_slices_without_losing_bytes()
    {
        var (h, b) = Attached();
        var sb = new StringBuilder();
        for (var i = 0; i < 20000; i++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"line {i} {E}[3{i % 8}mcolour{E}[0m\r\n");
        }

        b.EmitFromBackground(Encoding.UTF8.GetBytes(sb.ToString()));
        h.Pump();
        Assert.Contains("line 19999 colour", h.Screen(), StringComparison.Ordinal);
        Assert.Equal(0, h.View.DroppedBytes);
    }

    [AvaloniaFact]
    public void Pending_output_beyond_the_limit_drops_oldest_chunks()
    {
        var (h, b) = Attached();
        h.View.MaxPendingBytes = 10;
        b.EmitFromBackground(Encoding.UTF8.GetBytes("aaaaaaaa"));
        b.EmitFromBackground(Encoding.UTF8.GetBytes("bbbbbbbb"));
        h.Pump();
        Assert.Equal(8, h.View.DroppedBytes);
        Assert.Equal("bbbbbbbb", h.Screen());
    }

    [AvaloniaFact]
    public void Visible_text_is_the_live_screen_even_when_scrolled_back()
    {
        var (h, _) = Attached();
        for (var i = 0; i < 100; i++)
        {
            h.View.Feed($"row {i}\r\n");
        }

        h.View.Feed("$ ");
        h.View.ScrollLines(-50);
        h.Pump();
        Assert.True(h.View.IsScrolledBack);
        var text = h.View.GetVisibleText(3);
        Assert.Equal("row 98\nrow 99\n$", text);
    }
}

public class SynchronizedOutputTests
{
    private const string E = Harness.E;

    [AvaloniaFact]
    public void Rows_are_not_rebuilt_mid_frame_in_mode_2026()
    {
        var h = new Harness();
        h.Feed("before");
        h.View.Stats.Reset();
        h.Feed($"{E}[?2026h{E}[H{E}[2Jhalf-drawn frame");
        Assert.Equal(0, h.View.Stats.RowsBuilt); // the old frame stays on screen
        h.Feed($" complete{E}[?2026l");
        Assert.True(h.View.Stats.RowsBuilt > 0);
        Assert.Contains(h.View.RowVisuals.Where(r => r.InUse && r.Slot == 0), r => r.Line!.TranslateToString(true, 0, 80) == "half-drawn frame complete");
    }
}
