using System.Globalization;

namespace InterviewAssist.Desktop.Platform;

/// <summary>Hotkey modifiers. The values are the Win32 RegisterHotKey MOD_* flags.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>A parsed hotkey: modifiers plus one key, identified by its Win32 virtual-key code.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey);

/// <summary>
/// The hotkey grammar shared with the MAUI app's WindowsWindowService.TryParseHotkey, plus mappings of the parsed
/// result to Windows (MOD_* / VK_*) and X11 (modifier masks / keysym names).
/// </summary>
public static class HotkeyParser
{
    public const uint VkF1 = 0x70;
    public const uint VkF24 = 0x87;

    private const uint VkEnter = 0x0D;

    private static readonly Dictionary<string, HotkeyModifiers> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = HotkeyModifiers.Control,
        ["Control"] = HotkeyModifiers.Control,
        ["Alt"] = HotkeyModifiers.Alt,
        ["Shift"] = HotkeyModifiers.Shift,
        ["Win"] = HotkeyModifiers.Win,
        ["Windows"] = HotkeyModifiers.Win,
    };

    private static readonly Dictionary<string, uint> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = VkEnter,
        ["Return"] = VkEnter,
        ["Space"] = 0x20,
        ["Tab"] = 0x09,
        ["Esc"] = 0x1B,
        ["Escape"] = 0x1B,
        ["Backspace"] = 0x08,
        ["Insert"] = 0x2D,
        ["Ins"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Del"] = 0x2E,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PgUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["PgDn"] = 0x22,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["Pause"] = 0x13,
    };

    // Virtual key -> X11 keysym names (X11/keysymdef.h, without the XK_ prefix) for the non-alphanumeric keys.
    // Numpad Enter is grabbed along with Return because Windows reports both as VK_RETURN.
    private static readonly Dictionary<uint, string[]> X11KeysymNames = new()
    {
        [VkEnter] = ["Return", "KP_Enter"],
        [0x20] = ["space"],
        [0x09] = ["Tab"],
        [0x1B] = ["Escape"],
        [0x08] = ["BackSpace"],
        [0x2D] = ["Insert"],
        [0x2E] = ["Delete"],
        [0x24] = ["Home"],
        [0x23] = ["End"],
        [0x21] = ["Prior"],
        [0x22] = ["Next"],
        [0x25] = ["Left"],
        [0x26] = ["Up"],
        [0x27] = ["Right"],
        [0x28] = ["Down"],
        [0x13] = ["Pause"],
    };

    /// <summary>True for empty text or "None" (the hotkey is switched off).</summary>
    public static bool IsNone(string? text) =>
        string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), "None", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses hotkey text such as "Ctrl+Shift+Enter", "alt + q" or "F9". Case-insensitive; accepts Ctrl/Control,
    /// Alt, Shift, Win/Windows and exactly one key: Enter/Return, Space, Tab, Esc/Escape, Backspace, Insert, Delete,
    /// Home, End, PageUp, PageDown, arrow keys, Pause, F1–F24, A–Z, 0–9.
    /// Returns false (and a default hotkey) for "None", empty text, or anything unrecognised.
    /// </summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;

        if (text is null || IsNone(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        uint key = 0;

        foreach (var rawPart in text.Split('+'))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
            {
                return false;
            }

            if (ModifierNames.TryGetValue(part, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            // Exactly one non-modifier key is allowed.
            if (key != 0 || !TryParseKey(part, out key))
            {
                return false;
            }
        }

        if (key == 0)
        {
            return false;
        }

        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    /// <summary>
    /// A global hotkey without Ctrl/Alt/Win would steal an everyday key (Enter, Shift+A, ...) from every app,
    /// so only those combinations and the F-keys are allowed.
    /// </summary>
    public static bool IsSafeGlobal(Hotkey hotkey) =>
        (hotkey.Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0
        || hotkey.VirtualKey is >= VkF1 and <= VkF24;

    /// <summary>RegisterHotKey fsModifiers (MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN).</summary>
    public static uint ToWindowsModifiers(HotkeyModifiers modifiers) => (uint)modifiers;

    /// <summary>The X11 modifier mask (X11/X.h): Ctrl = ControlMask, Alt = Mod1Mask, Shift = ShiftMask, Win = Mod4Mask (Super).</summary>
    public static uint ToX11Modifiers(HotkeyModifiers modifiers)
    {
        uint mask = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            mask |= X11Native.ShiftMask;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            mask |= X11Native.ControlMask;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            mask |= X11Native.Mod1Mask;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Win))
        {
            mask |= X11Native.Mod4Mask;
        }

        return mask;
    }

    /// <summary>
    /// The X11 keysym names (for XStringToKeysym) of a virtual key produced by <see cref="TryParse"/>; the first is
    /// the main key. Letters map to their lower-case keysym (the unshifted level of the key). Empty when unknown.
    /// </summary>
    public static IReadOnlyList<string> GetX11KeysymNames(uint virtualKey)
    {
        if (X11KeysymNames.TryGetValue(virtualKey, out var names))
        {
            return names;
        }

        if (virtualKey is >= 'A' and <= 'Z')
        {
            return [char.ToLowerInvariant((char)virtualKey).ToString()];
        }

        if (virtualKey is >= '0' and <= '9')
        {
            return [((char)virtualKey).ToString()];
        }

        if (virtualKey is >= VkF1 and <= VkF24)
        {
            return ["F" + (virtualKey - VkF1 + 1).ToString(CultureInfo.InvariantCulture)];
        }

        return [];
    }

    private static bool TryParseKey(string name, out uint vk)
    {
        if (KeyNames.TryGetValue(name, out vk))
        {
            return true;
        }

        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
        }

        if (name.Length is 2 or 3
            && (name[0] == 'F' || name[0] == 'f')
            && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 24)
        {
            vk = VkF1 + (uint)(number - 1);
            return true;
        }

        vk = 0;
        return false;
    }
}
