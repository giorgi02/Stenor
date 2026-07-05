using Stenor.Models;
using Stenor.UI;

namespace Stenor.Services;

/// <summary>
/// The state machine at the heart of Stenor: Idle → Recording → Transcribing → Injecting → Idle.
///
/// Hotkey semantics are applied here: Hold mode starts on key-down and stops on key-up
/// (holds under 150 ms are treated as accidental taps and discarded); Toggle mode starts on a
/// press while Idle and stops on the next press. Activations while busy are ignored, which
/// also makes rapid double-activation safe. Every failure path ends back in Idle with the
/// overlay/tray informed - nothing here may ever throw out to the caller.
/// </summary>
public sealed class DictationController
{
    private static readonly TimeSpan MinHoldDuration = TimeSpan.FromMilliseconds(150);
    private const int MinWavBytes = 44 + 8000; // ~0.25 s of 16 kHz 16-bit audio

    private enum State
    {
        Idle,
        Recording,
        Transcribing,
        Injecting,
    }

    private readonly Logger _log;
    private readonly SettingsStore _settings;
    private readonly HotkeyService _hotkeys;
    private readonly RecorderService _recorder;
    private readonly TranscriptionService _transcription;
    private readonly InjectionService _injection;
    private readonly OverlayController _overlay;
    private readonly TrayIcon _tray;

    private readonly object _gate = new();
    private State _state = State.Idle;
    private CancellationTokenSource? _transcribeCts;

    public DictationController(
        Logger log,
        SettingsStore settings,
        HotkeyService hotkeys,
        RecorderService recorder,
        TranscriptionService transcription,
        InjectionService injection,
        OverlayController overlay,
        TrayIcon tray)
    {
        _log = log;
        _settings = settings;
        _hotkeys = hotkeys;
        _recorder = recorder;
        _transcription = transcription;
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
        lock (_gate)
        {
            if (_state != State.Recording)
            {
                return;
            }
            _state = State.Idle;
        }
        _overlay.ShowError(message);
        _tray.ShowError("Stenor", message);
    }

    // ------------------------------------------------------------ transitions

    private void StartRecording()
    {
        lock (_gate)
        {
            if (_state != State.Idle)
            {
                return;
            }
            _state = State.Recording;
        }

        if (_settings.GetApiKey() is null)
        {
            lock (_gate)
            {
                _state = State.Idle;
            }
            _overlay.ShowError("Add your Gemini API key in Settings.");
            _tray.ShowError("Stenor", "No API key configured. Open Settings from the tray icon.");
            return;
        }

        try
        {
            _recorder.Start();
            _overlay.ShowRecording(() => _recorder.CurrentLevel);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _state = State.Idle;
            }
            _log.Error("Recording could not be started.", ex);
            _overlay.ShowError("Microphone unavailable.");
            _tray.ShowError("Stenor", "Could not start recording - check your microphone.");
        }
    }

    private void CancelRecording()
    {
        lock (_gate)
        {
            if (_state != State.Recording)
            {
                return;
            }
            _state = State.Idle;
        }
        _recorder.Cancel();
        _overlay.Hide();
    }

    private async Task StopAndTranscribeAsync()
    {
        lock (_gate)
        {
            if (_state != State.Recording)
            {
                return;
            }
            _state = State.Transcribing;
        }

        try
        {
            var wav = _recorder.Stop();
            if (wav is null || wav.Length < MinWavBytes)
            {
                lock (_gate)
                {
                    _state = State.Idle;
                }
                _overlay.Hide();
                return;
            }

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
                    .TranscribeAsync(wav, _settings.Current.PrimaryLanguage, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _state = State.Idle;
                }
                _overlay.Hide();
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                lock (_gate)
                {
                    _state = State.Idle;
                }
                _overlay.ShowError("No speech detected.");
                return;
            }

            lock (_gate)
            {
                _state = State.Injecting;
            }
            await _injection
                .InjectAsync(text, _settings.Current.UseUnicodeTypingFallback)
                .ConfigureAwait(false);

            _log.Info($"Injected {text.Length} characters.");
            _overlay.ShowDone();
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
}
