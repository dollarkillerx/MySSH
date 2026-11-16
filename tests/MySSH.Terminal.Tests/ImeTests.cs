using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;

namespace MySSH.Terminal.Tests;

/// <summary>
/// The IME path as the platform drives it: preedit via <see cref="Avalonia.Input.TextInput.TextInputMethodClient"/>,
/// key events marked ImeProcessed while composing, committed text via TextInput. Real candidate windows still need
/// manual checks on each OS (see spikes/terminal/FINDINGS.md §3).
/// </summary>
public class ImeTests
{
    private const string E = Harness.E;

    [AvaloniaFact]
    public void Client_is_offered_on_focus_and_tracks_the_cursor()
    {
        var h = new Harness();
        var client = h.View.ImeClient;
        Assert.Same(h.View, client.TextViewVisual);
        Assert.True(client.SupportsPreedit);
        var moved = 0;
        client.CursorRectangleChanged += (_, _) => moved++;
        h.Feed($"{E}[5;11H");
        var cell = h.View.CellSize;
        var rect = client.CursorRectangle;
        Assert.Equal(10 * cell.Width, rect.X, 3);
        Assert.Equal(4 * cell.Height, rect.Y, 3);
        Assert.True(moved > 0);

        // the focus manager asks the focused element for its client through this routed event
        var args = new Avalonia.Input.TextInput.TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        h.View.RaiseEvent(args);
        Assert.Same(client, args.Client);
    }

    [AvaloniaFact]
    public void Preedit_is_drawn_inline_and_keys_are_left_to_the_ime()
    {
        var h = new Harness();
        var b = new FakeBackend();
        h.View.Attach(b);
        h.Feed($"{E}[1;32mdeploy@web{E}[0m:~$ echo ");
        h.View.ImeClient.SetPreeditText("ni hao", 6);
        h.Pump();
        Assert.Equal("ni hao", h.View.Preedit);
        h.Save("40-preedit-pinyin");
        // while composing, Enter/Backspace/arrows belong to the IME
        h.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        h.Window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        h.Window.KeyPress(Key.ImeProcessed, RawInputModifiers.None, PhysicalKey.N, "n");
        Assert.Equal(string.Empty, b.WrittenText);

        h.View.ImeClient.SetPreeditText("にほんご", 2);
        h.Save("41-preedit-kana");

        // commit: the platform clears the preedit and delivers the text as TextInput
        h.View.ImeClient.SetPreeditText(null);
        h.Window.KeyTextInput("日本語");
        h.Pump();
        Assert.Null(h.View.Preedit);
        Assert.Equal("日本語", b.WrittenText);
    }

    [AvaloniaFact]
    public void Preedit_light_theme()
    {
        var h = new Harness(theme: ThemeVariant.Light);
        h.Feed("$ git commit -m \"");
        h.View.ImeClient.SetPreeditText("修正しました", 3);
        h.Save("42-preedit-light");
        Assert.Equal("修正しました", h.View.Preedit);
    }

    [AvaloniaFact]
    public void Pointer_presses_are_ignored_while_composing()
    {
        var h = new Harness();
        h.Feed("some text here");
        h.View.ImeClient.SetPreeditText("abc");
        h.Window.MouseDown(new Point(5, 5), MouseButton.Left, RawInputModifiers.None);
        h.Window.MouseMove(new Point(80, 5), RawInputModifiers.None);
        h.Window.MouseUp(new Point(80, 5), MouseButton.Left, RawInputModifiers.None);
        Assert.False(h.View.HasSelection);
    }
}
