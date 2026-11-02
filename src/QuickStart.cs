// QuickStart — 极简应用启动器
// 目标：Alt+Space 呼出 → 打字 → 回车打开。除此之外没有任何功能。
// 技术：单文件 C# / .NET Framework 4.8（系统自带，无需安装运行时）/ 无第三方库 / 全自绘 GDI+
//
// 编译：
//   csc /target:winexe /optimize+ /platform:anycpu /win32icon:app.ico
//       /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll
//       /out:QuickStart.exe QuickStart.cs
//
// 设计取舍：
//   * 索引是内存位图（开始菜单 + shell:AppsFolder + Program Files），一次约 100-400ms，
//     之后每次查询只是对几百个短字符串做 IndexOf，微秒级，输入零延迟。
//     （不读 Everything 那样的全盘 MFT，因为用户只要"应用"，不需要文件。）
//   * 不引 WPF / WinUI：常驻内存约 10-20MB，开机不加载 XAML、不建 D3D 设备。
//   * 常驻进程 = 一个隐藏的 NativeWindow + 托盘图标，没有窗口时不占 CPU。
//   * 不落盘缓存，程序启动后立刻在后台重建 → 新装的软件不需要任何"刷新"就能搜到。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("QuickStart")]
[assembly: System.Reflection.AssemblyDescription("极简应用启动器 Alt+Space")]
[assembly: System.Reflection.AssemblyProduct("QuickStart")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]

namespace QuickStart
{
    internal static class Program
    {
        public const string RunValue = "QuickStart";

        public static readonly string InstallDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickStart");

        public static readonly string ConfigPath = Path.Combine(InstallDir, "config.ini");

        public static Config Cfg;

        [STAThread]
        private static int Main(string[] args)
        {
            AppLog.Init();
            HighDpi.Enable();
            Cfg = LoadConfig();

            bool ensure = false;   // --ensure：自愈式启动，已在跑就静默退出（不自作主张弹出窗口）
            bool reg = false;      // --register：只登记开始菜单快捷方式 + App Paths，然后退出
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--ensure") ensure = true;
                else if (a == "--register") reg = true;
                else if (a == "--startup") return Cli.SetStartup(true);
                else if (a == "--nostartup") return Cli.SetStartup(false);
                else if (a == "--status") return Cli.Status();
                else if (a == "--quit") return Cli.Quit();
                else if (a == "--task") return Cli.InstallTask();
                else if (a == "--untask") return Cli.RemoveTask();
            }

            if (reg) return Cli.Register(false);

            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\QuickStart.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // 自愈任务/自启的第二次拉起：静默退出；用户手动双击才切换窗口
                    if (!ensure) SendToggleToRunningInstance();
                    return 0;
                }

                HighDpi.Enable();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { AppLog.W("UI 异常: " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { AppLog.W("未捕获: " + e.ExceptionObject); };

                AppLog.W("进程启动 pid=" + Process.GetCurrentProcess().Id + (ensure ? " (--ensure)" : ""));
                AppDomain.CurrentDomain.ProcessExit += delegate { AppLog.W("进程退出（正常路径）"); };
                Application.ApplicationExit += delegate { AppLog.W("ApplicationExit 触发"); };

                var host = new HostWindow();
                Application.Run();
                host.Dispose();
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        public static Config LoadConfig()
        {
            AppLog.Init();                       // 读取配置时可能需要写默认文件
            Cfg = Config.Load(ConfigPath);
            return Cfg;
        }

        private static void SendToggleToRunningInstance()
        {
            for (int i = 0; i < 25; i++)
            {
                IntPtr h = Native.FindWindow(null, HostWindow.WindowTitle);
                if (h != IntPtr.Zero)
                {
                    Native.PostMessage(h, Native.WM_QS_TOGGLE, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                Thread.Sleep(40);
            }
        }
    }

    // ------------------------------------------------------------------ 配置

    internal sealed class Config
    {
        public string Hotkey = "Alt+Space";
        public bool Dark = true;
        public double WidthRatio = 0.47;
        public bool MatchPath = true;
        public bool ScanProgramFiles = true;
        public bool IconWell = true;

        private const string Template =
            "# QuickStart 配置\r\n" +
            "# 修改后重启程序生效：右键托盘图标 → 退出，再双击 QuickStart.exe\r\n" +
            "\r\n" +
            "# 唤出热键。写法示例：Alt+Space / Ctrl+Alt+Space / Ctrl+Shift+K / Win+Space / Alt+`\r\n" +
            "# 可用键名：Alt Ctrl Shift Win Space Tab Esc Enter Insert Home End PageUp PageDown\r\n" +
            "#           Delete Backspace 字母 数字 F1-F24 以及 - = [ ] \\ ; , . / `\r\n" +
            "Hotkey=Alt+Space\r\n" +
            "\r\n" +
            "# 外观：dark / light\r\n" +
            "Theme=dark\r\n" +
            "\r\n" +
            "# 窗口宽度 = 屏幕工作区宽度 × 该比例（0.2 ~ 0.95）\r\n" +
            "WidthRatio=0.47\r\n" +
            "\r\n" +
            "# 搜索时是否把程序的安装路径也当作关键字\r\n" +
            "MatchPath=true\r\n" +
            "\r\n" +
            "# 是否扫描 Program Files 中的 exe（关掉可以再快一点，但绿色软件会搜不到）\r\n" +
            "ScanProgramFiles=true\r\n" +
            "\r\n" +
            "# 图标是否加一层极淡的圆角底片（让形状各异的图标看起来更整齐；true/false）\r\n" +
            "IconWell=true\r\n";

        public static Config Load(string path)
        {
            var c = new Config();
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (!File.Exists(path))
                {
                    File.WriteAllText(path, Template, new UTF8Encoding(true));
                    AppLog.W("写入默认配置: " + path);
                    return c;
                }

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "hotkey": if (v.Length > 0) c.Hotkey = v; break;
                        case "theme": c.Dark = v.ToLowerInvariant() != "light"; break;
                        case "widthratio":
                            double d;
                            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0.2 && d <= 0.95)
                                c.WidthRatio = d;
                            break;
                        case "matchpath": c.MatchPath = Truthy(v); break;
                        case "scanprogramfiles": c.ScanProgramFiles = Truthy(v); break;
                        case "iconwell": c.IconWell = Truthy(v); break;
                    }
                }
            }
            catch (Exception ex) { AppLog.W("读取配置失败: " + ex.Message); }
            return c;
        }

