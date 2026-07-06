using System.Threading.Channels;
using Stenor.Interfaces;
using Stenor.Interop;
using Stenor.Models;
using Stenor.UI;

namespace Stenor.Services;

/// <summary>
/// Global hotkey detection via a WH_KEYBOARD_LL hook.
///
/// The hook callback is the hottest path in the app: a slow callback lags every keystroke
/// system-wide and Windows silently removes hooks that exceed its timeout. The callback
/// therefore only (1) skips injected events, (2) updates a modifier bitmask with integer ops,
/// (3) posts the event to a bounded channel, and (4) decides combo-swallowing from
/// pre-computed fields. Everything else (matching, debounce, hold/toggle semantics) runs on
/// a dedicated consumer task.
///
/// Raised events: <see cref="Pressed"/> on a genuine (non-repeat) hotkey down,
/// <see cref="Released"/> with the held duration on hotkey up. The controller maps these to
/// Hold/Toggle behavior.
/// </summary>
public sealed class HotkeyService : IHotkeyService, IDisposable
{
    private readonly record struct KeyEvent(int Vk, bool Down);

    // Physical modifier bits (hook thread only).
    private const int LCtrl = 1 << 0;
    private const int RCtrl = 1 << 1;
    private const int LShift = 1 << 2;
    private const int RShift = 1 << 3;
    private const int LAlt = 1 << 4;
    private const int RAlt = 1 << 5;
    private const int LWin = 1 << 6;
    private const int RWin = 1 << 7;

    // Generic modifier bits, matching HotkeySpec flags.
    private const int ModCtrl = 1;
    private const int ModShift = 2;
    private const int ModAlt = 4;
    private const int ModWin = 8;

    private readonly Logger _log;
    private readonly Channel<KeyEvent> _events = Channel.CreateBounded<KeyEvent>(
        new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private readonly CancellationTokenSource _cts = new();

    // Rooted delegate: the GC must never collect the hook callback.
    private NativeMethods.LowLevelKeyboardProc? _hookProc;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private nint _hook;

    // --- Hot-path fields read by the hook callback (written via Volatile from other threads).
    private volatile bool _suspended;
    private int _targetMainVk = HotkeySpec.VkRControl;
    private int _targetMods;          // generic bitmask; 0 for single-key hotkeys
    private int _swallowCombo;        // 1 when the main key of a matched combo must be swallowed
    // --- Hook-thread-only state.
    private int _physMods;
    private bool _swallowedMainDown;

    // --- Consumer-thread-only state.
    private int _streamMods;
    private bool _mainKeyDown;
    private bool _pressMatched;
    private long _pressTimestamp;

    public event Action? Pressed;
    public event Action<TimeSpan>? Released;

    public HotkeyService(Logger log) => _log = log;

    /// <summary>While true (Settings is capturing a new hotkey) events are ignored and nothing
    /// is swallowed.</summary>
    public bool Suspended
    {
        get => _suspended;
        set => _suspended = value;
    }

    public void Start()
    {
        if (_hookThread is not null)
        {
            return;
        }

        var ready = new ManualResetEventSlim(false);
        _hookThread = new Thread(() => HookThreadMain(ready))
        {
            Name = "Stenor.KeyboardHook",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _hookThread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(5)) || _hook == 0)
        {
            _log.Error("Low-level keyboard hook failed to install.");
        }
        else
        {
            _log.Info("Keyboard hook installed.");
        }

        _ = Task.Run(() => ConsumeEventsAsync(_cts.Token));
    }

    public void UpdateHotkey(HotkeySpec spec)
    {
        var mods = (spec.Ctrl ? ModCtrl : 0) | (spec.Shift ? ModShift : 0)
                 | (spec.Alt ? ModAlt : 0) | (spec.Win ? ModWin : 0);
        Volatile.Write(ref _targetMods, mods);
        Volatile.Write(ref _targetMainVk, spec.VirtualKey);
        // Only swallow when the hotkey is a real combo whose main key is a printable/non-modifier
        // key. Bare modifiers (default Right Ctrl) must pass through to the OS untouched.
        Volatile.Write(ref _swallowCombo, mods != 0 && !HotkeySpec.IsModifierKey(spec.VirtualKey) ? 1 : 0);
        _log.Info($"Hotkey set to '{HotkeyDisplay.Describe(spec)}'.");
    }

    // ------------------------------------------------------------ hook thread

