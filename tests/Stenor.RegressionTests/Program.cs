using System.IO;
using System.Threading.Channels;
using System.Windows;
using Google.GenAI.Types;
using Stenor.Interfaces;
using Stenor.Interop;
using Stenor.Models;
using Stenor.Services;

namespace Stenor.RegressionTests;

// Dependency-free regression runner. No Gemini calls, microphone capture, or real key injection.
internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                SpeechChecks();
                await LiveChecks();
                await ControllerChecks();
                await InjectionChecks();
                Console.WriteLine($"Passed {_passed} regression checks.");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                app.Shutdown(1);
            }
        });
        return app.Run();
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine($"PASS: {name}");
        _passed++;
    }

    private static async Task<T> Throws<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T ex)
        {
            Check(true, name);
            return ex;
        }
        throw new InvalidOperationException(name);
    }

    private static void SpeechChecks()
    {
        Check(!SpeechDetector.Analyze(Wav(30, _ => 0)).HasSpeech, "digital silence rejected");
        Check(!SpeechDetector.Analyze(Wav(30, _ => .01)).HasSpeech, "steady noise rejected");
        Check(!SpeechDetector.Analyze(Wav(10, t => t is >= 4 and < 4.02 ? .1 : .002)).HasSpeech,
            "isolated click rejected");
        foreach (var (duration, start) in new[] { (3, 0d), (30, 0d), (30, 14d), (30, 28d), (300, 149d) })
        {
            var wav = Wav(duration, t => t >= start && t < start + 2
                ? .04 + .02 * Math.Sin((t - start) * 20) : .002);
            Check(SpeechDetector.Analyze(wav).HasSpeech, $"speech retained: {duration}s clip, offset {start}s");
        }
        Check(SpeechDetector.Analyze(Wav(10, t => t is >= .9 and < 1.1 ? .05 : .002)).HasSpeech,
            "short word crossing a window boundary retained");
    }

    private static byte[] Wav(int seconds, Func<double, double> amplitude)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var samples = seconds * 16000;
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            // Constant frame energy with a speech-like changing envelope.
            writer.Write((short)(32767 * amplitude(i / 16000d) * (i % 2 == 0 ? 1 : -1)));
        }
        return stream.ToArray();
    }

    private static async Task LiveChecks()
    {
        var messages = Channel.CreateUnbounded<LiveServerMessage>();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var session = new LiveTranscriptionService.Session(
            async ct => await messages.Reader.ReadAsync(ct),
            input => { if (input.AudioStreamEnd == true) ended.TrySetResult(); return Task.CompletedTask; },
            () => ValueTask.CompletedTask, new Logger()))
        {
            messages.Writer.TryWrite(Message(new LiveServerContent
            {
                InputTranscription = new Transcription { Text = "synthetic prefix" },
                TurnComplete = true,
            }));
            Check(await session.Transcripts.ReadAsync() == "synthetic prefix", "live utterance delivered before finish");
            var finish = session.FinishAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            await ended.Task;
            messages.Writer.TryWrite(Message(new LiveServerContent
            {
                GenerationComplete = true,
                InputTranscription = new Transcription { Text = "synthetic tail", Finished = true },
            }));
            Check(await session.Transcripts.ReadAsync() == "synthetic tail", "live tail retained");
            Check(!finish.IsCompleted, "generation/utterance completion does not end the session");
            messages.Writer.TryWrite(Message(new LiveServerContent { TurnComplete = true }));
            await finish.WaitAsync(TimeSpan.FromSeconds(5));
            Check(true, "explicit final turn completes normally");
        }

        await using (var session = new LiveTranscriptionService.Session(
            async ct => { await Task.Delay(Timeout.Infinite, ct); return null; },
            _ => Task.CompletedTask, () => ValueTask.CompletedTask, new Logger()))
        {
            await Throws<LiveTranscriptionService.LiveTranscriptionException>(
                () => session.FinishAsync(TimeSpan.FromMilliseconds(30), CancellationToken.None),
                "live drain timeout is a failure");
        }

        await using (var session = new LiveTranscriptionService.Session(
            _ => Task.FromException<LiveServerMessage?>(new IOException("synthetic receive failure")),
            _ => Task.CompletedTask, () => ValueTask.CompletedTask, new Logger()))
        {
            await Throws<IOException>(async () =>
            {
                await foreach (var _ in session.Transcripts.ReadAllAsync()) { }
            }, "receive error reaches transcript consumer");
            await Throws<IOException>(() => session.FinishAsync(TimeSpan.FromSeconds(1), CancellationToken.None),
                "receive error also reaches finish");
        }

        await using (var session = new LiveTranscriptionService.Session(
            _ => Task.FromResult<LiveServerMessage?>(null), _ => Task.CompletedTask,
            () => ValueTask.CompletedTask, new Logger()))
        {
            await Throws<LiveTranscriptionService.LiveTranscriptionException>(
                () => session.FinishAsync(TimeSpan.FromSeconds(1), CancellationToken.None),
                "premature socket close is a failure");
        }

        await using (var session = new LiveTranscriptionService.Session(
            async ct => { await Task.Delay(Timeout.Infinite, ct); return null; },
            _ => Task.FromException(new IOException("synthetic send failure")),
            () => ValueTask.CompletedTask, new Logger()))
        {
            await Throws<IOException>(() => session.FinishAsync(TimeSpan.FromSeconds(1), CancellationToken.None),
                "AudioStreamEnd send failure is preserved");
        }

        await using (var session = new LiveTranscriptionService.Session(
            async ct => { await Task.Delay(Timeout.Infinite, ct); return null; },
            _ => Task.CompletedTask, () => ValueTask.CompletedTask, new Logger()))
        {
            using var cts = new CancellationTokenSource();
            var finish = session.FinishAsync(TimeSpan.FromSeconds(3), cts.Token);
            cts.Cancel();
            await Throws<OperationCanceledException>(() => finish, "user cancellation remains cancellation");
        }
    }

    private static LiveServerMessage Message(LiveServerContent content) => new() { ServerContent = content };

    private static async Task ControllerChecks()
    {
        foreach (var scenario in new[] { "success", "receive failure", "timeout", "blocked input", "cancel", "batch fallback" })
        {
            var messages = Channel.CreateUnbounded<LiveServerMessage>();
            var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = new LiveTranscriptionService.Session(
                async ct => await messages.Reader.ReadAsync(ct),
                input =>
                {
                    if (input.AudioStreamEnd == true && scenario == "success")
                        messages.Writer.TryWrite(Message(new LiveServerContent { TurnComplete = true }));
                    return Task.CompletedTask;
                },
                () => { disposed.TrySetResult(); return ValueTask.CompletedTask; }, new Logger());
            var log = new Logger();
            var settings = new SettingsStore(log, new FakeSecrets());
            // In-memory settings only: never Load/Save the user's API key or preferences.
            settings.Current.ApiKeyEncrypted = "synthetic";
            settings.Current.ActivationMode = ActivationMode.Toggle;
            settings.Current.LiveTyping = true;
            using var clients = new GeminiClientProvider(settings);
            var hotkeys = new FakeHotkeys();
            var recorder = new FakeRecorder();
            var overlay = new FakeOverlay();
            var injector = new FakeInjector(scenario == "blocked input");
            var controller = new DictationController(log, settings, hotkeys, recorder,
                new TranscriptionService(log, clients), (_, _) => Task.FromResult(session), injector, overlay, overlay);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.DictationCompleted += () => completed.TrySetResult();
            controller.Initialize();
            hotkeys.Press();
            if (scenario != "batch fallback")
            {
                messages.Writer.TryWrite(Message(new LiveServerContent
                {
                    InputTranscription = new Transcription { Text = "synthetic controller text" },
                }));
                await injector.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            switch (scenario)
            {
                case "success":
                case "timeout":
                    hotkeys.Press();
                    break;
                case "receive failure":
                    messages.Writer.TryComplete(new IOException("synthetic receive failure"));
                    break;
                case "cancel":
                    overlay.Cancel();
                    break;
                case "batch fallback":
                    messages.Writer.TryComplete(new IOException("synthetic receive failure"));
                    await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(recorder.Recording, "live failure before text keeps recording for fallback");
                    // Force the real batch path to fail at key lookup, before any network call.
                    settings.Current.ApiKeyEncrypted = null;
                    hotkeys.Press();
                    break;
            }
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Check(!recorder.Recording, $"controller {scenario}: recorder stopped");
            Check(overlay.Done == (scenario == "success" ? 1 : 0), $"controller {scenario}: correct success status");
            var expectedError = scenario switch
            {
                "blocked input" => "synthetic blocked input",
                "receive failure" or "timeout" => "Live typing did not finish",
                "batch fallback" => "No API key configured",
                _ => null,
            };
            Check(expectedError is null ? overlay.Error is null : overlay.Error?.StartsWith(expectedError) == true,
                $"controller {scenario}: correct error or cancellation outcome");
            await session.DisposeAsync();
        }
    }

    private sealed class FakeSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    private sealed class FakeHotkeys : IHotkeyService
    {
        public event Action? Pressed;
        public event Action<TimeSpan>? Released { add { } remove { } }
        public void Press() => Pressed?.Invoke();
    }

    private sealed class FakeRecorder : IRecorderService
    {
        public bool Recording;
        public float CurrentLevel => 0;
        public event Action? MaxDurationReached { add { } remove { } }
        public event Action<string>? Failed { add { } remove { } }
        public event Action<byte[]>? PcmChunkAvailable { add { } remove { } }
        public void Start() => Recording = true;
        public void Cancel() => Recording = false;
        public byte[]? Stop()
        {
            Recording = false;
            return Wav(3, t => t < 2 ? .04 + .02 * Math.Sin(t * 20) : .002);
        }
    }

    private sealed class FakeInjector(bool fail) : ITextInjector
    {
        public readonly TaskCompletionSource Attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InjectAsync(string text, bool useUnicodeTyping)
        {
            Attempted.TrySetResult();
            return fail ? Task.FromException(new TextInjectionException("synthetic blocked input", false))
                : Task.CompletedTask;
        }
    }

    private sealed class FakeOverlay : IDictationOverlay, ITrayNotifier
    {
        public int Done;
        public string? Error;
        public event Action? CancelRequested;
        public void Cancel() => CancelRequested?.Invoke();
        public void ShowRecording(Func<float> levelSource) { }
        public void ShowTranscribing() { }
        public void ShowDone() => Done++;
        public void ShowError(string message) => Error = message;
        public void ShowError(string title, string message) { }
        public void Hide() { }
    }

    private static async Task InjectionChecks()
    {
        // Use the real STA clipboard path, restoring the user's data afterwards. Native
        // input is replaced at its boundary, so no keys reach the user's focused app.
        var source = Clipboard.GetDataObject();
        var original = new DataObject();
        if (source is not null)
        {
            foreach (var format in source.GetFormats(autoConvert: false))
            {
                original.SetData(format, source.GetData(format, autoConvert: false), autoConvert: false);
            }
        }
        try
        {
            const string transcript = "Synthetic regression transcript";
            Clipboard.SetText("synthetic clipboard backup");
            var blocked = new InjectionService(new Logger(), _ => 0);
            var error = await Throws<TextInjectionException>(() => blocked.InjectAsync(transcript, false),
                "blocked paste reports failure");
            Check(!error.MayHaveInjectedText, "zero input is distinguished from partial input");
            await Task.Delay(350);
            Check(Clipboard.GetText() == transcript, "blocked paste leaves recoverable transcript on clipboard");

            var batches = 0;
            var partial = new InjectionService(new Logger(), inputs =>
            {
                if (inputs.All(i => i.U.ki.wVk != 0)) return (uint)inputs.Length;
                return ++batches == 1 ? (uint)inputs.Length : 0;
            });
            var longText = new string('x', 80);
            error = await Throws<TextInjectionException>(() => partial.InjectAsync(longText, true),
                "partly blocked Unicode typing reports failure");
            Check(error.MayHaveInjectedText && batches == 2, "partial Unicode input stops without replaying the prefix");
            Check(Clipboard.GetText() == longText, "partial Unicode input keeps a recovery copy");

            Clipboard.SetText("synthetic clipboard backup");
            var success = new InjectionService(new Logger(), inputs => (uint)inputs.Length);
            await success.InjectAsync(transcript, false);
            Check(Clipboard.GetText() == "synthetic clipboard backup", "successful paste restores clipboard");
            await success.InjectAsync(transcript, true);
            Check(Clipboard.GetText() == "synthetic clipboard backup", "successful Unicode typing leaves clipboard alone");
        }
        finally
        {
            if (source is null) Clipboard.Clear();
            else Clipboard.SetDataObject(original, true);
        }
    }
}
