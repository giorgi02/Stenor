using System.Text.Json.Serialization;

namespace Stenor.Models;

public enum ActivationMode
{
    Hold,
    Toggle,
}

public sealed class AppSettings
{
    /// <summary>DPAPI-protected (CurrentUser) API key, base64. Never stored or logged in plaintext.</summary>
    public string? ApiKeyEncrypted { get; set; }

    public string PrimaryLanguage { get; set; } = "English";

    public HotkeySpec Hotkey { get; set; } = HotkeySpec.Default;

    [JsonConverter(typeof(JsonStringEnumConverter<ActivationMode>))]
    public ActivationMode ActivationMode { get; set; } = ActivationMode.Hold;

    public bool LaunchAtStartup { get; set; } = true;

    /// <summary>Off by default: per-character SendInput typing for apps where Ctrl+V paste is blocked.</summary>
    public bool UseUnicodeTypingFallback { get; set; }

    /// <summary>Off by default: stream audio to Gemini Live and type text while speaking,
    /// instead of transcribing the whole recording after the hotkey is released.</summary>
    public bool LiveTyping { get; set; }

    /// <summary>Optional Velopack feed override (hand-edit only). Empty means the default
    /// GitHub Releases feed built into the app.</summary>
    public string? UpdateFeedUrl { get; set; }

    public AppSettings Clone() => new()
    {
        ApiKeyEncrypted = ApiKeyEncrypted,
        PrimaryLanguage = PrimaryLanguage,
        Hotkey = Hotkey.Clone(),
        ActivationMode = ActivationMode,
        LaunchAtStartup = LaunchAtStartup,
        UseUnicodeTypingFallback = UseUnicodeTypingFallback,
        LiveTyping = LiveTyping,
        UpdateFeedUrl = UpdateFeedUrl,
    };
}
