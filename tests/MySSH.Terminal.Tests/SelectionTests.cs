using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Styling;

namespace MySSH.Terminal.Tests;

public class SelectionTests
{
    private const string E = Harness.E;

    private static Point At(Harness h, double col, double row)
    {
        var c = h.View.CellSize;
        return new Point((col + 0.5) * c.Width, (row + 0.5) * c.Height);
    }

    private static void Drag(Harness h, Point from, Point to, RawInputModifiers mods = RawInputModifiers.None)
    {
        h.Window.MouseDown(from, MouseButton.Left, mods);
        h.Window.MouseMove(to, mods | RawInputModifiers.LeftMouseButton);
        h.Window.MouseUp(to, MouseButton.Left, mods);
        h.Pump();
    }

    [AvaloniaFact]
    public void Drag_selects_across_lines_including_wide_characters()
    {
        var h = new Harness();
        h.Feed("first line\r\n中文 second\r\nthird");
        Drag(h, At(h, 6, 0), At(h, 3, 1)); // col 3 is the right half of 文
        Assert.Equal("line\n中文", h.View.GetSelectionText());
        h.Save("50-selection");
    }

    [AvaloniaFact]
    public void Double_click_selects_a_word_and_triple_click_a_line()
    {
        var h = new Harness();
        h.Feed("ssh deploy@web-01.example.test -p 22");
        var p = At(h, 8, 0);
        h.Window.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
        h.Window.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
        h.Window.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
        h.Window.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
        h.Pump();
        Assert.Equal("deploy@web-01.example.test", h.View.GetSelectionText());
        h.Window.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
        h.Window.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
        h.Pump();
        Assert.Equal("ssh deploy@web-01.example.test -p 22", h.View.GetSelectionText());
    }

    [AvaloniaFact]
    public void Soft_wrapped_lines_are_joined_and_hard_breaks_kept()
    {
        var h = new Harness(400, 200);
        var cols = h.View.Columns;
        var longLine = new string('x', cols + 5);
        h.Feed(longLine + "\r\nnext");
        h.View.SelectAll();
        Assert.Equal(longLine + "\nnext", h.View.GetSelectionText());
    }

    [AvaloniaFact]
    public void Shift_overrides_application_mouse_mode()
    {
        var h = new Harness();
        var b = new FakeBackend();
        h.View.Attach(b);
        b.Emit($"{E}[?1000h{E}[?1006hclick me");
        h.Pump();
        Drag(h, At(h, 0, 0), At(h, 4, 0));
        Assert.False(h.View.HasSelection);
        Assert.Equal($"{E}[<0;1;1M{E}[<0;5;1m", b.WrittenText);
        b.ClearWritten();
        Drag(h, At(h, 0, 0), At(h, 4, 0), RawInputModifiers.Shift);
        Assert.Equal("click", h.View.GetSelectionText());
        Assert.Equal(string.Empty, b.WrittenText);
    }

    [AvaloniaFact]
    public void Wheel_scrolls_scrollback_sends_arrows_on_alt_screen_and_reports_in_mouse_mode()
    {
        var h = new Harness();
        var b = new FakeBackend();
        h.View.Attach(b);
        for (var i = 0; i < 100; i++)
        {
            b.Emit($"{i}\r\n");
        }

        h.Pump();
        h.Window.MouseWheel(At(h, 1, 1), new Vector(0, 1), RawInputModifiers.None);
        h.Pump();
        Assert.True(h.View.IsScrolledBack);
        Assert.Equal(string.Empty, b.WrittenText);
        h.View.ScrollToBottom();

        b.Emit($"{E}[?1049h{E}[?1h");
        h.Pump();
        h.Window.MouseWheel(At(h, 1, 1), new Vector(0, -1), RawInputModifiers.None);
        Assert.Equal($"{E}OB{E}OB{E}OB", b.WrittenText);

        b.ClearWritten();
        b.Emit($"{E}[?1000h{E}[?1006h");
        h.Pump();
        h.Window.MouseWheel(At(h, 2, 3), new Vector(0, 1), RawInputModifiers.None);
        Assert.Equal($"{E}[<64;3;4M{E}[<64;3;4M{E}[<64;3;4M", b.WrittenText);
    }

    [AvaloniaFact]
    public async Task Copy_and_paste_through_the_clipboard()
    {
        var h = new Harness();
        var b = new FakeBackend();
        h.View.Attach(b);
        h.Feed("copy this text");
        Drag(h, At(h, 5, 0), At(h, 8, 0));
        Assert.True(await h.View.CopySelectionAsync());
        var clipboard = TopLevel.GetTopLevel(h.View)!.Clipboard!;
        Assert.Equal("this", await clipboard.TryGetTextAsync());

        await clipboard.SetTextAsync("echo 1\necho 2");
        b.Emit($"{E}[?2004h");
        h.Pump();
        TerminalPasteEventArgs? seen = null;
        h.View.PasteRequested += (_, e) => seen = e;
        Assert.True(await h.View.PasteAsync());
        Assert.NotNull(seen);
        Assert.True(seen.IsMultiLine);
        Assert.True(seen.IsBracketed);
        Assert.Equal($"{E}[200~echo 1\recho 2{E}[201~", b.WrittenText);
    }

    [AvaloniaFact]
    public async Task Paste_can_be_cancelled_rewritten_or_confirmed_asynchronously()
    {
        var h = new Harness();
        var b = new FakeBackend();
        h.View.Attach(b);
        h.View.PasteRequested += (_, e) =>
        {
            if (e.Text == "cancel")
            {
                e.Cancel = true;
            }
            else if (e.Text == "deny")
            {
                e.Defer(Task.FromResult(false));
            }
            else if (e.Text == "ask")
            {
                e.Defer(Task.FromResult(true));
            }
            else
            {
                e.Text = e.Text.ToUpperInvariant();
            }
        };
        Assert.False(await h.View.PasteTextAsync("cancel"));
        Assert.False(await h.View.PasteTextAsync("deny"));
        Assert.True(await h.View.PasteTextAsync("ask"));
        Assert.True(await h.View.PasteTextAsync("abc"));
        Assert.Equal("askABC", b.WrittenText);
    }

    [AvaloniaFact]
    public void Selection_light_theme_and_block_mode()
    {
        var h = new Harness(theme: ThemeVariant.Light);
        h.Feed(RenderingTests.Sample);
        Drag(h, At(h, 3, 1), At(h, 30, 3), RawInputModifiers.Alt);
        var text = h.View.GetSelectionText()!;
        Assert.Equal(3, text.Split('\n').Length);
        h.Save("51-selection-light-block");
    }
}
