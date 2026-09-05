using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;
using Stenor.Interop;
using Stenor.Models;
using Stenor.Services;

namespace Stenor.UI;

/// <summary>
/// Single-page settings. A fresh instance is created per open and fully destroyed on close so
/// WPF memory is reclaimed while the app idles in the tray.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly TranscriptionService _transcriptionService;
    private readonly HotkeyService _hotkeyService;
    private readonly Logger _log;

    private bool _isSyncingApiKeyFields;
    private CancellationTokenSource? _apiKeyTestCancellation;
    private nint _taskbarIconHandle;

    public SettingsWindow(SettingsStore settings, TranscriptionService transcription,
        HotkeyService hotkeys, Logger log)
    {
        _settingsStore = settings;
        _transcriptionService = transcription;
        _hotkeyService = hotkeys;
        _log = log;

        InitializeComponent();
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        Title = $"Stenor Settings — v{version}";

        var current = _settingsStore.Current;
        ApiKeyBox.Password = _settingsStore.GetApiKey() ?? string.Empty;
        Languages.Load(current.SpokenLanguages);
        HotkeyPicker.HotkeyService = _hotkeyService;
        HotkeyPicker.Hotkey = current.Hotkey.Clone();
        HoldRadio.IsChecked = current.ActivationMode == ActivationMode.Hold;
        ToggleRadio.IsChecked = current.ActivationMode == ActivationMode.Toggle;
        StartupCheck.IsChecked = current.LaunchAtStartup;
        LiveTypingCheck.IsChecked = current.LiveTyping;

        Closed += OnClosedCleanup;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _taskbarIconHandle = TaskbarIconOverride.Apply(this, _log);
    }

    private void OnClosedCleanup(object? sender, EventArgs e)
    {
        _apiKeyTestCancellation?.Cancel();
        HotkeyPicker.CancelCapture();
        if (_taskbarIconHandle != 0)
        {
            NativeMethods.DestroyIcon(_taskbarIconHandle);
            _taskbarIconHandle = 0;
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
        if (!_isSyncingApiKeyFields)
        {
            HideValidation();
        }
    }

    private void OnApiKeyVisibleChanged(object sender, RoutedEventArgs e)
    {
        if (!_isSyncingApiKeyFields)
        {
            HideValidation();
        }
    }

    private void SyncKeyBoxes(Action sync)
    {
        _isSyncingApiKeyFields = true;
        try
        {
            sync();
        }
        finally
        {
            _isSyncingApiKeyFields = false;
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
        _apiKeyTestCancellation?.Cancel();
        _apiKeyTestCancellation = new CancellationTokenSource();
        try
        {
            var (isValid, message) = await _transcriptionService.TestKeyAsync(
                key, _apiKeyTestCancellation.Token);
            ShowTestResult(isValid, message);
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

    private void ShowTestResult(bool? isValid, string message)
    {
        TestResultText.Text = message;
        TestResultText.Foreground = isValid switch
        {
            true => (Brush)FindResource("OkBrush"),
            false => (Brush)FindResource("DangerBrush"),
            null => (Brush)FindResource("MutedBrush"),
        };
        TestResultText.Visibility = Visibility.Visible;
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
            var updated = _settingsStore.Current.Clone();
            updated.ApiKeyEncrypted = _settingsStore.ProtectApiKey(key);
            updated.SpokenLanguages = Languages.SelectedLanguages;
            updated.Hotkey = HotkeyPicker.Hotkey;
            updated.ActivationMode = ToggleRadio.IsChecked == true ? ActivationMode.Toggle : ActivationMode.Hold;
            updated.LaunchAtStartup = StartupCheck.IsChecked == true;
            updated.LiveTyping = LiveTypingCheck.IsChecked == true;
            _settingsStore.Save(updated);
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
