# QuickStart UI test driver: set input text, press Enter, right-click a row, capture.
# ASCII only (Windows PowerShell 5.1 reads BOM-less files as ANSI).
param(
    [string]$Type = '',
    [switch]$Press,
    [ValidateSet('none', 'menu')][string]$Mode = 'none',
    [int]$Row = 0,
    [string]$Out = 'D:\Agent\quickstart\shots\ui.png'
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not ('QS' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class QS {
    public delegate bool EnumProc(IntPtr h, IntPtr p);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] public static extern IntPtr SendMessageStr(IntPtr h, int m, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr ByTitle(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr FindEdit(IntPtr parent) {
        IntPtr edit = IntPtr.Zero;
        EnumChildWindows(parent, (h, p) => {
            var c = new StringBuilder(256);
            GetClassName(h, c, 256);
            if (c.ToString().IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) >= 0) { edit = h; return false; }
            return true;
        }, IntPtr.Zero);
        return edit;
    }

    public static int SetText(IntPtr parent, string text) {
        IntPtr edit = FindEdit(parent);
        if (edit == IntPtr.Zero) return 0;
        SendMessageStr(edit, 0x000C, IntPtr.Zero, text);   // WM_SETTEXT
        return 1;
    }

    public static int Enter(IntPtr parent) {
        IntPtr edit = FindEdit(parent);
        if (edit == IntPtr.Zero) return 0;
        SendMessage(edit, 0x0100, (IntPtr)0x0D, IntPtr.Zero);   // WM_KEYDOWN VK_RETURN
        SendMessage(edit, 0x0101, (IntPtr)0x0D, IntPtr.Zero);   // WM_KEYUP
        return 1;
    }

    public static void ToFront(IntPtr h) {
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // HWND_TOPMOST|NOSIZE|NOMOVE|SHOWWINDOW
        ShowWindow(h, 5);
        SetForegroundWindow(h);
    }

    public static void RightClick(IntPtr h, int x, int y) {
        IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
        PostMessage(h, 0x0204, (IntPtr)2, lp);   // WM_RBUTTONDOWN
        System.Threading.Thread.Sleep(80);
        PostMessage(h, 0x0205, IntPtr.Zero, lp); // WM_RBUTTONUP
    }
}
'@
}

try { [QS]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null } catch { [QS]::SetProcessDPIAware() | Out-Null }

$panel = [QS]::ByTitle('QuickStart')
if ($panel -eq [IntPtr]::Zero) { Write-Output 'panel not found'; exit 1 }

if (-not [QS]::IsWindowVisible($panel)) {
    $hostWnd = [QS]::ByTitle('QuickStart.Host')
    if ($hostWnd -ne [IntPtr]::Zero) {
        [QS]::PostMessage($hostWnd, 0x8001, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 1100
    }
}

if ($PSBoundParameters.ContainsKey('Type')) {
    $n = [QS]::SetText($panel, $Type)
    Start-Sleep -Milliseconds 700
    Write-Output ("set text: " + $n)
}

if ($Press) {
    $n = [QS]::Enter($panel)
    Start-Sleep -Milliseconds 900
    Write-Output ("pressed enter: " + $n)
}

[QS]::ToFront($panel)
Start-Sleep -Milliseconds 600

$r = New-Object QS+RECT
[QS]::GetWindowRect($panel, [ref]$r) | Out-Null
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
Write-Output ("panel rect: {0},{1} {2}x{3}" -f $r.Left, $r.Top, $w, $h)

if ($Mode -eq 'menu') {
    $rowH = 70; $listTop = 95
    [QS]::RightClick($panel, 300, ($listTop + $Row * $rowH + [int]($rowH / 2)))
    Start-Sleep -Milliseconds 1000
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $full = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($full)
    $g.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bounds.Size)
    $g.Dispose()
    $full.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $full.Dispose()
    Write-Output ("saved fullscreen: " + $Out)
} else {
    if ($w -le 0 -or $h -le 0) { Write-Output 'panel has zero size'; exit 1 }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output ("saved: " + $Out)
}
