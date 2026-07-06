using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

    public InjectionService(Logger log) => _log = log;

    public async Task InjectAsync(string text, bool useUnicodeTyping)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

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

    private bool TrySetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: false);
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

    private const ushort VkControl = 0x11;
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
        Send(inputs);
    }

    /// <summary>Releases any physically-held modifiers (e.g. the hotkey's own Ctrl in Toggle
    /// mode) so they cannot merge into the injected chord. The OS treats the eventual real
    /// key-up of an already-released key as a no-op.</summary>
    private void ReleaseStrayModifiers()
    {
        Span<ushort> modifierVks = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];
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
                Send(batch.ToArray());
                batch.Clear();
                Thread.Sleep(5);
            }
        }
        if (batch.Count > 0)
        {
            Send(batch.ToArray());
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
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, NativeMethods.INPUT.Size);
        if (sent != inputs.Length)
        {
            _log.Warn($"SendInput injected {sent}/{inputs.Length} events (blocked by an elevated window?).");
        }
    }
}
