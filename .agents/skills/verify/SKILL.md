---
name: verify
description: Build, launch, and observe Stenor end-to-end on this Windows machine — smoke run, log check, and (when relevant) registry/Control Panel state.
---

# Verifying Stenor changes

## Build & launch

```powershell
dotnet build Stenor.slnx -c Release   # must stay warning-clean (TreatWarningsAsErrors)
Start-Process 'src\Stenor.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\Stenor.exe'
```

Plain Release build output runs directly (no publish needed). Single-instance mutex:
a second launch just signals the first to open Settings and exits — kill the old
process before relaunching (`Stop-Process -Name Stenor -Force`).

## Observe

- Log: `%APPDATA%\Stenor\logs\stenor.log` — expect "Stenor started."; startup events
  land within ~2 s, background tasks (audio prime, uninstall-size refresh) right after.
- Quit for scripted runs: `Stop-Process -Name Stenor -Force` (tray quit isn't scriptable).
- The app never crashes to desktop by design — check the log for `[ERR]`/`[WRN]` instead.

## Simulating an installed (Velopack) state

Dev builds aren't "installed"; to drive install-only paths, create the uninstall key
Velopack would have written, then launch:

```powershell
$k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Stenor'
New-Item $k -Force | Out-Null
New-ItemProperty $k -Name DisplayName -Value Stenor -PropertyType String -Force | Out-Null
New-ItemProperty $k -Name InstallLocation -Value <dir> -PropertyType String -Force | Out-Null
# Velopack 1.2.0 writes EstimatedSize as QWord (that's its blank-Size bug):
New-ItemProperty $k -Name EstimatedSize -Value 123456 -PropertyType QWord -Force | Out-Null
```

Delete the key afterwards — this machine has no real Stenor install.

## Gotchas

- PowerShell tool calls don't share state: `Add-Type` classes vanish between calls —
  put multi-step P/Invoke work in one call or a script file.
- P/Invoke null strings from PowerShell need `[NullString]::Value`, not `$null`.
- Control Panel "Programs and Features" window class is `CabinetWClass`; refresh with
  F5 after registry changes; capture it with GetWindowRect + Graphics.CopyFromScreen.
