using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace MySSH.Terminal.Tests;

public class InputTests
{
    private const string E = Harness.E;

    private static string Encode(Key key, KeyModifiers mods = KeyModifiers.None, string? setup = null, bool mac = false, bool optionIsMeta = true, string? symbol = null)
    {
        var t = new XTerm.Terminal(new XTerm.Options.TerminalOptions { Cols = 80, Rows = 24 });
        if (setup is not null)
        {
            t.Write(setup);
        }

        return KeyEncoder.Encode(t, key, mods, symbol, mac, optionIsMeta) ?? "<null>";
    }

    [AvaloniaTheory]
    [InlineData(Key.Up, KeyModifiers.None, null, "\u001b[A")]
    [InlineData(Key.Up, KeyModifiers.None, "\u001b[?1h", "\u001bOA")]          // DECCKM application cursor
    [InlineData(Key.Left, KeyModifiers.Control, null, "\u001b[1;5D")]
    [InlineData(Key.Right, KeyModifiers.Alt | KeyModifiers.Shift, null, "\u001b[1;4C")]
    [InlineData(Key.Home, KeyModifiers.None, null, "\u001b[H")]
    [InlineData(Key.Delete, KeyModifiers.None, null, "\u001b[3~")]
    [InlineData(Key.PageDown, KeyModifiers.None, null, "\u001b[6~")]
    [InlineData(Key.F1, KeyModifiers.None, null, "\u001bOP")]
    [InlineData(Key.F5, KeyModifiers.None, null, "\u001b[15~")]
    [InlineData(Key.F5, KeyModifiers.Shift, null, "\u001b[15;2~")]
    [InlineData(Key.F12, KeyModifiers.Control, null, "\u001b[24;5~")]
    [InlineData(Key.Enter, KeyModifiers.None, null, "\r")]
    [InlineData(Key.Enter, KeyModifiers.Alt, null, "\u001b\r")]
    [InlineData(Key.Tab, KeyModifiers.Shift, null, "\u001b[Z")]
    [InlineData(Key.Back, KeyModifiers.None, null, "\u007f")]
    [InlineData(Key.Back, KeyModifiers.Alt, null, "\u001b\u007f")]
    [InlineData(Key.Escape, KeyModifiers.None, null, "\u001b")]
    [InlineData(Key.C, KeyModifiers.Control, null, "\u0003")]
    [InlineData(Key.D, KeyModifiers.Control, null, "\u0004")]
    [InlineData(Key.OemOpenBrackets, KeyModifiers.Control, null, "\u001b")]
    [InlineData(Key.Space, KeyModifiers.Control, null, "\u0000")]
    [InlineData(Key.B, KeyModifiers.Alt, null, "\u001bb")]
    [InlineData(Key.NumPad1, KeyModifiers.None, "\u001b=", "\u001bOq")]       // DECKPAM application keypad
    [InlineData(Key.NumPad1, KeyModifiers.None, null, "<null>")]               // normal keypad: TextInput "1"
    [InlineData(Key.A, KeyModifiers.None, null, "<null>")]                     // printable: TextInput
    [InlineData(Key.A, KeyModifiers.Shift, null, "<null>")]
    public void Keys_encode_like_xterm(Key key, KeyModifiers mods, string? setup, string expected) =>
        Assert.Equal(expected, Encode(key, mods, setup));

    [AvaloniaFact]
    public void Mac_option_as_meta_and_command_shortcuts()
    {
        Assert.Equal($"{E}f", Encode(Key.F, KeyModifiers.Alt, mac: true, optionIsMeta: true, symbol: "ƒ"));
        Assert.Equal("<null>", Encode(Key.F, KeyModifiers.Alt, mac: true, optionIsMeta: false, symbol: "ƒ"));
        Assert.Equal("<null>", Encode(Key.C, KeyModifiers.Meta, mac: true));
        Assert.Equal("\u0003", Encode(Key.C, KeyModifiers.Control, mac: true));
    }

    [AvaloniaFact]
    public void Key_events_reach_the_backend_and_suppress_their_text_input()
    {
        var h = new Harness();
        var b = new FakeBackend();
        h.View.Attach(b);
        var wasMac = TerminalView.IsMac;
        try
        {
            TerminalView.IsMac = false;
            h.Window.KeyPress(Key.B, RawInputModifiers.Alt, PhysicalKey.B, "b");
            h.Window.KeyTextInput("b"); // what a platform might still deliver for the same key
            h.Window.KeyRelease(Key.B, RawInputModifiers.Alt, PhysicalKey.B, "b");
            h.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            h.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            h.Window.KeyTextInput("\r");
            h.Window.KeyTextInput("é");
            Assert.Equal($"{E}b{E}[A\ré", b.WrittenText);
        }
        finally
        {
            TerminalView.IsMac = wasMac;
        }
    }

    [AvaloniaFact]
    public void Text_after_a_special_key_is_not_swallowed()
    {
        var h = new Harness();
        var b = new FakeBackend();
        h.View.Attach(b);
        h.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        h.Window.KeyTextInput(":wq"); // e.g. an IME commit or a platform that sends text without a KeyDown
        Assert.Equal("\u001b:wq", b.WrittenText);
    }

    [AvaloniaFact]
    public void Shift_page_up_scrolls_locally_without_sending()
    {
        var h = new Harness();
        var b = new FakeBackend();
        h.View.Attach(b);
        for (var i = 0; i < 100; i++)
        {
            h.View.Feed($"{i}\r\n");
        }

        h.Window.KeyPress(Key.PageUp, RawInputModifiers.Shift, PhysicalKey.PageUp, null);
        h.Pump();
        Assert.True(h.View.IsScrolledBack);
        Assert.Equal(string.Empty, b.WrittenText);
        h.Window.KeyTextInput("x"); // typing returns to the live screen
        Assert.False(h.View.IsScrolledBack);
    }

    [AvaloniaFact]
    public void Focus_reporting_when_enabled()
    {
        var other = new Avalonia.Controls.TextBox();
        var h = new Harness();
        var b = new FakeBackend();
        h.View.Attach(b);
        var panel = new Avalonia.Controls.StackPanel();
        h.Window.Content = panel;
        panel.Children.Add(other);
        panel.Children.Add(h.View);
        h.Pump();
        h.View.Focus();
        h.Pump();
        b.Emit($"{E}[?1004h");
        h.Pump();
        b.ClearWritten();
        other.Focus();
        h.Pump();
        h.View.FocusTerminal();
        h.Pump();
        Assert.Equal($"{E}[O{E}[I", b.WrittenText);
    }

    [AvaloniaFact]
    public void Cursor_style_follows_decscusr()
    {
        var h = new Harness();
        Assert.Equal(TerminalCursorStyle.Block, h.View.EffectiveCursorStyle);
        h.Feed($"{E}[6 q");
        Assert.Equal(TerminalCursorStyle.Bar, h.View.EffectiveCursorStyle);
        h.Feed($"{E}[3 q");
        Assert.Equal(TerminalCursorStyle.Underline, h.View.EffectiveCursorStyle);
        h.Feed("bar cursor ");
        h.Feed($"{E}[6 q");
        h.Save("30-cursor-bar");
    }
}