        private static bool Truthy(string v)
        {
            v = v.Trim().ToLowerInvariant();
            return v == "true" || v == "1" || v == "yes" || v == "on";
        }
    }

    // ------------------------------------------------------------------ 日志

    internal static class AppLog
    {
        private static string _path;
        private static readonly object Sync = new object();

        public static void Init()
        {
            try
            {
                Directory.CreateDirectory(Program.InstallDir);
                string p = Path.Combine(Program.InstallDir, "quickstart.log");
                if (File.Exists(p) && new FileInfo(p).Length > 256 * 1024) File.Delete(p);
                _path = p;
            }
            catch { _path = null; }
        }

        public static void LogPaint(long ms, int rows)
        {
            long now = Environment.TickCount;
            if (now - _lastPaintLog < 3000) return;   // 每次重绘都写日志会拖慢主线程
            _lastPaintLog = now;
            W("paint " + ms + "ms rows=" + rows);
        }

        private static long _lastPaintLog;

        public static void W(string msg)
        {
            if (_path == null) return;
            lock (Sync)
            {
                try
                {
                    File.AppendAllText(_path,
                        DateTime.Now.ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + msg + "\r\n",
                        new UTF8Encoding(false));
                }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------------ Win32

    internal static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const int WM_QS_TOGGLE = 0x8001;
        public const int WM_ERASEBKGND = 0x0014;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_CLOSE = 0x0010;
        public const int HTCLIENT = 1;
        public const int EM_SETCUEBANNER = 0x1501;

        public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;

        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int CS_DROPSHADOW = 0x00020000;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        public const int SIIGBF_ICONONLY = 0x1;
        public const int SIIGBF_BIGGERSIZEOK = 0x10;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string title);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr h);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT p, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid, out IShellItemImageFactory item);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string path, uint attrs, out SHFILEINFO info, uint size, uint flags);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr h);

        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        public static extern bool FreeConsole();

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr h);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

        public static SHELLEXECUTEINFO ShellExecuteInfoFor(string file)
        {
            var sei = new SHELLEXECUTEINFO();
            sei.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
            sei.lpFile = file;
            try { sei.lpDirectory = Path.GetDirectoryName(file); } catch { }
            sei.nShow = 5;
            sei.fMask = 0x0000000C;   // SEE_MASK_INVOKEIDLIST
            return sei;
        }

        /// <summary>去掉 Win11 给无边框窗口自动描的那圈 1px 边线。</summary>
        public static void SuppressBorder(IntPtr h)
        {
            try
            {
                int none = unchecked((int)0xFFFFFFFE);   // DWMWA_COLOR_NONE
                DwmSetWindowAttribute(h, 34 /*DWMWA_BORDER_COLOR*/, ref none, sizeof(int));
            }
            catch { }
        }

        public static void DarkTitleBar(IntPtr h, bool dark)
        {
            try { int v = dark ? 1 : 0; DwmSetWindowAttribute(h, 20, ref v, sizeof(int)); }
            catch { }
        }

        public static RECT WorkAreaAtCursor(out uint dpi)
        {
            var p = new POINT();
            GetCursorPos(out p);
            IntPtr mon = MonitorFromPoint(p, 2);
            dpi = 96;
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            {
                uint dx, dy;
                if (GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) dpi = dx;
                return mi.rcWork;
            }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            return new RECT { Left = wa.Left, Top = wa.Top, Right = wa.Right, Bottom = wa.Bottom };
        }

        public static void ForceForeground(IntPtr h)
        {
            if (GetForegroundWindow() == h) return;
            SetForegroundWindow(h);
            if (GetForegroundWindow() == h) return;
            uint target = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint self = GetCurrentThreadId();
            if (target != 0 && target != self)
            {
                AttachThreadInput(self, target, true);
                SetForegroundWindow(h);
                AttachThreadInput(self, target, false);
            }
        }
    }

    internal static class HighDpi
    {
        public static void Enable()
        {
            try { if (Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
            try { Native.SetProcessDPIAware(); } catch { }
        }
    }

    // ------------------------------------------------------------------ 图标

    internal sealed class IconLoader
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private Thread _preload;

        // 关键性能点：SHCreateItemFromParsingName + GetImage 是跨进程 shell 调用，
        // 单项 1~3ms。以前在 OnPaint 里同步取，结果一变就要卡几十毫秒。
        // 现在索引建完就在后台把整份图标预热到内存，画的时候只查字典。
        public void PreloadAllAsync(IList<AppEntry> entries)
        {
            if (entries == null || entries.Count == 0) return;
            if (_preload != null && _preload.IsAlive) return;

            var snapshot = new List<AppEntry>(entries);
            _preload = new Thread(delegate ()
            {
                try
                {
                    AppLog.W("图标预热开始，共 " + snapshot.Count + " 项");
                    var keys = new List<string>(snapshot.Count);
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        string k = snapshot[i].IconKey;
                        if (!string.IsNullOrEmpty(k) && !keys.Contains(k)) keys.Add(k);
                    }
                    for (int i = 0; i < keys.Count; i++)
                    {
                        string key = keys[i];
                        lock (_sync) { if (_cache.ContainsKey(key)) continue; }
                        try
                        {
                            Bitmap bmp = LoadShellImage(key) ?? LoadShellIcon(key);
                            lock (_sync) { _cache[key] = bmp; }
                        }
                        catch (Exception iex) { AppLog.W("预热单项失败 " + key + ": " + iex.Message); }
                        if ((i % 50) == 0) AppLog.W("预热进度 " + i + "/" + keys.Count);
                    }
                    AppLog.W("图标预热完成 " + keys.Count + " 个");
                }
                catch (Exception ex) { AppLog.W("图标预热失败: " + ex.Message); }
            });
            _preload.IsBackground = true;
            _preload.Priority = ThreadPriority.BelowNormal;
            _preload.Start();
        }

        public Bitmap Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (_sync)
            {
                Bitmap cached;
                if (_cache.TryGetValue(key, out cached)) return cached;
            }

            Bitmap bmp = LoadShellImage(key) ?? LoadShellIcon(key);
            lock (_sync)
            {
                if (_cache.Count < 2000) _cache[key] = bmp;
            }
            return bmp;
        }

        // 画的时候直接拿"已经缩放到目标像素"的位图：GDI+ 缩放本身要 1~2ms/张，
        // 每次重绘都缩放 7 张就是十几毫秒，这就是之前卡顿的主要来源。
        private readonly Dictionary<string, Bitmap> _sized =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        private int _presizedPx;
        private Thread _presize;

        /// <summary>后台把图标预缩放到绘制尺寸，避免第一次绘制某个图标时卡住 UI 线程。</summary>
        public void PreSizeAllAsync(IList<AppEntry> entries, int px)
        {
            if (entries == null || entries.Count == 0 || px <= 0) return;
            if (_presize != null && _presize.IsAlive) return;
            if (_presizedPx == px) return;

            var keys = new List<string>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                string k = entries[i].IconKey;
                if (!string.IsNullOrEmpty(k) && !keys.Contains(k)) keys.Add(k);
            }
            _presizedPx = px;

            _presize = new Thread(delegate ()
            {
                try
                {
                    for (int i = 0; i < keys.Count; i++) GetSized(keys[i], px);
                    AppLog.W("图标预缩放完成 " + keys.Count + " 个 @" + px + "px");
                }
                catch (Exception ex) { AppLog.W("图标预缩放失败: " + ex.Message); }
            });
            _presize.IsBackground = true;
            _presize.Priority = ThreadPriority.BelowNormal;
            _presize.Start();
        }

        public Bitmap GetSized(string key, int px)
        {
            if (string.IsNullOrEmpty(key) || px <= 0) return null;
            string k = key + "|" + px;
            lock (_sync)
            {
                Bitmap hit;
                if (_sized.TryGetValue(k, out hit)) return hit;
            }

            Bitmap src = Get(key);
            if (src == null) return null;

            // 先裁掉四周的透明边距：Steam 这类图标自带很大留白，
            // 不裁的话在底片里会显得比别的图标小一圈。
            Rectangle srcRect = TrimBounds(src);

            var result = new Bitmap(px, px);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                // 等比缩放后居中，避免非正方形图标被拉伸变形
                double s = Math.Min((double)px / srcRect.Width, (double)px / srcRect.Height);
                int w = Math.Max(1, (int)Math.Round(srcRect.Width * s));
                int h = Math.Max(1, (int)Math.Round(srcRect.Height * s));
                g.DrawImage(src, new Rectangle((px - w) / 2, (px - h) / 2, w, h), srcRect, GraphicsUnit.Pixel);
            }

            lock (_sync) { if (_sized.Count < 800) _sized[k] = result; }
            return result;
        }

        /// <summary>计算图标里不透明像素的包围盒，用于裁掉透明留白。</summary>
        private static Rectangle TrimBounds(Bitmap bmp)
        {
            var full = new Rectangle(0, 0, bmp.Width, bmp.Height);
            try
            {
                int w = bmp.Width, h = bmp.Height;
                var data = bmp.LockBits(full, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    int stride = data.Stride;
                    byte[] buf = new byte[stride * h];
                    Marshal.Copy(data.Scan0, buf, 0, buf.Length);

                    int minX = w, minY = h, maxX = -1, maxY = -1;
                    const int threshold = 16;
                    for (int y = 0; y < h; y++)
                    {
                        int row = y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            if (buf[row + x * 4 + 3] <= threshold) continue;
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                    if (maxX < minX || maxY < minY) return full;      // 全透明，保持原样
                    var box = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
                    if (box.Width < 2 || box.Height < 2) return full;
                    return box;
                }
                finally { bmp.UnlockBits(data); }
            }
            catch { return full; }
        }

        private static Bitmap LoadShellImage(string key)
        {
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                Native.IShellItemImageFactory factory;
                if (Native.SHCreateItemFromParsingName(key, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;

                IntPtr hbm;
                int hr = factory.GetImage(new Native.SIZE(64, 64), Native.SIIGBF_ICONONLY, out hbm);
                if (hr != 0 || hbm == IntPtr.Zero)
                {
                    hr = factory.GetImage(new Native.SIZE(64, 64), 0, out hbm);   // 少数商店应用只认"缩略图"模式

                }
                Marshal.ReleaseComObject(factory);
                if (hr != 0 || hbm == IntPtr.Zero) return null;

                try
                {
                    using (Bitmap raw = Image.FromHbitmap(hbm))
                    {
                        var copy = new Bitmap(raw.Width, raw.Height);
                        using (Graphics g = Graphics.FromImage(copy)) g.DrawImage(raw, 0, 0);
                        return copy;
                    }
                }
                finally { Native.DeleteObject(hbm); }
            }
            catch (Exception ex) { AppLog.W("icon 异常 " + key + ": " + ex.GetType().Name + " " + ex.Message); return null; }
        }

        private static Bitmap LoadShellIcon(string key)
        {
            try
            {
                var sfi = new Native.SHFILEINFO();
                IntPtr r = Native.SHGetFileInfo(key, 0, out sfi, (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)), 0x100);
                if (r == IntPtr.Zero || sfi.hIcon == IntPtr.Zero) return null;
                try
                {
                    using (Icon ico = Icon.FromHandle(sfi.hIcon))
                    using (Bitmap raw = ico.ToBitmap())
                    {
                        var copy = new Bitmap(raw.Width, raw.Height);
                        using (Graphics g = Graphics.FromImage(copy)) g.DrawImage(raw, 0, 0);
                        return copy;
                    }
                }
                finally { Native.DestroyIcon(sfi.hIcon); }
            }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------ 索引

    internal sealed class AppEntry
    {
        public string Name;
        public string Target;
        public string WorkDir;
        public string IconKey;
        public string NameLower;
        public string PathLower;
        public int Rank;      // 0=开始菜单 1=商店/系统应用 2=Program Files
        public bool IsAumid;
        public bool Ignore;

        public override string ToString() { return Name; }
    }

    internal sealed class AppIndex
    {
        private readonly object _sync = new object();
        private List<AppEntry> _entries = new List<AppEntry>();
        private Thread _worker;
        private volatile bool _ready;

        // 同一个查询串只算一次。连打时上一个结果几乎总能命中。
        private readonly Dictionary<string, List<AppEntry>> _queryCache =
            new Dictionary<string, List<AppEntry>>(StringComparer.Ordinal);

        public event Action Completed;

        public int Count { get { lock (_sync) return _entries.Count; } }

        public bool Ready { get { return _ready; } }

        public List<AppEntry> Snapshot()
        {
            lock (_sync) return _entries;
        }

        public void Start()
        {
            if (_worker != null && _worker.IsAlive) return;
            _worker = new Thread(delegate ()
            {
                try { Build(); }
                catch (Exception ex) { AppLog.W("索引异常: " + ex); }
                finally
                {
                    _ready = true;
                    Action h = Completed;
                    if (h != null) { try { h(); } catch { } }
                }
            });
            _worker.IsBackground = true;
            _worker.Priority = ThreadPriority.BelowNormal;
            _worker.Start();
        }

        private void Build()
        {
            var sw = Stopwatch.StartNew();
            var list = new List<AppEntry>(400);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            AddStartMenu(list, seen, byName);
            long t1 = sw.ElapsedMilliseconds;
            AddAppsFolder(list, seen, byName);
            long t2 = sw.ElapsedMilliseconds;

            if (Program.Cfg.ScanProgramFiles)
            {
                string localPrograms = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
                foreach (string root in new[] { @"C:\Program Files", @"C:\Program Files (x86)", localPrograms })
                {
                    try { AddExes(root, 0, 2, list, seen, byName); }
                    catch (Exception ex) { AppLog.W("扫描 " + root + ": " + ex.Message); }
                }
            }

            list.Sort(delegate (AppEntry a, AppEntry b)
            {
                int c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                return c != 0 ? c : a.Rank.CompareTo(b.Rank);
            });

            lock (_sync)
            {
                _entries = list;
                _queryCache.Clear();
            }
            AppLog.W(string.Format("索引 {0} 项，总 {1}ms（开始菜单 {2}ms，AppsFolder {3}ms）",
                list.Count, sw.ElapsedMilliseconds, t1, t2 - t1));
        }

        private static bool JunkName(string name)
        {
            string n = name.ToLowerInvariant();
            return n.IndexOf("卸载", StringComparison.Ordinal) >= 0
                || n.IndexOf("uninstall", StringComparison.Ordinal) >= 0
                || n.IndexOf("unins", StringComparison.Ordinal) >= 0
                || n.IndexOf("readme", StringComparison.Ordinal) >= 0
                || n.IndexOf("帮助", StringComparison.Ordinal) >= 0
                || n.IndexOf("help", StringComparison.Ordinal) >= 0
                || n.IndexOf("网站", StringComparison.Ordinal) >= 0
                || n.IndexOf("官网", StringComparison.Ordinal) >= 0
                || n.IndexOf("website", StringComparison.Ordinal) >= 0
                || n.IndexOf("changelog", StringComparison.Ordinal) >= 0
                || n.IndexOf("license", StringComparison.Ordinal) >= 0;
        }

        private static void AddStartMenu(List<AppEntry> list, HashSet<string> seen, Dictionary<string, int> byName)
        {
            foreach (string root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
            })
            {
                if (!Directory.Exists(root)) continue;
                foreach (string f in SafeFiles(root, 4))
                {
                    string ext = Path.GetExtension(f);
                    if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase)) continue;

                    string name = Path.GetFileNameWithoutExtension(f);
                    if (string.IsNullOrWhiteSpace(name) || JunkName(name)) continue;

                    var e = new AppEntry();
                    e.Name = name;
                    e.Target = f;
                    e.IconKey = f;
                    e.Rank = 0;
                    Add(e, list, seen, byName, "f:" + f.ToLowerInvariant());
                }
            }
        }

        // 逐层容错遍历：某个目录没有权限就跳过，不影响整体
        private static List<string> SafeFiles(string dir, int depth)
        {
            var result = new List<string>(64);
            try
            {
                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { files = new string[0]; }
                for (int i = 0; i < files.Length; i++) result.Add(files[i]);
            }
            catch { }

            if (depth <= 0) return result;
            try
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(dir); }
                catch { dirs = new string[0]; }
                for (int i = 0; i < dirs.Length; i++)
                {
                    string d = dirs[i];
                    if (d.Length > 0 && d[d.Length - 1] == '.') continue; // 跳过 xxx.文件夹 之类的隐藏目录
                    result.AddRange(SafeFiles(d, depth - 1));
                }
            }
            catch { }
            return result;
        }

        private static void AddExes(string dir, int depth, int rank, List<AppEntry> list, HashSet<string> seen, Dictionary<string, int> byName)
        {
            if (depth > 2 || !Directory.Exists(dir)) return;

            string[] files;
            try { files = Directory.GetFiles(dir, "*.exe"); }
            catch { return; }

            foreach (string f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (string.IsNullOrWhiteSpace(name) || JunkName(name)) continue;

                string low = name.ToLowerInvariant();
                if (low.StartsWith("unins") || low.StartsWith("setup") || low.StartsWith("update") ||
                    low.StartsWith("vc_redist") || low.StartsWith("crash") || low.StartsWith("helper") ||
                    low.StartsWith("elevate") || low.StartsWith("dllhost") || low.EndsWith("service") ||
                    low.EndsWith("svc") || low.EndsWith("updater")) continue;

                var e = new AppEntry();
                e.Name = name;
                e.Target = f;
                e.WorkDir = Path.GetDirectoryName(f);
                e.IconKey = f;
                e.Rank = rank;
                Add(e, list, seen, byName, "f:" + f.ToLowerInvariant());
            }

            string[] dirs;
            try { dirs = Directory.GetDirectories(dir); }
            catch { return; }

            foreach (string d in dirs)
            {
                string dn = Path.GetFileName(d).ToLowerInvariant();
                if (dn == "installer" || dn == "install" || dn == "setup" || dn == "uninstall" ||
                    dn == "resources" || dn == "locales" || dn == "redist" || dn == "redistributable" ||
                    dn == "vcredist" || dn == "node_modules" || dn == "python" || dn.StartsWith("$")) continue;
                AddExes(d, depth + 1, rank, list, seen, byName);
            }
        }

        private static void AddAppsFolder(List<AppEntry> list, HashSet<string> seen, Dictionary<string, int> byName)
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;

            object shell = null;
            object folder = null;
            object items = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                folder = shellType.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { "shell:AppsFolder" });
                if (folder == null) return;

                items = folder.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, folder, null);
                if (items == null) return;

                int count = (int)items.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, items, null);
                for (int i = 0; i < count; i++)
                {
                    object item = null;
                    try
                    {
                        item = items.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, items, new object[] { i });
                        if (item == null) continue;

                        string name = (string)item.GetType().InvokeMember("Name", System.Reflection.BindingFlags.GetProperty, null, item, null);
                        string path = (string)item.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, item, null);
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                        if (JunkName(name)) continue;

                        string ext = Path.GetExtension(path).ToLowerInvariant();
                        if (ext == ".html" || ext == ".htm" || ext == ".chm" || ext == ".txt" ||
                            ext == ".pdf" || ext == ".url") continue;

                        var e = new AppEntry();
                        e.Name = name;
                        e.Target = path;
                        e.IsAumid = path.IndexOf('\\') < 0;
                        e.Rank = e.IsAumid ? 1 : 0;
                        e.IconKey = e.IsAumid ? "shell:AppsFolder\\" + path : path;
                        if (!e.IsAumid) e.WorkDir = Path.GetDirectoryName(path);
                        Add(e, list, seen, byName, (e.IsAumid ? "a:" : "f:") + path.ToLowerInvariant());
                    }
                    catch { }
                    finally { if (item != null) Marshal.ReleaseComObject(item); }
                }
            }
            catch (Exception ex) { AppLog.W("AppsFolder: " + ex.Message); }
            finally
            {
                if (items != null) { try { Marshal.ReleaseComObject(items); } catch { } }
                if (folder != null) { try { Marshal.ReleaseComObject(folder); } catch { } }
                if (shell != null) { try { Marshal.ReleaseComObject(shell); } catch { } }
            }
        }

        private static void Add(AppEntry e, List<AppEntry> list, HashSet<string> seen, Dictionary<string, int> byName, string key)
        {
            if (!seen.Add(key)) return;
            e.NameLower = e.Name.ToLowerInvariant();
            e.PathLower = (e.Target ?? "").ToLowerInvariant();
            e.Ignore = false;

            // 同名只留一条：rank 越小越权威（开始菜单 > 商店应用 > 目录扫描）
            int existing;
            if (byName.TryGetValue(e.NameLower, out existing))
            {
                if (list[existing].Rank <= e.Rank) return;
                list[existing] = e;
                return;
            }
            byName[e.NameLower] = list.Count;
            list.Add(e);
        }

        // ---------------------------------------------------------- 搜索

        public List<AppEntry> Query(string text, int limit)
        {
            string cacheKey = (text ?? "").Trim();
            lock (_sync)
            {
                List<AppEntry> hit;
                if (_queryCache.TryGetValue(cacheKey, out hit)) return hit;
            }

            List<AppEntry> computed = QueryCore(text, limit);
            lock (_sync)
            {
                // 索引还没建好时查出来必然是空的，这种结果绝不能缓存，
                // 否则同一个关键词会一直命中空结果（实测踩过：开机瞬间输入过 'st'，
                // 之后索引就绪了 'st' 也永远显示"没有匹配项"）。
                if (_entries.Count > 0)
                {
                    if (_queryCache.Count > 80) _queryCache.Clear();
                    _queryCache[cacheKey] = computed;
                }
            }
            return computed;
        }

        private List<AppEntry> QueryCore(string text, int limit)
        {
            List<AppEntry> all = Snapshot();
            var result = new List<AppEntry>(limit);
            if (all.Count == 0) return result;

            string q = (text ?? "").Trim().ToLowerInvariant();
            if (q.Length == 0)
            {
                for (int i = 0; i < all.Count && result.Count < limit; i++)
                    if (!all[i].Ignore && all[i].Rank < 2) result.Add(all[i]);
                return result;
            }

            string[] tokens = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var hits = new List<KeyValuePair<int, AppEntry>>(64);
            bool matchPath = Program.Cfg == null || Program.Cfg.MatchPath;

            for (int i = 0; i < all.Count; i++)
            {
                AppEntry e = all[i];
                if (e.Ignore) continue;

                int total = 0;
                bool ok = true;
                for (int t = 0; t < tokens.Length; t++)
                {
                    int s = ScoreOne(e, tokens[t], matchPath);
                    if (s < 0) { ok = false; break; }
                    total += s;
                }
                if (!ok) continue;
                hits.Add(new KeyValuePair<int, AppEntry>(total - e.Rank * 2, e));
            }

            hits.Sort(delegate (KeyValuePair<int, AppEntry> a, KeyValuePair<int, AppEntry> b)
            {
                if (a.Key != b.Key) return b.Key.CompareTo(a.Key);
                return string.Compare(a.Value.Name, b.Value.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            for (int i = 0; i < hits.Count && result.Count < limit; i++) result.Add(hits[i].Value);
            return result;
        }

        private static int ScoreOne(AppEntry e, string tok, bool matchPath)
        {
            string n = e.NameLower;
            int idx = n.IndexOf(tok, StringComparison.Ordinal);
            if (idx >= 0)
            {
                if (n.Length == tok.Length) return 1000;
                if (idx == 0) return 900 - Math.Min(150, n.Length - tok.Length);
                char prev = n[idx - 1];
                if (prev == ' ' || prev == '-' || prev == '_' || prev == '.' || prev == '(' || prev == '[' ||
                    prev == '·' || prev == ':' || prev == '：')
                    return 800 - Math.Min(150, idx);
                return 620 - Math.Min(150, idx);
            }
            if (IsSubsequence(n, tok)) return 300;
            if (matchPath && e.PathLower.Length > 0 && e.PathLower.IndexOf(tok, StringComparison.Ordinal) >= 0) return 100;
            return -1;
        }

        private static bool IsSubsequence(string hay, string needle)
        {
            if (needle.Length > 20) return false;
            int j = 0;
            for (int i = 0; i < hay.Length && j < needle.Length; i++)
                if (hay[i] == needle[j]) j++;
            return j == needle.Length;
        }
    }

    // ------------------------------------------------------------------ 主题

    internal sealed class Theme
    {
        public Color Bg, Card, Border, Text, Sub, Accent, SelBg, SelBorder, Hint;
        public bool Dark;

        // 配色参考 Raycast 的深色启动器：几乎纯黑的底 + 卡片层 + 单一强调色。
        // 强调色只留给"可执行的动作"（命令模式），列表选中用中性灰而不是蓝，避免整屏发蓝。
        public static Theme Create(bool dark)
        {
            var t = new Theme();
            t.Dark = dark;
            if (dark)
            {
                t.Bg = Color.FromArgb(255, 18, 18, 21);
                t.Card = Color.FromArgb(255, 28, 28, 32);
                t.Border = Color.FromArgb(255, 44, 44, 50);
                t.Text = Color.FromArgb(255, 242, 242, 244);
                t.Sub = Color.FromArgb(255, 138, 138, 146);
                t.Hint = Color.FromArgb(255, 104, 104, 112);
                t.Accent = Color.FromArgb(255, 92, 143, 240);
                t.SelBg = Color.FromArgb(255, 38, 38, 44);
                t.SelBorder = Color.FromArgb(255, 54, 54, 62);
            }
            else
            {
                t.Bg = Color.FromArgb(255, 251, 251, 252);
                t.Card = Color.FromArgb(255, 243, 243, 246);
                t.Border = Color.FromArgb(255, 226, 226, 231);
                t.Text = Color.FromArgb(255, 24, 24, 27);
                t.Sub = Color.FromArgb(255, 116, 116, 124);
                t.Hint = Color.FromArgb(255, 148, 148, 156);
                t.Accent = Color.FromArgb(255, 37, 99, 235);
                t.SelBg = Color.FromArgb(255, 238, 238, 242);
                t.SelBorder = Color.FromArgb(255, 222, 222, 228);
            }
            return t;
        }
    }
    // ------------------------------------------------------------------ 搜索窗

    internal sealed class LauncherForm : Form
    {
        private const int MaxRows = 8;
        private const int MinRows = 3;

        private readonly HostWindow _host;
        private readonly TextBox _box;
        private readonly System.Windows.Forms.Timer _fade;
        private readonly System.Windows.Forms.Timer _debounce;

        private Font _fontInput, _fontItem, _fontHint, _fontMono;
        private Theme _theme;
        private Brush _brBg, _brCard, _brText, _brSub, _brSel, _brAccent, _brSelBorder, _brTile;
        private bool _softWell = true;      // 图标是否加"极淡底片"，来自 config.ini 的 IconWell
        private Pen _penBorder, _penAccent, _penSelBorder, _penTile;
        private double _scale = 1.0;
        private List<AppEntry> _results = new List<AppEntry>(40);
        private int _selected;
        private int _scrollTop;
        private int _rows = 7;
        private int _maxRows = 7;
        private int _rowH = 40;
        private int _listTop;
        private string _status = "";
        private string _pending;
        private bool _closing;
        private bool _activated;
        private double _fadeT;
        private DateTime _shownAt = DateTime.UtcNow;

        // 命令模式（输入 > 开头的任何内容）
        private bool _cmdMode;
        private string _cmdText = "";
        private int _normalHeight;          // 普通模式窗口高度，命令模式会临时收缩
        private int _normalRows;

        // 右键菜单
        private MenuPopup _menu;
        private bool _menuOpen;
        private bool _menuBusy;      // 正在准备菜单（这期间也不能因为失焦而收起窗口）

        private readonly Stopwatch _sw = new Stopwatch();   // 性能探针

        public LauncherForm(HostWindow host)
        {
            _host = host;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            Text = "QuickStart";

            _softWell = Program.Cfg == null || Program.Cfg.IconWell;
            ApplyTheme(Theme.Create(Program.Cfg == null || Program.Cfg.Dark));
            _fontInput = MakeFont(17.5f);
            _fontItem = MakeFont(13.5f);
            _fontHint = MakeFont(11.5f);
            _fontMono = MakeMonoFont(14);

            _box = new TextBox();
            _box.BorderStyle = BorderStyle.None;
            _box.Font = _fontInput;
            _box.TabIndex = 0;
            _box.TextChanged += OnQueryChanged;
            _box.KeyDown += OnInputKeyDown;
            Controls.Add(_box);

            _fade = new System.Windows.Forms.Timer();
            _fade.Interval = 15;
            _fade.Tick += OnFadeTick;

            _debounce = new System.Windows.Forms.Timer();
            _debounce.Interval = 12;          // 查询是纯内存操作，几乎不需要等待
            _debounce.Tick += delegate { _debounce.Stop(); ApplyQuery(_pending); };

            _status = "正在建立索引…";

            IntPtr h = Handle;   // 提前建句柄，首次呼出没有窗口创建开销
            GC.KeepAlive(h);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;  // 不进 Alt+Tab
                cp.ClassStyle |= Native.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.DarkTitleBar(Handle, _theme.Dark);
            Native.SuppressBorder(Handle);
            Native.SendMessage(_box.Handle, Native.EM_SETCUEBANNER, (IntPtr)1, "搜索软件，或输入 > 执行命令");
        }

        // ---------------------------------------------------------- 字体与画笔

        private static Font MakeFont(float cssPx, double scale)
        {
            float px = (float)Math.Round(cssPx * scale, 1);
            return new Font("Microsoft YaHei UI", px, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private static Font MakeMonoFont(float cssPx, double scale)
        {
            float px = (float)Math.Round(cssPx * scale, 1);
            try { return new Font("Consolas", px, FontStyle.Regular, GraphicsUnit.Pixel); }
            catch { return new Font("Microsoft YaHei UI", px, FontStyle.Regular, GraphicsUnit.Pixel); }
        }

        private Font MakeFont(float cssPx) { return MakeFont(cssPx, _scale); }

        private Font MakeMonoFont(float cssPx) { return MakeMonoFont(cssPx, _scale); }

        private int D(int css) { return (int)Math.Round(css * _scale); }

        public bool IsShown { get { return Visible && !_closing; } }

        private Rectangle ListRect { get { return new Rectangle(D(6), _listTop, Width - D(12), _rows * _rowH); } }

        /// <summary>换主题时一次性重建画笔，避免每次 OnPaint 分配几十个 Brush/Pen。</summary>
        private void ApplyTheme(Theme t)
        {
            _theme = t;
            DisposeBrushes();

            _brBg = new SolidBrush(t.Bg);
            _brCard = new SolidBrush(t.Card);
            _brText = new SolidBrush(t.Text);
            _brSub = new SolidBrush(t.Sub);
            _brSel = new SolidBrush(t.SelBg);
            _brSelBorder = new SolidBrush(t.SelBorder);
            _brAccent = new SolidBrush(t.Accent);
            float w = Math.Max(1f, (float)_scale);
            _penBorder = new Pen(t.Border, w);
            _penAccent = new Pen(t.Accent, w);
            _penSelBorder = new Pen(t.SelBorder, w);

            // 图标统一底片：比卡片略亮一点，让圆形/方形/透明底的图标落在同一个"格子"里
            // 半透明叠加：在普通行上是极淡的亮块，在选中行上同样只是提亮，不会变暗
            _brTile = new SolidBrush(t.Dark
                ? Color.FromArgb(24, 255, 255, 255)
                : Color.FromArgb(12, 0, 0, 0));
            _penTile = new Pen(t.Border, w);
        }

        private void DisposeBrushes()
        {
            if (_brBg != null) { _brBg.Dispose(); _brBg = null; }
            if (_brCard != null) { _brCard.Dispose(); _brCard = null; }
            if (_brText != null) { _brText.Dispose(); _brText = null; }
            if (_brSub != null) { _brSub.Dispose(); _brSub = null; }
            if (_brSel != null) { _brSel.Dispose(); _brSel = null; }
            if (_brSelBorder != null) { _brSelBorder.Dispose(); _brSelBorder = null; }
            if (_brAccent != null) { _brAccent.Dispose(); _brAccent = null; }
            if (_penBorder != null) { _penBorder.Dispose(); _penBorder = null; }
            if (_penAccent != null) { _penAccent.Dispose(); _penAccent = null; }
            if (_penSelBorder != null) { _penSelBorder.Dispose(); _penSelBorder = null; }
            if (_brTile != null) { _brTile.Dispose(); _brTile = null; }
            if (_penTile != null) { _penTile.Dispose(); _penTile = null; }
        }

        // ---------------------------------------------------------- 显示 / 隐藏

        public void ShowLauncher()
        {
            if (Visible && !_closing)
            {
                Native.ForceForeground(Handle);
                _box.Focus();
                _box.SelectAll();
                return;
            }

            uint dpi;
            Native.RECT work = Native.WorkAreaAtCursor(out dpi);
            double scale = dpi / 96.0;
            if (Math.Abs(scale - _scale) > 0.01)
            {
                _scale = scale;
                _fontInput.Dispose(); _fontItem.Dispose(); _fontHint.Dispose(); _fontMono.Dispose();
                _fontInput = MakeFont(17.5f);
                _fontItem = MakeFont(13.5f);
                _fontHint = MakeFont(11.5f);
                _fontMono = MakeMonoFont(14.5f);
                _box.Font = _fontInput;
            }

            int workW = work.Right - work.Left;
            int workH = work.Bottom - work.Top;

            int w = (int)(workW * (Program.Cfg == null ? 0.47 : Program.Cfg.WidthRatio));
            w = Math.Max(D(420), Math.Min(D(900), w));

            int pad = D(8);
            int sidePad = D(16);
            int inputH = D(46);
            _rowH = D(40);
            int gap = D(9);
            int rows = 7;

            _listTop = pad + inputH + gap;
            int h = _listTop + rows * _rowH + pad + D(8);

            int maxH = (int)(workH * 0.78);
            if (h > maxH)
            {
                rows = Math.Max(MinRows, (maxH - _listTop - pad - D(8)) / _rowH);
                h = _listTop + rows * _rowH + pad + D(8);
            }
            if (rows > MaxRows) rows = MaxRows;
            _rows = rows;

            int x = work.Left + (workW - w) / 2;
            int y = work.Top + (int)(workH * 0.12);
            Bounds = new Rectangle(x, y, w, h);
            _normalHeight = h;
            _normalRows = rows;
            _maxRows = rows;

            _box.SetBounds(sidePad + D(22), pad + (inputH - _box.PreferredHeight) / 2, w - sidePad * 2 - D(22), _box.PreferredHeight);

            using (GraphicsPath path = Round(ClientRectangle, D(12)))
                Region = new Region(path);

            Theme t = Theme.Create(Program.Cfg == null || Program.Cfg.Dark);
            ApplyTheme(t);
            BackColor = t.Bg;
            _box.BackColor = t.Bg;
            _box.ForeColor = t.Text;
            Native.DarkTitleBar(Handle, t.Dark);

            _box.Text = "";
            _pending = null;
            _debounce.Stop();
            _closing = false;
            _activated = false;
            _selected = 0;
            _scrollTop = 0;
            _cmdMode = false;
            _cmdText = "";
            CloseMenu();
            Opacity = 0;
            Invalidate();

            Show();
            Native.ForceForeground(Handle);
            Activate();
            _box.Focus();
            _box.SelectionStart = 0;

            _fadeT = 0;
            _shownAt = DateTime.UtcNow;
            _fade.Start();

            try { _host.Icons.PreSizeAllAsync(_host.Index.Snapshot(), Math.Max(8, D(26) - D(4))); } catch { }
            if (_host.Index.Count == 0) _host.Index.Start();
            ApplyQuery("");
        }

        public void HideLauncher()
        {
            if (!Visible) return;
            _closing = true;
            _fade.Stop();
            _debounce.Stop();
            CloseMenu();
            Hide();
            Opacity = 1;
            _closing = false;
            _host.OnLauncherClosed();
        }

        public void Toggle()
        {
            if (Visible && !_closing) HideLauncher();
            else ShowLauncher();
        }

        private void OnFadeTick(object sender, EventArgs e)
        {
            _fadeT += 0.25;
            if (_fadeT >= 1) { _fadeT = 1; _fade.Stop(); }
            Opacity = 0.2 + 0.8 * (1 - Math.Pow(1 - _fadeT, 2));
        }

        // ---------------------------------------------------------- 查询

        public void OnIndexReady()
        {
            ApplyQuery(_pending ?? _box.Text);
        }

        private void OnQueryChanged(object sender, EventArgs e)
        {
            _pending = _box.Text;
            _debounce.Stop();
            _debounce.Start();
        }

        private void ApplyQuery(string text)
        {
            text = text ?? "";

            // 命令模式：以 > 开头，后面原样作为 cmd 命令
            bool cmd = text.Length > 0 && text[0] == '>';
            if (cmd)
            {
                string body = text.Substring(1).Trim();
                if (!_cmdMode || body != _cmdText)
                {
                    _cmdMode = true;
                    _cmdText = body;
                    _results.Clear();
                    LayoutForState();
                    Invalidate();
                }
                return;
            }
            if (_cmdMode)
            {
                _cmdMode = false;
                _cmdText = "";
            }

            if (_host.Index.Count == 0)
            {
                _results.Clear();
                _status = "正在建立索引…";
                _selected = 0;
                _scrollTop = 0;
                Invalidate(ListRect);
                return;
            }

            _sw.Restart();
            _results = _host.Index.Query(text, 60);
            long queryMs = _sw.ElapsedMilliseconds;
            _selected = 0;
            _scrollTop = 0;
            _status = _results.Count == 0 ? (string.IsNullOrEmpty(text) ? "没有可启动的软件" : "没有匹配项") : "";
            if (queryMs >= 2) AppLog.W("query \"" + text + "\" " + queryMs + "ms hits=" + _results.Count);
            LayoutForState();
            Invalidate();              // 高度可能变了，整窗重绘
        }

        /// <summary>
        /// 按当前状态自适应窗口高度：结果少就矮一点、空状态只留输入区、命令模式只留命令栏。
        /// 之前是固定高度，列表空的时候会留下一大块黑底。
        /// </summary>
        private void LayoutForState()
        {
            if (_normalHeight <= 0) return;

            int h;
            if (_cmdMode)
            {
                h = _listTop + D(132) + D(12);
            }
            else if (_results.Count == 0)
            {
                h = _listTop + D(96);
            }
            else
            {
                int rows = Math.Min(_maxRows, _results.Count);
                h = _listTop + rows * _rowH + D(8) + D(8);
            }
            h = Math.Min(Math.Max(h, _listTop + _rowH), _normalHeight);

            if (Height != h)
            {
                Height = h;
                using (GraphicsPath path = Round(ClientRectangle, D(14)))
                    Region = new Region(path);
            }

            // 可视行数随高度变化
            int visible = Math.Max(1, (Height - _listTop - D(16)) / _rowH);
            _rows = Math.Max(1, Math.Min(_maxRows, visible));
            if (_scrollTop > Math.Max(0, _results.Count - _rows)) _scrollTop = Math.Max(0, _results.Count - _rows);
        }

        public void MoveSelection(int delta)
        {
            if (_cmdMode) return;
            int n = _results.Count;
            if (n == 0) return;
            int i = _selected + delta;
            if (i < 0) i = n - 1;
            if (i >= n) i = 0;
            _selected = i;
            EnsureVisible();
            Invalidate(ListRect);
        }

        private void EnsureVisible()
        {
            if (_selected < _scrollTop) _scrollTop = _selected;
            else if (_selected >= _scrollTop + _rows) _scrollTop = _selected - _rows + 1;
            int maxTop = Math.Max(0, _results.Count - _rows);
            if (_scrollTop > maxTop) _scrollTop = maxTop;
            if (_scrollTop < 0) _scrollTop = 0;
        }

        public void ActivateSelected()
        {
            if (_cmdMode) { RunCommand(_cmdText); return; }
            if (_selected < 0 || _selected >= _results.Count) return;
            AppEntry e = _results[_selected];
            HideLauncher();
            Launch.Execute(e);
        }

        private void RunCommand(string command)
        {
            command = (command ?? "").Trim();
            if (command.Length == 0) return;

            HideLauncher();
            try
            {
                // /k 让窗口留在那儿，免得命令输出一闪就没了
                var psi = new ProcessStartInfo("cmd.exe", "/k \"" + command + "\"");
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Process.Start(psi);
                AppLog.W("执行命令: " + command);
            }
            catch (Exception ex)
            {
                AppLog.W("命令执行失败: " + ex.Message);
                try
                {
                    var psi2 = new ProcessStartInfo("cmd.exe", "/k \"" + command + "\"");
                    psi2.UseShellExecute = false;
                    psi2.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    Process.Start(psi2);
                }
                catch (Exception ex2) { AppLog.W("命令兜底失败: " + ex2.Message); }
            }
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    if (_menuOpen) { CloseMenu(); }
                    else HideLauncher();
                    e.Handled = true; e.SuppressKeyPress = true;
                    break;
                case Keys.Enter:
                    ActivateSelected();
                    e.Handled = true; e.SuppressKeyPress = true;
                    break;
                case Keys.Up: MoveSelection(-1); e.Handled = true; e.SuppressKeyPress = true; break;
                case Keys.Down: MoveSelection(1); e.Handled = true; e.SuppressKeyPress = true; break;
                case Keys.PageUp: MoveSelection(-_rows); e.Handled = true; e.SuppressKeyPress = true; break;
                case Keys.PageDown: MoveSelection(_rows); e.Handled = true; e.SuppressKeyPress = true; break;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (Visible)
            {
                if (keyData == Keys.Escape)
                {
                    if (_menuOpen) CloseMenu(); else HideLauncher();
                    return true;
                }
                if (keyData == Keys.Up) { MoveSelection(-1); return true; }
                if (keyData == Keys.Down) { MoveSelection(1); return true; }
                if (keyData == Keys.Enter) { ActivateSelected(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            _activated = true;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // 右键菜单期间（包括"正在构造菜单"的瞬间）不能收起窗口：
            // 构造菜单会激活 WScript.Shell 等 COM 对象，COM 会派发消息循环，
            // 这期间可能收到 Deactivate，此时菜单还没显示出来，只剩窗口被收掉。
            if (_menuOpen || _menuBusy) return;
            if (_activated && (DateTime.UtcNow - _shownAt).TotalMilliseconds > 260) HideLauncher();
        }

        // ---------------------------------------------------------- 鼠标

        private int IndexAt(Point p)
        {
            if (_cmdMode) return -1;
            Rectangle r = ListRect;
            if (!r.Contains(p)) return -1;
            int row = (p.Y - r.Top) / _rowH;
            int idx = _scrollTop + row;
            if (row < 0 || row >= _rows || idx < 0 || idx >= _results.Count) return -1;
            return idx;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_menuOpen) return;
            int i = IndexAt(e.Location);
            if (i >= 0 && i != _selected) { _selected = i; Invalidate(ListRect); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int i = IndexAt(e.Location);
            if (i >= 0) { _selected = i; ActivateSelected(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Right) return;
            int i = IndexAt(e.Location);
            if (i < 0) return;
            _selected = i;
            Invalidate(ListRect);
            ShowRowMenu(_results[i], PointToScreen(e.Location));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_cmdMode || _results.Count <= _rows) return;
            _scrollTop -= Math.Sign(e.Delta);
            int maxTop = Math.Max(0, _results.Count - _rows);
            if (_scrollTop > maxTop) _scrollTop = maxTop;
            if (_scrollTop < 0) _scrollTop = 0;
            Invalidate(ListRect);
        }

        // ---------------------------------------------------------- 右键菜单

        private void ShowRowMenu(AppEntry entry, Point screenPoint)
        {
            _menuBusy = true;
            try
            {
                BuildAndShowMenu(entry, screenPoint);
            }
            catch (Exception ex)
            {
                AppLog.W("菜单打开失败: " + ex.Message);
                CloseMenu();
            }
            finally
            {
                _menuBusy = false;
            }
        }

        private void BuildAndShowMenu(AppEntry entry, Point screenPoint)
        {
            CloseMenu();

            string exe = ShellActions.ResolveExe(entry);
            bool isAumid = entry.IsAumid;
            bool canAdmin = !isAumid && exe != null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

            var items = new List<MenuEntry>();
            items.Add(Entry("打开", delegate { HideLauncher(); ShellActions.Open(entry); }));
            if (canAdmin)
                items.Add(Entry("以管理员身份运行", delegate { HideLauncher(); ShellActions.RunAsAdmin(entry); }));
            if (!isAumid)
            {
                items.Add(Entry("打开文件所在位置", delegate { HideLauncher(); ShellActions.OpenLocation(entry); }));
                items.Add(Entry("复制路径", delegate { HideLauncher(); ShellActions.CopyPath(entry); }));
                items.Add(new MenuEntry { IsSeparator = true });
                items.Add(new MenuEntry
                {
                    Text = "属性",
                    Action = delegate { HideLauncher(); ShellActions.ShowProperties(entry); }
                });
            }
            else
            {
                items.Add(Entry("复制应用 ID", delegate
                {
                    HideLauncher();
                    try { Clipboard.SetText(entry.Target); } catch { }
                }));
            }

            var menu = new MenuPopup(_theme, _scale, _fontItem, items);
            menu.FormClosed += delegate
            {
                // 只清理自己：连续右键时旧菜单的关闭不能把新菜单的保护标记清掉
                if (ReferenceEquals(_menu, menu))
                {
                    _menu = null;
                    _menuOpen = false;
                }
            };

            _menu = menu;
            _menuOpen = true;
            menu.PopupAt(screenPoint);
        }

        private static MenuEntry Entry(string text, Action action)
        {
            return new MenuEntry { Text = text, Action = action };
        }

        private void CloseMenu()
        {
            MenuPopup m = _menu;
            _menu = null;
            _menuOpen = false;
            if (m != null)
            {
                try { m.Close(); } catch { }
                try { m.Dispose(); } catch { }
            }
        }

        // ---------------------------------------------------------- 绘制

        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintCore(e.Graphics); }
            catch (Exception ex) { AppLog.W("绘制异常: " + ex.Message); }
        }

        private void PaintCore(Graphics g)
        {
            _sw.Restart();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            g.FillRectangle(_brBg, ClientRectangle);

            using (var pen = new Pen(Color.FromArgb(_theme.Dark ? 42 : 30, _theme.Border), 1f))
            using (GraphicsPath wp = Round(new Rectangle(0, 0, Width - 1, Height - 1), D(10)))
                g.DrawPath(pen, wp);

            DrawInputGlyph(g);

            // 发丝分隔线
            using (var pen = new Pen(Color.FromArgb(_theme.Dark ? 40 : 24, _theme.Border), 1f))
                g.DrawLine(pen, D(16), _listTop - D(5), Width - D(16), _listTop - D(5));

            if (_cmdMode) { DrawCommandPanel(g); return; }

            if (_results.Count == 0)
            {
                DrawEmptyState(g);
                return;
            }

            Rectangle lr = ListRect;
            for (int i = 0; i < _rows; i++)
            {
                int idx = _scrollTop + i;
                if (idx >= _results.Count) break;
                DrawRow(g, idx, new Rectangle(lr.Left, lr.Top + i * _rowH, lr.Width, _rowH));
            }

            DrawScrollHint(g);

            if (_sw.ElapsedMilliseconds >= 3) AppLog.LogPaint(_sw.ElapsedMilliseconds, _results.Count);
        }

        /// <summary>输入框左侧的放大镜。对齐基准取文本框自身的垂直中线，而不是估一个魔法数字。</summary>
        private void DrawInputGlyph(Graphics g)
        {
            if (_cmdMode) return;   // 命令模式输入框本身以 > 开头，不再重复画

            int dia = D(17);                          // 圆直径（与文字大写高度大致相当）
            int handle = D(4);                        // 手柄长度（右下 45°）
            int cx = D(16) + dia / 2;

            // 圆心对齐文字中线：手柄本身就是向右下溢出的，把它算进居中反而会把圆顶高一截
            int cy = _box.Top + _box.Height / 2;

            var circle = new Rectangle(cx - dia / 2, cy - dia / 2, dia, dia);
            float w = Math.Max(1.3f, (float)_scale * 1.25f);
            using (var pen = new Pen(_theme.Sub, w))
            {
                g.DrawEllipse(pen, circle);
                int x1 = (int)(circle.Right - dia * 0.16);
                int y1 = (int)(circle.Bottom - dia * 0.16);
                g.DrawLine(pen, x1, y1, x1 + handle, y1 + handle);
            }
        }
        private void DrawEmptyState(Graphics g)
        {
            // 以"输入行以下到窗口底部"的整块区域居中，而不是按行高算出来的小块
            int top = _listTop;
            int areaH = Math.Max(D(40), Height - top - D(8));
            int blockH = D(24) + D(4) + D(20);
            int y = top + (areaH - blockH) / 2;

            using (var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(_status, _fontItem, _brSub, new Rectangle(0, y, Width, D(24)), f);

            using (var f2 = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var b2 = new SolidBrush(Color.FromArgb(150, _theme.Sub)))
                g.DrawString("输入 > 可以直接执行命令", _fontHint, b2,
                    new Rectangle(0, y + D(24) + D(4), Width, D(20)), f2);
        }

        /// <summary>结果超出可视行数时，右侧画一条细滚动条做位置提示。</summary>
        private void DrawScrollHint(Graphics g)
        {
            int total = _results.Count;
            if (total <= _rows) return;

            Rectangle lr = ListRect;
            int trackX = Width - D(9);
            int top = lr.Top + D(6);
            int trackH = _rows * _rowH - D(12);
            if (trackH < D(20)) return;

            int thumbH = Math.Max(D(18), (int)((double)trackH * _rows / total));
            double ratio = total - _rows <= 0 ? 0 : (double)_scrollTop / (total - _rows);
            int thumbY = top + (int)((trackH - thumbH) * ratio);

            using (var thumb = new SolidBrush(Color.FromArgb(135, _theme.Sub)))
            using (GraphicsPath hp = Round(new Rectangle(trackX, thumbY, D(3), thumbH), D(1)))
                g.FillPath(thumb, hp);
        }

        private void DrawRow(Graphics g, int index, Rectangle slot)
        {
            AppEntry entry = _results[index];
            bool selected = index == _selected;
            var row = new Rectangle(slot.Left + D(6), slot.Top + D(1), slot.Width - D(12), slot.Height - D(2));

            if (selected)
            {
                using (GraphicsPath p = Round(row, D(8)))
                    g.FillPath(_brSel, p);
            }

            // 裸图标，不加底框：Raycast / PowerToys Run / Win11 开始菜单都是这么做的。
            int iconSize = D(18);
            int ix = row.Left + D(10);
            int iy = row.Top + (row.Height - iconSize) / 2;

            if (_softWell)
            {
                var well = new Rectangle(ix - D(4), iy - D(4), iconSize + D(8), iconSize + D(8));
                using (GraphicsPath wp = Round(well, D(6)))
                    g.FillPath(_brTile, wp);
            }

            Bitmap icon = _host.Icons.GetSized(entry.IconKey, iconSize);
            if (icon != null)
            {
                g.DrawImageUnscaled(icon, ix, iy);
            }
            else
            {
                string letter = entry.Name.Length > 0 ? entry.Name.Substring(0, 1).ToUpperInvariant() : "?";
                using (GraphicsPath p = Round(new Rectangle(ix, iy, iconSize, iconSize), D(4)))
                    g.FillPath(_brSel, p);
                TextRenderer.DrawText(g, letter, _fontItem, new Rectangle(ix, iy, iconSize, iconSize), _theme.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }

            int tx = ix + iconSize + D(12);
            int tw = row.Right - tx - D(56);
            TextRenderer.DrawText(g, entry.Name, _fontItem, new Rectangle(tx, row.Top, tw, row.Height), _theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            // 快捷键提示当作一等元数据（Raycast 的做法）：只在选中行显示，避免整列噪音
            if (selected)
            {
                TextRenderer.DrawText(g, "Enter", _fontHint,
                    new Rectangle(row.Right - D(50), row.Top, D(44), row.Height), _theme.Hint,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }
        /// <summary>命令模式面板：显示将要执行的命令 + 提示。</summary>
        private void DrawCommandPanel(Graphics g)
        {
            int left = D(6) + D(4);
            int top = _listTop + D(2);
            int width = Math.Max(D(60), Width - (D(6) + D(4)) * 2);
            int height = Math.Max(D(60), Height - top - D(12));
            var card = new Rectangle(left, top, width, height);

            using (GraphicsPath p = Round(card, D(10)))
            {
                g.FillPath(_brCard, p);
                using (var pen = new Pen(Color.FromArgb(110, _theme.Accent), Math.Max(1f, (float)_scale)))
                    g.DrawPath(pen, p);
            }

            int padX = D(18);

            // 顶部：> 徽标 + "命令模式"
            int badge = D(24);
            var badgeRect = new Rectangle(card.Left + padX, card.Top + D(16), badge, badge);
            using (GraphicsPath bp = Round(badgeRect, D(7)))
                g.FillPath(_brAccent, bp);
            TextRenderer.DrawText(g, ">", _fontMono,
                new Rectangle(badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height),
                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            TextRenderer.DrawText(g, "命令模式", _fontHint,
                new Rectangle(badgeRect.Right + D(10), badgeRect.Top, card.Width - padX * 2 - badge, badge),
                _theme.Sub, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // 命令正文
            string shown = _cmdText.Length == 0 ? "输入要执行的命令，例如 ipconfig、dir" : _cmdText;
            Color cmdColor = _cmdText.Length == 0 ? Color.FromArgb(145, _theme.Sub) : _theme.Text;
            int cmdTop = badgeRect.Bottom + D(16);
            TextRenderer.DrawText(g, shown, _fontMono,
                new Rectangle(card.Left + padX, cmdTop, card.Width - padX * 2, D(32)),
                cmdColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            // 底部键位提示（键名用强调色）
            int hintY = card.Bottom - D(38);
            int x = card.Left + padX;
            int hh = D(20);
            TextRenderer.DrawText(g, "Enter", _fontHint, new Rectangle(x, hintY, D(44), hh), _theme.Accent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            x += D(44);
            TextRenderer.DrawText(g, "在 cmd 中执行", _fontHint, new Rectangle(x, hintY, D(110), hh), _theme.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            x += D(118);
            TextRenderer.DrawText(g, "Esc", _fontHint, new Rectangle(x, hintY, D(32), hh), _theme.Accent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            x += D(32);
            TextRenderer.DrawText(g, "取消", _fontHint, new Rectangle(x, hintY, D(60), hh), _theme.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
        private static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) { p.AddRectangle(new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height))); return p; }
            int d = Math.Max(0, radius) * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            if (d > r.Height) d = r.Height;
            if (d > r.Width) d = r.Width;
            if (d < 2) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }
            if (m.Msg == Native.WM_NCHITTEST) { m.Result = (IntPtr)Native.HTCLIENT; return; }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeBrushes();
                CloseMenu();
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ Shell 动作（右键菜单用）

    internal static class ShellActions
    {
        public static void Open(AppEntry e)
        {
            Launch.Execute(e);
        }

        public static void RunAsAdmin(AppEntry e)
        {
            try
            {
                string target = ResolveExe(e) ?? e.Target;
                var psi = new ProcessStartInfo(target);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                string dir = e.WorkDir;
                if (string.IsNullOrEmpty(dir) && target != null && target.IndexOf('\\') > 0)
                    dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
                Process.Start(psi);
            }
            catch (Exception ex) { AppLog.W("管理员启动失败: " + ex.Message); }
        }

        public static void OpenLocation(AppEntry e)
        {
            try
            {
                string target = ResolveExe(e) ?? e.Target;
                if (!string.IsNullOrEmpty(target) && File.Exists(target))
                {
                    Process.Start("explorer.exe", "/select,\"" + target + "\"");
                    return;
                }
                if (!string.IsNullOrEmpty(e.WorkDir) && Directory.Exists(e.WorkDir))
                {
                    Process.Start("explorer.exe", "\"" + e.WorkDir + "\"");
                }
            }
            catch (Exception ex) { AppLog.W("打开位置失败: " + ex.Message); }
        }

        public static void CopyPath(AppEntry e)
        {
            try { Clipboard.SetText(e.Target ?? ""); }
            catch (Exception ex) { AppLog.W("复制路径失败: " + ex.Message); }
        }

        public static void ShowProperties(AppEntry e)
        {
            try
            {
                string target = e.Target;
                Native.SHELLEXECUTEINFO sei = Native.ShellExecuteInfoFor(target);
                sei.lpVerb = "properties";
                sei.lpFile = target;
                sei.nShow = 5;
                sei.fMask = 0x0000000C;      // SEE_MASK_INVOKEIDLIST
                if (!Native.ShellExecuteEx(ref sei))
                {
                    // 快捷方式自身也可能是用户想看的对象，退回到 ShellExecute
                    Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,ShellExec_RunDLL \"" + target + "\"")
                    { UseShellExecute = false, CreateNoWindow = true });
                }
            }
            catch (Exception ex) { AppLog.W("属性失败: " + ex.Message); }
        }

        /// <summary>把条目解析成真正的 exe 路径（.lnk 会解析到目标）。</summary>
        public static string ResolveExe(AppEntry e)
        {
            if (e == null || e.IsAumid) return null;
            string target = e.Target;
            if (string.IsNullOrEmpty(target)) return null;

            if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string resolved = ReadLnkTarget(target);
                if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved)) return resolved;
                return null;
            }
            return File.Exists(target) ? target : null;
        }

        private static string ReadLnkTarget(string lnkPath)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                object shell = Activator.CreateInstance(t);
                try
                {
                    object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                    object val = lnk.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, lnk, null);
                    return val as string;
                }
                finally { Marshal.ReleaseComObject(shell); }
            }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------ 自绘右键菜单

    internal sealed class MenuEntry
    {
        public string Text;
        public Action Action;
        public bool IsSeparator;
    }

    /// <summary>
    /// 完全自绘的弹出菜单。
    /// 不用 ContextMenuStrip：它的专业渲染器会强行套系统配色，
    /// 主题化非常别扭；自绘一个轻量弹窗反而更简单、样式也完全可控。
    /// </summary>
    internal sealed class MenuPopup : Form
    {
        private readonly List<MenuEntry> _items;
        private readonly Theme _theme;
        private readonly Font _font;
        private readonly double _scale;
        private int _hover = -1;
        private int _rowH;

        private Brush _brBg, _brText, _brSub, _brSel;
        private Pen _penBorder, _penSel;

        public MenuPopup(Theme theme, double scale, Font font, List<MenuEntry> items)
        {
            _theme = theme;
            _scale = scale;
            _font = font;
            _items = items;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = theme.Card;

            _rowH = Math.Max(D(26), font.Height + D(14));

            _brBg = new SolidBrush(theme.Card);
            _brText = new SolidBrush(theme.Text);
            _brSub = new SolidBrush(theme.Sub);
            _brSel = new SolidBrush(theme.SelBg);
            _penBorder = new Pen(theme.Border, Math.Max(1f, (float)scale));
            _penSel = new Pen(theme.SelBorder, Math.Max(1f, (float)scale));

            int width = 0;
            using (Graphics g = CreateGraphics())
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].IsSeparator) continue;
                    Size sz = TextRenderer.MeasureText(g, items[i].Text, font);
                    if (sz.Width > width) width = sz.Width;
                }
            }
            if (width <= 0) width = D(120);

            int h = 0;
            for (int i = 0; i < items.Count; i++) h += items[i].IsSeparator ? D(9) : _rowH;

            Width = width + D(38);
            Height = h + D(12);
        }

        private int D(int css) { return (int)Math.Round(css * _scale); }

        public void PopupAt(Point screenPoint)
        {
            Rectangle wa = Screen.FromPoint(screenPoint).WorkingArea;
            int x = screenPoint.X;
            int y = screenPoint.Y;
            if (x + Width > wa.Right) x = wa.Right - Width - D(4);
            if (y + Height > wa.Bottom) y = wa.Bottom - Height - D(4);
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;

            Bounds = new Rectangle(x, y, Width, Height);
            using (GraphicsPath path = Round(ClientRectangle, D(10)))
                Region = new Region(path);
            Show();
            Native.SuppressBorder(Handle);
            Native.ForceForeground(Handle);
            Activate();
        }

        private Rectangle ItemRect(int index)
        {
            int y = D(6);
            for (int i = 0; i < index; i++)
                y += _items[i].IsSeparator ? D(9) : _rowH;
            int h = _items[index].IsSeparator ? D(9) : _rowH;
            return new Rectangle(D(4), y, Width - D(8), h);
        }

        private int IndexAt(Point p)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].IsSeparator) continue;
                if (ItemRect(i).Contains(p)) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Location);
            if (i != _hover) { _hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int i = IndexAt(e.Location);
            if (i >= 0) Invoke(i);
        }

        private void Invoke(int index)
        {
            MenuEntry e = _items[index];
            Close();
            if (e.Action != null)
            {
                try { e.Action(); }
                catch (Exception ex) { AppLog.W("菜单动作失败: " + ex.Message); }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            {
                int dir = e.KeyCode == Keys.Down ? 1 : -1;
                int n = _items.Count;
                for (int step = 1; step <= n; step++)
                {
                    int idx = ((_hover < 0 ? (dir > 0 ? -1 : 0) : _hover) + dir * step) % n;
                    if (idx < 0) idx += n;
                    if (!_items[idx].IsSeparator) { _hover = idx; Invalidate(); break; }
                }
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Enter && _hover >= 0) { Invoke(_hover); e.Handled = true; }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillRectangle(_brBg, ClientRectangle);

            using (var pen = new Pen(Color.FromArgb(_theme.Dark ? 42 : 30, _theme.Border), 1f))
            using (GraphicsPath wp = Round(new Rectangle(0, 0, Width - 1, Height - 1), D(10)))
                g.DrawPath(pen, wp);

            for (int i = 0; i < _items.Count; i++)
            {
                MenuEntry item = _items[i];
                Rectangle r = ItemRect(i);

                if (item.IsSeparator)
                {
                    int y = r.Y + r.Height / 2;
                    g.DrawLine(_penBorder, D(12), y, Width - D(12), y);
                    continue;
                }

                if (i == _hover)
                {
                    using (GraphicsPath p = Round(r, D(6)))
                    {
                        g.FillPath(_brSel, p);
                        g.DrawPath(_penSel, p);
                    }
                }

                TextRenderer.DrawText(g, item.Text, _font, new Rectangle(r.X + D(12), r.Y, r.Width - D(20), r.Height), _theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }

            using (var pen = new Pen(_theme.Border, Math.Max(1f, (float)_scale)))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(0, radius) * 2;
            if (r.Width <= 0 || r.Height <= 0 || d <= 0) { p.AddRectangle(r); return p; }
            if (d > r.Height) d = r.Height;
            if (d > r.Width) d = r.Width;
            if (d < 2) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_brBg != null) _brBg.Dispose();
                if (_brText != null) _brText.Dispose();
                if (_brSub != null) _brSub.Dispose();
                if (_brSel != null) _brSel.Dispose();
                if (_penBorder != null) _penBorder.Dispose();
                if (_penSel != null) _penSel.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ 启动

    internal static class Launch
    {
        public static void Execute(AppEntry e)
        {
            try
            {
                AppLog.W("启动: " + e.Name + " -> " + e.Target);

                if (e.IsAumid)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + e.Target) { UseShellExecute = true });
                    return;
                }

                if (Path.GetExtension(e.Target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var psi = new ProcessStartInfo(e.Target);
                    psi.UseShellExecute = true;
                    if (!string.IsNullOrEmpty(e.WorkDir) && Directory.Exists(e.WorkDir)) psi.WorkingDirectory = e.WorkDir;
                    Process.Start(psi);
                    return;
                }

                Process.Start(new ProcessStartInfo(e.Target) { UseShellExecute = true });
            }
            catch (Exception ex) { AppLog.W("启动失败: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ 宿主（隐藏窗口 + 托盘）

    internal sealed class HostWindow : Form
    {
        public const string WindowTitle = "QuickStart.Host";

        public readonly AppIndex Index = new AppIndex();
        public readonly IconLoader Icons = new IconLoader();
        public readonly LauncherForm Ui;
        public bool Exiting;

        private NotifyIcon _tray;
        private Icon _trayIcon;
        private System.Windows.Forms.Timer _watch;
        private string _hotkeyLabel = "";
        private DateTime _lastBuild = DateTime.MinValue;
        private bool _indexReady;
        private bool _hotkeyOk;        // 期望热键是否真的注册上了
        private uint _wantMods, _wantVk;   // 配置里想要的热键（不随回退变化）
        private int _watchTick;

        public HostWindow()
        {
            // 永远不显示：只借它的窗口句柄接收 WM_HOTKEY
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-32000, -32000, 8, 8);
            Text = WindowTitle;
            Opacity = 0;
            CreateControl();

            Ui = new LauncherForm(this);
            GC.KeepAlive(Ui.Handle);

            RegisterHotkey();
            BuildTray();

            Index.Completed += OnIndexCompleted;
            Rebuild();

            _watch = new System.Windows.Forms.Timer();
            _watch.Interval = 2000;
            _watch.Tick += OnWatch;
            _watch.Start();
        }

        private void Rebuild()
        {
            _lastBuild = DateTime.UtcNow;
            Index.Start();
        }

        private void BuildTray()
        {
            _trayIcon = TrayIcon.Build();
            _tray = new NotifyIcon();
            _tray.Icon = _trayIcon;
            _tray.Text = "QuickStart — 极简启动器";
            _tray.Visible = true;
            _tray.DoubleClick += delegate { Ui.Toggle(); };
            _tray.MouseUp += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Right) ShowTrayMenu(); };
        }

        private void ShowTrayMenu()
        {
            var menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.Items.Add("打开搜索框（" + (_hotkeyLabel.Length > 0 ? _hotkeyLabel : "热键未生效") + "）", null,
                delegate { Ui.ShowLauncher(); });
            menu.Items.Add("重建应用索引", null, delegate { Rebuild(); });
            menu.Items.Add("重新登记到开始菜单", null, delegate
            {
                Cli.Register(false);
                try { _tray.ShowBalloonTip(4000, "QuickStart", "已登记到开始菜单，Windows 搜索现在能找到了。", ToolTipIcon.Info); }
                catch { }
            });

            var startup = new ToolStripMenuItem("开机自动启动");
            startup.Checked = Boot.IsStartupEnabled();
            startup.Click += delegate { Boot.SetStartup(!startup.Checked); };
            menu.Items.Add(startup);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("重新注册热键", null, delegate
            {
                Native.UnregisterHotKey(Handle, 1);
                _hotkeyOk = false;
                RegisterHotkey();
            });
            menu.Items.Add("打开配置文件夹", null, delegate
            {
                try { Process.Start("explorer.exe", Program.InstallDir); } catch { }
            });
            menu.Items.Add("卸载（删除程序与自启项）", null, delegate
            {
                if (MessageBox.Show("确定卸载 QuickStart 吗？\r\n\r\n会删除程序目录与开机自启项。",
                        "QuickStart", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                Exiting = true;
                _tray.Visible = false;
                Boot.Uninstall();
                Application.Exit();
            });
            menu.Items.Add("退出", null, delegate
            {
                Exiting = true;
                _tray.Visible = false;
                Application.Exit();
            });
            menu.Show(Cursor.Position);
        }

        public void OnLauncherClosed()
        {
            // 安装/卸载软件后无需任何操作：超过 2 分钟就在后台重建一次索引
            if (DateTime.UtcNow - _lastBuild > TimeSpan.FromMinutes(2)) Rebuild();
        }

        private void OnIndexCompleted()
        {
            _indexReady = true;
            try { Icons.PreloadAllAsync(Index.Snapshot()); } catch { }
            try
            {
                if (Ui.IsHandleCreated)
                    Ui.BeginInvoke((MethodInvoker)delegate { if (Ui.IsShown) Ui.OnIndexReady(); });
            }
            catch { }
        }

        private void OnWatch(object sender, EventArgs e)
        {
            // 热键被别的启动器抢走时定期重试，对方一退出就自动拿回来
            if (!_hotkeyOk && _wantVk != 0)
            {
                _watchTick++;
                if (_watchTick % 5 == 0 && Native.RegisterHotKey(Handle, 1, _wantMods | Native.MOD_NOREPEAT, _wantVk))
                {
                    _hotkeyOk = true;
                    _hotkeyLabel = Hotkey.ToText(_wantMods, _wantVk);
                    AppLog.W("热键已找回: " + _hotkeyLabel);
                    if (_tray != null) _tray.Text = "QuickStart — 极简启动器（" + _hotkeyLabel + "）";
                    return;
                }
            }

            if (_indexReady) return;
            if (DateTime.UtcNow - _lastBuild > TimeSpan.FromSeconds(20)) Rebuild();
        }

        // ---------------------------------------------------------- 热键

        private void RegisterHotkey()
        {
            uint mods, vk;
            if (!Hotkey.TryParse(Program.Cfg.Hotkey, out mods, out vk))
            {
                AppLog.W("热键无法解析: " + Program.Cfg.Hotkey + "，回退 Alt+Space");
                mods = Native.MOD_ALT;
                vk = 0x20;
            }

            _wantMods = mods;
            _wantVk = vk;

            if (Native.RegisterHotKey(Handle, 1, mods | Native.MOD_NOREPEAT, vk))
            {
                _hotkeyOk = true;
                _hotkeyLabel = Hotkey.ToText(mods, vk);
                AppLog.W("热键已注册: " + _hotkeyLabel);
                return;
            }

            AppLog.W("热键被占用: " + Hotkey.ToText(mods, vk));
            AppLog.W("提示：Flow Launcher 默认也注册 Alt+Space，两者只能有一个生效");

            var fallbacks = new[]
            {
                new[] { (uint)(Native.MOD_CONTROL | Native.MOD_ALT), (uint)0x20 },
                new[] { (uint)(Native.MOD_CONTROL | Native.MOD_SHIFT), (uint)0x20 },
                new[] { (uint)(Native.MOD_ALT | Native.MOD_SHIFT), (uint)0x20 },
                new[] { (uint)(Native.MOD_CONTROL), (uint)0x20 }
            };

            foreach (uint[] f in fallbacks)
            {
                if (Native.RegisterHotKey(Handle, 1, f[0] | Native.MOD_NOREPEAT, f[1]))
                {
                    _hotkeyOk = false;          // 现在用的是临时替代键，期望键仍然没拿到
                    _hotkeyLabel = Hotkey.ToText(f[0], f[1]);
                    AppLog.W("已回退热键: " + _hotkeyLabel + "（期望 " + Hotkey.ToText(_wantMods, _wantVk) + "）");
                    if (_tray != null) _tray.Text = "QuickStart — 热键 " + _hotkeyLabel;
                    return;
                }
            }

            AppLog.W("所有热键候选均注册失败");
            if (_tray != null) _tray.Text = "QuickStart — 热键注册失败，请双击图标";
            try
            {
                _tray.ShowBalloonTip(8000, "QuickStart",
                    "热键 " + Hotkey.ToText(mods, vk) + " 已被其它程序占用。\r\n" +
                    "可以关掉 Flow Launcher 的 Alt+Space，或修改 config.ini 里的 Hotkey。",
                    ToolTipIcon.Warning);
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == 1) { Ui.Toggle(); m.Result = IntPtr.Zero; return; }
            if (m.Msg == Native.WM_QS_TOGGLE) { Ui.Toggle(); m.Result = IntPtr.Zero; return; }
            if (m.Msg == Native.WM_CLOSE) { Exiting = true; Application.Exit(); m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }

        public new void Dispose()
        {
            try { Native.UnregisterHotKey(Handle, 1); } catch { }
            try { _watch.Stop(); } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { if (_trayIcon != null) _trayIcon.Dispose(); } catch { }
            try { Dispose(true); } catch { }
        }
    }

    // ------------------------------------------------------------------ 热键解析

    internal static class Hotkey
    {
        private static readonly Dictionary<string, uint> Vks = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            { "space", 0x20 }, { "tab", 0x09 }, { "esc", 0x1B }, { "escape", 0x1B },
            { "enter", 0x0D }, { "return", 0x0D }, { "insert", 0x2D }, { "ins", 0x2D },
            { "home", 0x24 }, { "end", 0x23 }, { "pageup", 0x21 }, { "pagedown", 0x22 },
            { "delete", 0x2E }, { "del", 0x2E }, { "backspace", 0x08 }, { "back", 0x08 },
            { "up", 0x26 }, { "down", 0x28 }, { "left", 0x25 }, { "right", 0x27 },
            { "`", 0xC0 }, { "-", 0xBD }, { "=", 0xBB }, { "[", 0xDB }, { "]", 0xDD },
            { "\\", 0xDC }, { ";", 0xBA }, { ",", 0xBC }, { ".", 0xBE }, { "/", 0xBF }, { "'", 0xDE }
        };

        public static bool TryParse(string spec, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(spec)) return false;

            string[] parts = spec.Split('+');
            if (parts.Length < 2) return false;

            for (int i = 0; i < parts.Length; i++)
            {
                string raw = parts[i].Trim();
                if (raw.Length == 0) return false;

                if (i < parts.Length - 1)
                {
                    string m = raw.ToLowerInvariant();
                    if (m == "alt") mods |= Native.MOD_ALT;
                    else if (m == "ctrl" || m == "control") mods |= Native.MOD_CONTROL;
                    else if (m == "shift") mods |= Native.MOD_SHIFT;
                    else if (m == "win" || m == "windows") mods |= Native.MOD_WIN;
                    else return false;
                    continue;
                }

                string k = raw.ToLowerInvariant();
                uint v;
                if (Vks.TryGetValue(k, out v)) { vk = v; continue; }
                if (raw.Length == 1)
                {
                    char c = char.ToUpperInvariant(raw[0]);
                    if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = c; continue; }
                }
                if (k.Length >= 2 && k[0] == 'f')
                {
                    int n;
                    if (int.TryParse(k.Substring(1), out n) && n >= 1 && n <= 24) { vk = (uint)(0x6F + n); continue; }
                }
                return false;
            }
            return vk != 0 && mods != 0;
        }

        public static string ToText(uint mods, uint vk)
        {
            var sb = new StringBuilder();
            if ((mods & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & Native.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & Native.MOD_WIN) != 0) sb.Append("Win+");

            if (vk == 0x20) { sb.Append("Space"); return sb.ToString(); }
            if (vk >= 0x70 && vk <= 0x87) { sb.Append('F').Append(vk - 0x6F); return sb.ToString(); }
            sb.Append((char)vk);
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ 托盘图标

    internal static class TrayIcon
    {
        public static Icon Build()
        {
            try
            {
                using (var bmp = new Bitmap(32, 32))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        using (var ring = new Pen(Color.FromArgb(255, 110, 170, 255), 3.2f))
                            g.DrawEllipse(ring, 4.5f, 4.5f, 17f, 17f);
                        using (var handle = new Pen(Color.FromArgb(255, 110, 170, 255), 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLine(handle, 18.5f, 18.5f, 27f, 27f);
                        using (var dot = new SolidBrush(Color.FromArgb(255, 170, 215, 255)))
                            g.FillEllipse(dot, 9f, 9f, 8f, 8f);
                    }
                    IntPtr h = bmp.GetHicon();
                    try
                    {
                        using (Icon tmp = Icon.FromHandle(h))
                            return (Icon)tmp.Clone();
                    }
                    finally { Native.DestroyIcon(h); }
                }
            }
            catch { return SystemIcons.Application; }
        }
    }

    // ------------------------------------------------------------------ 自启 / 卸载

    internal static class Boot
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return k != null && k.GetValue(Program.RunValue) != null;
            }
            catch { return false; }
        }

        public static bool SetStartup(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return false;
                    // 带 --ensure：万一自愈任务先一步拉起，这次就静默退出而不是把窗口弹出来
                    if (on) k.SetValue(Program.RunValue, "\"" + Application.ExecutablePath + "\" --ensure");
                    else k.DeleteValue(Program.RunValue, false);
                }
                AppLog.W("开机自启 = " + on);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.W("设置自启失败: " + ex.Message);
                return false;
            }
        }

        public static void Uninstall()
        {
            SetStartup(false);
            try
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + Program.InstallDir + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
            }
            catch (Exception ex) { AppLog.W("删除目录失败: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ 命令行

    internal static class Cli
    {
        public static int SetStartup(bool on)
        {
            AttachForOutput();
            bool ok = Boot.SetStartup(on);
            Console.WriteLine(on ? (ok ? "已开启开机自启" : "开启失败") : (ok ? "已关闭开机自启" : "关闭失败"));
            return ok ? 0 : 1;
        }

        private static void AttachForOutput()
        {
            try { Native.AttachConsole(-1); } catch { }
        }

        public static int Status()
        {
            AttachForOutput();
            Console.WriteLine("安装目录 : " + Program.InstallDir);
            Console.WriteLine("配置文件 : " + Program.ConfigPath);
            Console.WriteLine("开机自启 : " + (Boot.IsStartupEnabled() ? "已开启" : "未开启"));
            Console.WriteLine("热键     : " + Program.Cfg.Hotkey);
            IntPtr h = Native.FindWindow(null, HostWindow.WindowTitle);
            Console.WriteLine("运行状态 : " + (h != IntPtr.Zero ? "正在运行" : "未运行"));
            return 0;
        }

        public static int Quit()
        {
            AttachForOutput();
            IntPtr h = Native.FindWindow(null, HostWindow.WindowTitle);
            if (h == IntPtr.Zero) { Console.WriteLine("未在运行"); return 1; }
            Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Console.WriteLine("已请求退出");
            return 0;
        }

        /// <summary>
        /// 让 QuickStart 能被 Windows 搜索 / 开始菜单 / Win+R 找到：
        ///  1) 开始菜单快捷方式（Windows 搜索就是索引这个目录）
        ///  2) HKCU 的 App Paths（Win+R 直接敲 QuickStart 能启动）
        /// 两者都在当前用户范围，不需要管理员。
        /// </summary>
        public static int Register(bool verbose)
        {
            AttachForOutput();
            string exe = Application.ExecutablePath;

            // 1) 开始菜单快捷方式
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string dir = Path.Combine(programs, "QuickStart");
            string lnkPath = Path.Combine(dir, "QuickStart.lnk");
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object lnk = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { exe });
                lt.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(exe) });
                lt.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { "QuickStart 极简启动器（Alt+Space）" });
                lt.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { exe + ",0" });
                lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
                Marshal.ReleaseComObject(lnk);
                Marshal.ReleaseComObject(shell);
                AppLog.W("开始菜单快捷方式已登记: " + lnkPath);
            }
            catch (Exception ex)
            {
                AppLog.W("开始菜单快捷方式登记失败: " + ex.Message);
                if (verbose) Console.Error.WriteLine("快捷方式失败: " + ex.Message);
            }

            // 2) App Paths（HKCU）
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\QuickStart.exe"))
                {
                    k.SetValue(null, exe);
                    k.SetValue("Path", Path.GetDirectoryName(exe));
                }
                AppLog.W("App Paths 已登记");
            }
            catch (Exception ex)
            {
                AppLog.W("App Paths 登记失败: " + ex.Message);
                if (verbose) Console.Error.WriteLine("App Paths 失败: " + ex.Message);
            }

            if (verbose)
            {
                Console.WriteLine("开始菜单: " + lnkPath);
                Console.WriteLine("App Paths: HKCU\\...\\App Paths\\QuickStart.exe");
            }
            return 0;
        }

        /// <summary>
        /// 装一个"每 5 分钟自愈"的计划任务。
        /// 用 /SC MINUTE 而不是 LogonTrigger：前者普通权限就能建，后者要管理员。
        /// 任务本身不常驻：每次只是拉起 QuickStart.exe --ensure，
        /// 已经在跑就立刻静默退出（约 30ms），不会占内存也不会闪窗口。
        /// </summary>
        public static int InstallTask()
        {
            AttachForOutput();
            string exe = Application.ExecutablePath;

            // 先删掉可能存在的旧任务（比如之前用 XML 创建失败留下的半成品）
            TryRunSchtasks("/Delete /TN \"QuickStart\" /F");

            string output;
            int code = TryRunSchtasks(
                "/Create /TN \"QuickStart\" /TR \"\\\"" + exe + "\\\" --ensure\" /SC MINUTE /MO 5 /F",
                out output);

            bool ok = code == 0;
            AppLog.W("计划任务 QuickStart -> " + (ok ? "已创建" : "失败(" + code + "): " + output));
            Console.WriteLine(ok
                ? "计划任务已创建：每 5 分钟检查一次，掉线自动拉起"
                : "计划任务创建失败: " + output);
            return ok ? 0 : 1;
        }

        private static int TryRunSchtasks(string arguments)
        {
            string ignored;
            return TryRunSchtasks(arguments, out ignored);
        }

        private static int TryRunSchtasks(string arguments, out string output)
        {
            output = "";
            try
            {
                var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
                p.WaitForExit(15000);
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        public static int RemoveTask()
        {
            AttachForOutput();
            string output;
            int code = TryRunSchtasks("/Delete /TN \"QuickStart\" /F", out output);
            Console.WriteLine(code == 0 ? "计划任务已删除" : "删除失败: " + output);
            return code == 0 ? 0 : 1;
        }
    }
}
