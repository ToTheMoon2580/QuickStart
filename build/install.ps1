# Installs QuickStart into %LOCALAPPDATA%\QuickStart, enables autostart and launches it.
# ASCII only (Windows PowerShell 5.1 reads BOM-less files as ANSI).
# Usage: powershell -ExecutionPolicy Bypass -File build\install.ps1 [-NoStartup]

param(
    [switch]$NoStartup
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\QuickStart.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "not built yet: $exe (run build\build.ps1 first)" }

$target = Join-Path $env:LOCALAPPDATA 'QuickStart'
if (-not (Test-Path -LiteralPath $target)) { New-Item -ItemType Directory -Path $target | Out-Null }

$running = Get-Process QuickStart -ErrorAction SilentlyContinue
if ($running) { $running | Stop-Process -Force; Start-Sleep -Milliseconds 600 }

Copy-Item -LiteralPath $exe -Destination (Join-Path $target 'QuickStart.exe') -Force
Copy-Item -LiteralPath (Join-Path $root 'src\app.ico') -Destination (Join-Path $target 'app.ico') -Force

# NOTE: deliberately NO desktop shortcut.
# The owner does not want one; the tray icon + Alt+Space are the entry points,
# and autostart/search never depend on a desktop shortcut.
# Clean up one left over from an older install, if present.
$staleDesktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'QuickStart.lnk'
if (Test-Path -LiteralPath $staleDesktopLnk) { Remove-Item -LiteralPath $staleDesktopLnk -Force }

Write-Output ("installed : " + (Join-Path $target 'QuickStart.exe'))

if ($NoStartup) {
    & (Join-Path $target 'QuickStart.exe') --nostartup | Out-Null
    & (Join-Path $target 'QuickStart.exe') --untask | Out-Null
    Write-Output 'autostart : skipped (-NoStartup)'
} else {
    & (Join-Path $target 'QuickStart.exe') --startup | Out-Null
    & (Join-Path $target 'QuickStart.exe') --task | Out-Null
    Write-Output 'autostart : HKCU Run + 5-minute self-heal task'
}

# Make it findable from Windows Search / Start menu / Win+R
& (Join-Path $target 'QuickStart.exe') --register | Out-Null
Write-Output 'search    : Start Menu shortcut + App Paths registered'

Start-Process -FilePath (Join-Path $target 'QuickStart.exe')
Start-Sleep -Seconds 2

$proc = Get-Process QuickStart -ErrorAction SilentlyContinue
if ($proc) {
    Write-Output ("running   : pid {0}, {1:N0} MB private" -f $proc.Id, ($proc.PrivateMemorySize64 / 1MB))
} else {
    Write-Output 'running   : FAILED to start'
}
