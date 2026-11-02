# Test helper: toggle the launcher (WM_QS_TOGGLE), optionally type text, capture the screen.
# ASCII only (Windows PowerShell 5.1 reads BOM-less files as ANSI).
param(
    [string]$Action = 'toggle',
    [string]$Text = '',
    [string]$Out = 'D:\Agent\quickstart\shots\screen.png',
    [int]$Delay = 900
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not ('QSProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class QSProbe {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr FindByTitle(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            var t = new StringBuilder(256);
            GetWindowText(h, t, 256);
            if (t.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string Title(IntPtr h) {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Cls(IntPtr h) {
        var sb = new StringBuilder(512);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static List<string> WindowsOf(uint pid) {
        var list = new List<string>();
        EnumWindows((h, p) => {
            uint wp; GetWindowThreadProcessId(h, out wp);
            if (wp == pid && IsWindowVisible(h)) {
                RECT r; GetWindowRect(h, out r);
                list.Add(string.Format("{0} cls={1} title='{2}' {3},{4} {5}x{6}",
                    h, Cls(h), Title(h), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@
}

try { [QSProbe]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null } catch { [QSProbe]::SetProcessDPIAware() | Out-Null }

$hostWnd = [QSProbe]::FindByTitle('QuickStart.Host')

function Test-LauncherVisible {
    $proc = Get-Process QuickStart -ErrorAction SilentlyContinue
    if (-not $proc) { return $false }
    $wins = [QSProbe]::WindowsOf([uint32]$proc.Id)
    foreach ($w in $wins) { if ($w -like '*title=''QuickStart''*') { return $true } }
    return $false
}

switch ($Action) {
    'toggle' { [QSProbe]::PostMessage($hostWnd, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
    'show'   { if (-not (Test-LauncherVisible)) { [QSProbe]::PostMessage($hostWnd, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null } }
    'close'  { if (Test-LauncherVisible) { [QSProbe]::PostMessage($hostWnd, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null } }
}

Start-Sleep -Milliseconds $Delay

if ($Text.Length -gt 0) {
    [System.Windows.Forms.SendKeys]::SendWait($Text)
    Start-Sleep -Milliseconds 600
}

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bounds.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$fg = [QSProbe]::GetForegroundWindow()
Write-Output ("host hwnd = {0}" -f $hostWnd)
Write-Output ("screen {0}x{1} -> {2}" -f $bounds.Width, $bounds.Height, $Out)
Write-Output ("foreground: " + [QSProbe]::Cls($fg) + " | " + [QSProbe]::Title($fg))
$r = New-Object QSProbe+RECT
if ([QSProbe]::GetWindowRect($fg, [ref]$r)) {
    Write-Output ("fg rect: {0},{1} {2}x{3}" -f $r.Left, $r.Top, ($r.Right - $r.Left), ($r.Bottom - $r.Top))
}
$proc = Get-Process QuickStart -ErrorAction SilentlyContinue
if ($proc) {
    [QSProbe]::WindowsOf([uint32]$proc.Id) | ForEach-Object { Write-Output ("  win: " + $_) }
}
