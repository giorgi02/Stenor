using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private readonly Dictionary<string, CheckBox> _languageCheckBoxes = [];
    private Border? _checkedGroupSeparator;

    private HotkeySpec _selectedHotkey;
    private bool _isCapturingHotkey;
    private bool _isSyncingApiKeyFields;
    private bool _isSyncingLanguageSelection;
    private DateTime _languagePopupClosedAt;
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
        BuildLanguageList(current.SpokenLanguages);
        _selectedHotkey = current.Hotkey.Clone();
        HotkeyButton.Content = HotkeyDisplay.Describe(_selectedHotkey);
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
        if (_isCapturingHotkey)
        {
            EndHotkeyCapture(null);
        }
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

    // ---------------------------------------------------- spoken languages

    private void BuildLanguageList(IReadOnlyList<string> selected)
    {
        foreach (var language in LanguageCatalog.All)
        {
            var check = new CheckBox
            {
                Content = language,
                Style = (Style)FindResource("DarkCheckBox"),
                Padding = new Thickness(6, 0, 0, 0),
                Margin = new Thickness(14, 5, 14, 5),
                IsChecked = selected.Contains(language),
            };
            check.Checked += OnLanguageChecked;
            check.Unchecked += OnLanguageUnchecked;
            _languageCheckBoxes.Add(language, check);
        }
        AutoDetectCheck.IsChecked = GetSelectedLanguages().Count == 0;
        ReorderLanguageList();
        UpdateLanguagesSummary();
    }

    /// <summary>Catalog order with the checked group first, so both groups stay alphabetical;
    /// a faint line separates the two groups when both are present.</summary>
    private void ReorderLanguageList()
    {
        LanguagesPanel.Children.Clear();
        foreach (var language in LanguageCatalog.All.Where(
            language => _languageCheckBoxes[language].IsChecked == true))
        {
            LanguagesPanel.Children.Add(_languageCheckBoxes[language]);
        }
        if (LanguagesPanel.Children.Count > 0
            && LanguagesPanel.Children.Count < _languageCheckBoxes.Count)
        {
            _checkedGroupSeparator ??= new Border
            {
                Height = 1,
                Background = (Brush)FindResource("EdgeBrush"),
                Margin = new Thickness(10, 4, 10, 4),
            };
            LanguagesPanel.Children.Add(_checkedGroupSeparator);
        }
        foreach (var language in LanguageCatalog.All.Where(
            language => _languageCheckBoxes[language].IsChecked != true))
        {
            LanguagesPanel.Children.Add(_languageCheckBoxes[language]);
        }
    }

    private List<string> GetSelectedLanguages() =>
        [.. LanguageCatalog.All.Where(
            language => _languageCheckBoxes[language].IsChecked == true)];

    private void UpdateLanguagesSummary()
    {
        var selected = GetSelectedLanguages();
        LanguagesSummary.Text = selected.Count == 0 ? "Auto-detect" : string.Join(", ", selected);
    }

    private void OnLanguageChecked(object sender, RoutedEventArgs e)
    {
        if (_isSyncingLanguageSelection)
        {
            return;
        }
        SyncLanguageChecks(() => AutoDetectCheck.IsChecked = false);
        UpdateLanguagesSummary();
    }

    private void OnLanguageUnchecked(object sender, RoutedEventArgs e)
    {
        if (_isSyncingLanguageSelection)
        {
            return;
        }
        if (GetSelectedLanguages().Count == 0)
        {
            SyncLanguageChecks(() => AutoDetectCheck.IsChecked = true);
        }
        UpdateLanguagesSummary();
    }

    private void OnAutoDetectChecked(object sender, RoutedEventArgs e)
    {
        if (_isSyncingLanguageSelection)
        {
            return;
        }
        SyncLanguageChecks(() =>
        {
            foreach (var check in _languageCheckBoxes.Values)
            {
                check.IsChecked = false;
            }
        });
        UpdateLanguagesSummary();
    }

    private void OnAutoDetectUnchecked(object sender, RoutedEventArgs e)
    {
        // Auto-detect only turns off by picking a language; unchecking it directly would
        // leave nothing selected, so snap it back on.
        if (!_isSyncingLanguageSelection && GetSelectedLanguages().Count == 0)
        {
            SyncLanguageChecks(() => AutoDetectCheck.IsChecked = true);
        }
    }

    private void SyncLanguageChecks(Action sync)
    {
        _isSyncingLanguageSelection = true;
        try
        {
            sync();
        }
        finally
        {
            _isSyncingLanguageSelection = false;
        }
    }

    private void OnLanguagesPopupOpened(object? sender, EventArgs e)
    {
        ReorderLanguageList();
        LanguagesScroll.ScrollToTop();
    }

    private void OnLanguagesPopupClosed(object? sender, EventArgs e) =>
        _languagePopupClosedAt = DateTime.UtcNow;

    private void OnLanguagesToggleMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking the toggle while the popup is open first closes it via StaysOpen=False;
        // swallow that same click so it does not immediately reopen the popup.
        if ((DateTime.UtcNow - _languagePopupClosedAt) < TimeSpan.FromMilliseconds(250))
        {
            e.Handled = true;
        }
    }

    // -------------------------------------------------------- hotkey capture

    private void OnHotkeyButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isCapturingHotkey)
        {
            return;
        }
        _isCapturingHotkey = true;
        _hotkeyService.Suspended = true;
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
        _isCapturingHotkey = false;
        _hotkeyService.Suspended = false;
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
            var updated = _settingsStore.Current.Clone();
            updated.ApiKeyEncrypted = _settingsStore.ProtectApiKey(key);
            updated.SpokenLanguages = GetSelectedLanguages();
            updated.Hotkey = _selectedHotkey;
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
