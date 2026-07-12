# CodexWinBar installer (PowerShell).
#
# Usage (PowerShell):
#   irm https://raw.githubusercontent.com/ItayCohen-Prog/CodexWinBar/main/install.ps1 | iex
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

Write-Host 'Finding the latest CodexWinBar release...' -ForegroundColor Cyan
$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers $headers

$asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
if (-not $asset) { throw "Could not find $assetName in release $($release.tag_name)." }

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
        Remove-Item $dest -ErrorAction SilentlyContinue
        throw "Checksum mismatch (expected $expected, got $actual). The download may be corrupt or tampered with. Aborting."
    }
    Write-Host "Verified SHA-256: $actual" -ForegroundColor Green
} else {
    Write-Host "SHA-256: $actual (no published checksum to compare against)" -ForegroundColor Yellow
}

Write-Host 'Installing...' -ForegroundColor Cyan
Start-Process -FilePath $dest -ArgumentList '--silent' -Wait
Remove-Item $dest -ErrorAction SilentlyContinue

# Launch it so the widget appears immediately (Velopack installs under %LOCALAPPDATA%\CodexWinBar).
$exe = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'CodexWinBar') -Recurse -Filter 'CodexWinBar.exe' `
    -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\packages\\' } | Select-Object -First 1
if ($exe) { Start-Process $exe.FullName }

Write-Host "CodexWinBar $($release.tag_name) installed." -ForegroundColor Green
Write-Host 'A setup window is opening — connect the providers you use, then the widget appears on the taskbar next to the clock.'
Write-Host 'It self-updates from here, and you can also install it via: winget install ItayCohen.CodexWinBar (once approved).'
