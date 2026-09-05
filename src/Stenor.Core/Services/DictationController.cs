using System.Threading.Channels;
using Stenor.Constants;
using Stenor.Interfaces;
using Stenor.Models;

namespace Stenor.Services;

/// <summary>
/// The state machine at the heart of Stenor: Idle → Recording → Transcribing → Injecting → Idle.
///
/// Hotkey semantics are applied here: Hold mode starts on key-down and stops on key-up
/// (holds under 150 ms are treated as accidental taps and discarded); Toggle mode starts on a
/// press while Idle and stops on the next press. Activations while busy are ignored, which
/// also makes rapid double-activation safe. Every failure path ends back in Idle with the
/// overlay/tray informed - nothing here may ever throw out to the caller.
///
/// Live typing (opt-in setting) reuses the same states: while Recording, audio is streamed to
/// a Gemini Live session and transcript chunks are typed as they arrive; Transcribing covers
/// the short drain after the hotkey is released. The WAV keeps being recorded in parallel so
/// a live session that dies before typing anything falls back to batch transcription.
/// </summary>
public sealed class DictationController
{
    /// <summary>Hold-mode presses shorter than this are accidental taps and are discarded.</summary>
    public static readonly TimeSpan MinHoldDuration = TimeSpan.FromMilliseconds(150);
    private const int WavHeaderBytes = 44;
    private const int MinimumAudioDurationMs = 250;
    private const int MinimumWavBytes = WavHeaderBytes
        + PcmFormat.BytesPerSecond * MinimumAudioDurationMs / 1000;

    /// <summary>How long after the audio stream ends to wait for the trailing transcript
    /// chunks (measured ~0.5 s on the live model).</summary>
    private static readonly TimeSpan LiveDrainTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Outer bound on the whole live shutdown before the session is aborted.</summary>
    private static readonly TimeSpan LiveStopGuard = TimeSpan.FromSeconds(8);

    private enum State
    {
        Idle,
        Recording,
        Transcribing,
        Injecting,
    }

    private readonly Logger _log;
    private readonly SettingsStore _settings;
    private readonly IHotkeyService _hotkeyService;
    private readonly IRecorderService _recorderService;
    private readonly TranscriptionService _transcriptionService;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<LiveTranscriptionService.Session>> _connectLive;
    private readonly ITextInjector _textInjector;
    private readonly IDictationOverlay _overlay;
    private readonly ITrayNotifier _trayNotifier;

    private readonly object _stateLock = new();
    private State _state = State.Idle;
    private CancellationTokenSource? _transcriptionCancellation;
    private LiveCycle? _liveCycle; // written under _stateLock; read lock-free by the PCM tap

    /// <summary>Per-dictation live-typing state. Torn down by exactly one of: graceful stop,
    /// cancel/tap discard, recorder failure, or session failure (arbitrated by TryTransition).</summary>
    private sealed class LiveCycle
    {
        /// <summary>Raw PCM from the recorder tap, buffered from t=0 while the socket connects.</summary>
        public readonly Channel<byte[]> Pcm =
            Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        /// <summary>Single abort lever for connect, send, receive, drain, and typing.</summary>
        public readonly CancellationTokenSource Cts = new();

        public Task? Pipeline;
        public Task? Injector; // final once Pipeline completes; awaited before reading AnyTextInjected
        public volatile bool AnyTextInjected;
        public volatile bool Failed;
        public string? InjectionFailure;
    }

    /// <summary>Raised when recording has actually started (recorder running, overlay shown).</summary>
    public event Action? DictationStarted;

    /// <summary>Raised when the recording has been accepted and handed to Gemini (batch or
    /// live drain); the paste follows unless <see cref="DictationFailed"/> fires.</summary>
    public event Action? TranscriptionStarted;

    /// <summary>Raised with the user-facing message whenever a dictation ends in an error
    /// shown on the overlay (no speech, rejected key, microphone gone, ...). Lets the setup
    /// wizard report the reason next to its checklist. Raised before <see cref="DictationCompleted"/>.</summary>
    public event Action<string>? DictationFailed;

    /// <summary>Raised when a dictation cycle has fully finished (success, failure, or
    /// cancellation) and the machine is back in Idle. Not raised for batch tap discards or
    /// batch recorder failures - those paths allocate almost nothing - but always raised for
    /// live cycles, which open a session even on a tap.</summary>
    public event Action? DictationCompleted;

