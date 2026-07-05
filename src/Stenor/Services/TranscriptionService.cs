using System.IO;
using System.Net.Http;
using Google.GenAI;
using Google.GenAI.Types;

namespace Stenor.Services;

/// <summary>
/// Gemini transcription via the official Google.GenAI SDK. Sends the WAV inline with a strict
/// transcription prompt. 30 s timeout, one automatic retry on transient (5xx / network /
/// timeout) failures. The API key and transcripts are never logged.
/// </summary>
public sealed class TranscriptionService : IDisposable
{
    public const string Model = "gemini-3.1-flash-lite";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly Logger _log;
    private readonly SettingsStore _settings;
    private readonly object _sync = new();
    private Client? _client;
    private string? _clientKey;

    public TranscriptionService(Logger log, SettingsStore settings)
    {
        _log = log;
        _settings = settings;
        _settings.Changed += InvalidateClient;
    }

    /// <summary>Thrown for failures with a user-presentable message.</summary>
    public sealed class TranscriptionException(string userMessage, Exception? inner = null)
        : Exception(userMessage, inner);

    public async Task<string> TranscribeAsync(byte[] wav, string primaryLanguage, CancellationToken ct)
    {
        var client = GetClient() ?? throw new TranscriptionException("No API key configured. Open Settings to add one.");

        var content = new Content
        {
            Role = "user",
            Parts =
            [
                new Part { Text = BuildPrompt(primaryLanguage) },
                new Part { InlineData = new Blob { MimeType = "audio/wav", Data = wav } },
            ],
        };
        var config = new GenerateContentConfig { Temperature = 0.2f };

        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                var response = await client.Models
                    .GenerateContentAsync(Model, content, config, timeout.Token)
                    .ConfigureAwait(false);
                return ExtractText(response);
            }
            catch (ClientError ex)
            {
                _log.Error($"Gemini rejected the request ({ex.GetType().Name}).", ex);
                throw new TranscriptionException("Gemini rejected the request - check your API key in Settings.", ex);
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
            {
                if (attempt >= 2)
                {
                    _log.Error("Transcription failed after retry.", ex);
                    throw new TranscriptionException("Transcription failed - network or service error.", ex);
                }
                _log.Warn($"Transient transcription failure (attempt {attempt}); retrying.", ex);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // user cancelled
            }
        }
    }

    /// <summary>Tiny text-only call used by the Settings "Test key" button.</summary>
    public async Task<(bool Ok, string Message)> TestKeyAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            using var client = new Client(apiKey: apiKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var config = new GenerateContentConfig { MaxOutputTokens = 5 };
            await client.Models.GenerateContentAsync(Model, "Say OK", config, timeout.Token).ConfigureAwait(false);
            return (true, "Key works.");
        }
        catch (ClientError)
        {
            return (false, "Key was rejected by Gemini.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, "Cancelled.");
        }
        catch (Exception ex)
        {
            _log.Warn("Test key call failed.", ex);
            return (false, "Could not reach Gemini (network error or timeout).");
        }
    }

    private static string BuildPrompt(string primaryLanguage)
    {
        var languageHint = primaryLanguage is "Other / Auto-detect"
            ? "Detect the spoken language automatically."
            : $"The speaker most likely speaks {primaryLanguage}. The audio may also contain other languages - transcribe whatever language is actually spoken.";
        return "Transcribe this audio verbatim into clean written text. " +
               "Remove filler words (um, uh, ეე). Add proper punctuation and capitalization. " +
               "Output ONLY the transcribed text, nothing else - no preamble, no quotes, no markdown. " +
               languageHint;
    }

    private static string ExtractText(GenerateContentResponse response)
    {
        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            // Defensive fallback: concatenate non-thought text parts of the first candidate.
            var parts = response.Candidates?.FirstOrDefault()?.Content?.Parts;
            if (parts is not null)
            {
                text = string.Concat(parts
                    .Where(p => p.Thought is not true && !string.IsNullOrEmpty(p.Text))
                    .Select(p => p.Text));
            }
        }
        return text?.Trim() ?? string.Empty;
    }

    private static bool IsTransient(Exception ex) => ex
        is ServerError
        or HttpRequestException
        or OperationCanceledException // request timeout (user cancellation is filtered by the caller)
        or IOException;

    private Client? GetClient()
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
                _client?.Dispose();
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
            _client?.Dispose();
            _client = null;
            _clientKey = null;
        }
    }

    public void Dispose()
    {
        _settings.Changed -= InvalidateClient;
        InvalidateClient();
    }
}
