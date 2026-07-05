#Requires -Version 7
<#
.SYNOPSIS
  Builds the Windows CodexWinBar installer + update assets with Velopack.
.DESCRIPTION
  Publishes the WPF app self-contained (no .NET runtime prerequisite for users) and packs it with
  `vpk` into a Setup.exe plus the update feed (RELEASES / *.nupkg) under artifacts/releases.
  Install the matching CLI once with:  dotnet tool install -g vpk --version 1.2.0
.PARAMETER Version
  Release version (SemVer). Defaults to <Version> in Directory.Build.props.
#>
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$app = Join-Path $repo "src/CodexWinBar.App/CodexWinBar.App.csproj"
$publishDir = Join-Path $repo "artifacts/publish"
$releaseDir = Join-Path $repo "artifacts/releases"
$icon = Join-Path $repo "src/CodexWinBar.App/app.ico"

if (-not $Version) {
    $props = Get-Content (Join-Path $repo "Directory.Build.props") -Raw
    if ($props -match "<Version>([^<]+)</Version>") { $Version = $Matches[1].Trim() }
    else { throw "Could not read <Version> from Directory.Build.props; pass -Version." }
}

Write-Host "==> Publishing CodexWinBar $Version ($Runtime, self-contained)" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish $app -c $Configuration -r $Runtime --self-contained true `
    -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Write-Host "==> Packing with Velopack" -ForegroundColor Cyan
# NOTE: no --signParams yet. Builds are unsigned for now; users pass the SmartScreen
# "More info -> Run anyway" prompt once. Add signing here once a certificate is available.
vpk pack `
    --packId CodexWinBar `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe CodexWinBar.exe `
    --packTitle "CodexWinBar" `
    --packAuthors "Itay Cohen" `
    --icon $icon `
    --outputDir $releaseDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

Write-Host ""
Write-Host "==> Done. Installer + update assets in: $releaseDir" -ForegroundColor Green
Get-ChildItem $releaseDir | Select-Object Name, @{N = "Size"; E = { "{0:N1} MB" -f ($_.Length / 1MB) } }
