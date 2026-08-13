using System.Windows.Input;
using Bridget.Interop;

namespace Bridget.UI;

/// <summary>
/// A global hotkey combo (RegisterHotKey modifiers + virtual key) with its
/// registry round-trip and display formatting in one place. Stored as
/// "modifiers,vk" in decimal ("3,32" = Ctrl+Alt+Space); "off" disables the
/// hotkey; unset means the default combo.
/// </summary>
readonly record struct Hotkey(uint Modifiers, uint Vk)
{
    public static readonly Hotkey Default = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x20 /* Space */);
    public static readonly Hotkey AssistantDefault = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x42 /* B */);
    public static readonly Hotkey Off = new(0, 0);

    public bool IsOff => Vk == 0;

    public static Hotkey LoadDictation() => Parse(Settings.DictationHotkey);
    public static Hotkey LoadAssistant() => Parse(Settings.AssistantHotkey, AssistantDefault);

    public static Hotkey Parse(string? stored) => Parse(stored, Default);

    public static Hotkey Parse(string? stored, Hotkey fallback)
    {
        if (stored is null) return fallback;
        if (stored == "off") return Off;
        var parts = stored.Split(',');
        if (parts.Length == 2
            && uint.TryParse(parts[0], out var mods)
            && uint.TryParse(parts[1], out var vk)
            && vk != 0)
        {
            return new Hotkey(mods & 0xF, vk);
        }
        return fallback;
    }

    public string Serialize() => IsOff ? "off" : $"{Modifiers & 0xF},{Vk}";

    public static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    /// <summary>
    /// Builds a combo from a key press in a normal WPF window. Null when the
    /// press isn't usable as a global hotkey: a bare modifier (the user is
    /// mid-combo), or a combo without Ctrl/Alt/Win — a Shift-only or plain
    /// key would fire on ordinary typing in other apps.
    /// </summary>
    public static Hotkey? FromKeyEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None || IsModifierKey(key)) return null;

        uint mods = 0;
        var held = Keyboard.Modifiers;
        if (held.HasFlag(ModifierKeys.Control)) mods |= NativeMethods.MOD_CONTROL;
        if (held.HasFlag(ModifierKeys.Alt)) mods |= NativeMethods.MOD_ALT;
        if (held.HasFlag(ModifierKeys.Shift)) mods |= NativeMethods.MOD_SHIFT;
        if (held.HasFlag(ModifierKeys.Windows)) mods |= NativeMethods.MOD_WIN;
        if ((mods & (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_WIN)) == 0)
            return null;

        var vk = KeyInterop.VirtualKeyFromKey(key);
        return vk > 0 ? new Hotkey(mods, (uint)vk) : null;
    }

    public override string ToString()
    {
        if (IsOff) return "Off";
        var parts = new List<string>(5);
        if ((Modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName((int)Vk));
        return string.Join("+", parts);
    }

    static string KeyName(int vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),   // 0–9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),   // A–Z
        >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",         // F1–F24
        _ => KeyInterop.KeyFromVirtualKey(vk).ToString(),
    };
}
