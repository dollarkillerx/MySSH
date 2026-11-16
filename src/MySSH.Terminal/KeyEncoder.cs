using Avalonia.Input;
using XKey = XTerm.Input.Key;
using XMods = XTerm.Input.KeyModifiers;

namespace MySSH.Terminal;

/// <summary>
/// Maps Avalonia key events to xterm input via the engine (so application cursor/keypad modes and modifier
/// encodings are the engine's). Printable text is not handled here: it arrives as TextInput (which is also
/// how committed IME text arrives).
/// </summary>
internal static class KeyEncoder
{
    /// <param name="optionIsMeta">macOS only: treat Option as Meta (ESC prefix) instead of letting it compose characters.</param>
    /// <returns>The bytes to send as a string, or null when the key is not the terminal's (let TextInput / the app have it).</returns>
    public static string? Encode(XTerm.Terminal engine, Key key, KeyModifiers modifiers, string? keySymbol, bool isMac, bool optionIsMeta) =>
        Encode(engine, key, modifiers, keySymbol, isMac, optionIsMeta, out _);

    /// <param name="isCharacterCombo">
    /// True for Ctrl/Meta + character keys: the platform may still deliver TextInput for that key (macOS Option+b → "∫"),
    /// which must then be dropped. Special keys never produce printable TextInput.
    /// </param>
    public static string? Encode(XTerm.Terminal engine, Key key, KeyModifiers modifiers, string? keySymbol, bool isMac, bool optionIsMeta, out bool isCharacterCombo)
    {
        isCharacterCombo = false;
        if (isMac && modifiers.HasFlag(KeyModifiers.Meta))
        {
            return null; // Cmd shortcuts belong to the app
        }

        var altIsMeta = modifiers.HasFlag(KeyModifiers.Alt) && (!isMac || optionIsMeta);
        var mods = XMods.None;
        if (modifiers.HasFlag(KeyModifiers.Shift)) mods |= XMods.Shift;
        if (altIsMeta) mods |= XMods.Alt;
        if (modifiers.HasFlag(KeyModifiers.Control)) mods |= XMods.Control;

        if (MapSpecial(key, engine.ApplicationKeypad) is { } xk)
        {
            var seq = engine.GenerateKeyInput(xk, mods);
            if (mods.HasFlag(XMods.Alt) && (string.IsNullOrEmpty(seq) || seq[0] != '\u001b'))
            {
                // e.g. Alt+Enter: the engine has no modified form (or drops Alt); Meta = ESC prefix
                var plain = string.IsNullOrEmpty(seq) ? engine.GenerateKeyInput(xk, mods & ~XMods.Alt) : seq;
                seq = string.IsNullOrEmpty(plain) ? null : "\u001b" + plain;
            }

            return string.IsNullOrEmpty(seq) ? null : seq;
        }

        if (mods.HasFlag(XMods.Control) || mods.HasFlag(XMods.Alt))
        {
            // Ctrl+letter, Ctrl+[ …, and Alt(Option)-as-Meta: the platform gives no usable TextInput for these
            var ch = KeyToChar(key, modifiers.HasFlag(KeyModifiers.Shift));
            if (ch is null && !isMac && keySymbol is { Length: 1 } sym && !char.IsControl(sym[0]))
            {
                ch = sym[0];
            }

            if (ch is { } c)
            {
                var seq = engine.GenerateCharInput(c, mods);
                isCharacterCombo = !string.IsNullOrEmpty(seq);
                return isCharacterCombo ? seq : null;
            }
        }

        return null;
    }

    private static XKey? MapSpecial(Key k, bool appKeypad) => k switch
    {
        Key.Enter => XKey.Enter,
        Key.Tab => XKey.Tab,
        Key.Back => XKey.Backspace,
        Key.Escape => XKey.Escape,
        Key.Up => XKey.UpArrow,
        Key.Down => XKey.DownArrow,
        Key.Left => XKey.LeftArrow,
        Key.Right => XKey.RightArrow,
        Key.Home => XKey.Home,
        Key.End => XKey.End,
        Key.PageUp => XKey.PageUp,
        Key.PageDown => XKey.PageDown,
        Key.Insert => XKey.Insert,
        Key.Delete => XKey.Delete,
        Key.F1 => XKey.F1, Key.F2 => XKey.F2, Key.F3 => XKey.F3, Key.F4 => XKey.F4,
        Key.F5 => XKey.F5, Key.F6 => XKey.F6, Key.F7 => XKey.F7, Key.F8 => XKey.F8,
        Key.F9 => XKey.F9, Key.F10 => XKey.F10, Key.F11 => XKey.F11, Key.F12 => XKey.F12,
        Key.F13 => XKey.F13, Key.F14 => XKey.F14, Key.F15 => XKey.F15, Key.F16 => XKey.F16,
        Key.F17 => XKey.F17, Key.F18 => XKey.F18, Key.F19 => XKey.F19, Key.F20 => XKey.F20,
        // the keypad only differs from the main keys in application keypad mode (DECKPAM)
        Key.NumPad0 when appKeypad => XKey.Keypad0,
        Key.NumPad1 when appKeypad => XKey.Keypad1,
        Key.NumPad2 when appKeypad => XKey.Keypad2,
        Key.NumPad3 when appKeypad => XKey.Keypad3,
        Key.NumPad4 when appKeypad => XKey.Keypad4,
        Key.NumPad5 when appKeypad => XKey.Keypad5,
        Key.NumPad6 when appKeypad => XKey.Keypad6,
        Key.NumPad7 when appKeypad => XKey.Keypad7,
        Key.NumPad8 when appKeypad => XKey.Keypad8,
        Key.NumPad9 when appKeypad => XKey.Keypad9,
        Key.Decimal when appKeypad => XKey.KeypadDecimal,
        Key.Divide when appKeypad => XKey.KeypadDivide,
        Key.Multiply when appKeypad => XKey.KeypadMultiply,
        Key.Subtract when appKeypad => XKey.KeypadSubtract,
        Key.Add when appKeypad => XKey.KeypadAdd,
        _ => null,
    };

    private static char? KeyToChar(Key k, bool shift)
    {
        if (k is >= Key.A and <= Key.Z)
        {
            return (char)((shift ? 'A' : 'a') + (k - Key.A));
        }

        if (k is >= Key.D0 and <= Key.D9)
        {
            return (char)('0' + (k - Key.D0));
        }

        return k switch
        {
            Key.Space => ' ',
            Key.OemOpenBrackets => '[',
            Key.OemCloseBrackets => ']',
            Key.OemPipe or Key.OemBackslash => '\\',
            Key.OemMinus => '-',
            Key.OemPlus => '=',
            Key.OemPeriod => '.',
            Key.OemComma => ',',
            Key.OemQuestion => '/',
            Key.OemSemicolon => ';',
            Key.OemQuotes => '\'',
            Key.OemTilde => '`',
            _ => null,
        };
    }
}
