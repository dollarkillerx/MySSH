using Avalonia;
using Avalonia.Input.TextInput;

namespace TermControl;

/// <summary>
/// Bridges the platform IME (TSF / macOS NSTextInputClient / IBus-Fcitx) to the terminal:
/// the candidate window follows the terminal cursor, preedit text is drawn inline by the control,
/// and committed text arrives through the normal TextInput event.
/// </summary>
internal sealed class TerminalImeClient : TextInputMethodClient
{
    readonly TerminalControl _owner;
    string? _preedit;

    public TerminalImeClient(TerminalControl owner) => _owner = owner;

    public override Visual TextViewVisual => _owner;
    public override bool SupportsPreedit => true;
    public override bool SupportsSurroundingText => false;
    public override string SurroundingText => string.Empty;
    public override Rect CursorRectangle => _owner.GetCursorRect();
    public override TextSelection Selection { get; set; }

    public string? Preedit => _preedit;

    public override void SetPreeditText(string? preeditText)
    {
        _preedit = string.IsNullOrEmpty(preeditText) ? null : preeditText;
        _owner.OnPreeditChanged();
    }

    public void NotifyCursorMoved() => RaiseCursorRectangleChanged();
}
