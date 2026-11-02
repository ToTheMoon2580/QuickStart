# Retakes docs screenshots on a blank desktop (all windows minimized).
# ASCII only: PowerShell 5.1 reads BOM-less scripts as ANSI, so CJK comments break parsing.
param(
    [string]$Docs = 'D:\Agent\quickstart\docs'
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class CAPX {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc c, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr par, EnumProc c, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] public static extern IntPtr SendText(IntPtr h, int m, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr ByTitle(string t) {
        IntPtr f = IntPtr.Zero;
        EnumWindows((h, p) => {
            var s = new StringBuilder(256);
            GetWindowText(h, s, 256);
            if (s.ToString() == t) { f = h; return false; }
            return true;
        }, IntPtr.Zero);
        return f;
    }

    public static void SetInput(IntPtr par, string text) {
        EnumChildWindows(par, (h, p) => {
            var c = new StringBuilder(256);
            GetClassName(h, c, 256);
            if (c.ToString().IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) >= 0) {
                SendText(h, 0x000C, IntPtr.Zero, text);
                return false;
            }
            return true;
        }, IntPtr.Zero);
    }

    public static void RightClick(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(250);
        mouse_event(0x0008, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(90);
        mouse_event(0x0010, 0, 0, 0, IntPtr.Zero);
    }
}
'@

try { [CAPX]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null } catch { [CAPX]::SetProcessDPIAware() | Out-Null }

$panel = [CAPX]::ByTitle('QuickStart')
$hostW = [CAPX]::ByTitle('QuickStart.Host')
if ([CAPX]::IsWindowVisible($panel)) {
    [void][CAPX]::PostMessage($hostW, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700
}

function Show-Panel {
    if (-not [CAPX]::IsWindowVisible($panel)) {
        [void][CAPX]::PostMessage($hostW, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 1200
    }
}

function Hide-Panel {
    if ([CAPX]::IsWindowVisible($panel)) {
        [void][CAPX]::PostMessage($hostW, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 600
    }
}

function Shot([string]$text, [string]$out, [int]$margin, [bool]$menu, [int]$row) {
    Show-Panel
    if ($text.Length -gt 0) {
        [CAPX]::SetInput($panel, $text)
        Start-Sleep -Milliseconds 900
    }
    $r = New-Object CAPX+RECT
    [void][CAPX]::GetWindowRect($panel, [ref]$r)
    if ($menu) {
        $mx = $r.Left + 420
        $my = $r.Top + 95 + $row * 70 + 35
        [CAPX]::RightClick($mx, $my)
        Start-Sleep -Milliseconds 1200
    }
    $x0 = $r.Left - $margin
    $y0 = $r.Top - $margin
    $w = ($r.Right - $r.Left) + $margin * 2
    $h = ($r.Bottom - $r.Top) + $margin * 2
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x0, $y0, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output ("{0}: {1}x{2}" -f (Split-Path $out -Leaf), $w, $h)
    if ($menu) {
        [CAPX]::RightClick(($r.Left + 30), ($r.Bottom + 12))
        Start-Sleep -Milliseconds 400
    }
    Hide-Panel
}

$shell = New-Object -ComObject Shell.Application
$shell.MinimizeAll()
Start-Sleep -Milliseconds 1200

Shot 'st' (Join-Path $Docs 'screenshot-list.png') 36 $false 0
Shot '> ipconfig /all' (Join-Path $Docs 'screenshot-command.png') 36 $false 0
Shot 'st' (Join-Path $Docs 'screenshot-menu.png') 36 $true 1

$shell.UndoMinimizeAll()
Write-Output 'done'
