using Stenor.Constants;

namespace Stenor.Models;

/// <summary>
/// A hotkey: either a single key (including left/right-specific modifiers such as Right Ctrl)
/// or a generic-modifier combo (e.g. Ctrl+Shift+D). For combos, <see cref="VirtualKey"/> is the
/// non-modifier main key and the bool flags name the required modifiers.
/// </summary>
public sealed class HotkeySpec
{
    public const int DefaultVirtualKey = VirtualKeys.RightControl;

    public int VirtualKey { get; set; } = DefaultVirtualKey;
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public bool Win { get; set; }

    public static HotkeySpec Default => new() { VirtualKey = DefaultVirtualKey };

    public static bool IsModifierKey(int vk) => vk is
        VirtualKeys.LeftShift or VirtualKeys.RightShift or
        VirtualKeys.LeftControl or VirtualKeys.RightControl or
        VirtualKeys.LeftAlt or VirtualKeys.RightAlt or
        VirtualKeys.LeftWin or VirtualKeys.RightWin or
        VirtualKeys.Shift or VirtualKeys.Control or VirtualKeys.Alt;

    public HotkeySpec Clone() => new()
    {
        VirtualKey = VirtualKey,
        Ctrl = Ctrl,
        Shift = Shift,
        Alt = Alt,
        Win = Win,
    };
}
