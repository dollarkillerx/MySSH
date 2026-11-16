using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;

namespace MySSH.Terminal;

/// <summary>Cursor shape (DECSCUSR can change it at runtime).</summary>
public enum TerminalCursorStyle
{
    /// <summary>Filled cell.</summary>
    Block = 0,

    /// <summary>Vertical bar at the left of the cell.</summary>
    Bar = 1,

    /// <summary>Line at the bottom of the cell.</summary>
    Underline = 2,
}

/// <summary>
/// Raised before text is pasted into the terminal. Handlers may rewrite <see cref="Text"/>, set
/// <see cref="Cancel"/>, or call <see cref="Defer"/> with a confirmation task (e.g. a "paste 12 lines?" dialog).
/// </summary>
public sealed class TerminalPasteEventArgs(string text) : EventArgs
{
    private Task<bool>? _confirmation;

    /// <summary>The text to paste; handlers may change it.</summary>
    public string Text { get; set; } = text;

    /// <summary>True when the text contains a line break (would execute commands without bracketed paste).</summary>
    public bool IsMultiLine => Text.Contains('\n', StringComparison.Ordinal) || Text.Contains('\r', StringComparison.Ordinal);

    /// <summary>Whether the remote application enabled bracketed paste (mode 2004).</summary>
    public bool IsBracketed { get; init; }

    /// <summary>Set to true to drop the paste.</summary>
    public bool Cancel { get; set; }

    internal Task<bool>? Confirmation => _confirmation;

    /// <summary>Delays the paste until <paramref name="confirmation"/> completes; false cancels it.</summary>
    public void Defer(Task<bool> confirmation) => _confirmation = confirmation;
}

/// <summary>Accessibility: exposes the visible terminal text as a read-only value.</summary>
internal sealed class TerminalViewAutomationPeer(TerminalView owner) : ControlAutomationPeer(owner), IValueProvider
{
    public bool IsReadOnly => true;

    public string? Value => owner.GetVisibleText(owner.Rows);

    public void SetValue(string? value) => throw new InvalidOperationException("The terminal text is read-only.");

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    protected override string GetClassNameCore() => nameof(TerminalView);

    protected override string? GetNameCore() => base.GetNameCore() is { Length: > 0 } n ? n : owner.Title is { Length: > 0 } t ? t : "Terminal";

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    internal void NotifyTextChanged(string? oldValue, string? newValue) =>
        RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, oldValue, newValue);
}
