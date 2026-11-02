# Reproduce/verify the "right-click hides the launcher" bug with REAL input:
# global hotkey (Alt+Space) -> type -> real right-click -> screenshot.
# ASCII only.
param(
    [string]$Type = 'ca',
    [int]$Row = 0,
    [string]$Out = 'D:\Agent\quickstart\shots\rightclick.png'
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class RC {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc c, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc c, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] public static extern IntPtr SendText(IntPtr h, int m, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

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

    public static bool SetInput(IntPtr parent, string text) {
        bool done = false;
        EnumChildWindows(parent, (h, p) => {
            var c = new StringBuilder(256);
            GetClassName(h, c, 256);
            if (c.ToString().IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) >= 0) {
                SendText(h, 0x000C, IntPtr.Zero, text);
                done = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return done;
    }
}
'@

try { [RC]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null } catch { [RC]::SetProcessDPIAware() | Out-Null }

# 1) Summon via the real global hotkey so QuickStart actually gets foreground (_activated = true)
$panel = [RC]::ByTitle('QuickStart')
if ([RC]::IsWindowVisible($panel)) {
    [System.Windows.Forms.SendKeys]::SendWait('% ')
    Start-Sleep -Milliseconds 800
    $panel = [RC]::ByTitle('QuickStart')
}
[System.Windows.Forms.SendKeys]::SendWait('% ')
Start-Sleep -Milliseconds 1400
$panel = [RC]::ByTitle('QuickStart')
Write-Output ("visible=" + [RC]::IsWindowVisible($panel) + "  isForeground=" + ([RC]::GetForegroundWindow() -eq $panel))

# 2) Type the query
if ($PSBoundParameters.ContainsKey('Type')) {
    [RC]::SetInput($panel, $Type) | Out-Null
    Start-Sleep -Milliseconds 700
}

# 3) Real right-click
$r = New-Object RC+RECT
[RC]::GetWindowRect($panel, [ref]$r) | Out-Null
Write-Output ("rect=" + $r.Left + "," + $r.Top + " " + ($r.Right - $r.Left) + "x" + ($r.Bottom - $r.Top))
$cx = $r.Left + [int](($r.Right - $r.Left) * 0.35)
$cy = $r.Top + 95 + $Row * 70 + 35
Write-Output ("right-click at " + $cx + "," + $cy)
[RC]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 250
[RC]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 80
[RC]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 1300

$stillVisible = [RC]::IsWindowVisible($panel)
Write-Output ("after right-click: panelVisible=" + $stillVisible)

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bounds.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("saved: " + $Out)
