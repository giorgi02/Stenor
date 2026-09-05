using System.Buffers.Binary;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Stenor.Constants;
using Stenor.Interop;
using Stenor.Models;
using Stenor.Services;

namespace Stenor.UI;

/// <summary>
/// First-run setup and self-test, also re-runnable from the tray: API key (tested on Next),
/// microphone (live meter + the real <see cref="SpeechDetector"/> gate), hotkey, a trial
/// dictation into the wizard's own text box with a live checklist that shows where a failure
/// happens, and a summary. Each step saves its settings as it is left, so closing the window
/// early keeps what was done. A fresh instance is created per open and destroyed on close.
/// </summary>
public partial class SetupWizardWindow : Window
{
    private enum Step
    {
        ApiKey,
        Microphone,
        Hotkey,
        Trial,
        Done,
    }

    private enum Verdict
    {
        NotChecked,
        Passed,
        Failed,
    }

    /// <summary>Furthest point a trial dictation reached; failures are tracked separately.</summary>
    private enum TrialStage
    {
        Waiting,
        Recording,
        Transcribing,
        Injecting,
        Inserted,
    }

    private const int StepCount = 5;
    private static readonly TimeSpan MicListenTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Rolling window the speech gate is run over.</summary>
    private const int MicWindowBytes = PcmFormat.BytesPerSecond * 3;
    /// <summary>The gate needs some context before its percentiles mean anything.</summary>
    private const int MicMinimumAnalysisBytes = PcmFormat.BytesPerSecond;
    /// <summary>Device start-up transient (and any leading digital silence) is ignored.</summary>
    private const int MicDiscardBytes = PcmFormat.BytesPerSecond / 5;
    /// <summary>~-40 dBFS. On top of the runtime gate the wizard wants a healthy level: the
    /// gate is deliberately permissive (it fails open) and keyboard clicks or room noise can
    /// clear its dynamic-range test - observed passing at a -47.7 dBFS peak while nobody spoke.
    /// Normal speech into a default-gain mic peaks around -20 to -30 dBFS.</summary>
    private const double MicMinimumPeak = 0.01;
    /// <summary>Paste lands via the message queue; give it a moment after the cycle ends.</summary>
    private static readonly TimeSpan TrialSettleDelay = TimeSpan.FromMilliseconds(600);

    private readonly SettingsStore _settings;
    private readonly TranscriptionService _transcription;
    private readonly HotkeyService _hotkeys;
    private readonly RecorderService _recorder;
    private readonly DictationController _controller;
    private readonly Logger _log;

    private readonly Ellipse[] _dots = new Ellipse[StepCount];
    private Step _step;
    private nint _taskbarIconHandle;

    // API key step
    private bool _isSyncingApiKeyFields;
    private CancellationTokenSource? _keyTestCancellation;
    private Verdict _keyVerdict;

    // Microphone step
    private readonly DispatcherTimer _micTimer;
    private readonly object _pcmLock = new();
    private readonly Queue<byte[]> _pcmWindow = new();
    private int _pcmWindowBytes;
    private int _pcmDiscardRemaining;
    private bool _micListening;
    private long _micStartedAt;
    private int _micTick;
    private double _meterLevel;
    private Verdict _micVerdict;

    // Trial step
    private readonly DispatcherTimer _trialSettleTimer;
    private TrialStage _trialStage;
    private string? _trialFailure;
    private Verdict _trialVerdict;

