using System.Windows.Input;
using static Vox.Core.Native;

namespace Vox.Core;

/// <summary>
/// A user-chosen shortcut: any of Ctrl / Alt / Shift / Win (either side) plus up to two other keys, e.g. "Ctrl+Win",
/// "Ctrl+Alt", "F9", "Alt+Shift+Z", "Ctrl+Shift+Space". Stored in settings as text ("Ctrl+Win"). Modifiers must match
/// exactly (Ctrl+Win does not fire while Alt is also held), so a shortcut never steals another shortcut that contains it.
/// </summary>
public sealed class KeyCombo : IEquatable<KeyCombo>
{
    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Shift { get; init; }
    public bool Win { get; init; }
    /// <summary>Non-modifier keys (virtual-key codes), sorted.</summary>
    public int[] Keys { get; init; } = Array.Empty<int>();

    public static KeyCombo DefaultDictation => Parse("Ctrl+Win")!;
    public static KeyCombo DefaultPasteLast => Parse("Alt+Shift+Z")!;
    public static KeyCombo DefaultCopyLast => Parse("Alt+Shift+X")!;

    public bool IsEmpty => !Ctrl && !Alt && !Shift && !Win && Keys.Length == 0;
    public bool IsModifierOnly => Keys.Length == 0;
    int ModifierCount => (Ctrl ? 1 : 0) + (Alt ? 1 : 0) + (Shift ? 1 : 0) + (Win ? 1 : 0);

    public static bool IsModifierKey(int vk) => vk is VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU
        or VK_LWIN or VK_RWIN or VK_SHIFT or VK_CONTROL or VK_MENU;

    /// <summary>Builds a combo from the keys currently held (virtual-key codes, any side).</summary>
    public static KeyCombo FromKeys(IEnumerable<int> vks)
    {
        var set = vks.ToHashSet();
        return new KeyCombo
        {
            Ctrl = set.Contains(VK_LCONTROL) || set.Contains(VK_RCONTROL) || set.Contains(VK_CONTROL),
            Alt = set.Contains(VK_LMENU) || set.Contains(VK_RMENU) || set.Contains(VK_MENU),
            Shift = set.Contains(VK_LSHIFT) || set.Contains(VK_RSHIFT) || set.Contains(VK_SHIFT),
            Win = set.Contains(VK_LWIN) || set.Contains(VK_RWIN),
            Keys = set.Where(v => !IsModifierKey(v)).OrderBy(v => v).Take(2).ToArray(),
        };
    }

    /// <summary>True while exactly this combo is held: its modifiers and no others, and all its keys.</summary>
    public bool IsHeld(Func<int, bool> down)
    {
        if (IsEmpty) return false;
        bool ctrl = down(VK_LCONTROL) || down(VK_RCONTROL), alt = down(VK_LMENU) || down(VK_RMENU),
             shift = down(VK_LSHIFT) || down(VK_RSHIFT), win = down(VK_LWIN) || down(VK_RWIN);
        if (ctrl != Ctrl || alt != Alt || shift != Shift || win != Win) return false;
        foreach (var k in Keys) if (!down(k)) return false;
        return true;
    }

    /// <summary>Modifiers of this combo match exactly what is held (used for "press the last key" shortcuts).</summary>
    public bool ModifiersMatch(Func<int, bool> down) =>
        (down(VK_LCONTROL) || down(VK_RCONTROL)) == Ctrl && (down(VK_LMENU) || down(VK_RMENU)) == Alt &&
        (down(VK_LSHIFT) || down(VK_RSHIFT)) == Shift && (down(VK_LWIN) || down(VK_RWIN)) == Win;

