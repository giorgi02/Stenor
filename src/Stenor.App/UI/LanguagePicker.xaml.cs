using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Stenor.Models;

namespace Stenor.UI;

/// <summary>
/// Multi-select dropdown over <see cref="LanguageCatalog"/>, styled like a combo box. An empty
/// selection means auto-detect. Shared by the Settings window and the setup wizard.
/// </summary>
public partial class LanguagePicker : UserControl
{
    private readonly Dictionary<string, CheckBox> _languageCheckBoxes = [];
    private Border? _checkedGroupSeparator;
    private bool _isSyncingSelection;
    private DateTime _popupClosedAt;

    /// <summary>Raised after any user change to the selection.</summary>
    public event Action? SelectionChanged;

    public LanguagePicker()
    {
        InitializeComponent();
        foreach (var language in LanguageCatalog.All)
        {
            var check = new CheckBox
            {
                Content = language,
                Style = (Style)FindResource("DarkCheckBox"),
                Padding = new Thickness(6, 0, 0, 0),
                Margin = new Thickness(14, 5, 14, 5),
            };
            check.Checked += OnLanguageChecked;
            check.Unchecked += OnLanguageUnchecked;
            _languageCheckBoxes.Add(language, check);
        }
        Load([]);
    }

    /// <summary>Catalog order, checked languages only.</summary>
    public List<string> SelectedLanguages =>
        [.. LanguageCatalog.All.Where(language => _languageCheckBoxes[language].IsChecked == true)];

    /// <summary>Replaces the selection without raising <see cref="SelectionChanged"/>.</summary>
    public void Load(IReadOnlyList<string> selected)
    {
        SyncSelection(() =>
        {
            foreach (var (language, check) in _languageCheckBoxes)
            {
                check.IsChecked = selected.Contains(language);
            }
            AutoDetectCheck.IsChecked = SelectedLanguages.Count == 0;
        });
        ReorderLanguageList();
        UpdateSummary();
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

    private void UpdateSummary()
    {
        var selected = SelectedLanguages;
        LanguagesSummary.Text = selected.Count == 0 ? "Auto-detect" : string.Join(", ", selected);
    }

    private void OnLanguageChecked(object sender, RoutedEventArgs e)
    {
        if (_isSyncingSelection)
        {
            return;
        }
        SyncSelection(() => AutoDetectCheck.IsChecked = false);
        NotifyChanged();
    }

    private void OnLanguageUnchecked(object sender, RoutedEventArgs e)
    {
        if (_isSyncingSelection)
        {
            return;
        }
        if (SelectedLanguages.Count == 0)
        {
            SyncSelection(() => AutoDetectCheck.IsChecked = true);
        }
        NotifyChanged();
    }

    private void OnAutoDetectChecked(object sender, RoutedEventArgs e)
    {
        if (_isSyncingSelection)
        {
            return;
        }
        SyncSelection(() =>
        {
            foreach (var check in _languageCheckBoxes.Values)
            {
                check.IsChecked = false;
            }
        });
        NotifyChanged();
    }

    private void OnAutoDetectUnchecked(object sender, RoutedEventArgs e)
    {
        // Auto-detect only turns off by picking a language; unchecking it directly would
        // leave nothing selected, so snap it back on.
        if (!_isSyncingSelection && SelectedLanguages.Count == 0)
        {
            SyncSelection(() => AutoDetectCheck.IsChecked = true);
        }
    }

    private void NotifyChanged()
    {
        UpdateSummary();
        SelectionChanged?.Invoke();
    }

    private void SyncSelection(Action sync)
    {
        _isSyncingSelection = true;
        try
        {
            sync();
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private void OnLanguagesPopupOpened(object? sender, EventArgs e)
    {
        ReorderLanguageList();
        LanguagesScroll.ScrollToTop();
    }

    private void OnLanguagesPopupClosed(object? sender, EventArgs e) =>
        _popupClosedAt = DateTime.UtcNow;

    private void OnLanguagesToggleMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking the toggle while the popup is open first closes it via StaysOpen=False;
        // swallow that same click so it does not immediately reopen the popup.
        if ((DateTime.UtcNow - _popupClosedAt) < TimeSpan.FromMilliseconds(250))
        {
            e.Handled = true;
        }
    }
}