    public DictationController(
        Logger log,
        SettingsStore settings,
        IHotkeyService hotkeys,
        IRecorderService recorder,
        TranscriptionService transcription,
        LiveTranscriptionService live,
        ITextInjector injection,
        IDictationOverlay overlay,
        ITrayNotifier tray)
        : this(log, settings, hotkeys, recorder, transcription, live.ConnectAsync, injection, overlay, tray) { }

    internal DictationController(
        Logger log,
        SettingsStore settings,
        IHotkeyService hotkeys,
        IRecorderService recorder,
        TranscriptionService transcription,
        Func<IReadOnlyList<string>, CancellationToken, Task<LiveTranscriptionService.Session>> connectLive,
        ITextInjector injection,
        IDictationOverlay overlay,
        ITrayNotifier tray)
    {
        _log = log;
        _settings = settings;
        _hotkeyService = hotkeys;
        _recorderService = recorder;
        _transcriptionService = transcription;
        _connectLive = connectLive;
        _textInjector = injection;
        _overlay = overlay;
        _trayNotifier = tray;
    }

    public void Initialize()
    {
        _hotkeyService.Pressed += OnHotkeyPressed;
        _hotkeyService.Released += OnHotkeyReleased;
        _overlay.CancelRequested += Cancel;
        _recorderService.MaxDurationReached += OnMaxDuration;
        _recorderService.Failed += OnRecorderFailed;
    }

    // ---------------------------------------------------------- hotkey input

