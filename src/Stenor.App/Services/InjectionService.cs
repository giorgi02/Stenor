using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Stenor.Constants;
using Stenor.Interfaces;
using Stenor.Interop;

namespace Stenor.Services;

/// <summary>
/// Injects transcribed text into the focused app via clipboard + simulated Ctrl+V:
/// back up clipboard, set text, SendInput Ctrl+V, restore the clipboard ~300 ms later.
/// Every injected key event carries the STNR dwExtraInfo sentinel so the keyboard hook
/// ignores it, and any physically-held modifiers are released first so they cannot corrupt
/// the paste chord. An optional per-character KEYEVENTF_UNICODE fallback handles apps that
/// block paste. Limitation (UIPI): a non-elevated Stenor cannot inject into elevated windows.
/// </summary>
public sealed class InjectionService : ITextInjector
{
    private static readonly TimeSpan ClipboardRestoreDelay = TimeSpan.FromMilliseconds(300);

    private readonly Logger _log;

    private readonly Func<NativeMethods.INPUT[], uint> _sendInput;

    public InjectionService(Logger log)
        : this(log, inputs => NativeMethods.SendInput((uint)inputs.Length, inputs, NativeMethods.INPUT.Size)) { }

    internal InjectionService(Logger log, Func<NativeMethods.INPUT[], uint> sendInput)
    {
        _log = log;
        _sendInput = sendInput;
    }

    private sealed class InputDeliveryException(bool mayHaveInjectedText) : Exception
    {
        public bool MayHaveInjectedText { get; } = mayHaveInjectedText;
    }