    private void HookThreadMain(ManualResetEventSlim ready)
    {
        try
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            _hookProc = HookCallback;
            _hook = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_KEYBOARD_LL, _hookProc, NativeMethods.GetModuleHandleW(null), 0);
            ready.Set();

            if (_hook == 0)
            {
                return;
            }

            // LL hooks are delivered through this thread's message queue; pump until WM_QUIT.
            while (NativeMethods.GetMessageW(out _, 0, 0, 0) > 0)
            {
            }
        }
        catch (Exception ex)
        {
            _log.Error("Keyboard hook thread crashed.", ex);
        }
        finally
        {
            if (_hook != 0)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = 0;
            }
        }
    }

    private unsafe nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var info = (NativeMethods.KBDLLHOOKSTRUCT*)lParam;
            if ((info->flags & NativeMethods.LLKHF_INJECTED) == 0
                && info->dwExtraInfo != NativeMethods.InjectionSentinel)
            {
                var msg = (uint)wParam;
                var down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                var up = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
                if (down || up)
                {
                    var vk = (int)info->vkCode;
                    UpdatePhysicalModifiers(vk, down);
                    _events.Writer.TryWrite(new KeyEvent(vk, down));

                    if (!_suspended && _swallowCombo == 1 && vk == _targetMainVk)
                    {
                        if (down && GenericMods(_physMods) == _targetMods)
                        {
                            _swallowedMainDown = true;
                            return 1;
                        }
                        if (up && _swallowedMainDown)
                        {
                            _swallowedMainDown = false;
                            return 1;
                        }
                    }
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void UpdatePhysicalModifiers(int vk, bool down)
    {
        var bit = vk switch
        {
            0xA2 => LCtrl,
            0xA3 => RCtrl,
            0xA0 => LShift,
            0xA1 => RShift,
            0xA4 => LAlt,
            0xA5 => RAlt,
            0x5B => LWin,
            0x5C => RWin,
            _ => 0,
        };
        if (bit != 0)
        {
            _physMods = down ? _physMods | bit : _physMods & ~bit;
        }
    }

    private static int GenericMods(int phys) =>
        ((phys & (LCtrl | RCtrl)) != 0 ? ModCtrl : 0)
        | ((phys & (LShift | RShift)) != 0 ? ModShift : 0)
        | ((phys & (LAlt | RAlt)) != 0 ? ModAlt : 0)
        | ((phys & (LWin | RWin)) != 0 ? ModWin : 0);

    // --------------------------------------------------------- consumer task

    private async Task ConsumeEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    HandleEvent(e);
                }
                catch (Exception ex)
                {
                    _log.Error("Hotkey event handling failed.", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void HandleEvent(KeyEvent e)
    {
        TrackStreamModifiers(e);

        var mainVk = Volatile.Read(ref _targetMainVk);
        if (e.Vk != mainVk)
        {
            return;
        }

        if (e.Down)
        {
            if (_mainKeyDown)
            {
                return; // keyboard auto-repeat
            }
            _mainKeyDown = true;

            if (_suspended)
            {
                return;
            }

            var requiredMods = Volatile.Read(ref _targetMods);
            var isBareOrSingle = requiredMods == 0;
            if (!isBareOrSingle && _streamMods != requiredMods)
            {
                return; // combo pressed with wrong modifiers
            }

            _pressMatched = true;
            _pressTimestamp = Environment.TickCount64;
            Pressed?.Invoke();
        }
        else
        {
            _mainKeyDown = false;
            if (!_pressMatched)
            {
                return;
            }
            _pressMatched = false;

            if (_suspended)
            {
                return;
            }
            Released?.Invoke(TimeSpan.FromMilliseconds(Environment.TickCount64 - _pressTimestamp));
        }
    }

    private void TrackStreamModifiers(KeyEvent e)
    {
        var bit = e.Vk switch
        {
            0xA2 or 0xA3 => ModCtrl,
            0xA0 or 0xA1 => ModShift,
            0xA4 or 0xA5 => ModAlt,
            0x5B or 0x5C => ModWin,
            _ => 0,
        };
        if (bit != 0)
        {
            // Generic bits are a slight simplification (releasing one of two held Ctrl keys
            // clears the bit), which is harmless for hotkey matching.
            _streamMods = e.Down ? _streamMods | bit : _streamMods & ~bit;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _events.Writer.TryComplete();
        if (_hookThreadId != 0)
        {
            NativeMethods.PostThreadMessageW(_hookThreadId, NativeMethods.WM_QUIT, 0, 0);
        }
        _hookThread?.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
