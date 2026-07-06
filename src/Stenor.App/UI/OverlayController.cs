using System.Windows;
using System.Windows.Threading;
using Stenor.Interfaces;
using Stenor.Services;

namespace Stenor.UI;

/// <summary>
/// Owns the overlay window lifetime and marshals state changes from background threads onto
/// the UI thread. Done/error states auto-hide after 1 s / 3 s.
/// </summary>
public sealed class OverlayController : IDictationOverlay
{
    private readonly Logger _log;
    private OverlayWindow? _window;
    private DispatcherTimer? _hideTimer;

    public event Action? CancelRequested;

    public OverlayController(Logger log) => _log = log;

    public void ShowRecording(Func<float> levelSource) =>
        Post(() =>
        {
            CancelAutoHide();
            GetWindow().ShowRecording(levelSource);
        });

    public void ShowTranscribing() =>
        Post(() =>
        {
            CancelAutoHide();
            GetWindow().ShowTranscribing();
        });

    public void ShowDone() =>
        Post(() =>
        {
            GetWindow().ShowDone();
            AutoHide(TimeSpan.FromSeconds(1));
        });

    public void ShowError(string message) =>
        Post(() =>
        {
            GetWindow().ShowError(message);
            AutoHide(TimeSpan.FromSeconds(3));
        });

    public void Hide() =>
        Post(() =>
        {
            CancelAutoHide();
            _window?.HideOverlay();
        });

    private OverlayWindow GetWindow()
    {
        if (_window is null)
        {
            _window = new OverlayWindow();
            _window.CancelRequested += () => CancelRequested?.Invoke();
        }
        return _window;
    }

    private void AutoHide(TimeSpan after)
    {
        CancelAutoHide();
        _hideTimer = new DispatcherTimer { Interval = after };
        _hideTimer.Tick += (_, _) =>
        {
            CancelAutoHide();
            _window?.HideOverlay();
        };
        _hideTimer.Start();
    }

    private void CancelAutoHide()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
    }

    private void Post(Action action)
    {
        try
        {
            Application.Current?.Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            _log.Error("Overlay dispatch failed.", ex);
        }
    }
}