    private void OnHotkeyPressed()
    {
        try
        {
            if (_settings.Current.ActivationMode == ActivationMode.Hold)
            {
                StartRecording();
            }
            else
            {
                switch (CurrentState())
                {
                    case State.Idle:
                        StartRecording();
                        break;
                    case State.Recording:
                        _ = StopAndProcessDictationAsync();
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Hotkey press handling failed.", ex);
        }
    }

    private void OnHotkeyReleased(TimeSpan held)
    {
        try
        {
            if (_settings.Current.ActivationMode != ActivationMode.Hold)
            {
                return;
            }
            if (held < MinHoldDuration)
            {
                CancelRecording(); // accidental tap - discard
            }
            else
            {
                _ = StopAndProcessDictationAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Error("Hotkey release handling failed.", ex);
        }
    }

    private void OnMaxDuration()
    {
        _log.Info("Max recording duration reached; auto-stopping.");
        _ = StopAndProcessDictationAsync();
    }

    private void OnRecorderFailed(string message)
    {
        if (!TryTransition(State.Recording, State.Idle))
        {
            return;
        }
        var hadLiveCycle = AbandonLiveCycle();
        ReportError(message);
        _trayNotifier.ShowError("Stenor", message);
        if (hadLiveCycle)
        {
            RaiseSafely(DictationCompleted, nameof(DictationCompleted));
        }
    }

    // ------------------------------------------------------------ transitions

    private void StartRecording()
    {
        if (!TryTransition(State.Idle, State.Recording))
        {
            return;
        }

        if (_settings.GetApiKey() is null)
        {
            SetState(State.Idle);
            ReportError("Add your Gemini API key in Settings.");
            _trayNotifier.ShowError(
                "Stenor", "No API key configured. Open Settings from the tray icon.");
            return;
        }

        if (_settings.Current.LiveTyping)
        {
            var cycle = new LiveCycle();
            lock (_stateLock)
            {
                _liveCycle = cycle;
            }
            _recorderService.PcmChunkAvailable += OnPcmChunk; // before Start so no chunk is missed
            cycle.Pipeline = Task.Run(() => RunLivePipelineAsync(cycle));
        }

        try
        {
            _recorderService.Start();
            _overlay.ShowRecording(() => _recorderService.CurrentLevel);
            RaiseSafely(DictationStarted, nameof(DictationStarted));
        }
        catch (Exception ex)
        {
            SetState(State.Idle);
            AbandonLiveCycle();
            _log.Error("Recording could not be started.", ex);
            ReportError("Microphone unavailable.");
            _trayNotifier.ShowError(
                "Stenor", "Could not start recording - check your microphone.");
        }
    }

    private void CancelRecording()
    {
        if (!TryTransition(State.Recording, State.Idle))
        {
            return;
        }
        _recorderService.Cancel();
        _overlay.Hide();
        if (AbandonLiveCycle())
        {
            // Unlike a batch tap discard, a live cancel opened a session - let the trim run.
            RaiseSafely(DictationCompleted, nameof(DictationCompleted));
        }
    }

    private async Task StopAndProcessDictationAsync()
    {
        if (!TryTransition(State.Recording, State.Transcribing))
        {
            return;
        }

        try
        {
            var wav = _recorderService.Stop(); // blocks until the last PCM chunk reached the tap
            var cycle = DetachLiveCycle();
            if (cycle is not null)
            {
                await FinishLiveDictationAsync(cycle, wav).ConfigureAwait(false);
            }
            else if (wav is null || wav.Length < MinimumWavBytes)
            {
                SetState(State.Idle);
                _overlay.Hide();
            }
            else
            {
                await TranscribeAndInjectAsync(wav).ConfigureAwait(false);
            }
        }
        catch (TextInjectionException ex)
        {
            _log.Warn("Text injection failed.", ex);
            ReportError(ex.Message);
            _trayNotifier.ShowError("Stenor", ex.Message);
        }
        catch (TranscriptionService.TranscriptionException ex)
        {
            _log.Error("Transcription failed.", ex);
            ReportError(ex.Message);
            _trayNotifier.ShowError("Stenor", ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error("Dictation pipeline failed.", ex);
            ReportError("Something went wrong - see the log.");
            _trayNotifier.ShowError(
                "Stenor", "Dictation failed unexpectedly. Details were logged.");
        }
        finally
        {
            lock (_stateLock)
            {
                _state = State.Idle;
                _transcriptionCancellation?.Dispose();
                _transcriptionCancellation = null;
            }
            RaiseSafely(DictationCompleted, nameof(DictationCompleted));
        }
    }

    /// <summary>The batch path: one Gemini call over the finished WAV, then a single injection.
    /// Also the fallback when a live session produced no text. Runs inside
    /// <see cref="StopAndProcessDictationAsync"/>'s try/finally.</summary>
    private async Task TranscribeAndInjectAsync(byte[] wav)
    {
        // Never upload silence: the model answers an empty recording with invented, fluent text.
        var speech = SpeechDetector.Analyze(wav);
        if (!speech.HasSpeech)
        {
            _log.Info($"Recording contains no speech; skipping transcription ({speech}).");
            SetState(State.Idle);
            ReportError("No speech detected.");
            return;
        }

        _overlay.ShowTranscribing();
        RaiseSafely(TranscriptionStarted, nameof(TranscriptionStarted));
        _log.Info($"Transcribing {wav.Length / 1024} KB of audio.");

        CancellationToken token;
        lock (_stateLock)
        {
            _transcriptionCancellation?.Dispose();
            _transcriptionCancellation = new CancellationTokenSource();
            token = _transcriptionCancellation.Token;
        }

        string text;
        try
        {
            text = await _transcriptionService
                .TranscribeAsync(wav, _settings.Current.SpokenLanguages, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetState(State.Idle);
            _overlay.Hide();
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            SetState(State.Idle);
            ReportError("No speech detected.");
            return;
        }

        SetState(State.Injecting);
        await _textInjector
            .InjectAsync(text, _settings.Current.UseUnicodeTypingFallback)
            .ConfigureAwait(false);

        _log.Info($"Injected {text.Length} characters.");
        _overlay.ShowDone();
    }

    /// <summary>Winds down a live cycle after the recorder stopped: lets the send pump drain,
    /// waits for the trailing transcript chunks, then picks the outcome - Done, batch fallback
    /// (nothing was typed), partial-kept error, or plain hide on user cancel.</summary>
    private async Task FinishLiveDictationAsync(LiveCycle cycle, byte[]? wav)
    {
        cycle.Pcm.Writer.TryComplete(); // send pump finishes the tail, then signals AudioStreamEnd
        _overlay.ShowTranscribing();
        RaiseSafely(TranscriptionStarted, nameof(TranscriptionStarted));
        lock (_stateLock)
        {
            _transcriptionCancellation?.Dispose();
            _transcriptionCancellation = cycle.Cts; // the overlay's X aborts the drain like a batch cancel
        }

        var pipeline = cycle.Pipeline ?? Task.CompletedTask;
        var guardTripped = false;
        var finished = await Task.WhenAny(pipeline, Task.Delay(LiveStopGuard)).ConfigureAwait(false);
        if (finished != pipeline)
        {
            guardTripped = true;
            cycle.Failed = true;
            _log.Warn("Live session did not wind down in time; aborting it.");
            cycle.Cts.Cancel();
        }
        try
        {
            await pipeline.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // RunLivePipelineAsync handles its own failures; this is a defensive backstop.
            cycle.Failed = true;
            _log.Error("Live pipeline ended unexpectedly.", ex);
        }
        if (cycle.Injector is { } injector)
        {
            // On failure paths the pipeline abandons the inject pump; let it finish typing the
            // buffered backlog so AnyTextInjected is final before the outcome is chosen.
            try
            {
                await injector.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (cycle.Cts.IsCancellationRequested && !guardTripped)
        {
            SetState(State.Idle); // user cancelled during the drain; typed text stays
            _overlay.Hide();
            return;
        }

        if (cycle.InjectionFailure is { } injectionFailure)
        {
            ReportError(injectionFailure);
            _trayNotifier.ShowError("Stenor", injectionFailure);
            return; // retrying transcription cannot fix a blocked input target
        }

        if (cycle.AnyTextInjected)
        {
            if (cycle.Failed)
            {
                const string message = "Live typing did not finish - the text typed so far was kept.";
                ReportError(message);
                _trayNotifier.ShowError("Stenor", message);
            }
            else
            {
                _log.Info("Live dictation completed.");
                _overlay.ShowDone();
            }
            return;
        }

        if (wav is null || wav.Length < MinimumWavBytes)
        {
            SetState(State.Idle);
            _overlay.Hide();
            return;
        }

        _log.Info("Live session yielded no text; falling back to batch transcription.");
        await TranscribeAndInjectAsync(wav).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ live typing

    /// <summary>Recorder tap (capture thread): forward the PCM chunk without blocking.
    /// Writes to a completed channel are dropped harmlessly.</summary>
    private void OnPcmChunk(byte[] pcm) => _liveCycle?.Pcm.Writer.TryWrite(pcm);

    /// <summary>One live session end to end: connect, pump PCM out, type transcript chunks as
    /// they arrive, drain on finish. Never throws - failures are flagged on the cycle, and a
    /// failure after text was already typed aborts the dictation outright (a batch retry would
    /// duplicate the typed prefix).</summary>
    private async Task RunLivePipelineAsync(LiveCycle cycle)
    {
        try
        {
            var session = await _connectLive(_settings.Current.SpokenLanguages, cycle.Cts.Token)
                .ConfigureAwait(false);
            await using (session.ConfigureAwait(false))
            {
                _log.Info("Live session connected.");
                cycle.Injector = Task.Run(() => RunLiveInjectionLoopAsync(session, cycle));
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cycle.Cts.Token);
                var send = SendLiveAudioAsync(session, cycle, sendCts.Token);
                try
                {
                    // Receive/injection can fail while the PCM sender is waiting for more audio.
                    var first = await Task.WhenAny(send, cycle.Injector).ConfigureAwait(false);
                    await first.ConfigureAwait(false);
                    await send.ConfigureAwait(false);
                    await cycle.Injector.ConfigureAwait(false);
                }
                finally
                {
                    sendCts.Cancel();
                    try { await send.ConfigureAwait(false); }
                    catch { } // the first failure is propagated after cleanup
                }
            }
        }
        catch (OperationCanceledException) when (cycle.Cts.IsCancellationRequested)
        {
            // Tap discard, overlay X, or the stop guard - the owning path reports the outcome.
        }
        catch (Exception ex)
        {
            cycle.Failed = true;
            cycle.Pcm.Writer.TryComplete(); // stop buffering; the WAV is the fallback source
            _log.Error("Live session failed.", ex);
            // Disposal completed the transcript channel. Finalize queued input before
            // deciding whether batch fallback would duplicate text.
            if (cycle.Injector is { } injector)
            {
                try { await injector.ConfigureAwait(false); }
                catch { }
            }
            if (cycle.AnyTextInjected || cycle.InjectionFailure is not null)
            {
                AbortLiveDictation(cycle);
            }
        }
    }

    private static async Task SendLiveAudioAsync(
        LiveTranscriptionService.Session session, LiveCycle cycle, CancellationToken ct)
    {
        await foreach (var pcm in cycle.Pcm.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await session.SendAudioAsync(pcm, ct).ConfigureAwait(false);
        }
        await session.FinishAsync(LiveDrainTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>Single consumer of the transcript chunks; keeps injections strictly in order.
    /// Chunks arrive one utterance at a time with no separating whitespace, so consecutive
    /// chunks are joined with a space. Chunk text is never logged.</summary>
    private async Task RunLiveInjectionLoopAsync(
        LiveTranscriptionService.Session session, LiveCycle cycle)
    {
        try
        {
            var lastChar = '\0';
            await foreach (var chunk in session.Transcripts.ReadAllAsync(cycle.Cts.Token).ConfigureAwait(false))
            {
                var text = lastChar != '\0' && !char.IsWhiteSpace(lastChar) && !char.IsWhiteSpace(chunk[0])
                    ? " " + chunk
                    : chunk;
                await _textInjector.InjectAsync(text, useUnicodeTyping: true).ConfigureAwait(false);
                cycle.AnyTextInjected = true;
                lastChar = chunk[^1];
            }
        }
        catch (TextInjectionException ex)
        {
            cycle.AnyTextInjected |= ex.MayHaveInjectedText;
            cycle.InjectionFailure = ex.Message;
            throw; // the pipeline reports the recovery instructions
        }
        catch (OperationCanceledException) when (cycle.Cts.IsCancellationRequested)
        {
            // Cancelled mid-dictation - stop typing immediately.
        }
    }

    /// <summary>Called from the pipeline when the session dies mid-recording after text was
    /// already typed: end the dictation now instead of letting the user talk into a dead
    /// session. No-op when a stop or cancel already owns the teardown.</summary>
    private void AbortLiveDictation(LiveCycle cycle)
    {
        lock (_stateLock)
        {
            if (_state != State.Recording || !ReferenceEquals(_liveCycle, cycle))
            {
                return;
            }
            _state = State.Transcribing; // keep new presses out until teardown is complete
        }
        AbandonLiveCycle(); // detach the tap first so a rapid re-press cannot double-subscribe
        _recorderService.Cancel();
        SetState(State.Idle);
        var message = cycle.InjectionFailure
            ?? "Live typing did not finish - the text typed so far was kept.";
        ReportError(message);
        _trayNotifier.ShowError("Stenor", message);
        RaiseSafely(DictationCompleted, nameof(DictationCompleted));
    }

    /// <summary>Removes the live cycle from the controller and unhooks the recorder tap.
    /// Exactly one caller gets the cycle; later callers get null.</summary>
    private LiveCycle? DetachLiveCycle()
    {
        LiveCycle? cycle;
        lock (_stateLock)
        {
            cycle = _liveCycle;
            _liveCycle = null;
        }
        if (cycle is not null)
        {
            _recorderService.PcmChunkAvailable -= OnPcmChunk;
        }
        return cycle;
    }

    /// <summary>Detaches and hard-stops the live cycle, if any: no more audio, session aborted,
    /// typing stopped. Cleanup is observed in the background. Returns whether a cycle existed.</summary>
    private bool AbandonLiveCycle()
    {
        var cycle = DetachLiveCycle();
        if (cycle is null)
        {
            return false;
        }
        cycle.Pcm.Writer.TryComplete();
        cycle.Cts.Cancel();
        _ = ObserveLiveCycleAsync(cycle);
        return true;
    }

    /// <summary>Awaits the abandoned pipeline and inject pump (which never throw) and then
    /// disposes the cycle's CTS.</summary>
    private async Task ObserveLiveCycleAsync(LiveCycle cycle)
    {
        if (cycle.Pipeline is { } pipeline)
        {
            try
            {
                await pipeline.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("Abandoned live pipeline ended unexpectedly.", ex);
            }
        }
        if (cycle.Injector is { } injector)
        {
            try
            {
                await injector.ConfigureAwait(false);
            }
            catch
            {
            }
        }
        cycle.Cts.Dispose();
    }

    /// <summary>Shows the error on the overlay and notifies <see cref="DictationFailed"/> listeners.</summary>
    private void ReportError(string message)
    {
        _overlay.ShowError(message);
        try
        {
            DictationFailed?.Invoke(message);
        }
        catch (Exception ex)
        {
            _log.Error($"{nameof(DictationFailed)} handler failed.", ex);
        }
    }

    private void RaiseSafely(Action? handler, string name)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error($"{name} handler failed.", ex);
        }
    }

    /// <summary>Cancel from the overlay's X: discards a recording or abandons a transcription.</summary>
    private void Cancel()
    {
        State state;
        lock (_stateLock)
        {
            state = _state;
        }
        switch (state)
        {
            case State.Recording:
                CancelRecording();
                break;
            case State.Transcribing:
                lock (_stateLock)
                {
                    _transcriptionCancellation?.Cancel();
                }
                break;
        }
    }

    private State CurrentState()
    {
        lock (_stateLock)
        {
            return _state;
        }
    }

    private void SetState(State state)
    {
        lock (_stateLock)
        {
            _state = state;
        }
    }

    /// <summary>Atomically moves <paramref name="from"/> → <paramref name="to"/>; false when
    /// the machine is in any other state (the trigger is then ignored).</summary>
    private bool TryTransition(State from, State to)
    {
        lock (_stateLock)
        {
            if (_state != from)
            {
                return false;
            }
            _state = to;
            return true;
        }
    }
}
