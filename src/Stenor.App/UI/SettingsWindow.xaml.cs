using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Stenor.Models;
using Stenor.Services;

namespace Stenor.UI;

/// <summary>
/// Single-page settings. A fresh instance is created per open and fully destroyed on close so
/// WPF memory is reclaimed while the app idles in the tray.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly string[] Languages =
    [
        "English", "Georgian", "Russian", "German", "French", "Spanish", "Other / Auto-detect",
    ];

    private readonly SettingsStore _settings;
    private readonly TranscriptionService _transcription;
    private readonly HotkeyService _hotkeys;
    private readonly Logger _log;

    private HotkeySpec _selectedHotkey;
    private bool _capturingHotkey;
    private bool _syncingKeyBoxes;
    private CancellationTokenSource? _testCts;

    public SettingsWindow(SettingsStore settings, TranscriptionService transcription,
        HotkeyService hotkeys, Logger log)
    {
        _settings = settings;
        _transcription = transcription;
        _hotkeys = hotkeys;
        _log = log;

        InitializeComponent();

        foreach (var language in Languages)
        {
            LanguageBox.Items.Add(language);
        }

        var current = _settings.Current;
        ApiKeyBox.Password = _settings.GetApiKey() ?? string.Empty;
        LanguageBox.SelectedItem = Languages.Contains(current.PrimaryLanguage)
            ? current.PrimaryLanguage
            : Languages[0];
        _selectedHotkey = current.Hotkey.Clone();
        HotkeyButton.Content = HotkeyDisplay.Describe(_selectedHotkey);
        HoldRadio.IsChecked = current.ActivationMode == ActivationMode.Hold;
        ToggleRadio.IsChecked = current.ActivationMode == ActivationMode.Toggle;
        StartupCheck.IsChecked = current.LaunchAtStartup;

        Closed += OnClosedCleanup;
    }

    private void OnClosedCleanup(object? sender, EventArgs e)
    {
        _testCts?.Cancel();
        if (_capturingHotkey)
        {
            EndHotkeyCapture(null);
        }
    }

    // --------------------------------------------------------------- API key

    private string EnteredApiKey =>
        (ApiKeyVisibleBox.Visibility == Visibility.Visible ? ApiKeyVisibleBox.Text : ApiKeyBox.Password).Trim();

    private void OnToggleKeyVisibility(object sender, RoutedEventArgs e)
    {
        if (ApiKeyVisibleBox.Visibility == Visibility.Visible)
        {
            SyncKeyBoxes(() => ApiKeyBox.Password = ApiKeyVisibleBox.Text);
            ApiKeyVisibleBox.Visibility = Visibility.Collapsed;
            ApiKeyBox.Visibility = Visibility.Visible;
            ToggleKeyVisibilityButton.Content = "Show";
        }
        else
        {
            SyncKeyBoxes(() => ApiKeyVisibleBox.Text = ApiKeyBox.Password);
            ApiKeyBox.Visibility = Visibility.Collapsed;
            ApiKeyVisibleBox.Visibility = Visibility.Visible;
            ToggleKeyVisibilityButton.Content = "Hide";
        }
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingKeyBoxes)
        {
            HideValidation();
        }
    }

    private void OnApiKeyVisibleChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingKeyBoxes)
        {
            HideValidation();
        }
    }

    private void SyncKeyBoxes(Action sync)
    {
        _syncingKeyBoxes = true;
        try
        {
            sync();
        }
        finally
        {
            _syncingKeyBoxes = false;
        }
    }

    private async void OnTestKey(object sender, RoutedEventArgs e)
    {
        var key = EnteredApiKey;
        if (key.Length == 0)
        {
            ShowTestResult(false, "Enter an API key first.");
            return;
        }

        TestKeyButton.IsEnabled = false;
        ShowTestResult(null, "Testing…");
        _testCts?.Cancel();
        _testCts = new CancellationTokenSource();
        try
        {
            var (ok, message) = await _transcription.TestKeyAsync(key, _testCts.Token);
            ShowTestResult(ok, message);
        }
        catch (Exception ex)
        {
            _log.Warn("Test key click failed.", ex);
            ShowTestResult(false, "Test failed unexpectedly.");
        }
        finally
        {
            TestKeyButton.IsEnabled = true;
        }
    }

    private void ShowTestResult(bool? ok, string message)
    {
        TestResultText.Text = message;
        TestResultText.Foreground = ok switch
        {
            true => (Brush)FindResource("OkBrush"),
            false => (Brush)FindResource("DangerBrush"),
            null => (Brush)FindResource("MutedBrush"),
        };
        TestResultText.Visibility = Visibility.Visible;
    }

    // -------------------------------------------------------- hotkey capture

    private void OnHotkeyButtonClick(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey)
        {
            return;
        }
        _capturingHotkey = true;
        _hotkeys.Suspended = true;
        HotkeyButton.Content = "Press a key or combo…";
        PreviewKeyDown += OnCaptureKeyDown;
        PreviewKeyUp += OnCaptureKeyUp;
    }

    private void OnCaptureKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            EndHotkeyCapture(null);
            return;
        }
        if (IsModifier(key))
        {
            HotkeyButton.Content = DescribeHeldModifiers() + "…";
            return;
        }

        var modifiers = Keyboard.Modifiers;
        EndHotkeyCapture(new HotkeySpec
        {
            VirtualKey = KeyInterop.VirtualKeyFromKey(key),
            Ctrl = modifiers.HasFlag(ModifierKeys.Control),
            Shift = modifiers.HasFlag(ModifierKeys.Shift),
            Alt = modifiers.HasFlag(ModifierKeys.Alt),
            Win = modifiers.HasFlag(ModifierKeys.Windows),
        });
    }

    private void OnCaptureKeyUp(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifier(key))
        {
            // A modifier released without any main key: the hotkey is that bare (left/right
            // specific) modifier - e.g. the default Right Ctrl.
            EndHotkeyCapture(new HotkeySpec { VirtualKey = KeyInterop.VirtualKeyFromKey(key) });
        }
    }

    private void EndHotkeyCapture(HotkeySpec? captured)
    {
        PreviewKeyDown -= OnCaptureKeyDown;
        PreviewKeyUp -= OnCaptureKeyUp;
        _capturingHotkey = false;
        _hotkeys.Suspended = false;
        if (captured is not null)
        {
            _selectedHotkey = captured;
        }
        HotkeyButton.Content = HotkeyDisplay.Describe(_selectedHotkey);
    }

    private static bool IsModifier(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin;

    private static string DescribeHeldModifiers()
    {
        var modifiers = Keyboard.Modifiers;
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }
        return string.Join(" + ", parts) + " + ";
    }

    // ------------------------------------------------------------ save/close

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var key = EnteredApiKey;
        if (key.Length == 0)
        {
            ValidationText.Text = "An API key is required. Get one at aistudio.google.com/apikey.";
            ValidationText.Visibility = Visibility.Visible;
            ApiKeyBox.Focus();
            return;
        }

        try
        {
            var updated = _settings.Current.Clone();
            updated.ApiKeyEncrypted = _settings.ProtectApiKey(key);
            updated.PrimaryLanguage = LanguageBox.SelectedItem as string ?? "English";
            updated.Hotkey = _selectedHotkey;
            updated.ActivationMode = ToggleRadio.IsChecked == true ? ActivationMode.Toggle : ActivationMode.Hold;
            updated.LaunchAtStartup = StartupCheck.IsChecked == true;
            _settings.Save(updated);
            Close();
        }
        catch (Exception ex)
        {
            _log.Error("Saving settings failed.", ex);
            ValidationText.Text = "Could not save settings - see the log for details.";
            ValidationText.Visibility = Visibility.Visible;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void HideValidation() => ValidationText.Visibility = Visibility.Collapsed;

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
