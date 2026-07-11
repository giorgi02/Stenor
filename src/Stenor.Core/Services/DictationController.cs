using System.Threading.Channels;
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
    private static readonly TimeSpan MinHoldDuration = TimeSpan.FromMilliseconds(150);
    private const int MinWavBytes = 44 + 8000; // ~0.25 s of 16 kHz 16-bit audio

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
    private readonly IHotkeyService _hotkeys;
    private readonly IRecorderService _recorder;
    private readonly TranscriptionService _transcription;
    private readonly LiveTranscriptionService _live;
    private readonly ITextInjector _injection;
    private readonly IDictationOverlay _overlay;
    private readonly ITrayNotifier _tray;

    private readonly object _gate = new();
    private State _state = State.Idle;
    private CancellationTokenSource? _transcribeCts;
    private LiveCycle? _liveCycle; // written under _gate; read lock-free by the PCM tap

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
    }

    /// <summary>Raised when recording has actually started (recorder running, overlay shown).</summary>
    public event Action? DictationStarted;

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
    {
        _log = log;
        _settings = settings;
        _hotkeys = hotkeys;
        _recorder = recorder;
        _transcription = transcription;
        _live = live;
        _injection = injection;
        _overlay = overlay;
        _tray = tray;
    }

    public void Initialize()
    {
        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.Released += OnHotkeyReleased;
        _overlay.CancelRequested += Cancel;
        _recorder.MaxDurationReached += OnMaxDuration;
        _recorder.Failed += OnRecorderFailed;
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
                        _ = StopAndTranscribeAsync();
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
                _ = StopAndTranscribeAsync();
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
        _ = StopAndTranscribeAsync();
    }

    private void OnRecorderFailed(string message)
    {
        if (!TryTransition(State.Recording, State.Idle))
        {
            return;
        }
        var hadLiveCycle = AbandonLiveCycle();
        _overlay.ShowError(message);
        _tray.ShowError("Stenor", message);
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
            _overlay.ShowError("Add your Gemini API key in Settings.");
            _tray.ShowError("Stenor", "No API key configured. Open Settings from the tray icon.");
            return;
        }

        if (_settings.Current.LiveTyping)
        {
            var cycle = new LiveCycle();
            lock (_gate)
            {
                _liveCycle = cycle;
            }
            _recorder.PcmChunkAvailable += OnPcmChunk; // before Start so no chunk is missed
            cycle.Pipeline = Task.Run(() => RunLivePipelineAsync(cycle));
        }

        try
        {
            _recorder.Start();
            _overlay.ShowRecording(() => _recorder.CurrentLevel);
            RaiseSafely(DictationStarted, nameof(DictationStarted));
        }
        catch (Exception ex)
        {
            SetState(State.Idle);
            AbandonLiveCycle();
            _log.Error("Recording could not be started.", ex);
            _overlay.ShowError("Microphone unavailable.");
            _tray.ShowError("Stenor", "Could not start recording - check your microphone.");
        }
    }

    private void CancelRecording()
    {
        if (!TryTransition(State.Recording, State.Idle))
        {
            return;
        }
        _recorder.Cancel();
        _overlay.Hide();
        if (AbandonLiveCycle())
        {
            // Unlike a batch tap discard, a live cancel opened a session - let the trim run.
            RaiseSafely(DictationCompleted, nameof(DictationCompleted));
        }
    }

    private async Task StopAndTranscribeAsync()
    {
        if (!TryTransition(State.Recording, State.Transcribing))
        {
            return;
        }

        try
        {
            var wav = _recorder.Stop(); // blocks until the last PCM chunk reached the tap
            var cycle = DetachLiveCycle();
            if (cycle is not null)
            {
                await FinishLiveAsync(cycle, wav).ConfigureAwait(false);
            }
            else if (wav is null || wav.Length < MinWavBytes)
            {
                SetState(State.Idle);
                _overlay.Hide();
            }
            else
            {
                await TranscribeAndInjectAsync(wav).ConfigureAwait(false);
            }
        }
        catch (TranscriptionService.TranscriptionException ex)
        {
            _log.Error("Transcription failed.", ex);
            _overlay.ShowError(ex.Message);
            _tray.ShowError("Stenor", ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error("Dictation pipeline failed.", ex);
            _overlay.ShowError("Something went wrong - see the log.");
            _tray.ShowError("Stenor", "Dictation failed unexpectedly. Details were logged.");
        }
        finally
        {
            lock (_gate)
            {
                _state = State.Idle;
                _transcribeCts?.Dispose();
                _transcribeCts = null;
            }
            RaiseSafely(DictationCompleted, nameof(DictationCompleted));
        }
    }

    /// <summary>The batch path: one Gemini call over the finished WAV, then a single injection.
    /// Also the fallback when a live session produced no text. Runs inside
    /// <see cref="StopAndTranscribeAsync"/>'s try/finally.</summary>
    private async Task TranscribeAndInjectAsync(byte[] wav)
    {
        _overlay.ShowTranscribing();
        _log.Info($"Transcribing {wav.Length / 1024} KB of audio.");

        CancellationToken token;
        lock (_gate)
        {
            _transcribeCts?.Dispose();
            _transcribeCts = new CancellationTokenSource();
            token = _transcribeCts.Token;
        }

        string text;
        try
        {
            text = await _transcription
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
            _overlay.ShowError("No speech detected.");
            return;
        }

        SetState(State.Injecting);
        await _injection
            .InjectAsync(text, _settings.Current.UseUnicodeTypingFallback)
            .ConfigureAwait(false);

        _log.Info($"Injected {text.Length} characters.");
        _overlay.ShowDone();
    }

    /// <summary>Winds down a live cycle after the recorder stopped: lets the send pump drain,
    /// waits for the trailing transcript chunks, then picks the outcome - Done, batch fallback
    /// (nothing was typed), partial-kept error, or plain hide on user cancel.</summary>
    private async Task FinishLiveAsync(LiveCycle cycle, byte[]? wav)
    {
        cycle.Pcm.Writer.TryComplete(); // send pump finishes the tail, then signals AudioStreamEnd
        _overlay.ShowTranscribing();
        lock (_gate)
        {
            _transcribeCts?.Dispose();
            _transcribeCts = cycle.Cts; // the overlay's X aborts the drain like a batch cancel
        }

        var pipeline = cycle.Pipeline ?? Task.CompletedTask;
        var guardTripped = false;
        var finished = await Task.WhenAny(pipeline, Task.Delay(LiveStopGuard)).ConfigureAwait(false);
        if (finished != pipeline)
        {
            guardTripped = true;
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

        if (cycle.AnyTextInjected)
        {
            if (cycle.Failed)
            {
                const string message = "Live typing lost the connection - the text typed so far was kept.";
                _overlay.ShowError(message);
                _tray.ShowError("Stenor", message);
            }
            else
            {
                _log.Info("Live dictation completed.");
                _overlay.ShowDone();
            }
            return;
        }

        if (wav is null || wav.Length < MinWavBytes)
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
            var session = await _live
                .ConnectAsync(_settings.Current.SpokenLanguages, cycle.Cts.Token)
                .ConfigureAwait(false);
            await using (session.ConfigureAwait(false))
            {
                _log.Info("Live session connected.");
                cycle.Injector = Task.Run(() => InjectLoopAsync(session, cycle));
                await foreach (var pcm in cycle.Pcm.Reader.ReadAllAsync(cycle.Cts.Token).ConfigureAwait(false))
                {
                    await session.SendAudioAsync(pcm, cycle.Cts.Token).ConfigureAwait(false);
                }
                await session.FinishAsync(LiveDrainTimeout, cycle.Cts.Token).ConfigureAwait(false);
                await cycle.Injector.ConfigureAwait(false);
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
            if (cycle.AnyTextInjected)
            {
                AbortLiveDictation();
            }
        }
    }

    /// <summary>Single consumer of the transcript chunks; keeps injections strictly in order.
    /// Chunks arrive one utterance at a time with no separating whitespace, so consecutive
    /// chunks are joined with a space. Chunk text is never logged.</summary>
    private async Task InjectLoopAsync(LiveTranscriptionService.Session session, LiveCycle cycle)
    {
        try
        {
            var lastChar = '\0';
            await foreach (var chunk in session.Transcripts.ReadAllAsync(cycle.Cts.Token).ConfigureAwait(false))
            {
                var text = lastChar != '\0' && !char.IsWhiteSpace(lastChar) && !char.IsWhiteSpace(chunk[0])
                    ? " " + chunk
                    : chunk;
                await _injection.InjectAsync(text, useUnicodeTyping: true).ConfigureAwait(false);
                cycle.AnyTextInjected = true;
                lastChar = chunk[^1];
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled mid-dictation - stop typing immediately.
        }
        catch (Exception)
        {
            // Channel faulted - the pipeline's send/finish path owns the error reporting.
        }
    }

    /// <summary>Called from the pipeline when the session dies mid-recording after text was
    /// already typed: end the dictation now instead of letting the user talk into a dead
    /// session. No-op when a stop or cancel already owns the teardown.</summary>
    private void AbortLiveDictation()
    {
        if (!TryTransition(State.Recording, State.Idle))
        {
            return;
        }
        AbandonLiveCycle(); // detach the tap first so a rapid re-press cannot double-subscribe
        _recorder.Cancel();
        const string message = "Live typing lost the connection - the text typed so far was kept.";
        _overlay.ShowError(message);
        _tray.ShowError("Stenor", message);
        RaiseSafely(DictationCompleted, nameof(DictationCompleted));
    }

    /// <summary>Removes the live cycle from the controller and unhooks the recorder tap.
    /// Exactly one caller gets the cycle; later callers get null.</summary>
    private LiveCycle? DetachLiveCycle()
    {
        LiveCycle? cycle;
        lock (_gate)
        {
            cycle = _liveCycle;
            _liveCycle = null;
        }
        if (cycle is not null)
        {
            _recorder.PcmChunkAvailable -= OnPcmChunk;
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
        lock (_gate)
        {
            state = _state;
        }
        switch (state)
        {
            case State.Recording:
                CancelRecording();
                break;
            case State.Transcribing:
                lock (_gate)
                {
                    _transcribeCts?.Cancel();
                }
                break;
        }
    }

    private State CurrentState()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    private void SetState(State state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }

    /// <summary>Atomically moves <paramref name="from"/> → <paramref name="to"/>; false when
    /// the machine is in any other state (the trigger is then ignored).</summary>
    private bool TryTransition(State from, State to)
    {
        lock (_gate)
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
