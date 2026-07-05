# Stenor — Claude Code guide

Windows-only background dictation utility (Wispr Flow alternative): global hotkey → WASAPI
recording → Gemini transcription → paste into the focused app. C# / .NET 10 + WPF, tray-only.

## Commands

```powershell
dotnet build src/Stenor/Stenor.csproj -c Release          # must stay warning-clean (TreatWarningsAsErrors)
dotnet publish src/Stenor/Stenor.csproj -c Release -r win-x64 --self-contained   # production build
```

- Publish output: `src/Stenor/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/Stenor.exe`
- No test project; verification is the manual checklist in README.md plus a smoke run
  (launch exe → check `%APPDATA%\Stenor\logs\stenor.log` for "Stenor started", tray icon, quit via tray).
- **NuGet:** the machine-wide config has a dead feed (`nuget.lb.ge`). The repo-root
  `NuGet.config` (`<clear />` + nuget.org) must stay, or restores fail.

## Architecture

Single WPF exe, DI-wired singletons (`App.xaml.cs` → `BuildServices`). Entry:
`Program.Main` (runs Velopack first) → `App.OnStartup`.

| File | Responsibility |
|---|---|
| `Services/DictationController.cs` | State machine Idle→Recording→Transcribing→Injecting→Idle; all hotkey semantics (Hold/Toggle, 150 ms tap discard) |
| `Services/HotkeyService.cs` | `WH_KEYBOARD_LL` hook on a dedicated pump thread; raises `Pressed`/`Released(duration)` |
| `Services/RecorderService.cs` | Warm-primed WasapiCapture → 16 kHz/16-bit/mono WAV in memory; 5-min cap; device-change recovery |
| `Services/TranscriptionService.cs` | Google.GenAI SDK, model `gemini-3.1-flash-lite`; 30 s timeout, 1 retry on transient |
| `Services/InjectionService.cs` | Clipboard backup → SendInput Ctrl+V → restore; Unicode-typing fallback |
| `Services/SettingsStore.cs` | `%APPDATA%\Stenor\settings.json`; API key DPAPI-encrypted (CurrentUser) |
| `UI/OverlayWindow.xaml(.cs)` | Recording pill; `WS_EX_NOACTIVATE|TOOLWINDOW`, positioned on the active monitor in raw pixels |
| `UI/SettingsWindow.xaml(.cs)` | One-page settings; new instance per open, destroyed on close |
| `UI/TrayIcon.cs` | H.NotifyIcon menu (Settings, mode switch, Quit) |
| `Interop/NativeMethods.cs` | All P/Invoke (hand-written, no CsWin32) |

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
   close and after startup to hold the <70 MB idle RAM target (measured ~6 MB WS idle).

## Gemini API notes (verified against the SDK, v1.12.0)

- `new Client(apiKey: key)`; `client.Models.GenerateContentAsync(model, content, config, ct)`.
- Inline audio: `new Part { InlineData = new Blob { MimeType = "audio/wav", Data = bytes } }`.
- Text out: `response.Text`, fallback = concat non-`Thought` text parts of first candidate.
- Exceptions: `ClientError` (4xx → no retry), `ServerError` (5xx → one retry).

## Documentation language

README.md is written in Georgian (user preference). Code, comments, and this file are English.
