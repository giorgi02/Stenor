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
    private UpdateManager? _updateManager;
    private VelopackAsset? _pendingUpdate;
    private bool _updateApplyScheduled;
    private bool _updateCheckRunning;

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
        _log.Info($"Network guard: {NetworkGuard.Summary}.");

        _showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        StartSettingsActivationListener();

        _services = BuildServices(_log);

        var settings = _services.GetRequiredService<SettingsStore>();
        settings.Load();
        _services.GetRequiredService<StartupManager>().Apply(settings.Current.LaunchAtStartup);

        _updateManager = CreateUpdateManager(settings.Current.UpdateFeedUrl);
        var tray = _services.GetRequiredService<TrayIcon>();
        tray.Initialize(settings.Current.ActivationMode);
        tray.SettingsRequested += OpenSettings;
        tray.QuitRequested += Shutdown;
        tray.CheckForUpdatesRequested += () => _ = CheckForUpdatesAsync(userInitiated: true);
        tray.RestartToUpdateRequested += RestartToApplyUpdate;
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

        _ = CheckForUpdatesAsync(userInitiated: false);

        // Off the UI thread: walks the install dir to size it, which may touch cold disk.
        var sizeUpdater = _services.GetRequiredService<UninstallSizeUpdater>();
        Task.Run(sizeUpdater.Refresh);

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
        services.AddSingleton<UninstallSizeUpdater>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<IHotkeyService>(sp => sp.GetRequiredService<HotkeyService>());
        services.AddSingleton<RecorderService>();
        services.AddSingleton<IRecorderService>(sp => sp.GetRequiredService<RecorderService>());
        services.AddSingleton<GeminiClientProvider>();
        services.AddSingleton<TranscriptionService>();
        services.AddSingleton<LiveTranscriptionService>();
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

    private static UpdateManager CreateUpdateManager(string? feedUrlOverride) =>
        string.IsNullOrWhiteSpace(feedUrlOverride)
            ? new UpdateManager(new GithubSource(UpdateRepoUrl, accessToken: null, prerelease: false))
            : new UpdateManager(feedUrlOverride);

    private static string CurrentVersion(UpdateManager manager) =>
        manager.CurrentVersion?.ToString()
        ?? typeof(App).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    /// <summary>Velopack: checks and stages updates in the background. Failures are logged
    /// and never block startup; user-initiated checks also report the result in the tray.</summary>
    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updateCheckRunning)
        {
            return;
        }

        _updateCheckRunning = true;
        var tray = _services?.GetService<TrayIcon>();
        tray?.SetUpdateCheckInProgress(true);
        try
        {
            var manager = _updateManager;
            if (manager is null)
            {
                return;
            }
            if (!manager.IsInstalled)
            {
                if (userInitiated)
                {
                    tray?.ShowInfo("Updates unavailable",
                        "Update checks are available in an installed copy of Stenor.");
                }
                return; // running unpackaged (dev build)
            }

            var pending = manager.UpdatePendingRestart;
            if (pending is not null)
            {
                UpdateReady(pending, tray);
                return;
            }

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                if (userInitiated)
                {
                    tray?.ShowInfo("Stenor is up to date",
                        $"You’re running the latest version ({CurrentVersion(manager)}).");
                }
                return;
            }

            await manager.DownloadUpdatesAsync(update);
            UpdateReady(update.TargetFullRelease, tray);
            _log?.Info($"Update {update.TargetFullRelease.Version} downloaded and ready.");
        }
        catch (Exception ex)
        {
            _log?.Warn("Background update check failed.", ex);
            if (userInitiated)
            {
                tray?.ShowError("Update check failed",
                    "Stenor couldn’t check for updates. Please try again later.");
            }
        }
        finally
        {
            _updateCheckRunning = false;
            if (_pendingUpdate is null)
            {
                tray?.SetUpdateCheckInProgress(false);
            }
        }
    }

    private void UpdateReady(VelopackAsset update, TrayIcon? tray)
    {
        _pendingUpdate = update;
        if (tray is not null)
        {
            tray.SetUpdateReady();
            tray.ShowInfo("Update ready",
                $"Stenor {update.Version} is ready. Restart Stenor to install it.");
        }
    }

    private void RestartToApplyUpdate()
    {
        if (_updateManager is null || _pendingUpdate is null)
        {
            return;
        }

        try
        {
            _updateManager.WaitExitThenApplyUpdates(
                _pendingUpdate, silent: true, restart: true);
            _updateApplyScheduled = true;
            Shutdown();
        }
        catch (Exception ex)
        {
            _log?.Warn("Failed to restart for update.", ex);
            _services?.GetService<TrayIcon>()?.ShowError(
                "Update restart failed", "Quit and reopen Stenor to install the update.");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstance)
        {
            if (!_updateApplyScheduled && _updateManager is not null && _pendingUpdate is not null)
            {
                try
                {
                    _updateManager.WaitExitThenApplyUpdates(
                        _pendingUpdate, silent: true, restart: false);
                }
                catch (Exception ex)
                {
                    _log?.Warn("Failed to schedule update on exit.", ex);
                }
            }
            _log?.Info("Stenor shutting down.");
            _services?.Dispose(); // disposes hook, recorder, tray, Gemini client
            _showSettingsEvent?.Dispose();
            _instanceMutex?.ReleaseMutex();
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
