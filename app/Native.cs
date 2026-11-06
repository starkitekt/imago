using System.Reflection;
using System.Runtime.InteropServices;

namespace Imago;

[StructLayout(LayoutKind.Sequential)]
internal struct ScWindow
{
    public long Hwnd;
    public int X, Y, W, H;
    public uint Pid;
    public uint Affinity;
    public int Flags;
    public int Reserved;

    public readonly Rectangle Rect => new(X, Y, W, H);
    public readonly bool IsForeground => (Flags & 1) != 0;
    public readonly bool IsProtected => (Flags & 2) != 0;
}

/// <summary>Calls into snapcore.dll (the C capture core) and a few Win32 APIs.</summary>
internal static class Native
{
    private const string Core = "snapcore.dll";

    /// <summary>Loads snapcore.dll by absolute path from the app folder, never by search order.</summary>
    public static void Init()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) =>
        {
            if (!name.Equals(Core, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            string path = Path.Combine(AppContext.BaseDirectory, Core);
            if (!File.Exists(path))
                throw new DllNotFoundException($"snapcore.dll is missing. Keep it in the same folder as Imago ({AppContext.BaseDirectory}).");
            return NativeLibrary.Load(path);
        });
        if (sc_version() < 2) throw new InvalidOperationException("snapcore.dll is out of date.");
    }

    [DllImport(Core)] public static extern int sc_version();
    [DllImport(Core)] public static extern int sc_harden_process();
    [DllImport(Core)] public static extern int sc_virtual_screen([Out] int[] out4);
    [DllImport(Core)] public static extern int sc_monitor_at(int x, int y, [Out] int[] out4);
    [DllImport(Core)] public static extern int sc_capture(int x, int y, int w, int h, IntPtr dst, int stride, long dstLen);
    [DllImport(Core)] public static extern int sc_enum_windows([Out] ScWindow[] output, int cap);
    [DllImport(Core)] public static extern long sc_foreground();
    [DllImport(Core, CharSet = CharSet.Unicode)]
    public static extern int sc_window_info(long hwnd, out ScWindow w, [Out] char[] exe, int exeCap, [Out] char[] title, int titleCap);

    public static Rectangle VirtualScreen()
    {
        var a = new int[4];
        sc_virtual_screen(a);
        return new Rectangle(a[0], a[1], a[2], a[3]);
    }

    public static Rectangle MonitorAt(Point p, out int dpi)
    {
        var a = new int[4];
        dpi = sc_monitor_at(p.X, p.Y, a);
        if (dpi <= 0) dpi = 96;
        return new Rectangle(a[0], a[1], a[2], a[3]);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PosX, PosY, Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency, IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DevMode dm);

    /// <summary>The refresh rate of the monitor holding this rectangle, clamped to 24..120. Falls back to 60 if Windows will not say.</summary>
    public static int RefreshRate(Rectangle monitor)
    {
        try
        {
            var dm = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettingsW(Screen.FromRectangle(monitor).DeviceName, -1, ref dm) && dm.DisplayFrequency > 1)
                return Math.Clamp(dm.DisplayFrequency, 24, 120);
        }
        catch (Exception e) when (e is ArgumentException or MarshalDirectiveException) { }
        return 60;
    }

    public static ScWindow[] Windows()
    {
        var buf = new ScWindow[512];
        int n = sc_enum_windows(buf, buf.Length);
        return n <= 0 ? [] : buf[..n];
    }

    public static (ScWindow win, string exe, string title)? WindowInfo(long hwnd)
    {
        if (hwnd == 0) return null;
        var exe = new char[1024];
        var title = new char[1024];
        if (sc_window_info(hwnd, out var w, exe, exe.Length, title, title.Length) != 0) return null;
        return (w, new string(exe).TrimEnd('\0').Split('\0')[0], new string(title).TrimEnd('\0').Split('\0')[0]);
    }

    // ---- user32 / shell ----

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    public const uint VK_SNAPSHOT = 0x2C;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int pid);

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserNameExW(int format, [Out] char[] buffer, ref uint size);

    /// <summary>The signed-in person's display name, or the account name.</summary>
    public static string DisplayName()
    {
        try
        {
            var buf = new char[256];
            uint n = (uint)buf.Length;
            if (GetUserNameExW(3 /* NameDisplay */, buf, ref n) && n > 0)
            {
                string s = new string(buf, 0, (int)n).Trim();
                if (s.Length > 0) return s;
            }
        }
        catch (EntryPointNotFoundException) { }
        return Environment.UserName;
    }

    public static string AppVersion =>
        typeof(Native).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
}
