using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Stenor.Interfaces;
using Stenor.Services;
using Stenor.UI;
using Velopack;
using Velopack.Sources;

namespace Stenor;

/// <summary>
/// Application bootstrap: single-instance guard, DI wiring, tray, global exception handling.
/// There is no main window - Stenor lives in the tray and shuts down only via its menu.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "Stenor.SingleInstance";
    private const string ShowSettingsEventName = "Stenor.ShowSettings";
    private const string UpdateRepoUrl = "https://github.com/giorgi02/Stenor";

    private Mutex? _instanceMutex;
    private bool _ownsInstance;
    private EventWaitHandle? _showSettingsEvent;
    private ServiceProvider? _services;
    private Logger? _log;
    private SettingsWindow? _settingsWindow;
    private CancellationTokenSource? _trimCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Second launch: poke the running instance to open Settings, then leave.
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSettingsEventName);
                signal.Set();
            }
            catch
            {
            }
            Shutdown();
            return;
        }
        _ownsInstance = true;

        _log = new Logger();
        RegisterGlobalExceptionHandlers(_log);

        _showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        StartSettingsActivationListener();

        _services = BuildServices(_log);

        var settings = _services.GetRequiredService<SettingsStore>();
        settings.Load();
        _services.GetRequiredService<StartupManager>().Apply(settings.Current.LaunchAtStartup);

        var tray = _services.GetRequiredService<TrayIcon>();
        tray.Initialize(settings.Current.ActivationMode);
        tray.SettingsRequested += OpenSettings;
        tray.QuitRequested += Shutdown;
        tray.ModeChangeRequested += mode =>
        {
            var updated = settings.Current.Clone();
            updated.ActivationMode = mode;
            settings.Save(updated);
        };

        var hotkeys = _services.GetRequiredService<HotkeyService>();
        hotkeys.Start();
        hotkeys.UpdateHotkey(settings.Current.Hotkey);

        settings.Changed += () =>
        {
            hotkeys.UpdateHotkey(settings.Current.Hotkey);
            tray.SetMode(settings.Current.ActivationMode);
            _services.GetRequiredService<StartupManager>().Apply(settings.Current.LaunchAtStartup);
        };

        var controller = _services.GetRequiredService<DictationController>();
        controller.Initialize();
        // A dictation cycle inflates the working set (WAV buffers, base64 request, first-use
        // WPF/HTTP state); trim shortly after it ends. Cancel while recording so the blocking
        // GC never pauses the keyboard-hook thread mid-dictation.
        controller.DictationStarted += CancelPendingTrim;
        controller.DictationCompleted += () => ScheduleTrim(TimeSpan.FromSeconds(30));

        // Warm up the capture device off the UI thread so the first hotkey press is instant.
        var recorder = _services.GetRequiredService<RecorderService>();
        Task.Run(recorder.Prime);

        if (settings.GetApiKey() is null)
        {
            OpenSettings();
        }

        CheckForUpdatesInBackground(settings.Current.UpdateFeedUrl);
        _log.Info("Stenor started.");

        // Once startup settles, return unused pages to the OS so the idle footprint stays small.
        ScheduleTrim(TimeSpan.FromSeconds(4));
    }

    /// <summary>Schedules a working-set trim after <paramref name="delay"/>. A newer schedule
    /// or <see cref="CancelPendingTrim"/> supersedes any pending one.</summary>
    private void ScheduleTrim(TimeSpan delay)
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token; // captured now: cts may be disposed by a later call before the task runs
        var previous = Interlocked.Exchange(ref _trimCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (token.IsCancellationRequested)
            {
                return; // superseded while the delay was running
            }
            TrimWorkingSet();
        });
    }

    private void CancelPendingTrim()
    {
        var previous = Interlocked.Exchange(ref _trimCts, null);
        previous?.Cancel();
        previous?.Dispose();
    }

    /// <summary>Compacts the GC heap and trims the working set. Trimmed pages reload on demand,
    /// which is imperceptible for a tray utility but keeps idle RAM low.</summary>
    private static void TrimWorkingSet()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Interop.NativeMethods.SetProcessWorkingSetSize(Interop.NativeMethods.GetCurrentProcess(), -1, -1);
    }

    private static ServiceProvider BuildServices(Logger log)
    {
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<StartupManager>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<IHotkeyService>(sp => sp.GetRequiredService<HotkeyService>());
        services.AddSingleton<RecorderService>();
        services.AddSingleton<IRecorderService>(sp => sp.GetRequiredService<RecorderService>());
        services.AddSingleton<TranscriptionService>();
        services.AddSingleton<ITextInjector, InjectionService>();
        services.AddSingleton<IDictationOverlay, OverlayController>();
        services.AddSingleton<TrayIcon>();
        services.AddSingleton<ITrayNotifier>(sp => sp.GetRequiredService<TrayIcon>());
        services.AddSingleton<DictationController>();
        services.AddTransient<SettingsWindow>();
        return services.BuildServiceProvider();
    }

    private void OpenSettings()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_settingsWindow is not null)
            {
                _settingsWindow.Activate();
                return;
            }
            if (_services is null)
            {
                return;
            }
            _settingsWindow = _services.GetRequiredService<SettingsWindow>();
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null; // destroyed, not hidden
                ScheduleTrim(TimeSpan.FromSeconds(1)); // reclaim the WPF memory the window used
            };
            _settingsWindow.Show();
            _settingsWindow.Activate();
        });
    }

    private void StartSettingsActivationListener()
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (_showSettingsEvent is { } signal && signal.WaitOne())
                {
                    _log?.Info("Second instance detected; opening Settings.");
                    OpenSettings();
                }
            }
            catch
            {
                // Handle disposed on shutdown.
            }
        })
        {
            Name = "Stenor.ActivationListener",
            IsBackground = true,
        };
        thread.Start();
    }

    private void RegisterGlobalExceptionHandlers(Logger log)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            log.Error("Unhandled UI exception.", args.Exception);
            args.Handled = true; // never crash to desktop
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            log.Error("Unhandled domain exception.", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            log.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>Velopack: checks and stages updates in the background. Defaults to GitHub
    /// Releases on this repo; a non-empty UpdateFeedUrl in settings.json overrides it (handy
    /// for testing against a local feed). Failures are logged and never block startup.</summary>
    private void CheckForUpdatesInBackground(string? feedUrlOverride)
    {
        Task.Run(async () =>
        {
            try
            {
                var manager = string.IsNullOrWhiteSpace(feedUrlOverride)
                    ? new UpdateManager(new GithubSource(UpdateRepoUrl, accessToken: null, prerelease: false))
                    : new UpdateManager(feedUrlOverride);
                if (!manager.IsInstalled)
                {
                    return; // running unpackaged (dev build)
                }
                var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
                if (update is null)
                {
                    return;
                }
                await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
                manager.WaitExitThenApplyUpdates(update, silent: true, restart: false);
                _log?.Info($"Update {update.TargetFullRelease.Version} staged; applies on next launch.");
            }
            catch (Exception ex)
            {
                _log?.Warn("Background update check failed.", ex);
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstance)
        {
            _log?.Info("Stenor shutting down.");
            _services?.Dispose(); // disposes hook, recorder, tray, Gemini client
            _showSettingsEvent?.Dispose();
            _instanceMutex?.ReleaseMutex();
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
