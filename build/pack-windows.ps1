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
    [string]$Runtime = "win-x64",
    # Signing (optional). Provide ONE of these to produce a signed build:
    #   -AzureTrustedSignFile  path to an Azure Artifact Signing metadata.json (recommended, cloud)
    #   -SignParams            signtool.exe parameters for a local/traditional certificate
    # See docs/windows-port/CODE-SIGNING.md.
    [string]$AzureTrustedSignFile,
    [string]$SignParams
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
$packArgs = @(
    "pack",
    "--packId", "CodexWinBar",
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--mainExe", "CodexWinBar.exe",
    "--packTitle", "CodexWinBar",
    "--packAuthors", "Itay Cohen",
    "--icon", $icon,
    "--outputDir", $releaseDir
)
if ($AzureTrustedSignFile) {
    Write-Host "    signing via Azure Artifact Signing" -ForegroundColor Cyan
    $packArgs += @("--azureTrustedSignFile", $AzureTrustedSignFile)
}
elseif ($SignParams) {
    Write-Host "    signing via signtool" -ForegroundColor Cyan
    $packArgs += @("--signParams", $SignParams)
}
else {
    Write-Host "    UNSIGNED build (users get a one-time SmartScreen prompt)" -ForegroundColor Yellow
}
vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

Write-Host ""
Write-Host "==> Done. Installer + update assets in: $releaseDir" -ForegroundColor Green
Get-ChildItem $releaseDir | Select-Object Name, @{N = "Size"; E = { "{0:N1} MB" -f ($_.Length / 1MB) } }
