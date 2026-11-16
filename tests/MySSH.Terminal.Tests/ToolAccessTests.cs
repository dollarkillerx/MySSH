using Avalonia.Headless.XUnit;

namespace MySSH.Terminal.Tests;

/// <summary>GetRecentText / TypeText: what MCP and the AI assistant use for read_terminal / send_to_terminal.</summary>
public class ToolAccessTests
{
    private const string E = Harness.E;

    [AvaloniaFact]
    public void Recent_text_includes_scrollback_and_joins_wrapped_rows()
    {
        var h = Harness.ForGrid(20, 5);
        var b = new FakeBackend();
        h.View.Attach(b);
        for (var i = 1; i <= 12; i++) b.Emit($"line {i}\r\n");
        b.Emit("abcdefghijklmnopqrstuvwxyz0123"); // 30 chars: soft-wraps at 20 columns
        h.Pump();

        var text = h.View.GetRecentText(4);
        Assert.Equal("line 10\nline 11\nline 12\nabcdefghijklmnopqrstuvwxyz0123", text);
        Assert.StartsWith("line 1\n", h.View.GetRecentText(1000), StringComparison.Ordinal);
        Assert.Equal("", h.View.GetRecentText(0));
    }

    [AvaloniaFact]
    public void Type_text_without_enter_and_with_enter()
    {
        var h = new Harness();
        var b = new FakeBackend();
        h.View.Attach(b);
        Assert.True(h.View.TypeText("ls -la", pressEnter: false));
        Assert.Equal("ls -la", b.WrittenText);
        Assert.True(h.View.TypeText("", pressEnter: true));
        Assert.Equal("ls -la\r", b.WrittenText);
    }

    [AvaloniaFact]
    public void Multi_line_text_needs_bracketed_paste_or_enter()
    {
        var h = new Harness();
        var b = new FakeBackend();
        h.View.Attach(b);
        Assert.False(h.View.TypeText("a\nb", pressEnter: false)); // would run "a" in a plain shell
        Assert.Equal("", b.WrittenText);

        b.Emit($"{E}[?2004h"); // shell enables bracketed paste
        h.Pump();
        Assert.True(h.View.TypeText("a\nb", pressEnter: false));
        Assert.Equal($"{E}[200~a\rb{E}[201~", b.WrittenText);
    }

    [AvaloniaFact]
    public void Type_text_fails_without_a_backend()
    {
        var h = new Harness();
        Assert.False(h.View.TypeText("x", pressEnter: true));
    }
}
