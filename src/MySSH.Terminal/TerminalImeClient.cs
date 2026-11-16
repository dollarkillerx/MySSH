using Avalonia;
using Avalonia.Input.TextInput;

namespace MySSH.Terminal;

/// <summary>
/// Bridges the platform IME (TSF / NSTextInputClient / IBus-Fcitx) to the terminal: the candidate window
/// follows the terminal cursor, preedit text is drawn inline by the view, committed text arrives through the
/// normal TextInput event.
/// </summary>
internal sealed class TerminalImeClient(TerminalView owner) : TextInputMethodClient
{
    public override Visual TextViewVisual => owner;
    public override bool SupportsPreedit => true;
    public override bool SupportsSurroundingText => false;
    public override string SurroundingText => string.Empty;
    public override Rect CursorRectangle => owner.GetImeCursorRect();
    public override TextSelection Selection { get; set; }

    public string? Preedit { get; private set; }
    public int? PreeditCursor { get; private set; }

    public override void SetPreeditText(string? preeditText) => SetPreeditText(preeditText, null);

    public override void SetPreeditText(string? preeditText, int? cursorPos)
    {
        Preedit = string.IsNullOrEmpty(preeditText) ? null : preeditText;
        PreeditCursor = Preedit is null ? null : cursorPos;
        owner.OnPreeditChanged();
        RaiseCursorRectangleChanged();
    }

    public void NotifyCursorMoved() => RaiseCursorRectangleChanged();

    /// <summary>Drops a composition the platform did not finish (focus loss, reset).</summary>
    public void Cancel()
    {
        if (Preedit is not null)
        {
            Preedit = null;
            PreeditCursor = null;
            owner.OnPreeditChanged();
        }

        RequestReset();
    }
}
