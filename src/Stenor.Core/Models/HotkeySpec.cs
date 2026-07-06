namespace Stenor.Models;

/// <summary>
/// A hotkey: either a single key (including left/right-specific modifiers such as Right Ctrl)
/// or a generic-modifier combo (e.g. Ctrl+Shift+D). For combos, <see cref="VirtualKey"/> is the
/// non-modifier main key and the bool flags name the required modifiers.
/// </summary>
public sealed class HotkeySpec
{
    public const int VkRControl = 0xA3;

    public int VirtualKey { get; set; } = VkRControl;
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public bool Win { get; set; }

    public static HotkeySpec Default => new() { VirtualKey = VkRControl };

    public bool HasModifiers => Ctrl || Shift || Alt || Win;

    /// <summary>True when the hotkey is a lone modifier key (e.g. Right Ctrl). These are never
    /// swallowed by the hook - the key passes through to the OS untouched.</summary>
    public bool IsBareModifier => !HasModifiers && IsModifierKey(VirtualKey);

    public static bool IsModifierKey(int vk) => vk is
        0xA0 or 0xA1 or // L/R Shift
        0xA2 or 0xA3 or // L/R Ctrl
        0xA4 or 0xA5 or // L/R Alt
        0x5B or 0x5C or // L/R Win
        0x10 or 0x11 or 0x12; // generic Shift/Ctrl/Alt

    public HotkeySpec Clone() => new()
    {
        VirtualKey = VirtualKey,
        Ctrl = Ctrl,
        Shift = Shift,
        Alt = Alt,
        Win = Win,
    };
}
