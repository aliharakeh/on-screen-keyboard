using System.Windows.Input;
using static OnScreenKeyboard.NativeMethods;

namespace OnScreenKeyboard;

/// <summary>
/// A key combination such as "Win+H" or "Ctrl+Shift+Space". <see cref="Key"/> may be <see cref="Key.None"/>
/// for modifier-only shortcuts (e.g. "Ctrl+Win", used by some push-to-talk apps).
/// </summary>
public sealed record Shortcut(bool Ctrl, bool Shift, bool Alt, bool Win, Key Key)
{
    public const string WindowsVoiceTyping = "Win+H";

    public bool IsEmpty => !Ctrl && !Shift && !Alt && !Win && Key == Key.None;

    private static readonly Dictionary<string, Key> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = Key.Escape, ["Del"] = Key.Delete, ["Ins"] = Key.Insert, ["PgUp"] = Key.PageUp,
        ["PgDn"] = Key.PageDown, ["Enter"] = Key.Return, ["`"] = Key.OemTilde, ["-"] = Key.OemMinus,
        ["="] = Key.OemPlus, [","] = Key.OemComma, ["."] = Key.OemPeriod, ["/"] = Key.OemQuestion,
        [";"] = Key.OemSemicolon, ["'"] = Key.OemQuotes, ["["] = Key.OemOpenBrackets, ["]"] = Key.OemCloseBrackets,
        ["\\"] = Key.OemPipe,
    };

    public static Shortcut? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        bool ctrl = false, shift = false, alt = false, win = false;
        var key = Key.None;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) return null;
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; continue;
                case "shift": shift = true; continue;
                case "alt": alt = true; continue;
                case "win" or "windows": win = true; continue;
            }
            if (key != Key.None) return null; // only one non-modifier key
            if (part.Length == 1 && char.IsAsciiDigit(part[0])) key = Key.D0 + (part[0] - '0');
            else if (Aliases.TryGetValue(part, out var alias)) key = alias;
            else if (!Enum.TryParse(part, true, out key) || key is Key.None or Key.LeftCtrl or Key.RightCtrl
                     or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return null;
        }
        var shortcut = new Shortcut(ctrl, shift, alt, win, key);
        return shortcut.IsEmpty ? null : shortcut;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        if (Win) parts.Add("Win");
        if (Key != Key.None) parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        var alias = Aliases.FirstOrDefault(p => p.Value == key && p.Key.Length == 1).Key;
        return alias ?? (key == Key.Return ? "Enter" : key.ToString());
    }

    private const ushort VK_LCONTROL = 0xA2, VK_LSHIFT = 0xA0, VK_LMENU = 0xA4;

    private IEnumerable<ushort> VirtualKeys()
    {
        if (Ctrl) yield return VK_LCONTROL;
        if (Shift) yield return VK_LSHIFT;
        if (Alt) yield return VK_LMENU;
        if (Win) yield return VK_LWIN;
        if (Key != Key.None) yield return (ushort)KeyInterop.VirtualKeyFromKey(Key);
    }

    /// <summary>Presses the modifiers, then the key. Holding until <see cref="Release"/> supports push-to-talk apps.</summary>
    public void Press()
    {
        foreach (var vk in VirtualKeys()) SendVirtualKey(vk, up: false);
    }

    public void Release()
    {
        foreach (var vk in VirtualKeys().Reverse()) SendVirtualKey(vk, up: true);
    }

    private static void SendVirtualKey(ushort vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0x90 or 0x2C) flags |= KEYEVENTF_EXTENDEDKEY;
        SendKey(vk, (ushort)MapVirtualKey(vk, 0), flags);
    }
}
