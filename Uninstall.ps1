<#
    QuickPane uninstaller. Removes the executable, the context menu verb, and the startup entry.
    It deliberately leaves %APPDATA%\QuickPane in place so your groups and settings survive a
    reinstall. Delete that folder by hand if you want a clean wipe.

        powershell -ExecutionPolicy Bypass -File .\Uninstall.ps1
#>

$ErrorActionPreference = 'SilentlyContinue'

$InstallDir = Join-Path $env:LOCALAPPDATA 'QuickPane'

Write-Host 'Uninstalling QuickPane...' -ForegroundColor Cyan

# 1. Stop the running instance. Disposing restores every Explorer window to its normal layout.
Get-Process -Name 'QuickPane' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

# 2. Remove the context menu verb (both folder and folder-background variants).
Remove-Item -Path 'HKCU:\Software\Classes\Directory\shell\QuickPanePin' -Recurse -Force
Remove-Item -Path 'HKCU:\Software\Classes\Directory\Background\shell\QuickPanePin' -Recurse -Force

# 3. Remove the startup entry.
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'QuickPane' -Force

# 4. Remove the installed files.
Remove-Item -Path $InstallDir -Recurse -Force

Write-Host ''
Write-Host 'QuickPane removed. Your groups and settings in %APPDATA%\QuickPane were kept.' -ForegroundColor Green
Write-Host 'If Explorer still looks shifted, open a new Explorer window or sign out and back in.'
