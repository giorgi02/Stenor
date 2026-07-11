using System.IO;
using System.Text.Json;
using Stenor.Interfaces;
using Stenor.Models;

namespace Stenor.Services;

/// <summary>
/// Persists settings as plaintext JSON at %APPDATA%\Stenor\settings.json, with the API key
/// encrypted via <see cref="ISecretProtector"/> (DPAPI in the Windows app). The decrypted key
/// never touches disk or logs.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Logger _log;
    private readonly ISecretProtector _protector;
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _file;

    public event Action? Changed;

    public SettingsStore(Logger log, ISecretProtector protector)
    {
        _log = log;
        _protector = protector;
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
                        MigrateLegacyLanguage(loaded);
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

    /// <summary>Settings written before 1.0.4 hold a single PrimaryLanguage string;
    /// "Other / Auto-detect" meant auto-detect, which is now the empty list.</summary>
    private static void MigrateLegacyLanguage(AppSettings settings)
    {
        if (settings.PrimaryLanguage is { Length: > 0 } legacy)
        {
            if (settings.SpokenLanguages.Count == 0 && legacy != "Other / Auto-detect")
            {
                settings.SpokenLanguages.Add(legacy);
            }
            settings.PrimaryLanguage = null;
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
            return _protector.Unprotect(encrypted);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to decrypt API key (different user profile?).", ex);
            return null;
        }
    }

    public string ProtectApiKey(string apiKey) => _protector.Protect(apiKey);
}
