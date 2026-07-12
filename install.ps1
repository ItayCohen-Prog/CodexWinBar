# CodexWinBar installer (PowerShell).
#
# Usage (PowerShell):
#   irm https://raw.githubusercontent.com/ItayCohen-Prog/CodexWinBar/main/install.ps1 | iex
#
# Downloads the latest published CodexWinBar release and installs it to %LOCALAPPDATA%\CodexWinBar
# (per-user, no admin). Because the download happens through PowerShell rather than a browser, the
# installer does not carry the Mark of the Web, so there is no SmartScreen "Run anyway" prompt.

$ErrorActionPreference = 'Stop'
$repo = 'ItayCohen-Prog/CodexWinBar'
$assetName = 'CodexWinBar-win-Setup.exe'

Write-Host 'Finding the latest CodexWinBar release...' -ForegroundColor Cyan
$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" `
    -Headers @{ 'User-Agent' = 'CodexWinBar-Installer'; 'Accept' = 'application/vnd.github+json' }

$asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
if (-not $asset) {
    throw "Could not find $assetName in release $($release.tag_name)."
}

$dest = Join-Path $env:TEMP $assetName
Write-Host "Downloading CodexWinBar $($release.tag_name)..." -ForegroundColor Cyan
Invoke-WebRequest $asset.browser_download_url -OutFile $dest -UseBasicParsing

Write-Host 'Installing...' -ForegroundColor Cyan
Start-Process -FilePath $dest -ArgumentList '--silent' -Wait
Remove-Item $dest -ErrorAction SilentlyContinue

# Launch it so the widget appears immediately (Velopack installs under %LOCALAPPDATA%\CodexWinBar).
$exe = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'CodexWinBar') -Recurse -Filter 'CodexWinBar.exe' `
    -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\packages\\' } | Select-Object -First 1
if ($exe) { Start-Process $exe.FullName }

Write-Host "CodexWinBar $($release.tag_name) installed. It should appear on your taskbar shortly." -ForegroundColor Green
Write-Host 'It self-updates from here, and you can also install it via: winget install ItayCohen.CodexWinBar (once approved).'
