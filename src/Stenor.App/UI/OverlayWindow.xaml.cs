using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Stenor.Interop;

namespace Stenor.UI;

/// <summary>
/// The floating recording pill. Created once and reused; never activated (WS_EX_NOACTIVATE)
/// so keyboard focus - and therefore paste targeting - always stays with the user's app.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int BarCount = 7;
    private const double BarMaxHeight = 20;
    private const double BarMinHeight = 3;
    private static readonly double[] BarWeights = [0.55, 0.85, 1.0, 0.75, 0.95, 0.65, 0.45];

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _barHeights = new double[BarCount];
    private readonly DispatcherTimer _levelTimer;
    private readonly Random _jitter = new();
    private Storyboard? _spinnerStoryboard;
    private Func<float>? _levelSource;

    public event Action? CancelRequested;

    public OverlayWindow()
    {
        InitializeComponent();

        for (var i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = BarMinHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(1.7, 0, 1.7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)Resources["AccentBrush"],
            };
            _bars[i] = bar;
            BarsPanel.Children.Add(bar);
        }

        _levelTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _levelTimer.Tick += OnLevelTick;

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => PositionOnActiveMonitor(moveOnly: true);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST);
    }

    // ------------------------------------------------------------ public API

    public void ShowRecording(Func<float> levelSource)
    {
        _levelSource = levelSource;
        SwitchPanel(RecordingPanel);
        CancelButton.Visibility = Visibility.Visible;
        _levelTimer.Start();
        Reveal();
    }

    public void ShowTranscribing()
    {
        SwitchPanel(TranscribingPanel);
        CancelButton.Visibility = Visibility.Visible;
        StartSpinner();
        Reveal();
    }

    public void ShowDone()
    {
        SwitchPanel(DonePanel);
        CancelButton.Visibility = Visibility.Collapsed;
        Reveal();
    }

    public void ShowError(string message)
    {
        ErrorText.Text = message;
        SwitchPanel(ErrorPanel);
        CancelButton.Visibility = Visibility.Collapsed;
        Reveal();
    }

    public void HideOverlay()
    {
        _levelTimer.Stop();
        StopSpinner();
        _levelSource = null;
        Hide();
    }

    // -------------------------------------------------------------- plumbing

    private void SwitchPanel(UIElement active)
    {
        if (!ReferenceEquals(active, RecordingPanel))
        {
            _levelTimer.Stop();
        }
        if (!ReferenceEquals(active, TranscribingPanel))
        {
            StopSpinner();
        }
        RecordingPanel.Visibility = ReferenceEquals(active, RecordingPanel) ? Visibility.Visible : Visibility.Collapsed;
        TranscribingPanel.Visibility = ReferenceEquals(active, TranscribingPanel) ? Visibility.Visible : Visibility.Collapsed;
        DonePanel.Visibility = ReferenceEquals(active, DonePanel) ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = ReferenceEquals(active, ErrorPanel) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Reveal()
    {
        if (!IsVisible)
        {
            Show();
        }
        PositionOnActiveMonitor(moveOnly: false);
    }

    private void StartSpinner()
    {
        if (_spinnerStoryboard is null)
        {
            var animation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Storyboard.SetTarget(animation, Spinner);
            Storyboard.SetTargetProperty(animation,
                new PropertyPath("RenderTransform.(RotateTransform.Angle)"));
            _spinnerStoryboard = new Storyboard();
            _spinnerStoryboard.Children.Add(animation);
        }
        _spinnerStoryboard.Begin(this, true);
    }

    private void StopSpinner() => _spinnerStoryboard?.Stop(this);

    private void OnLevelTick(object? sender, EventArgs e)
    {
        var level = Math.Clamp(_levelSource?.Invoke() ?? 0f, 0f, 1f);
        // Perceptual boost so quiet speech still moves the bars.
        var boosted = Math.Sqrt(level);
        for (var i = 0; i < BarCount; i++)
        {
            var jitter = 0.85 + _jitter.NextDouble() * 0.3;
            var target = BarMinHeight + boosted * BarWeights[i] * jitter * (BarMaxHeight - BarMinHeight);
            _barHeights[i] += (target - _barHeights[i]) * 0.45;
            _bars[i].Height = Math.Clamp(_barHeights[i], BarMinHeight, BarMaxHeight);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();

    /// <summary>Places the pill at the bottom-center of the monitor hosting the currently
    /// focused window, in raw pixels via SetWindowPos (correct across mixed-DPI monitors).</summary>
    private void PositionOnActiveMonitor(bool moveOnly)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
        {
            return;
        }

        var monitor = NativeMethods.MonitorFromWindow(
            moveOnly ? hwnd : NativeMethods.GetForegroundWindow(),
            NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return;
        }

        var scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0)
        {
            scale = dpiX / 96.0;
        }

        var widthPx = (int)Math.Ceiling(ActualWidth * scale);
        var heightPx = (int)Math.Ceiling(ActualHeight * scale);
        var x = info.rcWork.Left + ((info.rcWork.Right - info.rcWork.Left) - widthPx) / 2;
        var y = info.rcWork.Bottom - heightPx - (int)(36 * scale);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
