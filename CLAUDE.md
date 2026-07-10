# Stenor — Claude Code guide

Windows-only background dictation utility (Wispr Flow alternative): global hotkey → WASAPI
recording → Gemini transcription → paste into the focused app. An opt-in "Live typing" mode
streams audio to Gemini Live instead and types text while the user speaks.
C# / .NET 10 + WPF, tray-only.

## Commands

```powershell
dotnet build Stenor.slnx -c Release                       # must stay warning-clean (TreatWarningsAsErrors)
dotnet publish src/Stenor.App/Stenor.App.csproj -c Release -r win-x64 --self-contained   # production build
pwsh scripts/pack.ps1                                     # Velopack installer → releases/ (needs: dotnet tool install -g vpk)
```

- Publish output: `src/Stenor.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/Stenor.exe`
- Releases/auto-update: Velopack + GitHub Releases feed (`GithubSource` in `App.xaml.cs`).
  Primary path is CI: bump `<Version>` in Stenor.App.csproj, push, then trigger the
  `Release` workflow by hand (GitHub → Actions → Run workflow; `.github/workflows/release.yml`,
  `workflow_dispatch`) — it packs + uploads the release. Manual fallback runbook in docs/release.md. Installer is unsigned — SmartScreen warns on first run (known limitation).
- No test project; verification is the manual checklist in docs/manual-checklist.md plus a smoke run
  (launch exe → check `%APPDATA%\Stenor\logs\stenor.log` for "Stenor started", tray icon, quit via tray).
- **NuGet:** the machine-wide config has a dead feed (`nuget.lb.ge`). The repo-root
  `NuGet.config` (`<clear />` + nuget.org) must stay, or restores fail.

## Architecture

Two projects: **`src/Stenor.Core`** (plain `net10.0` class library — no WPF, no Windows APIs:
the dictation state machine, models, settings/JSON, Gemini transcription, logging) and
**`src/Stenor.App`** (`net10.0-windows` WPF exe, assembly name `Stenor` — UI, bootstrap, DI
wiring, and *all* Windows integration: keyboard hook, WASAPI, SendInput/clipboard, DPAPI,
registry, every P/Invoke). DI-wired singletons (`App.xaml.cs` → `BuildServices`). Entry:
`Program.Main` (runs Velopack first) → `App.OnStartup`.

Core stays platform-neutral via interfaces in `Stenor.Core/Interfaces/` (namespace
`Stenor.Interfaces`), each implemented in App:
`IDictationOverlay`→`OverlayController`, `ITrayNotifier`→`TrayIcon`,
`ITextInjector`→`InjectionService`, `IHotkeyService`→`HotkeyService`,
`IRecorderService`→`RecorderService`, `ISecretProtector`→`DpapiSecretProtector`.
Keep it that way: new Windows/UI dependencies go in App behind a Core interface.

