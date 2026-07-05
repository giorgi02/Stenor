using System.Text;

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

    public string DisplayString
    {
        get
        {
            var sb = new StringBuilder();
            if (Ctrl) sb.Append("Ctrl + ");
            if (Shift) sb.Append("Shift + ");
            if (Alt) sb.Append("Alt + ");
            if (Win) sb.Append("Win + ");
            sb.Append(KeyName(VirtualKey));
            return sb.ToString();
        }
    }

    public static string KeyName(int vk) => vk switch
    {
        0xA0 => "Left Shift",
        0xA1 => "Right Shift",
        0xA2 => "Left Ctrl",
        0xA3 => "Right Ctrl",
        0xA4 => "Left Alt",
        0xA5 => "Right Alt",
        0x5B => "Left Win",
        0x5C => "Right Win",
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x14 => "Caps Lock",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "Page Up",
        0x22 => "Page Down",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
        _ => KeyNameFromWpf(vk),
    };

    private static string KeyNameFromWpf(int vk)
    {
        try
        {
            var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk);
            return key == System.Windows.Input.Key.None ? $"Key 0x{vk:X2}" : key.ToString();
        }
        catch
        {
            return $"Key 0x{vk:X2}";
        }
    }

    public HotkeySpec Clone() => new()
    {
        VirtualKey = VirtualKey,
        Ctrl = Ctrl,
        Shift = Shift,
        Alt = Alt,
        Win = Win,
    };
}
