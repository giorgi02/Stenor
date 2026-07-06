using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Stenor.Interfaces;
using Stenor.Models;
using Stenor.Services;

namespace Stenor.UI;

/// <summary>System tray icon with the Settings / activation-mode / quit menu.</summary>
public sealed class TrayIcon : ITrayNotifier, IDisposable
{
    private readonly Logger _log;
    private TaskbarIcon? _icon;
    private MenuItem? _holdItem;
    private MenuItem? _toggleItem;

    public event Action? SettingsRequested;
    public event Action? QuitRequested;
    public event Action<ActivationMode>? ModeChangeRequested;

    public TrayIcon(Logger log) => _log = log;

    public void Initialize(ActivationMode currentMode)
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "Stenor — hold the hotkey to dictate",
            IconSource = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Stenor.ico")),
        };

        var menu = new ContextMenu();

        var settingsItem = new MenuItem { Header = "Settings…" };
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Separator());

        _holdItem = new MenuItem { Header = "Activation: Hold (push-to-talk)", IsCheckable = true };
        _holdItem.Click += (_, _) => ModeChangeRequested?.Invoke(ActivationMode.Hold);
        menu.Items.Add(_holdItem);

        _toggleItem = new MenuItem { Header = "Activation: Toggle", IsCheckable = true };
        _toggleItem.Click += (_, _) => ModeChangeRequested?.Invoke(ActivationMode.Toggle);
        menu.Items.Add(_toggleItem);

        menu.Items.Add(new Separator());
        var quitItem = new MenuItem { Header = "Quit Stenor" };
        quitItem.Click += (_, _) => QuitRequested?.Invoke();
        menu.Items.Add(quitItem);

        _icon.ContextMenu = menu;
        _icon.TrayMouseDoubleClick += (_, _) => SettingsRequested?.Invoke();

        SetMode(currentMode);
        _icon.ForceCreate();
        _log.Info("Tray icon created.");
    }

    public void SetMode(ActivationMode mode)
    {
        if (_holdItem is not null)
        {
            _holdItem.IsChecked = mode == ActivationMode.Hold;
        }
        if (_toggleItem is not null)
        {
            _toggleItem.IsChecked = mode == ActivationMode.Toggle;
        }
    }

    public void ShowError(string title, string message)
    {
        try
        {
            _icon?.ShowNotification(title, message, NotificationIcon.Error);
        }
        catch (Exception ex)
        {
            _log.Warn("Balloon notification failed.", ex);
        }
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
