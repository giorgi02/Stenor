# Packs Stenor into Velopack installers: publishes the app twice and produces two
# channels in releases/ — Stenor-win-Setup.exe (self-contained, no prerequisites)
# and Stenor-win-lite-Setup.exe (framework-dependent, ~6x smaller; needs the
# .NET 10 Desktop Runtime, Setup offers to install it when missing) — plus the
# per-channel update packages (full/delta nupkg + feed metadata).
# Prerequisite: dotnet tool install -g vpk   (see docs/release.md)
# Usage: pwsh scripts/pack.ps1

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $repoRoot 'src/Stenor.App/Stenor.App.csproj'
$binDir = Join-Path $repoRoot 'src/Stenor.App/bin/Release/net10.0-windows10.0.19041.0/win-x64'
$releaseDir = Join-Path $repoRoot 'releases'

$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version
if (-not $version) { throw "No <Version> found in $csproj" }

function Invoke-Pack([string]$publishDir, [string]$channel, [string[]]$extraArgs) {
    vpk pack `
        --packId Stenor `
        --packVersion $version `
        --packDir $publishDir `
        --mainExe Stenor.exe `
        --packTitle Stenor `
        --packAuthors 'Giorgi Gelashvili' `
        --icon (Join-Path $repoRoot 'src/Stenor.App/Assets/Stenor.ico') `
        --shortcuts StartMenuRoot `
        --channel $channel `
        --outputDir $releaseDir `
        @extraArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack ($channel) failed." }
}

# dotnet publish never cleans its output, so wipe the dirs to keep stale files
# (e.g. runtime DLLs from a previous self-contained publish) out of the packages.
$publishDir = Join-Path $binDir 'publish'
$publishLiteDir = Join-Path $binDir 'publish-lite'
foreach ($dir in $publishDir, $publishLiteDir) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}

# Channel "win": self-contained — runs anywhere, no prerequisites.
dotnet publish $csproj -c Release -r win-x64 --self-contained
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (self-contained) failed.' }
Invoke-Pack $publishDir 'win' @()

# Channel "win-lite": framework-dependent — much smaller; --framework makes Setup
# check for the .NET 10 Desktop Runtime and offer to install it when missing.
dotnet publish $csproj -c Release -r win-x64 --self-contained false -o $publishLiteDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (framework-dependent) failed.' }
Invoke-Pack $publishLiteDir 'win-lite' @('--framework', 'net10.0-x64-desktop')

Write-Host "Packed Stenor $version (win + win-lite) -> $releaseDir"
