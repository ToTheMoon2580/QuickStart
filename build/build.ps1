# Builds QuickStart.exe with the csc.exe that ships with .NET Framework.
# No SDK required. ASCII-only (Windows PowerShell 5.1 reads BOM-less files as ANSI).
# Usage: powershell -ExecutionPolicy Bypass -File build\build.ps1
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\dist')
)

$ErrorActionPreference = 'Stop'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found (need .NET Framework 4.x)' }

$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src\QuickStart.cs'
$ico = Join-Path $root 'src\app.ico'

if (-not (Test-Path -LiteralPath $ico)) {
    & (Join-Path $PSScriptRoot 'make-icon.ps1') -Out $ico | Out-Null
}
if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir | Out-Null
}

$out = Join-Path $OutDir 'QuickStart.exe'

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/optimize+'
    '/platform:anycpu'
    '/codepage:65001'
    ('/win32icon:' + $ico)
    '/reference:System.dll'
    '/reference:System.Core.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    ('/out:' + $out)
    $src
)

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw ("build failed, exit code " + $LASTEXITCODE) }

$info = Get-Item -LiteralPath $out
Write-Output ("OK  {0}  {1:N0} bytes" -f $info.FullName, $info.Length)