    /// <summary>Why this combo cannot be used as a global shortcut, or null when it is fine.</summary>
    public string? Problem()
    {
        if (IsEmpty) return "No keys were pressed.";
        if (Keys.Contains(VK_ESCAPE)) return "Esc is reserved for cancelling a dictation.";
        if (IsModifierOnly && ModifierCount < 2)
            return $"{this} on its own is used by everyday shortcuts (copy, paste, capitals...). Hold two of Ctrl, Alt, Shift and Win, or add another key.";
        bool typingKeys = Keys.Any(IsTypingKey);
        if (typingKeys && (ModifierCount == 0 || (ModifierCount == 1 && Shift)))
            return $"{this} would fire while you type. Add Ctrl, Alt or Win.";
        if (Ctrl && Alt && Keys.Contains(0x2E)) return "Ctrl+Alt+Delete belongs to Windows.";
        return null;
    }

    /// <summary>Letters, digits, punctuation, Space, Enter, Tab, Backspace, arrows: keys used for typing and moving around.</summary>
    static bool IsTypingKey(int vk) =>
        vk is >= 0x30 and <= 0x5A                       // 0-9, A-Z
        or VK_SPACE or 0x0D or 0x09 or 0x08             // Space, Enter, Tab, Backspace
        or >= 0x21 and <= 0x28                          // PageUp/Down, End, Home, arrows
        or 0x2D or 0x2E                                 // Insert, Delete
        or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF   // ; = , - . / ` [ \ ] '
        or >= 0x60 and <= 0x6F;                         // number pad

    // ----- text form -----

    public static KeyCombo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        bool ctrl = false, alt = false, shift = false, win = false; var keys = new List<int>();
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; continue;
                case "alt": alt = true; continue;
                case "shift": shift = true; continue;
                case "win" or "windows": win = true; continue;
            }
            int vk = VkFromName(raw);
            if (vk == 0) return null;
            keys.Add(vk);
        }
        var c = new KeyCombo { Ctrl = ctrl, Alt = alt, Shift = shift, Win = win, Keys = keys.Distinct().OrderBy(v => v).Take(2).ToArray() };
        return c.IsEmpty ? null : c;
    }

    /// <summary>Settings form, e.g. "Ctrl+Win" or "Ctrl+Alt+Space".</summary>
    public string Serialize() => string.Join("+", Parts());

    /// <summary>Display form, e.g. "Ctrl + Win".</summary>
    public override string ToString() => string.Join(" + ", Parts());

    IEnumerable<string> Parts()
    {
        if (Ctrl) yield return "Ctrl";
        if (Alt) yield return "Alt";
        if (Shift) yield return "Shift";
        if (Win) yield return "Win";
        foreach (var k in Keys) yield return NameOf(k);
    }

    static readonly Dictionary<int, string> Friendly = new()
    {
        [VK_SPACE] = "Space", [0x0D] = "Enter", [0x09] = "Tab", [0x08] = "Backspace", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home", [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
        [0x13] = "Pause", [0x91] = "ScrollLock", [0x14] = "CapsLock", [0x90] = "NumLock", [0x2C] = "PrintScreen", [0x5D] = "Menu",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/", [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
    };

    public static string NameOf(int vk)
    {
        if (Friendly.TryGetValue(vk, out var f)) return f;
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        if (vk is >= 0x60 and <= 0x69) return "Num" + (vk - 0x60);
        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key == System.Windows.Input.Key.None ? "Key" + vk : key.ToString();
    }

    public static int VkFromName(string name)
    {
        foreach (var (vk, n) in Friendly) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return vk;
        if (name.Length == 1 && char.IsLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        if (name.Length >= 2 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out var fn) && fn is >= 1 and <= 24) return 0x6F + fn;
        if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[3..], out var nd) && nd is >= 0 and <= 9) return 0x60 + nd;
        if (name.StartsWith("Key", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[3..], out var raw) && raw is > 0 and < 256) return raw;
        if (Enum.TryParse<System.Windows.Input.Key>(name, true, out var key)) { int v = KeyInterop.VirtualKeyFromKey(key); if (v > 0) return v; }
        return 0;
    }

    public bool Equals(KeyCombo? o) => o != null && o.Ctrl == Ctrl && o.Alt == Alt && o.Shift == Shift && o.Win == Win && o.Keys.SequenceEqual(Keys);
    public override bool Equals(object? obj) => Equals(obj as KeyCombo);
    public override int GetHashCode() => Serialize().GetHashCode();
}
