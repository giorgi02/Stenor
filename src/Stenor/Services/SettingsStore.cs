using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stenor.Models;

namespace Stenor.Services;

/// <summary>
/// Persists settings as plaintext JSON at %APPDATA%\Stenor\settings.json, with the API key
/// encrypted via DPAPI (CurrentUser scope). The decrypted key never touches disk or logs.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Logger _log;
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _file;

    public event Action? Changed;

    public SettingsStore(Logger log)
    {
        _log = log;
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stenor");
        _file = Path.Combine(_directory, "settings.json");
        Current = new AppSettings();
    }

    public AppSettings Current { get; private set; }

    public void Load()
    {
        lock (_sync)
        {
            try
            {
                if (File.Exists(_file))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_file), JsonOptions);
                    if (loaded is not null)
                    {
                        Current = loaded;
                        _log.Info("Settings loaded.");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("Failed to load settings; using defaults.", ex);
            }
            Current = new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                File.WriteAllText(_file, JsonSerializer.Serialize(settings, JsonOptions));
                Current = settings;
                _log.Info("Settings saved.");
            }
            catch (Exception ex)
            {
                _log.Error("Failed to save settings.", ex);
                throw;
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Returns the decrypted API key, or null when unset or undecryptable.</summary>
    public string? GetApiKey()
    {
        var encrypted = Current.ApiKeyEncrypted;
        if (string.IsNullOrEmpty(encrypted))
        {
            return null;
        }
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to decrypt API key (different user profile?).", ex);
            return null;
        }
    }

    public static string ProtectApiKey(string apiKey)
    {
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }
}
