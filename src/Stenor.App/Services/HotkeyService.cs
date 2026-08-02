using System.Threading.Channels;
using Stenor.Constants;
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
    private readonly record struct HookKeyEvent(int VirtualKey, bool IsKeyDown);

    // Physical modifier bits (hook thread only).
    private const int PhysicalLeftControl = 1 << 0;
    private const int PhysicalRightControl = 1 << 1;
    private const int PhysicalLeftShift = 1 << 2;
    private const int PhysicalRightShift = 1 << 3;
    private const int PhysicalLeftAlt = 1 << 4;
    private const int PhysicalRightAlt = 1 << 5;
    private const int PhysicalLeftWin = 1 << 6;
    private const int PhysicalRightWin = 1 << 7;

    // Generic modifier bits, matching HotkeySpec flags.
    private const int GenericControl = 1;
    private const int GenericShift = 2;
    private const int GenericAlt = 4;
    private const int GenericWin = 8;

    private readonly Logger _log;
    private readonly Channel<HookKeyEvent> _keyEvents = Channel.CreateBounded<HookKeyEvent>(
        new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private readonly CancellationTokenSource _shutdownCancellation = new();

    // Rooted delegate: the GC must never collect the hook callback.
    private NativeMethods.LowLevelKeyboardProc? _hookProcedure;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private nint _hookHandle;

    // --- Hot-path fields read by the hook callback (written via Volatile from other threads).
    private volatile bool _suspended;
    private int _configuredMainVirtualKey = HotkeySpec.DefaultVirtualKey;
    private int _requiredModifierMask; // 0 for single-key hotkeys
    private int _shouldSwallowComboMainKey; // 1 when a matched combo's main key must be swallowed
    // --- Hook-thread-only state.
    private int _physicalModifierMask;
    private bool _swallowedMainKeyDown;

    // --- Consumer-thread-only state.
    private int _eventStreamModifierMask;
    private bool _isMainKeyDown;
    private bool _activePressMatched;
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

        if (!ready.Wait(TimeSpan.FromSeconds(5)) || _hookHandle == 0)
        {
            _log.Error("Low-level keyboard hook failed to install.");
        }
        else
        {
            _log.Info("Keyboard hook installed.");
        }

        _ = Task.Run(() => ProcessKeyEventsAsync(_shutdownCancellation.Token));
    }

    public void UpdateHotkey(HotkeySpec spec)
    {
        var modifierMask = (spec.Ctrl ? GenericControl : 0) | (spec.Shift ? GenericShift : 0)
                         | (spec.Alt ? GenericAlt : 0) | (spec.Win ? GenericWin : 0);
        Volatile.Write(ref _requiredModifierMask, modifierMask);
        Volatile.Write(ref _configuredMainVirtualKey, spec.VirtualKey);
        // Only swallow when the hotkey is a real combo whose main key is a printable/non-modifier
        // key. Bare modifiers (default Right Ctrl) must pass through to the OS untouched.
        Volatile.Write(ref _shouldSwallowComboMainKey,
            modifierMask != 0 && !HotkeySpec.IsModifierKey(spec.VirtualKey) ? 1 : 0);
        _log.Info($"Hotkey set to '{HotkeyDisplay.Describe(spec)}'.");
    }

    // ------------------------------------------------------------ hook thread

    private void HookThreadMain(ManualResetEventSlim ready)
    {
        try
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            _hookProcedure = HookCallback;
            _hookHandle = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_KEYBOARD_LL, _hookProcedure, NativeMethods.GetModuleHandleW(null), 0);
            ready.Set();

            if (_hookHandle == 0)
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
            if (_hookHandle != 0)
            {
                NativeMethods.UnhookWindowsHookEx(_hookHandle);
                _hookHandle = 0;
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
                var message = (uint)wParam;
                var isKeyDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                var isKeyUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
                if (isKeyDown || isKeyUp)
                {
                    var virtualKey = (int)info->vkCode;
                    UpdatePhysicalModifierMask(virtualKey, isKeyDown);
                    _keyEvents.Writer.TryWrite(new HookKeyEvent(virtualKey, isKeyDown));

                    if (!_suspended && _shouldSwallowComboMainKey == 1
                        && virtualKey == _configuredMainVirtualKey)
                    {
                        if (isKeyDown
                            && ToGenericModifierMask(_physicalModifierMask) == _requiredModifierMask)
                        {
                            _swallowedMainKeyDown = true;
                            return 1;
                        }
                        if (isKeyUp && _swallowedMainKeyDown)
                        {
                            _swallowedMainKeyDown = false;
                            return 1;
                        }
                    }
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void UpdatePhysicalModifierMask(int virtualKey, bool isKeyDown)
    {
        var modifierBit = virtualKey switch
        {
            VirtualKeys.LeftControl => PhysicalLeftControl,
            VirtualKeys.RightControl => PhysicalRightControl,
            VirtualKeys.LeftShift => PhysicalLeftShift,
            VirtualKeys.RightShift => PhysicalRightShift,
            VirtualKeys.LeftAlt => PhysicalLeftAlt,
            VirtualKeys.RightAlt => PhysicalRightAlt,
            VirtualKeys.LeftWin => PhysicalLeftWin,
            VirtualKeys.RightWin => PhysicalRightWin,
            _ => 0,
        };
        if (modifierBit != 0)
        {
            _physicalModifierMask = isKeyDown
                ? _physicalModifierMask | modifierBit
                : _physicalModifierMask & ~modifierBit;
        }
    }

    private static int ToGenericModifierMask(int physicalModifierMask) =>
        ((physicalModifierMask & (PhysicalLeftControl | PhysicalRightControl)) != 0
            ? GenericControl : 0)
        | ((physicalModifierMask & (PhysicalLeftShift | PhysicalRightShift)) != 0
            ? GenericShift : 0)
        | ((physicalModifierMask & (PhysicalLeftAlt | PhysicalRightAlt)) != 0
            ? GenericAlt : 0)
        | ((physicalModifierMask & (PhysicalLeftWin | PhysicalRightWin)) != 0
            ? GenericWin : 0);

    // --------------------------------------------------------- consumer task

    private async Task ProcessKeyEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var keyEvent in _keyEvents.Reader
                .ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    HandleKeyEvent(keyEvent);
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

    private void HandleKeyEvent(HookKeyEvent keyEvent)
    {
        UpdateEventStreamModifierMask(keyEvent);

        var configuredMainVirtualKey = Volatile.Read(ref _configuredMainVirtualKey);
        if (keyEvent.VirtualKey != configuredMainVirtualKey)
        {
            return;
        }

        if (keyEvent.IsKeyDown)
        {
            if (_isMainKeyDown)
            {
                return; // keyboard auto-repeat
            }
            _isMainKeyDown = true;

            if (_suspended)
            {
                return;
            }

            var requiredModifierMask = Volatile.Read(ref _requiredModifierMask);
            var isSingleKeyHotkey = requiredModifierMask == 0;
            if (!isSingleKeyHotkey && _eventStreamModifierMask != requiredModifierMask)
            {
                return; // combo pressed with wrong modifiers
            }

            _activePressMatched = true;
            _pressTimestamp = Environment.TickCount64;
            Pressed?.Invoke();
        }
        else
        {
            _isMainKeyDown = false;
            if (!_activePressMatched)
            {
                return;
            }
            _activePressMatched = false;

            if (_suspended)
            {
                return;
            }
            Released?.Invoke(TimeSpan.FromMilliseconds(Environment.TickCount64 - _pressTimestamp));
        }
    }

    private void UpdateEventStreamModifierMask(HookKeyEvent keyEvent)
    {
        var modifierBit = keyEvent.VirtualKey switch
        {
            VirtualKeys.LeftControl or VirtualKeys.RightControl => GenericControl,
            VirtualKeys.LeftShift or VirtualKeys.RightShift => GenericShift,
            VirtualKeys.LeftAlt or VirtualKeys.RightAlt => GenericAlt,
            VirtualKeys.LeftWin or VirtualKeys.RightWin => GenericWin,
            _ => 0,
        };
        if (modifierBit != 0)
        {
            // Generic bits are a slight simplification (releasing one of two held Ctrl keys
            // clears the bit), which is harmless for hotkey matching.
            _eventStreamModifierMask = keyEvent.IsKeyDown
                ? _eventStreamModifierMask | modifierBit
                : _eventStreamModifierMask & ~modifierBit;
        }
    }

    public void Dispose()
    {
        _shutdownCancellation.Cancel();
        _keyEvents.Writer.TryComplete();
        if (_hookThreadId != 0)
        {
            NativeMethods.PostThreadMessageW(_hookThreadId, NativeMethods.WM_QUIT, 0, 0);
        }
        _hookThread?.Join(TimeSpan.FromSeconds(2));
        _shutdownCancellation.Dispose();
    }
}
