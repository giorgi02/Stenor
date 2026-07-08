using System.IO;
using Microsoft.Win32;

namespace Stenor.Services;

/// <summary>
/// Rewrites the Velopack uninstall entry's EstimatedSize so Apps &amp; Features shows a size.
/// Velopack (1.2.0) writes it as REG_QWORD, which Windows ignores - the "Size" column stays
/// blank - and never refreshes it after updates. We recompute the install footprint on every
/// startup and store it as the REG_DWORD (size in KB) Windows expects.
/// </summary>
public sealed class UninstallSizeUpdater
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Stenor";

    private readonly Logger _log;

    public UninstallSizeUpdater(Logger log) => _log = log;

    public void Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, writable: true);
            if (key is null)
            {
                return; // running unpackaged (dev build)
            }
            if (key.GetValue("InstallLocation") is not string root || !Directory.Exists(root))
            {
                return;
            }
            long bytes = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None,
            };
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                bytes += file.Length;
            }
            var sizeKb = (int)Math.Min(bytes / 1024, int.MaxValue);
            key.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
            _log.Info("Uninstall size entry refreshed.");
        }
        catch (Exception ex)
        {
            _log.Warn("Failed to refresh uninstall size entry.", ex);
        }
    }
}
