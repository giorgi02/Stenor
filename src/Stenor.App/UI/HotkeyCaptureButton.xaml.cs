using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Stenor.Models;
using Stenor.Services;

namespace Stenor.UI;

/// <summary>
/// Button showing the current hotkey; clicking it captures the next key or combo pressed in
/// the owning window (Esc cancels). The global hook is suspended while capturing so the
/// pressed key does not also trigger a dictation. Shared by Settings and the setup wizard.
/// </summary>
public partial class HotkeyCaptureButton : UserControl
{
    private HotkeySpec _hotkey = HotkeySpec.Default;
    private bool _isCapturing;
    private Window? _window;

    /// <summary>Set by the host window so capture can suspend the global hook.</summary>
    public HotkeyService? HotkeyService { get; set; }

    /// <summary>Raised after a capture ends with a new hotkey.</summary>
    public event Action? HotkeyChanged;

    public HotkeyCaptureButton()
    {
        InitializeComponent();
        Hotkey = HotkeySpec.Default;
    }

    public HotkeySpec Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            CaptureButton.Content = HotkeyDisplay.Describe(value);
        }
    }

    /// <summary>Ends an in-progress capture without changing the hotkey. Hosts call this
    /// when they close or move the control off-screen.</summary>
    public void CancelCapture()
    {
        if (_isCapturing)
        {
            EndCapture(null);
        }
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (_isCapturing)
        {
            return;
        }
        _window = Window.GetWindow(this);
        if (_window is null)
        {
            return;
        }
        _isCapturing = true;
        if (HotkeyService is not null)
        {
            HotkeyService.Suspended = true;
        }
        CaptureButton.Content = "Press a key or combo…";
        _window.PreviewKeyDown += OnCaptureKeyDown;
        _window.PreviewKeyUp += OnCaptureKeyUp;
    }

    private void OnCaptureKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            EndCapture(null);
            return;
        }
        if (IsModifier(key))
        {
            CaptureButton.Content = DescribeHeldModifiers() + "…";
            return;
        }

        var modifiers = Keyboard.Modifiers;
        EndCapture(new HotkeySpec
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
            EndCapture(new HotkeySpec { VirtualKey = KeyInterop.VirtualKeyFromKey(key) });
        }
    }

    private void EndCapture(HotkeySpec? captured)
    {
        if (_window is not null)
        {
            _window.PreviewKeyDown -= OnCaptureKeyDown;
            _window.PreviewKeyUp -= OnCaptureKeyUp;
            _window = null;
        }
        _isCapturing = false;
        if (HotkeyService is not null)
        {
            HotkeyService.Suspended = false;
        }
        if (captured is not null)
        {
            Hotkey = captured;
            HotkeyChanged?.Invoke();
        }
        else
        {
            Hotkey = _hotkey; // restore the label after a cancelled capture
        }
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
}
