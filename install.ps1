# CodexWinBar installer (PowerShell).
#
# Usage (PowerShell):
#   irm https://codexwinbar.webivize.com | iex
#
# Downloads the latest published CodexWinBar release, verifies its SHA-256 against the checksum
# GitHub publishes for the asset, and installs it to %LOCALAPPDATA%\CodexWinBar (per-user, no admin).
# Because the download runs through PowerShell rather than a browser, the installer does not carry the
# Mark of the Web, so there is no SmartScreen "Run anyway" prompt.

#Requires -Version 5
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1's Invoke-WebRequest renders a per-chunk progress bar that cripples large
# downloads (turns a ~79 MB fetch from seconds into minutes). Silencing it restores full speed.
$ProgressPreference = 'SilentlyContinue'
# Windows PowerShell 5.1 may default to an old TLS; GitHub requires TLS 1.2+.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}

$repo = 'ItayCohen-Prog/CodexWinBar'
$assetName = 'CodexWinBar-win-Setup.exe'
$headers = @{ 'User-Agent' = 'CodexWinBar-Installer'; 'Accept' = 'application/vnd.github+json' }
$currentExe = Join-Path $env:LOCALAPPDATA 'CodexWinBar\current\CodexWinBar.exe'

# PowerShell's Remove-Item/New-Item/Copy-Item normalize paths through a provider that throws
# PSArgumentException on the 8.3 short paths Windows returns for non-ASCII (e.g. Hebrew) user profiles
# (PowerShell/PowerShell#21070), and -ErrorAction cannot suppress it. These helpers use the .NET file
# APIs, which accept those paths directly, so the account's display language never breaks the install.
function Remove-PathQuiet([string]$path) {
    if (-not $path) { return }
    try {
        if ([System.IO.Directory]::Exists($path)) { [System.IO.Directory]::Delete($path, $true) }
        elseif ([System.IO.File]::Exists($path)) { [System.IO.File]::Delete($path) }
    } catch {}
}
function Copy-DatFiles([string]$sourceDir, [string]$destDir) {
    if (-not [System.IO.Directory]::Exists($sourceDir)) { return }
    [void][System.IO.Directory]::CreateDirectory($destDir)
    foreach ($file in [System.IO.Directory]::GetFiles($sourceDir, '*.dat')) {
        [System.IO.File]::Copy($file, [System.IO.Path]::Combine($destDir, [System.IO.Path]::GetFileName($file)), $true)
    }
}

Write-Host 'Finding the latest CodexWinBar release...' -ForegroundColor Cyan
$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers $headers

$asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
if (-not $asset) { throw "Could not find $assetName in release $($release.tag_name)." }

$latestVersionText = $release.tag_name -replace '^v', ''
$latestVersion = $null
try { $latestVersion = [version]$latestVersionText } catch {}
$installedVersion = $null
$installedVersionText = $null
if (Test-Path -LiteralPath $currentExe) {
    try {
        $installedVersionText = [Diagnostics.FileVersionInfo]::GetVersionInfo($currentExe).ProductVersion
        if ($installedVersionText) {
            $installedVersionText = ($installedVersionText -split '[+-]')[0]
            $installedVersion = [version]$installedVersionText
        }
    } catch {}
}

if ($installedVersion -and $latestVersion -and $installedVersion -gt $latestVersion) {
    Write-Host "CodexWinBar $installedVersionText is newer than the latest published release ($latestVersionText). Nothing was changed." -ForegroundColor Yellow
    Start-Process $currentExe
    return
}

$installAction = if (-not (Test-Path -LiteralPath $currentExe)) {
    'Installing CodexWinBar'
} elseif ($installedVersion -and $latestVersion -and $installedVersion -lt $latestVersion) {
    "Updating CodexWinBar from $installedVersionText to $latestVersionText"
} elseif ($installedVersionText) {
    "Repairing CodexWinBar $installedVersionText"
} else {
    'Repairing/updating the existing CodexWinBar installation'
}

# Defence in depth: only ever download from this project's own GitHub release location.
$url = $asset.browser_download_url
if ($url -notmatch '^https://github\.com/ItayCohen-Prog/CodexWinBar/releases/download/') {
    throw "Refusing to download from an unexpected URL: $url"
}

# Expected checksum, as published by GitHub for the stored asset (e.g. "sha256:abcd...").
$expected = $null
if (($asset.PSObject.Properties.Name -contains 'digest') -and $asset.digest) {
    $expected = ($asset.digest -replace '^sha256:', '').ToLowerInvariant()
}

$dest = Join-Path $env:TEMP $assetName
Write-Host "Downloading CodexWinBar $($release.tag_name)..." -ForegroundColor Cyan
Invoke-WebRequest $url -OutFile $dest -UseBasicParsing

$actual = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expected) {
    if ($actual -ne $expected) {
        Remove-PathQuiet $dest
        throw "Checksum mismatch (expected $expected, got $actual). The download may be corrupt or tampered with. Aborting."
    }
    Write-Host "Verified SHA-256: $actual" -ForegroundColor Green
} else {
    Write-Host "SHA-256: $actual (no published checksum to compare against)" -ForegroundColor Yellow
}

$legacyCredentials = Join-Path $env:LOCALAPPDATA 'CodexWinBar\credentials'
$safeCredentials = Join-Path $env:LOCALAPPDATA 'CodexWinBarData\credentials'
$credentialBackup = $null
if (Test-Path -LiteralPath $legacyCredentials) {
    # Versions through 1.1.7 stored app-owned OAuth credentials inside Velopack's install root.
    # A version upgrade can replace that root, so preserve the encrypted files before setup runs.
    $credentialBackup = Join-Path $env:TEMP ("CodexWinBar-credentials-" + [guid]::NewGuid().ToString('N'))
    Copy-DatFiles $legacyCredentials $credentialBackup
}

$installed = $false
try {
    Write-Host "$installAction..." -ForegroundColor Cyan
    $installer = Start-Process -FilePath $dest -ArgumentList '--silent' -PassThru -Wait
    if ($installer.ExitCode -ne 0) { throw "CodexWinBar setup failed with exit code $($installer.ExitCode)." }

    if ($credentialBackup -and [System.IO.Directory]::Exists($credentialBackup)) {
        Copy-DatFiles $credentialBackup $safeCredentials
    }

    $installed = $true
}
finally {
    if (-not $installed -and $credentialBackup -and [System.IO.Directory]::Exists($credentialBackup)) {
        Copy-DatFiles $credentialBackup $legacyCredentials
    }
    Remove-PathQuiet $credentialBackup
    Remove-PathQuiet $dest
}

# Launch it so the widget appears immediately (Velopack installs under %LOCALAPPDATA%\CodexWinBar).
if (Test-Path -LiteralPath $currentExe) { Start-Process $currentExe }

Write-Host "CodexWinBar $($release.tag_name) installed." -ForegroundColor Green
Write-Host 'A setup window is opening - connect the providers you use and the widget appears on your taskbar.'
