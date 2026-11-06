using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Imago;

/// <summary>
/// Records what the person does during a screen recording: pointer moves, clicks, scrolls, shortcut and special keys,
/// and which window is in front. Written next to the video as a JSON track, and the signed manifest names its hash.
/// Typed characters are counted but never stored, so passwords and private text do not end up in the file.
/// </summary>
internal sealed class EventTrack : IDisposable
{
    public const string Ext = ".imago-track.json";
    public const string KeysPolicy = "Shortcuts and special keys are stored by name. Typed characters are counted only, never stored.";

    private readonly record struct Ev(long Qpc, string Kind, int X, int Y, int N, string? S, string? S2, int W, int H);

    private readonly Rectangle _monitor;
    private readonly List<Ev> _events = new(4096);
    private readonly object _lock = new();
    private readonly HashSet<uint> _down = [];
    private Thread? _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _mh, _kh;
    private HookProc? _mouseProc, _keyProc;
    private System.Threading.Timer? _poll;
    private long _lastMoveQpc;
    private int _lastX = int.MinValue, _lastY = int.MinValue;
    private long _lastHwnd;
    private string _lastExe = "", _lastTitle = "";

    /// <summary>False when Windows refused the input hooks (only window changes are recorded then).</summary>
    public bool HooksActive { get; private set; }

    /// <summary>True when the pointer is in the video pixels; false when it was left out so an editor can redraw it.</summary>
    public bool CursorBaked { get; set; } = true;

    public EventTrack(Rectangle monitor) { _monitor = monitor; }

    public static EventTrack Start(Rectangle monitor)
    {
        var t = new EventTrack(monitor);
        t._thread = new Thread(t.Pump) { IsBackground = true, Name = "Imago event track" };
        t._thread.SetApartmentState(ApartmentState.STA);
        t._thread.Start();
        t._ready.Wait(2000);
        t._poll = new System.Threading.Timer(_ => t.PollWindow(), null, 0, 150);
        return t;
    }

    public void Stop()
    {
        _poll?.Dispose(); _poll = null;
        if (_thread is { IsAlive: true }) { PostThreadMessage(_threadId, 0x12 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero); _thread.Join(1500); }
        _thread = null;
    }

    public void Dispose() => Stop();

    public int Count { get { lock (_lock) return _events.Count; } }

    // ---------- capture ----------

    private static long Now() => Stopwatch.GetTimestamp();

    private void Add(Ev e) { lock (_lock) _events.Add(e); }

    internal void AddForTest(string kind, int x = 0, int y = 0, int n = 0, string? s = null, string? s2 = null, long qpc = 0)
        => Add(new Ev(qpc, kind, x, y, n, s, s2, 0, 0));

    private void PollWindow()
    {
        try
        {
            long h = Native.sc_foreground();
            if (h == 0) return;
            var info = Native.WindowInfo(h);
            if (info is null) return;
            var (w, exe, title) = info.Value;
            if (h == _lastHwnd && exe == _lastExe && title == _lastTitle) return;
            _lastHwnd = h; _lastExe = exe; _lastTitle = title;
            Add(new Ev(Now(), "win", w.X - _monitor.X, w.Y - _monitor.Y, 0, Path.GetFileName(exe), title, w.W, w.H));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ObjectDisposedException) { }
    }

