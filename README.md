# Stenor

**Write with your voice — in any application.**

Hold a key, say what you want to write, release it — and the text appears exactly where your
cursor is: in Word, in the browser, in VS Code, in Slack or Teams. Speaking is far faster than
typing, and Stenor turns what you say into text with the help of Google Gemini.

## What it gives you

- **Faster writing** — emails, messages, documents, comments: say it and it is ready, no typing
  needed.
- **Works everywhere** — it has no window of its own and needs none: wherever a text field has
  focus, that is where the text lands.
- **Georgian and 96 more languages** — it recognises the language on its own and copes with
  mixed speech.
- **Stays out of your way** — it lives in the system tray, consumes almost nothing while idle,
  and is ready in a fraction of a second the moment you press the key.
- **Your data stays with you** — audio is sent for transcription to Google Gemini only, using
  your own API key; Stenor stores neither the audio nor the text, and never writes the key to
  its logs.

The only thing you need is a Gemini API key — get one for free at
<https://aistudio.google.com/apikey>.

## Installation and updates

1. **Download** — from the [latest release](https://github.com/giorgi02/Stenor/releases/latest)
   pick one of:
   - `Stenor-win-lite-Setup.exe` — the small build (~19 MB installer, ~40 MB installed). It
     requires the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)
     — if you do not have it, the installer offers to download it for you.
   - `Stenor-win-Setup.exe` — the full build: everything is bundled and nothing else is needed,
     but it takes ~317 MB once installed.
2. **Install** — run it. The installer is unsigned, so SmartScreen may warn you — click
   **More info → Run anyway**. The app installs into `%LocalAppData%\Stenor`, gets a Start Menu
   shortcut and starts straight away (into the tray).
3. **Updates are automatic** — on every launch Stenor checks for a new version in the
   background, downloads it silently and installs it **on the next launch**. Because the app
   sits in the tray permanently, the update actually lands once you quit it from the tray
   (*Quit*) and start it again, or reboot the machine. Nothing else is required from you.

To uninstall: **Settings → Apps → Stenor**. Your settings (`%APPDATA%\Stenor`) are left
untouched by the uninstall.

## First run

On the first launch the settings window opens by itself — paste in a Gemini API key from
<https://aistudio.google.com/apikey>, optionally click **Test key**, and save.

After that:

1. Move to any text field.
2. **Hold Right Ctrl** and speak — a recording indicator appears on screen.
3. Release it — within seconds the text lands in the focused field.

## Settings

Double-click the tray icon, or pick *Settings…* from its menu:

| Setting | Default | Notes |
|---|---|---|
| Gemini API key | — | Stored encrypted, on your machine only |
| Spoken languages | Auto-detect | Several languages can be ticked (out of the 97 supported); it is only a hint for transcription — other languages work too |
| Hotkey | Right Ctrl | A single key (left/right are told apart) or a combination |
| Activation mode | Hold | Hold = you speak for as long as you hold the key; Toggle = one press starts, another one ends |
| Start with Windows | Enabled | |
| Live typing (experimental) | Disabled | Text is typed while you dictate — sentence by sentence, at the pauses in your speech; the result is rougher than in the normal mode |

A few small details:

- Recording stops automatically after 5 minutes; ✕ on the overlay cancels the recording or the
  transcription.
- Starting Stenor.exe a second time does not create a new instance — it opens the settings of
  the running one.
- Logs: `%APPDATA%\Stenor\logs\` (they never contain audio, transcripts or the key).

## Known limitations

- **In elevated (administrator) windows** text cannot be inserted directly (a Windows
  restriction) — the transcript stays on the clipboard for a manual Ctrl+V; if you want direct
  insertion, run Stenor as administrator.
- With combinations that involve the **Win** key, the OS shortcut may still fire.
- **In Live typing mode**, switching to another window mid-dictation sends the remaining text
  into the newly focused field; if the connection drops, the text already typed stays and the
  rest is lost (if nothing has been typed yet, the recording is processed by normal
  transcription automatically).
- After insertion, only text/image/file-list content is restored from the clipboard —
  application-private formats are lost.

## For developers

Stenor is written in C# (.NET 10 + WPF). Build instructions and the architecture description
are in [CLAUDE.md](CLAUDE.md); the release process is in [docs/release.md](docs/release.md).
