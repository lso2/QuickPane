<#
    QuickPane installer (no administrator rights required).

    This architecture is a single user-mode executable plus two HKCU registry entries, so unlike
    the old COM build there is no regsvr32 and no elevation. The script copies QuickPane.exe into
    %LOCALAPPDATA%\QuickPane, registers the "Pin to Quick Pane" folder verb, adds a startup entry,
    and launches the app.

    Run it by right-clicking and choosing "Run with PowerShell", or from a console:
        powershell -ExecutionPolicy Bypass -File .\Install.ps1
#>

$ErrorActionPreference = 'Stop'

$InstallDir = Join-Path $env:LOCALAPPDATA 'QuickPane'
$AppData    = Join-Path $env:APPDATA   'QuickPane'
$ExeName    = 'QuickPane.exe'

function Find-SourceExe {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Definition
    $candidates = @(
        (Join-Path $here $ExeName),
        (Join-Path $here "QuickPane\bin\x64\Release\$ExeName"),
        (Join-Path $here "QuickPane\bin\x64\Debug\$ExeName")
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "Could not find $ExeName. Build the solution in Visual Studio first (Ctrl+Shift+B), or place $ExeName next to this script."
}

Write-Host 'Installing QuickPane...' -ForegroundColor Cyan

# 1. Stop any running instance so the file is not locked.
Get-Process -Name 'QuickPane' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

# 2. Copy the executable into the per-user install folder.
$src = Find-SourceExe
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Force -Path $src -Destination (Join-Path $InstallDir $ExeName)
$exe = Join-Path $InstallDir $ExeName

# 3. Register the "Pin to Quick Pane" context menu verb under HKCU (no admin needed).
$verbCmd = "`"$exe`" --pin `"%1`""
$verbCmdBg = "`"$exe`" --pin `"%V`""

New-Item -Path 'HKCU:\Software\Classes\Directory\shell\QuickPanePin\command' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\Classes\Directory\shell\QuickPanePin' -Name '(default)' -Value 'Pin to Quick Pane'
Set-ItemProperty -Path 'HKCU:\Software\Classes\Directory\shell\QuickPanePin' -Name 'Icon' -Value $exe
Set-ItemProperty -Path 'HKCU:\Software\Classes\Directory\shell\QuickPanePin\command' -Name '(default)' -Value $verbCmd

New-Item -Path 'HKCU:\Software\Classes\Directory\Background\shell\QuickPanePin\command' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\Classes\Directory\Background\shell\QuickPanePin' -Name '(default)' -Value 'Pin to Quick Pane'
Set-ItemProperty -Path 'HKCU:\Software\Classes\Directory\Background\shell\QuickPanePin\command' -Name '(default)' -Value $verbCmdBg

# 4. Start with Windows.
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'QuickPane' -Value "`"$exe`""

# 5. Make sure the data folder exists. The app writes a default settings.json on first run.
New-Item -ItemType Directory -Force -Path (Join-Path $AppData 'Groups') | Out-Null

# 6. Launch.
Start-Process -FilePath $exe

Write-Host ''
Write-Host 'QuickPane installed and running.' -ForegroundColor Green
Write-Host 'A tray icon is now in the notification area. Open any File Explorer window and the sidebar appears on the left.'
Write-Host 'If you still see the built-in navigation pane, turn it off once via View, Navigation pane, Navigation pane. Explorer remembers the choice.'