| File | Responsibility |
|---|---|
| `Stenor.Core/Services/DictationController.cs` | State machine Idle→Recording→Transcribing→Injecting→Idle; all hotkey semantics (Hold/Toggle, 150 ms tap discard); live-typing cycle (PCM channel → send pump → sequential inject pump; batch fallback when the live session typed nothing) |
| `Stenor.Core/Services/TranscriptionService.cs` | Batch path: `GenerateContentAsync`, model `gemini-3.1-flash-lite`; 30 s timeout, 1 retry on transient; prompt template embedded from `Prompts/TranscriptionPrompt.md` (`{languageHint}` placeholder) |
| `Stenor.Core/Services/LiveTranscriptionService.cs` | Live-typing sessions: Gemini Live WebSocket, model `gemini-3.1-flash-live-preview`, `inputAudioTranscription`, auto-VAD ON (tuned start sensitivity/prefix padding); yields append-only per-utterance transcript chunks via a Channel; finish = `AudioStreamEnd` |
| `Stenor.Core/Services/GeminiClientProvider.cs` | Single cached Google.GenAI `Client` keyed on the API key (invalidated on settings change); shared by batch + live. Replaced/invalidated clients are dropped, never disposed — disposing aborts in-flight requests |
| `Stenor.Core/Services/SettingsStore.cs` | `%APPDATA%\Stenor\settings.json`; API key encrypted via `ISecretProtector` (DPAPI impl in App) |
| `Stenor.App/Services/HotkeyService.cs` | `WH_KEYBOARD_LL` hook on a dedicated pump thread; raises `Pressed`/`Released(duration)` |
| `Stenor.App/Services/RecorderService.cs` | Warm-primed WasapiCapture → 16 kHz/16-bit/mono WAV in memory; 5-min cap; device-change recovery; `PcmChunkAvailable` raw-PCM tap (capture thread, only converted while a handler is attached) |
| `Stenor.App/Services/InjectionService.cs` | Clipboard backup → SendInput Ctrl+V → restore; Unicode-typing fallback |
| `Stenor.App/Services/UninstallSizeUpdater.cs` | Rewrites uninstall-entry `EstimatedSize` as REG_DWORD at startup (Velopack writes REG_QWORD → blank Control Panel "Size") |
| `Stenor.App/Services/NetworkGuard.cs` | Startup probe for networks that advertise but blackhole IPv6 (.NET walks every AAAA at ~21 s each — every Gemini call times out; no Happy Eyeballs): TCP-443 probes the Gemini host over v6+v4 in raw Winsock, sets process-wide `System.Net.DisableIPv6` when only IPv4 works. Must run in `Program.Main` before any managed socket is created — the runtime latches that switch on first socket use (also why the probe can't use `System.Net.Sockets`; managed `Dns` is safe, verified) |
| `Stenor.App/Interop/NativeMethods.cs` | All P/Invoke (hand-written, no CsWin32) |
| `Stenor.App/UI/OverlayWindow.xaml(.cs)` | Recording pill; `WS_EX_NOACTIVATE|TOOLWINDOW`, positioned on the active monitor in raw pixels |
| `Stenor.App/UI/SettingsWindow.xaml(.cs)` | One-page settings; new instance per open, destroyed on close |
| `Stenor.App/UI/TrayIcon.cs` | H.NotifyIcon menu (Settings, mode switch, Quit) |
| `Stenor.App/UI/HotkeyDisplay.cs` | Hotkey display names (`Describe`/`KeyName`); layout-aware fallback via GetKeyNameText |

## Hard constraints — do not violate

1. **Hook callback hot path** (`HotkeyService.HookCallback`): no allocation, no locks, no
   logging, no I/O. It only filters injected events, updates a bitmask, `TryWrite`s to a
   Channel, and decides combo swallowing from precomputed fields. Slow LL hooks lag the entire
   system keyboard and Windows silently removes them.
2. **Never log** the API key, transcripts, or audio. Log event names and error types only.
3. **The overlay must never take focus** (would break paste targeting). Keep
   `ShowActivated=False` and the WS_EX_NOACTIVATE style; position via `SetWindowPos` with
   `SWP_NOACTIVATE`.
4. **All injected input must carry `NativeMethods.InjectionSentinel`** in `dwExtraInfo` so the
   hook ignores Stenor's own keystrokes.
5. **Bare-modifier hotkeys (default Right Ctrl) are never swallowed**; only the main key of a
   full combo may return 1 from the hook.
6. The app must **never crash to desktop**: services catch their own failures and surface them
   via overlay + tray balloon; global handlers in `App.xaml.cs` log and continue.
7. Keep the build **warning-clean** — `TreatWarningsAsErrors` is on.
8. Settings window is **destroyed on close** (never hidden); `App.TrimWorkingSet()` runs after
   startup, after settings close, and ~30 s after each dictation cycle (scheduled via
   `App.ScheduleTrim`; `DictationController.DictationStarted` cancels a pending trim so the
   blocking GC never fires mid-recording) to hold the <70 MB idle RAM target (measured ~6 MB
   WS idle).

## Gemini API notes (verified against the SDK, v1.13.0)

- `new Client(apiKey: key)`; `client.Models.GenerateContentAsync(model, content, config, ct)`.
- Inline audio: `new Part { InlineData = new Blob { MimeType = "audio/wav", Data = bytes } }`.
- Text out: `response.Text`, fallback = concat non-`Thought` text parts of first candidate.
- Exceptions: `ClientError` (4xx → no retry), `ServerError` (5xx → one retry).
- Live API: `client.Live.ConnectAsync(model, LiveConnectConfig, ct)` → `AsyncSession`
  (`SendRealtimeInputAsync`, `ReceiveAsync` — null on graceful close, `DisposeAsync`).
  `ConnectAsync` reads SetupComplete itself, so bad keys/configs throw right there. Audio in:
  `Audio = new Blob { MimeType = "audio/pcm;rate=16000", Data = pcm }` (raw PCM16, no WAV
  header). Verified against the real API (2026-07): the live model only supports **AUDIO**
  response modality (TEXT → socket close); `InputTranscription.Text` arrives **one utterance
  at a time when auto-VAD detects a pause** — with VAD disabled + manual ActivityStart/End the
  whole transcript arrives only after ActivityEnd (useless for live typing), so keep auto-VAD
  on and finish with `AudioStreamEnd = true`. `TurnComplete` fires after *every* utterance —
  only treat it as "transcript done" once finishing. Model replies (`ModelTurn` audio) are
  discarded; enum members are PascalCase (`Modality.Audio`).

## Documentation language

README.md is written in Georgian (user preference). Code, comments, and this file are English.
