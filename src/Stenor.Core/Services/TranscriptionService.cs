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
public sealed class TranscriptionService
{
    public const string Model = "gemini-3.1-flash-lite";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly Lazy<string> PromptTemplate = new(LoadPromptTemplate);

    private readonly Logger _log;
    private readonly GeminiClientProvider _clients;

    public TranscriptionService(Logger log, GeminiClientProvider clients)
    {
        _log = log;
        _clients = clients;
    }

    /// <summary>Thrown for failures with a user-presentable message.</summary>
    public sealed class TranscriptionException(string userMessage, Exception? inner = null)
        : Exception(userMessage, inner);

    public async Task<string> TranscribeAsync(byte[] wav, IReadOnlyList<string> spokenLanguages, CancellationToken ct)
    {
        var content = new Content
        {
            Role = "user",
            Parts =
            [
                new Part { Text = BuildPrompt(spokenLanguages) },
                new Part { InlineData = new Blob { MimeType = "audio/wav", Data = wav } },
            ],
        };
        var config = new GenerateContentConfig { Temperature = 0.2f };

        for (var attempt = 1; ; attempt++)
        {
            // Fetched per attempt: a settings save mid-request invalidates the cached client,
            // so the retry must not reuse the stale instance.
            var client = _clients.GetClient()
                ?? throw new TranscriptionException("No API key configured. Open Settings to add one.");
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

    private static string BuildPrompt(IReadOnlyList<string> spokenLanguages)
    {
        var languageHint = spokenLanguages.Count switch
        {
            0 => "Detect the spoken language automatically.",
            1 => $"The speaker most likely speaks {spokenLanguages[0]}. The audio may also contain other languages - transcribe whatever language is actually spoken.",
            _ => $"The speaker's languages are: {string.Join(", ", spokenLanguages)}. First determine which one of them is actually spoken in this audio, then transcribe in that language, written in its own native script. Never translate or transliterate the speech into any other language from the list. If the speech is in a language outside the list, transcribe that language instead.",
        };
        return PromptTemplate.Value.Replace("{languageHint}", languageHint);
    }

    /// <summary>Loads Prompts/TranscriptionPrompt.md, embedded in the assembly so the
    /// single-exe publish carries it with no loose files.</summary>
    private static string LoadPromptTemplate()
    {
        using var stream = typeof(TranscriptionService).Assembly
            .GetManifestResourceStream("Stenor.Prompts.TranscriptionPrompt.md")
            ?? throw new InvalidOperationException("Embedded resource 'Stenor.Prompts.TranscriptionPrompt.md' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
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
}
