using Microsoft.Win32;

namespace Stenor.Services;

/// <summary>Manages the "launch at Windows startup" HKCU Run entry.</summary>
public sealed class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Stenor";

    private readonly Logger _log;

    public StartupManager(Logger log) => _log = log;

    public void Apply(bool launchAtStartup)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (launchAtStartup)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    key.SetValue(ValueName, $"\"{exe}\"");
                }
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Failed to update startup registry entry.", ex);
        }
    }
}