    private void Pump()
    {
        _threadId = GetCurrentThreadId();
        var mod = GetModuleHandle(null);
        _mouseProc = MouseHook;
        _keyProc = KeyHook;
        _mh = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _mouseProc, mod, 0);
        _kh = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _keyProc, mod, 0);
        HooksActive = _mh != IntPtr.Zero && _kh != IntPtr.Zero;
        _ready.Set();
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        if (_mh != IntPtr.Zero) UnhookWindowsHookEx(_mh);
        if (_kh != IntPtr.Zero) UnhookWindowsHookEx(_kh);
        _mh = _kh = IntPtr.Zero;
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var m = Marshal.PtrToStructure<MsLlHook>(lParam);
                int msg = (int)wParam;
                int x = m.X - _monitor.X, y = m.Y - _monitor.Y;
                bool inside = x >= 0 && y >= 0 && x < _monitor.Width && y < _monitor.Height;
                long q = Now();
                if (inside)
                {
                    switch (msg)
                    {
                        case 0x200: // move, at most about 60 per second
                            if ((x != _lastX || y != _lastY) && (q - _lastMoveQpc) * 1000 / Stopwatch.Frequency >= 12)
                            {
                                _lastMoveQpc = q; _lastX = x; _lastY = y;
                                Add(new Ev(q, "move", x, y, 0, null, null, 0, 0));
                            }
                            break;
                        case 0x201: Add(new Ev(q, "down", x, y, 0, "L", null, 0, 0)); break;
                        case 0x202: Add(new Ev(q, "up", x, y, 0, "L", null, 0, 0)); break;
                        case 0x204: Add(new Ev(q, "down", x, y, 0, "R", null, 0, 0)); break;
                        case 0x205: Add(new Ev(q, "up", x, y, 0, "R", null, 0, 0)); break;
                        case 0x207: Add(new Ev(q, "down", x, y, 0, "M", null, 0, 0)); break;
                        case 0x208: Add(new Ev(q, "up", x, y, 0, "M", null, 0, 0)); break;
                        case 0x20A: Add(new Ev(q, "wheel", x, y, (short)(m.Data >> 16), "V", null, 0, 0)); break;
                        case 0x20E: Add(new Ev(q, "wheel", x, y, (short)(m.Data >> 16), "H", null, 0, 0)); break;
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException or AccessViolationException or OverflowException) { }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                int msg = (int)wParam;
                uint vk = (uint)Marshal.ReadInt32(lParam);
                if (msg is 0x101 or 0x105) _down.Remove(vk);
                else if (msg is 0x100 or 0x104 && _down.Add(vk) && vk != 0x2C /* Print Screen: the stop hotkey */)
                {
                    bool ctrl = Down(0x11), alt = Down(0x12), win = Down(0x5B) || Down(0x5C), shift = Down(0x10);
                    var (kind, label) = Classify(vk, ctrl, alt, win, shift);
                    if (kind is not null) Add(new Ev(Now(), kind, 0, 0, 0, label, null, 0, 0));
                }
            }
            catch (Exception e) when (e is ArgumentException or AccessViolationException) { }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Decides what to keep for one key press. Returns ("key", name) for shortcuts and special keys, ("char", null)
    /// for printable characters (counted, never stored), and (null, null) for lone modifiers.
    /// </summary>
    internal static (string? Kind, string? Label) Classify(uint vk, bool ctrl, bool alt, bool win, bool shift)
    {
        if (vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C) return (null, null);
        bool printable = vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or 0x20 or >= 0x60 and <= 0x6F or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF;
        if (printable && !ctrl && !alt && !win) return ("char", null);
        var sb = new StringBuilder();
        if (ctrl) sb.Append("Ctrl+");
        if (alt) sb.Append("Alt+");
        if (win) sb.Append("Win+");
        if (shift) sb.Append("Shift+");
        sb.Append(vk switch
        {
            0x0D => "Enter", 0x1B => "Esc", 0x08 => "Backspace", 0x09 => "Tab", 0x20 => "Space", 0x2E => "Delete", 0x2D => "Insert",
            0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home", 0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
            >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            _ => "Key" + vk.ToString("X2"),
        });
        return ("key", sb.ToString());
    }

    // ---------- save ----------

    /// <summary>
    /// Writes the track. Times are milliseconds from the first video frame, so they line up with the video;
    /// earlier events are dropped, except the window that was already in front, which is kept at 0.
    /// </summary>
    public string Save(string path, string videoFile, long firstFrameQpc, int width, int height, double seconds)
    {
        Ev[] evs;
        lock (_lock) evs = [.. _events];
        double toMs = 1000.0 / Stopwatch.Frequency;
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("kind", "imago-track");
            w.WriteString("video", videoFile);
            w.WriteNumber("width", width);
            w.WriteNumber("height", height);
            w.WriteNumber("durationS", Math.Round(seconds, 2));
            w.WriteString("keys", KeysPolicy);
            w.WriteBoolean("inputHooks", HooksActive);
            w.WriteString("cursor", CursorBaked ? "baked" : "separate");
            w.WriteStartArray("events");
            Ev? lastWinBefore = null;
            foreach (var e in evs)
            {
                long t = (long)Math.Round((e.Qpc - firstFrameQpc) * toMs);
                if (t < 0) { if (e.Kind == "win") lastWinBefore = e; continue; }
                if (lastWinBefore is { } lw) { Write(w, lw, 0); lastWinBefore = null; }
                Write(w, e, t);
            }
            if (lastWinBefore is { } lw2) Write(w, lw2, 0);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static void Write(Utf8JsonWriter w, Ev e, long t)
    {
        w.WriteStartObject();
        w.WriteNumber("t", t);
        w.WriteString("e", e.Kind);
        switch (e.Kind)
        {
            case "move": w.WriteNumber("x", e.X); w.WriteNumber("y", e.Y); break;
            case "down": case "up": w.WriteNumber("x", e.X); w.WriteNumber("y", e.Y); w.WriteString("b", e.S); break;
            case "wheel": w.WriteNumber("x", e.X); w.WriteNumber("y", e.Y); w.WriteNumber("d", e.N); w.WriteString("axis", e.S); break;
            case "key": w.WriteString("k", e.S); break;
            case "win":
                w.WriteString("exe", e.S); w.WriteString("title", e.S2);
                w.WriteNumber("x", e.X); w.WriteNumber("y", e.Y); w.WriteNumber("w", e.W); w.WriteNumber("h", e.H);
                break;
        }
        w.WriteEndObject();
    }

    /// <summary>Hash and event count of a saved track, for the manifest.</summary>
    public static (string Sha, int Events) Describe(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes);
        int n = doc.RootElement.TryGetProperty("events", out var ev) ? ev.GetArrayLength() : 0;
        return (Convert.ToHexStringLower(SHA256.HashData(bytes)), n);
    }

    // ---------- Win32 ----------

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHook { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
