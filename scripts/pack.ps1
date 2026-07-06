# Packs Stenor into a Velopack installer: publishes the app, then produces
# releases/Stenor-win-Setup.exe plus the update packages (full/delta nupkg + feed metadata).
# Prerequisite: dotnet tool install -g vpk   (see docs/release.md)
# Usage: pwsh scripts/pack.ps1

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $repoRoot 'src/Stenor.App/Stenor.App.csproj'
$publishDir = Join-Path $repoRoot 'src/Stenor.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish'
$releaseDir = Join-Path $repoRoot 'releases'

$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version
if (-not $version) { throw "No <Version> found in $csproj" }

dotnet publish $csproj -c Release -r win-x64 --self-contained
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

vpk pack `
    --packId Stenor `
    --packVersion $version `
    --packDir $publishDir `
    --mainExe Stenor.exe `
    --packTitle Stenor `
    --packAuthors 'Giorgi Gelashvili' `
    --icon (Join-Path $repoRoot 'src/Stenor.App/Assets/Stenor.ico') `
    --shortcuts StartMenuRoot `
    --outputDir $releaseDir
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }

Write-Host "Packed Stenor $version -> $releaseDir"