    public async Task InjectAsync(string text, bool useUnicodeTyping)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            await InjectCoreAsync(text, useUnicodeTyping).ConfigureAwait(false);
        }
        catch (InputDeliveryException ex)
        {
            // Do not restore the old clipboard after a failed paste. Keep a persistent copy
            // for manual recovery, including when Unicode typing was blocked partway through.
            var copied = await OnStaAsync(Application.Current.Dispatcher,
                () => TrySetClipboardText(text, persistent: true)).ConfigureAwait(false);
            var message = copied
                ? ex.MayHaveInjectedText
                    ? "Text was only partly inserted. A copy is on the clipboard; check the field before pasting."
                    : "Could not insert text. It is on the clipboard - paste it manually with Ctrl+V."
                : "Could not insert text or copy it to the clipboard. Check the target app and try again.";
            throw new TextInjectionException(message, ex.MayHaveInjectedText);
        }
    }

    private async Task InjectCoreAsync(string text, bool useUnicodeTyping)
    {
        if (useUnicodeTyping)
        {
            ReleaseStrayModifiers();
            TypeUnicode(text);
            return;
        }

        var dispatcher = Application.Current.Dispatcher;
        var backup = await OnStaAsync(dispatcher, BackupClipboard).ConfigureAwait(false);
        var copied = await OnStaAsync(dispatcher, () => TrySetClipboardText(text)).ConfigureAwait(false);
        if (!copied)
        {
            _log.Warn("Clipboard was locked by another app; falling back to Unicode typing.");
            ReleaseStrayModifiers();
            TypeUnicode(text);
            return;
        }

        ReleaseStrayModifiers();
        SendCtrlV();

        await Task.Delay(ClipboardRestoreDelay).ConfigureAwait(false);
        await OnStaAsync(dispatcher, () =>
        {
            RestoreClipboard(backup);
            return true;
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- clipboard

    private sealed class ClipboardBackup
    {
        public string? Text;
        public BitmapSource? Image;
        public StringCollection? Files;
    }

    private ClipboardBackup BackupClipboard()
    {
        var backup = new ClipboardBackup();
        try
        {
            if (Clipboard.ContainsText())
            {
                backup.Text = Clipboard.GetText();
            }
            if (Clipboard.ContainsImage())
            {
                backup.Image = Clipboard.GetImage();
            }
            if (Clipboard.ContainsFileDropList())
            {
                backup.Files = Clipboard.GetFileDropList();
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Clipboard backup failed; original contents may be lost.", ex);
        }
        return backup;
    }

    private bool TrySetClipboardText(string text, bool persistent = false)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: persistent);
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(60);
            }
        }
        return false;
    }

    private void RestoreClipboard(ClipboardBackup backup)
    {
        try
        {
            var data = new DataObject();
            var any = false;
            if (backup.Text is not null)
            {
                data.SetText(backup.Text);
                any = true;
            }
            if (backup.Image is not null)
            {
                data.SetImage(backup.Image);
                any = true;
            }
            if (backup.Files is not null)
            {
                data.SetFileDropList(backup.Files);
                any = true;
            }

            if (any)
            {
                Clipboard.SetDataObject(data, copy: true);
            }
            else
            {
                Clipboard.Clear();
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Clipboard restore failed.", ex);
        }
    }

    private static Task<T> OnStaAsync<T>(Dispatcher dispatcher, Func<T> action) =>
        dispatcher.InvokeAsync(action, DispatcherPriority.Send).Task;

    // -------------------------------------------------------------- keyboard

    private const ushort VkControl = VirtualKeys.Control;
    private const ushort VkV = 0x56;
    private const ushort VkReturn = 0x0D;

    private void SendCtrlV()
    {
        var inputs = new[]
        {
            Key(VkControl, up: false),
            Key(VkV, up: false),
            Key(VkV, up: true),
            Key(VkControl, up: true),
        };
        try
        {
            Send(inputs);
        }
        catch (InputDeliveryException)
        {
            // A partial chord can leave Ctrl/V logically down. Release our own keys;
            // failure of this best-effort cleanup must not hide the original failure.
            _sendInput([Key(VkV, up: true), Key(VkControl, up: true)]);
            throw;
        }
    }

    /// <summary>Releases any physically-held modifiers (e.g. the hotkey's own Ctrl in Toggle
    /// mode) so they cannot merge into the injected chord. The OS treats the eventual real
    /// key-up of an already-released key as a no-op.</summary>
    private void ReleaseStrayModifiers()
    {
        Span<ushort> modifierVks =
        [
            VirtualKeys.LeftShift, VirtualKeys.RightShift,
            VirtualKeys.LeftControl, VirtualKeys.RightControl,
            VirtualKeys.LeftAlt, VirtualKeys.RightAlt,
            VirtualKeys.LeftWin, VirtualKeys.RightWin,
        ];
        var releases = new List<NativeMethods.INPUT>(4);
        foreach (var vk in modifierVks)
        {
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                releases.Add(Key(vk, up: true));
            }
        }
        if (releases.Count > 0)
        {
            Send(releases.ToArray());
            Thread.Sleep(15); // give the target app a moment to process the modifier change
        }
    }

    private void TypeUnicode(string text)
    {
        const int chunkSize = 24;
        var anyTextSent = false;
        var batch = new List<NativeMethods.INPUT>(chunkSize * 2);
        foreach (var ch in text)
        {
            if (ch == '\r')
            {
                continue;
            }
            if (ch == '\n')
            {
                batch.Add(Key(VkReturn, up: false));
                batch.Add(Key(VkReturn, up: true));
            }
            else
            {
                batch.Add(UnicodeKey(ch, up: false));
                batch.Add(UnicodeKey(ch, up: true));
            }

            if (batch.Count >= chunkSize * 2)
            {
                SendTextBatch(batch.ToArray(), ref anyTextSent);
                batch.Clear();
                Thread.Sleep(5);
            }
        }
        if (batch.Count > 0)
        {
            SendTextBatch(batch.ToArray(), ref anyTextSent);
        }
    }

    private void SendTextBatch(NativeMethods.INPUT[] inputs, ref bool anyTextSent)
    {
        try
        {
            Send(inputs);
            anyTextSent = true;
        }
        catch (InputDeliveryException ex)
        {
            throw new InputDeliveryException(anyTextSent || ex.MayHaveInjectedText);
        }
    }

    private static NativeMethods.INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC),
                dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
                dwExtraInfo = NativeMethods.InjectionSentinel,
            },
        },
    };

    private static NativeMethods.INPUT UnicodeKey(char ch, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE | (up ? NativeMethods.KEYEVENTF_KEYUP : 0),
                dwExtraInfo = NativeMethods.InjectionSentinel,
            },
        },
    };

    private void Send(NativeMethods.INPUT[] inputs)
    {
        var sent = _sendInput(inputs);
        if (sent != inputs.Length)
        {
            _log.Warn($"SendInput injected {sent}/{inputs.Length} events (blocked by an elevated window?).");
            throw new InputDeliveryException(sent > 0);
        }
    }
}