    public SetupWizardWindow(SettingsStore settings, TranscriptionService transcription,
        HotkeyService hotkeys, RecorderService recorder, DictationController controller, Logger log)
    {
        _settings = settings;
        _transcription = transcription;
        _hotkeys = hotkeys;
        _recorder = recorder;
        _controller = controller;
        _log = log;

        InitializeComponent();

        for (var i = 0; i < StepCount; i++)
        {
            _dots[i] = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0) };
            StepDots.Children.Add(_dots[i]);
        }

        _micTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _micTimer.Tick += OnMicTick;
        _trialSettleTimer = new DispatcherTimer { Interval = TrialSettleDelay };
        _trialSettleTimer.Tick += OnTrialSettle;

        var current = _settings.Current;
        ApiKeyBox.Password = _settings.GetApiKey() ?? string.Empty;
        HotkeyPicker.HotkeyService = _hotkeys;
        HotkeyPicker.Hotkey = current.Hotkey.Clone();
        HoldRadio.IsChecked = current.ActivationMode == ActivationMode.Hold;
        ToggleRadio.IsChecked = current.ActivationMode == ActivationMode.Toggle;
        Languages.Load(current.SpokenLanguages);
        Languages.SelectionChanged += OnLanguagesChanged;

        Closed += OnClosedCleanup;
        ShowStep(Step.ApiKey);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _taskbarIconHandle = TaskbarIconOverride.Apply(this, _log);
    }

    private void OnClosedCleanup(object? sender, EventArgs e)
    {
        LeaveStep(_step);
        _keyTestCancellation?.Cancel();
        if (_taskbarIconHandle != 0)
        {
            NativeMethods.DestroyIcon(_taskbarIconHandle);
            _taskbarIconHandle = 0;
        }
    }

    // ------------------------------------------------------------ navigation

    private void ShowStep(Step step)
    {
        _step = step;
        ApiKeyStep.Visibility = step == Step.ApiKey ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneStep.Visibility = step == Step.Microphone ? Visibility.Visible : Visibility.Collapsed;
        HotkeyStep.Visibility = step == Step.Hotkey ? Visibility.Visible : Visibility.Collapsed;
        TrialStep.Visibility = step == Step.Trial ? Visibility.Visible : Visibility.Collapsed;
        DoneStep.Visibility = step == Step.Done ? Visibility.Visible : Visibility.Collapsed;

        var index = (int)step;
        for (var i = 0; i < StepCount; i++)
        {
            _dots[i].Fill = (Brush)FindResource(i <= index ? "AccentBrush" : "EdgeBrush");
        }
        StepCaption.Text = $"Step {index + 1} of {StepCount}";
        StepTitle.Text = step switch
        {
            Step.ApiKey => "Connect Gemini",
            Step.Microphone => "Check your microphone",
            Step.Hotkey => "Choose your hotkey",
            Step.Trial => "Try it out",
            _ => "Setup finished",
        };
        BackButton.Visibility = step == Step.ApiKey ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.Visibility = step == Step.Done ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = step switch
        {
            Step.ApiKey => "Test & continue",
            Step.Done => "Finish",
            _ => "Next",
        };
        HideFooterError();

        switch (step)
        {
            case Step.Microphone:
                EnterMicrophoneStep();
                break;
            case Step.Trial:
                EnterTrialStep();
                break;
            case Step.Done:
                EnterDoneStep();
                break;
        }
    }

    private void GoTo(Step step)
    {
        LeaveStep(_step);
        ShowStep(step);
    }

    private void LeaveStep(Step step)
    {
        switch (step)
        {
            case Step.ApiKey:
                _keyTestCancellation?.Cancel();
                break;
            case Step.Microphone:
                StopListening();
                break;
            case Step.Hotkey:
                HotkeyPicker.CancelCapture();
                break;
            case Step.Trial:
                LeaveTrialStep();
                break;
        }
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.ApiKey:
                await TestKeyAndContinueAsync();
                break;
            case Step.Microphone:
                GoTo(Step.Hotkey);
                break;
            case Step.Hotkey:
                if (TrySave(s =>
                    {
                        s.Hotkey = HotkeyPicker.Hotkey;
                        s.ActivationMode = ToggleRadio.IsChecked == true ? ActivationMode.Toggle : ActivationMode.Hold;
                    }))
                {
                    GoTo(Step.Trial);
                }
                break;
            case Step.Trial:
                GoTo(Step.Done);
                break;
            case Step.Done:
                if (TrySave(s => s.LaunchAtStartup = StartupCheck.IsChecked == true))
                {
                    _log.Info("Setup wizard finished.");
                    Close();
                }
                break;
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step > Step.ApiKey)
        {
            GoTo(_step - 1);
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        if (_step == Step.ApiKey)
        {
            // Keep whatever was typed so the trial step (and the app) can still use it.
            var key = EnteredApiKey;
            if (key.Length > 0 && !TrySave(s => s.ApiKeyEncrypted = _settings.ProtectApiKey(key)))
            {
                return;
            }
        }
        if (_step < Step.Done)
        {
            GoTo(_step + 1);
        }
    }

    /// <summary>Clones, mutates and saves settings; a failure is shown in the footer.</summary>
    private bool TrySave(Action<AppSettings> mutate)
    {
        try
        {
            var updated = _settings.Current.Clone();
            mutate(updated);
            _settings.Save(updated);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Saving settings from the setup wizard failed.", ex);
            FooterErrorText.Text = "Could not save settings - see the log for details.";
            FooterErrorText.Visibility = Visibility.Visible;
            return false;
        }
    }

    private void HideFooterError() => FooterErrorText.Visibility = Visibility.Collapsed;

    private Brush StatusBrush(bool? ok) => (Brush)FindResource(ok switch
    {
        true => "OkBrush",
        false => "DangerBrush",
        null => "MutedBrush",
    });

    // --------------------------------------------------------------- API key

    private string EnteredApiKey =>
        (ApiKeyVisibleBox.Visibility == Visibility.Visible ? ApiKeyVisibleBox.Text : ApiKeyBox.Password).Trim();

    private void OnToggleKeyVisibility(object sender, RoutedEventArgs e)
    {
        _isSyncingApiKeyFields = true;
        try
        {
            if (ApiKeyVisibleBox.Visibility == Visibility.Visible)
            {
                ApiKeyBox.Password = ApiKeyVisibleBox.Text;
                ApiKeyVisibleBox.Visibility = Visibility.Collapsed;
                ApiKeyBox.Visibility = Visibility.Visible;
                ToggleKeyVisibilityButton.Content = "Show";
            }
            else
            {
                ApiKeyVisibleBox.Text = ApiKeyBox.Password;
                ApiKeyBox.Visibility = Visibility.Collapsed;
                ApiKeyVisibleBox.Visibility = Visibility.Visible;
                ToggleKeyVisibilityButton.Content = "Hide";
            }
        }
        finally
        {
            _isSyncingApiKeyFields = false;
        }
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (!_isSyncingApiKeyFields)
        {
            KeyResultText.Visibility = Visibility.Collapsed;
        }
    }

    private async Task TestKeyAndContinueAsync()
    {
        var key = EnteredApiKey;
        if (key.Length == 0)
        {
            ShowKeyResult(false, "Enter your API key first, or skip this step.");
            return;
        }

        NextButton.IsEnabled = false;
        ShowKeyResult(null, "Testing…");
        _keyTestCancellation?.Cancel();
        _keyTestCancellation = new CancellationTokenSource();
        var token = _keyTestCancellation.Token;
        try
        {
            var (isValid, message) = await _transcription.TestKeyAsync(key, token);
            if (token.IsCancellationRequested)
            {
                return; // the step was left meanwhile
            }
            if (isValid)
            {
                _keyVerdict = Verdict.Passed;
                _log.Info("Setup wizard: API key verified.");
                if (TrySave(s => s.ApiKeyEncrypted = _settings.ProtectApiKey(key)))
                {
                    GoTo(Step.Microphone);
                }
                return;
            }
            _keyVerdict = Verdict.Failed;
            ShowKeyResult(false, message + " Fix the key and test again, or skip this step.");
        }
        catch (Exception ex)
        {
            _log.Warn("Setup wizard key test failed.", ex);
            _keyVerdict = Verdict.Failed;
            ShowKeyResult(false, "Test failed unexpectedly - see the log.");
        }
        finally
        {
            NextButton.IsEnabled = true;
        }
    }

    private void ShowKeyResult(bool? ok, string message)
    {
        KeyResultText.Text = message;
        KeyResultText.Foreground = StatusBrush(ok);
        KeyResultText.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------ microphone

    private void EnterMicrophoneStep()
    {
        DeviceText.Text = _recorder.TryGetDefaultDeviceName() is { } name
            ? $"Default microphone: {name}"
            : "No microphone found. Connect one, then click Try again.";
        StartListening();
    }

    private void OnMicRetry(object sender, RoutedEventArgs e) => EnterMicrophoneStep();

    /// <summary>Records through the real capture path and runs the real speech gate (plus a
    /// minimum level) over the last three seconds, so passing here means a dictation would be
    /// accepted at a usable level, not merely that the device produces signal. The global
    /// hotkey is suspended meanwhile so a press cannot start a dictation on top of the test
    /// recording.</summary>
    private void StartListening()
    {
        StopListening();
        MicTipsText.Visibility = Visibility.Collapsed;
        MicRetryButton.Visibility = Visibility.Collapsed;
        if (_recorder.IsRecording)
        {
            // A dictation started before this step was opened still owns the recorder; taking
            // it over here would discard that recording.
            SetMicStatus(null, "A dictation is still running. Finish it, then click Try again.");
            MicRetryButton.Visibility = Visibility.Visible;
            return;
        }
        lock (_pcmLock)
        {
            _pcmWindow.Clear();
            _pcmWindowBytes = 0;
            _pcmDiscardRemaining = MicDiscardBytes;
        }

        _hotkeys.Suspended = true;
        _recorder.PcmChunkAvailable += OnPcmChunk;
        try
        {
            _recorder.Start();
        }
        catch (Exception ex)
        {
            _log.Warn("Setup wizard could not start the microphone.", ex);
            _recorder.PcmChunkAvailable -= OnPcmChunk;
            _hotkeys.Suspended = false;
            FinishMicTest(false, "The microphone could not be started.");
            return;
        }

        _micListening = true;
        _micStartedAt = Environment.TickCount64;
        _micTick = 0;
        _meterLevel = 0;
        SetMicStatus(null, "Listening…");
        _micTimer.Start();
    }

    private void StopListening()
    {
        _micTimer.Stop();
        if (!_micListening)
        {
            return;
        }
        _micListening = false;
        _recorder.PcmChunkAvailable -= OnPcmChunk;
        try
        {
            _recorder.Cancel();
        }
        catch (Exception ex)
        {
            _log.Warn("Setup wizard could not stop the microphone.", ex);
        }
        _hotkeys.Suspended = false;
        _meterLevel = 0;
        LevelFill.Width = 0;
    }

    /// <summary>Capture thread: keep the last few seconds of PCM. Must not block.</summary>
    private void OnPcmChunk(byte[] pcm)
    {
        lock (_pcmLock)
        {
            if (_pcmDiscardRemaining > 0)
            {
                _pcmDiscardRemaining -= pcm.Length;
                return;
            }
            _pcmWindow.Enqueue(pcm);
            _pcmWindowBytes += pcm.Length;
            while (_pcmWindowBytes > MicWindowBytes && _pcmWindow.Count > 1)
            {
                _pcmWindowBytes -= _pcmWindow.Dequeue().Length;
            }
        }
    }

    private void OnMicTick(object? sender, EventArgs e)
    {
        var level = Math.Sqrt(Math.Clamp(_recorder.CurrentLevel, 0f, 1f)); // perceptual boost, as the overlay
        _meterLevel = level > _meterLevel ? level : _meterLevel * 0.85; // fast attack, slow release
        LevelFill.Width = Math.Max(0, LevelTrack.ActualWidth - 2) * _meterLevel;

        if (++_micTick % 6 != 0)
        {
            return; // analyze every 300 ms
        }

        byte[]? pcm = null;
        lock (_pcmLock)
        {
            if (_pcmWindowBytes >= MicMinimumAnalysisBytes)
            {
                pcm = new byte[_pcmWindowBytes];
                var offset = 0;
                foreach (var chunk in _pcmWindow)
                {
                    chunk.CopyTo(pcm, offset);
                    offset += chunk.Length;
                }
            }
        }
        if (pcm is not null)
        {
            var result = SpeechDetector.Analyze(WrapAsWav(pcm));
            if (result.HasSpeech && result.SignalPeak >= MicMinimumPeak)
            {
                _log.Info($"Setup wizard: microphone check passed ({result}).");
                FinishMicTest(true, "Speech detected - your microphone works.");
                return;
            }
        }
        if (Environment.TickCount64 - _micStartedAt > MicListenTimeout.TotalMilliseconds)
        {
            _log.Info("Setup wizard: microphone check heard no speech.");
            FinishMicTest(false, "No speech was detected in 10 seconds.");
        }
    }

    private void FinishMicTest(bool passed, string message)
    {
        StopListening();
        _micVerdict = passed ? Verdict.Passed : Verdict.Failed;
        SetMicStatus(passed, message);
        if (!passed)
        {
            MicTipsText.Visibility = Visibility.Visible;
            MicRetryButton.Visibility = Visibility.Visible;
        }
    }

    private void SetMicStatus(bool? ok, string message)
    {
        MicStatusText.Text = message;
        MicStatusText.Foreground = StatusBrush(ok);
    }

    /// <summary>Wraps raw 16 kHz mono PCM16 in a canonical 44-byte WAV header for the gate.</summary>
    private static byte[] WrapAsWav(byte[] pcm)
    {
        var wav = new byte[44 + pcm.Length];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + pcm.Length);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], PcmFormat.ChannelCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], PcmFormat.SampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], PcmFormat.BytesPerSecond);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], PcmFormat.BitsPerSample / 8 * PcmFormat.ChannelCount);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], PcmFormat.BitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], pcm.Length);
        pcm.CopyTo(span[44..]);
        return wav;
    }

    // ------------------------------------------------------------------ trial

    private void EnterTrialStep()
    {
        var hotkey = HotkeyDisplay.Describe(_settings.Current.Hotkey);
        TrialInstruction.Text = _settings.Current.ActivationMode == ActivationMode.Hold
            ? $"Click inside the box below, hold {hotkey}, say a sentence, then release the key."
            : $"Click inside the box below, press {hotkey}, say a sentence, then press {hotkey} again.";
        ResetTrial();
        _hotkeys.Released += OnTrialHotkeyReleased;
        _controller.DictationStarted += OnTrialDictationStarted;
        _controller.TranscriptionStarted += OnTrialTranscriptionStarted;
        _controller.DictationFailed += OnTrialDictationFailed;
        _controller.DictationCompleted += OnTrialDictationCompleted;
        Dispatcher.BeginInvoke(() => TrialBox.Focus(), DispatcherPriority.Background);
    }

    private void LeaveTrialStep()
    {
        _hotkeys.Released -= OnTrialHotkeyReleased;
        _controller.DictationStarted -= OnTrialDictationStarted;
        _controller.TranscriptionStarted -= OnTrialTranscriptionStarted;
        _controller.DictationFailed -= OnTrialDictationFailed;
        _controller.DictationCompleted -= OnTrialDictationCompleted;
        _trialSettleTimer.Stop();
    }

    private void OnLanguagesChanged() => TrySave(s => s.SpokenLanguages = Languages.SelectedLanguages);

    // The checklist follows the controller's lifecycle events, which arrive in causal order.
    // The hook's own Pressed event is deliberately not used: the controller handles it first
    // and may already have raised a failure by the time the wizard's handler would run.

    private void OnTrialDictationStarted() => Dispatcher.BeginInvoke(() =>
    {
        if (IsAttemptFinished)
        {
            ResetTrial(); // a new press after a finished attempt
        }
        Advance(TrialStage.Recording);
    });

    private void OnTrialTranscriptionStarted() => Dispatcher.BeginInvoke(() =>
    {
        if (_trialFailure is null)
        {
            Advance(TrialStage.Transcribing);
        }
    });

    private void OnTrialDictationFailed(string message) => Dispatcher.BeginInvoke(() =>
    {
        if (IsAttemptFinished)
        {
            ResetTrial();
        }
        Advance(TrialStage.Recording); // a start failure (no key, no mic) still counts as a detected press
        Fail(message);
    });

    private void OnTrialDictationCompleted() => Dispatcher.BeginInvoke(() =>
    {
        if (_trialFailure is not null)
        {
            return;
        }
        switch (_trialStage)
        {
            case TrialStage.Recording:
                Fail("The recording was too short or was cancelled. Try again.");
                break;
            case TrialStage.Transcribing:
                Advance(TrialStage.Injecting);
                _trialSettleTimer.Stop();
                _trialSettleTimer.Start();
                break;
        }
    });

    private void OnTrialHotkeyReleased(TimeSpan held) => Dispatcher.BeginInvoke(() =>
    {
        // The controller discards short Hold-mode taps silently (no DictationCompleted), so
        // explain them here. A real release has already moved the stage on.
        if (_settings.Current.ActivationMode == ActivationMode.Hold
            && _trialFailure is null && _trialStage == TrialStage.Recording
            && held < DictationController.MinHoldDuration)
        {
            Fail("That was only a tap. Hold the key down while you speak, then release it.");
        }
    });

    private void OnTrialSettle(object? sender, EventArgs e)
    {
        _trialSettleTimer.Stop();
        if (_trialFailure is null && _trialStage == TrialStage.Injecting)
        {
            Fail("The text did not land in the box. Click inside it first, then try again.");
        }
    }

    private void OnTrialTextChanged(object sender, TextChangedEventArgs e)
    {
        // Only a paste that arrives while a dictation is in flight counts; typing by hand
        // while idle does not.
        if (_trialFailure is null && _trialStage >= TrialStage.Recording
            && !string.IsNullOrWhiteSpace(TrialBox.Text))
        {
            _trialSettleTimer.Stop();
            _trialVerdict = Verdict.Passed;
            _log.Info("Setup wizard: trial dictation inserted text.");
            Advance(TrialStage.Inserted);
        }
    }

    private bool IsAttemptFinished => _trialFailure is not null || _trialStage == TrialStage.Inserted;

    private void Advance(TrialStage stage)
    {
        if (stage > _trialStage)
        {
            _trialStage = stage;
        }
        RenderChecklist();
    }

    private void Fail(string message)
    {
        _trialSettleTimer.Stop();
        _trialFailure = message;
        _trialVerdict = Verdict.Failed;
        RenderChecklist();
    }

    private void ResetTrial()
    {
        _trialSettleTimer.Stop();
        _trialStage = TrialStage.Waiting;
        _trialFailure = null;
        RenderChecklist();
    }

    /// <summary>Completed rows get a tick, the row in progress an ellipsis (or a cross when
    /// the attempt failed there), later rows stay dim - so the reader sees at a glance which
    /// link of the chain broke.</summary>
    private void RenderChecklist()
    {
        var completed = _trialStage switch
        {
            TrialStage.Waiting => 0,
            TrialStage.Recording => 1,
            TrialStage.Transcribing => 2,
            TrialStage.Injecting => 3,
            _ => 4,
        };
        var current = _trialStage is TrialStage.Waiting or TrialStage.Inserted ? 0 : completed + 1;

        RenderRow(HotkeyRow, 1, "Hotkey detected", "Hotkey detected", completed, current);
        RenderRow(RecordingRow, 2, "Recording", "Recording… speak now", completed, current);
        RenderRow(TranscribingRow, 3, "Transcribing", "Transcribing…", completed, current);
        RenderRow(InsertedRow, 4, "Text inserted", "Inserting text…", completed, current);

        if (_trialFailure is not null)
        {
            TrialResultText.Text = _trialFailure;
            TrialResultText.Foreground = StatusBrush(false);
            TrialResultText.Visibility = Visibility.Visible;
        }
        else if (_trialStage == TrialStage.Inserted)
        {
            TrialResultText.Text = "Everything works. Dictate the same way in any app.";
            TrialResultText.Foreground = StatusBrush(true);
            TrialResultText.Visibility = Visibility.Visible;
        }
        else
        {
            TrialResultText.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderRow(TextBlock row, int index, string label, string activeLabel, int completed, int current)
    {
        if (index <= completed)
        {
            row.Text = "✓  " + label;
            row.Foreground = StatusBrush(true);
        }
        else if (index == current && _trialFailure is not null)
        {
            row.Text = "✗  " + label;
            row.Foreground = StatusBrush(false);
        }
        else if (index == current)
        {
            row.Text = "…  " + activeLabel;
            row.Foreground = (Brush)FindResource("AccentBrush");
        }
        else
        {
            row.Text = "○  " + label;
            row.Foreground = StatusBrush(null);
        }
    }

    // ------------------------------------------------------------------- done

    private void EnterDoneStep()
    {
        var keySet = _settings.GetApiKey() is not null;
        var (keyOk, keyText) = (_keyVerdict, keySet) switch
        {
            (Verdict.Passed, _) => ((bool?)true, "Gemini API key verified"),
            (Verdict.Failed, true) => (false, "API key saved, but the test failed"),
            (_, true) => (null, "API key saved, not tested"),
            _ => (false, "No API key - add one in Settings"),
        };
        SetSummary(SummaryKey, keyOk, keyText);
        SetSummary(SummaryMic,
            _micVerdict switch { Verdict.Passed => true, Verdict.Failed => false, _ => null },
            _micVerdict switch
            {
                Verdict.Passed => "Microphone: speech detected",
                Verdict.Failed => "Microphone: no speech detected",
                _ => "Microphone: not tested",
            });
        var mode = _settings.Current.ActivationMode == ActivationMode.Hold ? "hold to talk" : "toggle";
        SetSummary(SummaryHotkey, true, $"Hotkey: {HotkeyDisplay.Describe(_settings.Current.Hotkey)} ({mode})");
        SetSummary(SummaryTrial,
            _trialVerdict switch { Verdict.Passed => true, Verdict.Failed => false, _ => null },
            _trialVerdict switch
            {
                Verdict.Passed => "Test dictation: text inserted",
                Verdict.Failed => "Test dictation: " + _trialFailure,
                _ => "Test dictation: not run",
            });

        var allPassed = _keyVerdict == Verdict.Passed && _micVerdict == Verdict.Passed
            && _trialVerdict == Verdict.Passed;
        StepTitle.Text = allPassed ? "Everything works" : "Setup finished";
        DoneHeadline.Text = allPassed
            ? "Stenor is ready. Put the cursor in any text field, use your hotkey and speak."
            : "Some checks did not pass or were skipped. Stenor still works for whatever did pass; you can run this setup again from the tray menu at any time.";
        StartupCheck.IsChecked = _settings.Current.LaunchAtStartup;
    }

    private void SetSummary(TextBlock row, bool? ok, string text)
    {
        row.Text = (ok switch { true => "✓  ", false => "✗  ", null => "–  " }) + text;
        row.Foreground = StatusBrush(ok);
    }

    // ---------------------------------------------------------------- misc

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn("Opening the API key link failed.", ex);
        }
        e.Handled = true;
    }
}
