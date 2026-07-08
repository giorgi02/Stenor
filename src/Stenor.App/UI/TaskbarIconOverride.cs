using System.IO;
using System.Windows;
using System.Windows.Interop;
using Stenor.Interop;
using Stenor.Services;

namespace Stenor.UI;

/// <summary>
/// Swaps a window's big icon (taskbar button, Alt-Tab) for the light-background logo variant so
/// the dark logo stays visible on a dark taskbar. The caption keeps the standard small icon.
/// </summary>
internal static class TaskbarIconOverride
{
    private static readonly Uri LightIconUri = new("pack://application:,,,/Assets/StenorTaskbar.ico");

    /// <summary>Applies the override; returns the HICON to destroy after the window closes, or 0.</summary>
    public static nint Apply(Window window, Logger log)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXICON);
            if (size <= 0)
            {
                size = 32;
            }
            var hIcon = CreateIconFromResource(size);
            if (hIcon != 0)
            {
                NativeMethods.SendMessageW(hwnd, NativeMethods.WM_SETICON, NativeMethods.ICON_BIG, hIcon);
            }
            return hIcon;
        }
        catch (Exception ex)
        {
            log.Warn("Taskbar icon override failed.", ex);
            return 0;
        }
    }

    /// <summary>Picks the frame closest to the desired size and turns it into an HICON.</summary>
    private static nint CreateIconFromResource(int desiredSize)
    {
        var resource = Application.GetResourceStream(LightIconUri);
        if (resource is null)
        {
            return 0;
        }

        using var stream = resource.Stream;
        using var reader = new BinaryReader(stream);
        reader.ReadUInt16();                 // reserved
        reader.ReadUInt16();                 // type
        int count = reader.ReadUInt16();

        var bestSide = 0;
        var bestLength = 0;
        var bestOffset = 0L;
        for (var i = 0; i < count; i++)
        {
            int side = reader.ReadByte();    // 0 means 256
            reader.ReadBytes(7);             // height, palette, reserved, planes, bpp
            var length = (int)reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            if (side == 0)
            {
                side = 256;
            }
            if (bestSide == 0 || Math.Abs(side - desiredSize) < Math.Abs(bestSide - desiredSize))
            {
                bestSide = side;
                bestLength = length;
                bestOffset = offset;
            }
        }
        if (bestLength == 0)
        {
            return 0;
        }

        stream.Position = bestOffset;
        var payload = reader.ReadBytes(bestLength);
        return NativeMethods.CreateIconFromResourceEx(payload, (uint)payload.Length, fIcon: true,
            NativeMethods.ICON_VERSION_3, desiredSize, desiredSize, 0);
    }
}
