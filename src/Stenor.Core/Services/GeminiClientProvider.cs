using Google.GenAI;

namespace Stenor.Services;

/// <summary>
/// Caches a single Google.GenAI <see cref="Client"/> keyed on the current API key, shared by
/// the batch and live transcription services. Invalidated whenever settings change so a new
/// key takes effect immediately. The API key itself is never logged.
/// </summary>
public sealed class GeminiClientProvider : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly object _sync = new();
    private Client? _client;
    private string? _clientKey;

    public GeminiClientProvider(SettingsStore settings)
    {
        _settings = settings;
        _settings.Changed += InvalidateClient;
    }

    /// <summary>Returns the cached client, or null when no API key is configured.</summary>
    public Client? GetClient()
    {
        var key = _settings.GetApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        lock (_sync)
        {
            if (_client is null || _clientKey != key)
            {
                // The replaced client is dropped, never disposed: a transcription may still be
                // mid-request on it, and disposing the underlying HttpClient aborts that call.
                // The GC reclaims it once the in-flight request completes.
                _client = new Client(apiKey: key);
                _clientKey = key;
            }
            return _client;
        }
    }

    private void InvalidateClient()
    {
        lock (_sync)
        {
            _client = null; // dropped, not disposed - see GetClient
            _clientKey = null;
        }
    }

    public void Dispose()
    {
        _settings.Changed -= InvalidateClient;
        lock (_sync)
        {
            _client?.Dispose(); // process shutdown: no request left to abort
            _client = null;
            _clientKey = null;
        }
    }
}
