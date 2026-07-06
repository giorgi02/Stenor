using System.Text;
using Stenor.Interop;
using Stenor.Models;

namespace Stenor.UI;

/// <summary>Human-readable names for hotkeys. Lives in the App project because the fallback
/// asks the Windows keyboard layout (GetKeyNameText) for names of uncommon keys.</summary>
public static class HotkeyDisplay
{
    public static string Describe(HotkeySpec spec)
    {
        var sb = new StringBuilder();
        if (spec.Ctrl) sb.Append("Ctrl + ");
        if (spec.Shift) sb.Append("Shift + ");
        if (spec.Alt) sb.Append("Alt + ");
        if (spec.Win) sb.Append("Win + ");
        sb.Append(KeyName(spec.VirtualKey));
        return sb.ToString();
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
        _ => KeyNameFromLayout(vk),
    };

    /// <summary>Asks the keyboard layout for the key's display name (GetKeyNameText). The
    /// extended-key bit distinguishes e.g. the arrow keys from their numpad twins.</summary>
    private static string KeyNameFromLayout(int vk)
    {
        var scan = NativeMethods.MapVirtualKeyW((uint)vk, NativeMethods.MAPVK_VK_TO_VSC);
        if (scan == 0)
        {
            return $"Key 0x{vk:X2}";
        }

        var lParam = (int)(scan << 16);
        var isExtended = vk is (>= 0x21 and <= 0x28) or 0x2C or 0x2D or 0x2E or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5;
        if (isExtended)
        {
            lParam |= 1 << 24;
        }

        var buffer = new char[64];
        var length = NativeMethods.GetKeyNameTextW(lParam, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : $"Key 0x{vk:X2}";
    }
}
