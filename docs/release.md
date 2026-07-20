# Release guide

Stenor is distributed as a Velopack installer (`Setup.exe`), and the update feed is GitHub
Releases: on launch, the installed app checks the releases of
<https://github.com/giorgi02/Stenor> in the background, downloads a new version silently and
installs it **on the next launch** (`App.xaml.cs` → `CheckForUpdatesInBackground`).

Every release contains two installers (two Velopack channels):

| Channel | File | What it contains |
|---|---|---|
| `win` | `Stenor-win-Setup.exe` | self-contained — .NET is bundled as well, nothing else is required (~317 MB installed) |
| `win-lite` | `Stenor-win-lite-Setup.exe` | framework-dependent — the app only (~19 MB installer, ~40 MB installed); requires the **.NET 10 Desktop Runtime (x64)** — if it is missing, the installer offers to download it |

The channel is written into the package itself, so each installation also takes its updates
from its own channel — a lite install stays lite.

## Building (locally)

```powershell
# ordinary build
dotnet build Stenor.slnx -c Release

# production publish (self-contained, ReadyToRun)
dotnet publish src/Stenor.App/Stenor.App.csproj -c Release -r win-x64 --self-contained
# → src/Stenor.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/Stenor.exe
```

## Releasing a new version (GitHub Actions — the main path)

Building and uploading is done by GitHub Actions (`.github/workflows/release.yml`); the run
itself is started by hand, with a button:

1. Raise `<Version>` (SemVer) in `src/Stenor.App/Stenor.App.csproj`, commit and push:

   ```powershell
   git commit -am "Release X.Y.Z"
   git push
   ```

2. On GitHub open **Actions → Release → Run workflow** and press the green button. The Windows
   runner publishes the app, packs the installers and posts them as a GitHub Release (creating
   the `vX.Y.Z` tag itself).

3. On success the release shows up at <https://github.com/giorgi02/Stenor/releases> and the
   installed apps update themselves.

If you press the button without having raised the version, the workflow fails — vpk refuses to
pack a version that is already published (or lower).

## Manual release (fallback path)

One-time setup:

```powershell
dotnet tool install -g vpk --version 1.2.0   # Velopack CLI — the version matches the Velopack package in the csproj
```

1. **Raise the version** — change `<Version>` (SemVer) in `src/Stenor.App/Stenor.App.csproj`.

2. **(Optional) download the previous release**, so that vpk can also build a small delta
   package:

   ```powershell
   vpk download github --repoUrl https://github.com/giorgi02/Stenor -o releases
   vpk download github --repoUrl https://github.com/giorgi02/Stenor --channel win-lite -o releases
   ```

3. **Packing** — publishes the app and builds both installers:

   ```powershell
   pwsh scripts/pack.ps1
   ```

   The result lands in `releases/`, one set per channel: `Stenor-win-Setup.exe` /
   `Stenor-win-lite-Setup.exe`, `Stenor-X.Y.Z-full.nupkg` /
   `Stenor-X.Y.Z-win-lite-full.nupkg` (+ `-delta.nupkg`, if you went through step 2),
   `releases.win.json` / `releases.win-lite.json` and the portable zips.

   > vpk refuses to run if `releases/` already holds the same or a higher version — either
   > raise the version, or clear the folder.

4. **Manual verification** — run `releases/Stenor-win-Setup.exe` and check the main scenarios;
   the installation goes into `%LocalAppData%\Stenor` (Start Menu shortcut + an entry under
   Apps). To uninstall: Settings → Apps → Stenor, or
   `%LocalAppData%\Stenor\Update.exe --uninstall`. The uninstall does not touch the user's
   settings (`%APPDATA%\Stenor`).

5. **Uploading to a GitHub Release** — two invocations (one per channel) are merged into one
   and the same release by `--merge`; after that the installed apps update themselves:

   ```powershell
   vpk upload github --repoUrl https://github.com/giorgi02/Stenor `
       --tag vX.Y.Z --releaseName "Stenor X.Y.Z" --publish --merge `
       --token <GitHub PAT> -o releases
   vpk upload github --repoUrl https://github.com/giorgi02/Stenor `
       --tag vX.Y.Z --releaseName "Stenor X.Y.Z" --publish --merge `
       --channel win-lite --token <GitHub PAT> -o releases
   ```

   The repo has to be public, so that installed apps can read the feed without a token.

## Overriding the feed for testing

In `%APPDATA%\Stenor\settings.json`, point `UpdateFeedUrl` at an alternative feed (a local
folder or a URL); an empty value = the built-in GitHub feed. The field is deliberately not
shown in the UI — it is meant for manual editing only.

## Known limitations

- The installer is **unsigned** (we have no code-signing certificate) — a SmartScreen warning
  is to be expected on first run ("More info" → "Run anyway").
- An update is applied only on the app's next launch; because Stenor sits in the tray
  permanently, in practice this means: after a Quit and a fresh start (or a system reboot).
