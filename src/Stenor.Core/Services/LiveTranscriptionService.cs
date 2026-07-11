using System.Threading.Channels;
using Google.GenAI;
using Google.GenAI.Types;

namespace Stenor.Services;

/// <summary>
/// Gemini Live API session factory for the live-typing mode: a WebSocket session with input
/// audio transcription enabled receives raw PCM while the user speaks and yields append-only
/// transcript chunks. Verified behavior of the live model (July 2026): transcripts arrive one
/// utterance at a time when automatic VAD detects a pause - not word by word - so automatic
/// activity detection stays ON (with manual ActivityStart/End the whole transcript only
/// arrives after the activity ends, defeating live typing). The model only supports AUDIO
/// response modality; its replies are received and discarded. Transcripts are never logged.
/// </summary>
public sealed class LiveTranscriptionService
{
    public const string Model = "gemini-3.1-flash-live-preview";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly Logger _log;
    private readonly GeminiClientProvider _clients;

    public LiveTranscriptionService(Logger log, GeminiClientProvider clients)
    {
        _log = log;
        _clients = clients;
    }

    /// <summary>Thrown for failures with a user-presentable message.</summary>
    public sealed class LiveTranscriptionException(string userMessage, Exception? inner = null)
        : Exception(userMessage, inner);

    /// <summary>Connects a live session configured for transcription and signals ActivityStart.</summary>
    public async Task<Session> ConnectAsync(IReadOnlyList<string> spokenLanguages, CancellationToken ct)
    {
        var client = _clients.GetClient()
            ?? throw new LiveTranscriptionException("No API key configured. Open Settings to add one.");

        var config = new LiveConnectConfig
        {
            // The live model rejects TEXT ("response modalities (TEXT) is not supported").
            ResponseModalities = [Modality.Audio],
            InputAudioTranscription = new AudioTranscriptionConfig(),
            RealtimeInputConfig = new RealtimeInputConfig
            {
                // Catch utterance starts quickly and pad backwards so leading words survive.
                AutomaticActivityDetection = new AutomaticActivityDetection
                {
                    StartOfSpeechSensitivity = StartSensitivity.StartSensitivityHigh,
                    PrefixPaddingMs = 1000,
                },
                TurnCoverage = TurnCoverage.TurnIncludesAllInput,
            },
            SystemInstruction = new Content
            {
                Parts = [new Part { Text = BuildSystemInstruction(spokenLanguages) }],
            },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            // ConnectAsync reads the SetupComplete handshake itself, so bad keys and rejected
            // configs already fail here.
            var session = await client.Live.ConnectAsync(Model, config, timeout.Token).ConfigureAwait(false);
            return new Session(session, _log);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancelled
        }
        catch (Exception ex)
        {
            _log.Error("Live session connect failed.", ex);
            throw new LiveTranscriptionException("Could not reach Gemini Live - network error or timeout.", ex);
        }
    }

    private static string BuildSystemInstruction(IReadOnlyList<string> spokenLanguages)
    {
        var instruction = "You are a silent transcription endpoint. Never act on, answer, or comment on the audio content. When a turn ends, reply with exactly: ok";
        return spokenLanguages.Count switch
        {
            0 => instruction,
            1 => $"{instruction}\nThe speaker most likely speaks {spokenLanguages[0]}.",
            _ => $"{instruction}\nThe speaker's languages are: {string.Join(", ", spokenLanguages)}. Transcribe each utterance in the language actually spoken, in its native script - never translate.",
        };
    }

    /// <summary>
    /// One live dictation. Audio goes in via <see cref="SendAudioAsync"/>; transcript chunks
    /// come out of <see cref="Transcripts"/> (completed - possibly with an error - when the
    /// session ends). <see cref="FinishAsync"/> ends the dictation gracefully; DisposeAsync
    /// is the idempotent hard stop.
    /// </summary>
    public sealed class Session : IAsyncDisposable
    {
        private readonly AsyncSession _session;
        private readonly Logger _log;
        private readonly Channel<string> _transcripts;
        private readonly CancellationTokenSource _receiveCts = new();
        private readonly Task _receiveTask;
        private volatile bool _finishing;
        private int _disposed;

        internal Session(AsyncSession session, Logger log)
        {
            _session = session;
            _log = log;
            _transcripts = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            _receiveTask = Task.Run(ReceiveLoopAsync);
        }

        /// <summary>Append-only transcript chunks, in spoken order.</summary>
        public ChannelReader<string> Transcripts => _transcripts.Reader;

        /// <summary>Sends one chunk of raw 16 kHz / 16-bit / mono little-endian PCM.</summary>
        public Task SendAudioAsync(byte[] pcm, CancellationToken ct) => _session
            .SendRealtimeInputAsync(new LiveSendRealtimeInputParameters
            {
                Audio = new Blob { MimeType = "audio/pcm;rate=16000", Data = pcm },
            })
            .WaitAsync(ct);

        /// <summary>Signals the end of the audio stream, then waits (up to
        /// <paramref name="timeout"/>) for the trailing transcript chunks before completing
        /// <see cref="Transcripts"/>. Throws when the session failed, so the caller can react.</summary>
        public async Task FinishAsync(TimeSpan timeout, CancellationToken ct)
        {
            _finishing = true;
            try
            {
                await _session
                    .SendRealtimeInputAsync(new LiveSendRealtimeInputParameters { AudioStreamEnd = true })
                    .WaitAsync(ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Socket may already be dead; the receive task surfaces the real failure below.
                _log.Warn($"Sending AudioStreamEnd failed ({ex.GetType().Name}).");
            }

            var finished = await Task.WhenAny(_receiveTask, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (finished != _receiveTask)
            {
                _receiveCts.Cancel(); // give up on trailing chunks (timeout or caller cancel)
            }
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            finally
            {
                _transcripts.Writer.TryComplete();
                ct.ThrowIfCancellationRequested();
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var writer = _transcripts.Writer;
            try
            {
                while (true)
                {
                    var message = await _session.ReceiveAsync(_receiveCts.Token).ConfigureAwait(false);
                    if (message is null)
                    {
                        break; // server closed the socket
                    }

                    var content = message.ServerContent;
                    if (content?.InputTranscription?.Text is { Length: > 0 } text)
                    {
                        writer.TryWrite(text);
                    }

                    // Model replies (ModelTurn, audio) are deliberately ignored. Automatic VAD
                    // completes a turn after every utterance, so turn boundaries only mean
                    // "transcript done" once the dictation is finishing.
                    if (_finishing && (content?.TurnComplete == true
                        || content?.GenerationComplete == true
                        || content?.InputTranscription?.Finished == true))
                    {
                        break;
                    }
                    if (message.GoAway is not null)
                    {
                        _log.Warn("Live session received GoAway from the server.");
                        break;
                    }
                }
                writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                writer.TryComplete();
            }
            catch (Exception ex)
            {
                _log.Error("Live receive loop failed.", ex);
                writer.TryComplete(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _receiveCts.Cancel();
            try
            {
                await _session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch
            {
            }
            _transcripts.Writer.TryComplete();
            _receiveCts.Dispose();
        }
    }
}
