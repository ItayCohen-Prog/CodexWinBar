# CodexWinBar diagnostics collector (comprehensive).
# Read-only: gathers the app version, full Windows display/taskbar configuration, settings, and the
# app's decision logs into a text file on the Desktop, then opens it. Run it on BOTH machines (the
# working one and the broken one) so the two files can be compared side by side.
$ErrorActionPreference = 'Continue'
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'CodexWinBar-diagnostics.txt'
$L = New-Object System.Collections.Generic.List[string]
function Add-Line($t) { $L.Add([string]$t) }
function Reg($path, $name) { try { return (Get-ItemProperty $path -Name $name -ErrorAction Stop).$name } catch { return '(unset)' } }

Add-Line "==== CodexWinBar diagnostics  $(Get-Date) ===="
Add-Line "Machine     : $env:COMPUTERNAME"
$appVersion = $null
try {
    $appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $env:LOCALAPPDATA 'CodexWinBar\current\CodexWinBar.exe')).ProductVersion
    Add-Line "App version : $appVersion"
} catch { Add-Line "App version : not found" }
if ($appVersion) {
    try {
        if ([Version]($appVersion.Split('-')[0].Split('+')[0]) -lt [Version]'1.1.15') {
            Add-Line "!! WARNING  : versions before 1.1.15 flood the log and erase useful history within"
            Add-Line "!!            ~30 minutes - please update the app (flyout update button) and re-collect."
        }
    } catch {}
}
try { $proc = Get-Process CodexWinBar -ErrorAction Stop | Select-Object -First 1; Add-Line ("App running : since " + $proc.StartTime) } catch { Add-Line "App running : NO" }
Add-Line ("Windows     : " + [Environment]::OSVersion.Version + "  " + (Reg 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' 'DisplayVersion'))
try { Add-Line ("UI language : " + (Get-UICulture).Name + "  RTL=" + (Get-Culture).TextInfo.IsRightToLeft) } catch {}

Add-Line ""
Add-Line "---- Taskbar & display settings ----"
$adv = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
try { $sr = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3' -Name Settings -ErrorAction Stop).Settings; Add-Line ("Auto-hide taskbar     : " + ((($sr[8] -band 1) -eq 1))) } catch { Add-Line "Auto-hide taskbar     : unknown" }
Add-Line ("Taskbar alignment     : " + (Reg $adv 'TaskbarAl') + "  (0=left, 1=center)")
Add-Line ("Show on all displays  : " + (Reg $adv 'MMTaskbarEnabled') + "  (1=all monitors have a taskbar)")
Add-Line ("Multi-monitor buttons : " + (Reg $adv 'MMTaskbarMode') + "  (0=all, 1=main+open, 2=open-monitor)")
Add-Line ("Taskbar size          : " + (Reg $adv 'TaskbarSi'))
Add-Line ("Small taskbar buttons : " + (Reg $adv 'TaskbarSmallIcons'))

Add-Line ""
Add-Line "---- Monitors ----"
try {
    Add-Type -AssemblyName System.Windows.Forms
    $i = 0
    foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
        Add-Line ("  #$i primary=$($s.Primary)  bounds=$($s.Bounds)  workArea=$($s.WorkingArea)  bpp=$($s.BitsPerPixel)")
        $i++
    }
} catch { Add-Line "  (monitor enumeration failed)" }

Add-Line ""
Add-Line "---- ui-settings.json ----"
try { Add-Line (Get-Content (Join-Path $env:APPDATA 'CodexWinBar\ui-settings.json') -Raw -ErrorAction Stop) } catch { Add-Line "(not found)" }

$logPath = Join-Path $env:LOCALAPPDATA 'CodexWinBar\logs\app.log'
Add-Line ""
Add-Line "---- app.log coverage ----"
try {
    $logItem = Get-Item $logPath -ErrorAction Stop
    $logLines = Get-Content $logPath -ErrorAction Stop
    Add-Line ("size={0:N0} bytes  lines={1}  lastWrite={2}" -f $logItem.Length, $logLines.Count, $logItem.LastWriteTime)
    if ($logLines.Count -gt 0) {
        Add-Line ("first line: " + $logLines[0])
        Add-Line ("last line : " + $logLines[$logLines.Count - 1])
    }
} catch { $logLines = @(); Add-Line "(app.log not found)" }

# Since 1.1.15 overlay decisions log on every CHANGE plus a 5-minute steady-state heartbeat,
# so 300 lines cover hours of history including every show/hide flip with its full inputs.
Add-Line ""
Add-Line "---- app.log: Overlay decision lines (last 300) ----"
$logLines | Select-String 'Overlay decision' | Select-Object -Last 300 | ForEach-Object { Add-Line $_.Line }

Add-Line ""
Add-Line "---- app.log: taskbar layout / fit / mode (last 80) ----"
$logLines | Select-String 'Taskbar start layout|horizontal fit|mode changed|probe|Taskbar found' | Select-Object -Last 80 | ForEach-Object { Add-Line $_.Line }

# New in 1.1.15+: provider fetch failures (with a needs-sign-in marker) are logged, so data-side
# problems (grayed-out cards, sign-outs) are separable from overlay/visibility problems.
Add-Line ""
Add-Line "---- app.log: provider fetch failures (last 40) ----"
$logLines | Select-String 'fetch failed for' | Select-Object -Last 40 | ForEach-Object { Add-Line $_.Line }

# Flyout activity: opens/switches/placements/resizes ("switch panel X -> Y dip" is new in 1.1.16).
Add-Line ""
Add-Line "---- app.log: flyout activity (last 60) ----"
$logLines | Select-String 'flyout|toggle-|close:|switch panel' | Select-Object -Last 60 | ForEach-Object { Add-Line $_.Line }

Add-Line ""
Add-Line "---- app.log: last 120 raw ----"
$logLines | Select-Object -Last 120 | ForEach-Object { Add-Line $_ }

[System.IO.File]::WriteAllLines($out, $L.ToArray())
Write-Host ""
Write-Host "Saved to your Desktop: CodexWinBar-diagnostics.txt" -ForegroundColor Green
Write-Host "Please send that file back. Opening it now..."
try { Start-Process notepad.exe $out } catch {}
