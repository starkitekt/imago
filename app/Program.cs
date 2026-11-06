using System.Globalization;
using System.Runtime.InteropServices;

namespace Imago;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int n);

    [STAThread]
    private static int Main(string[] args)
    {
        try { Native.Init(); }
        catch (Exception e) when (e is DllNotFoundException or InvalidOperationException or BadImageFormatException)
        {
            MessageBox.Show(e.Message, "Imago", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 4;
        }
        Native.sc_harden_process();
        ApplicationConfiguration.Initialize();

        if (args.Length == 2 && args[0] == "--edit" && File.Exists(args[1]))
        {
            string vf = Path.GetFullPath(args[1]);
            var rr = new RecordingResult(vf, 0, 0, 60, 0, true, DateTimeOffset.Now, DateTimeOffset.Now, "");
            Application.Run(new VideoEditor(vf, rr, (_, _, _, _) => Task.FromResult<string?>(null)));
            return 0;
        }

        if (args.Length is 2 or 3 && args[0] == "--flow" && File.Exists(args[1]))
        {
            Application.Run(new FlowEditor(Path.GetFullPath(args[1])) { AutoExport = args.Length == 3 && args[2] == "--export", AutoHtml = args.Length == 3 && args[2] == "--html", AutoSvg = args.Length == 3 && args[2] == "--svg", AutoReel = args.Length == 3 && args[2] == "--reel" });
            return Environment.ExitCode;
        }

        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
        {
            AttachConsoleIfNeeded();
            try { return Cli.Run(args); }
            catch (Exception e)
            {
                Console.Error.WriteLine("error: " + e.Message);
                return 4;
            }
        }

        if (args.Length == 1 && File.Exists(args[0]))
        {
            Viewer.Open(args[0]);
            if (Application.OpenForms.Count > 0) Application.Run(Application.OpenForms[0]!);
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\Imago.Tray.v1", out bool first);
        if (!first)
        {
            Alert.Show(null, "Imago is already running", "Look for its icon in the taskbar corner.", AlertKind.Info, "OK");
            return 0;
        }
        Application.Run(new TrayApp());
        return 0;
    }

    /// <summary>Shows CLI output in the calling console when there's no redirected stdout.</summary>
    private static void AttachConsoleIfNeeded()
    {
        var h = GetStdHandle(-11);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) Native.AttachConsole(-1);
    }
}

/// <summary>Command line for scripting and tests. All output goes to stdout or stderr.</summary>
internal static class Cli
{
    private const string Help = """
        Imago command line

          Imago.exe                                Start the tray app
          Imago.exe <file.png|jpg|snapseal>        Open and review a screenshot

          --capture full|window|area [--rect x,y,w,h] [--out dir]
                [--stamped] [--name "Name"] [--place "Place"] [--latlon lat,lon[,accuracyM]]
                [--use-location] [--tsa]
          --record [--seconds N] [--out dir] [--fps match|30|60|120] [--no-audio] [--no-mic] [--no-track] [--separate-cursor] [--stamped] [--name "Name"]
                Records the display under the cursor at full resolution to MP4 (system audio + mic).
                Without --seconds it records until you press Enter.
          --flow <file.mp4> [--export|--html|--svg|--reel]     Open the Flow editor, or export headless (MP4, HTML page, animated SVG, highlight reel)
          --flow-shots <a.png[@x,y]> <b.png> ... [--out dir]
                Builds an animated SVG walkthrough from screenshots; @x,y adds a click point for zoom and ripple.
                Derived work: unsigned, with a note naming and hashing every original.
          --verify <file.png|file.mp4>               Exit code: 0 valid, 2 tampered, 3 unsigned, 4 error
          --review <file.png|jpg>                     Changed or not, where, what kind, AI involvement
          --seal <file.png> --password-stdin [--delete-original]
          --unseal <file.snapseal> --out <dir> --password-stdin
          --key                                       Show this PC's signing key
          --selftest                                  Run the built-in checks
          --version
        """;

    public static int Run(string[] a)
    {
        string cmd = a[0];
        switch (cmd)
        {
            case "--help" or "-h" or "/?": Console.WriteLine(Help); return 0;
            case "--version": Console.WriteLine("Imago " + Native.AppVersion); return 0;
            case "--key": Console.WriteLine(Keys.Describe()); return 0;
            case "--selftest": return SelfTest.Run();
            case "--verify":
            {
                string f = Arg(a, 1);
                var r = f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? VideoProof.Verify(f) : Provenance.Verify(File.ReadAllBytes(f));
                Console.WriteLine(r.ToString().Replace("±", "+/-"));
                return (int)r.Status;
            }
            case "--record": return RecordCmd(a).GetAwaiter().GetResult();
            case "--review":
            {
                var r = Review.Analyze(File.ReadAllBytes(Arg(a, 1)));
                Console.WriteLine(r.Headline);
                if (r.StampId is { } id) Console.WriteLine("Stamp: " + id);
                foreach (var e in r.ImagoEdits) Console.WriteLine($"Imago edit: {e.Tool} at {e.Box.X},{e.Box.Y} {e.Box.Width}x{e.Box.Height}");
                foreach (var b in r.Altered) Console.WriteLine($"Changed: {b.X},{b.Y} {b.Width}x{b.Height}");
                foreach (var d in r.Details) Console.WriteLine("  " + d.Replace("±", "+/-"));
                Console.WriteLine("AI involvement: " + r.Ai);
                foreach (var w in r.AiReasons) Console.WriteLine("  " + w);
                return (int)r.Verdict;
            }
            case "--seal":
            {
                string pw = ReadPassword(a);
                if (pw.Length < Seal.MinPassword) throw new ArgumentException($"Password must be at least {Seal.MinPassword} characters.");
                Console.WriteLine(Seal.SealFile(Arg(a, 1), pw, Has(a, "--delete-original")));
                return 0;
            }
            case "--unseal":
            {
                string pw = ReadPassword(a);
                string outDir = Opt(a, "--out") ?? throw new ArgumentException("--out is required.");
                try
                {
                    var (name, png) = Seal.Decrypt(File.ReadAllBytes(Arg(a, 1)), pw);
                    Directory.CreateDirectory(outDir);
                    string safe = Naming.SafePart(Path.GetFileNameWithoutExtension(name), 120);
                    string p = Naming.Unique(outDir, safe.Length == 0 ? "Unsealed" : safe, ".png");
                    using (var fs = new FileStream(p, FileMode.CreateNew)) fs.Write(png);
                    Console.WriteLine(p);
                    return 0;
                }
                catch (Seal.WrongPasswordException e) { Console.Error.WriteLine(e.Message); return 2; }
            }
            case "--capture": return CaptureCmd(a).GetAwaiter().GetResult();
            case "--flow-shots":
            {
                var shots = a.Skip(1).TakeWhile(x => !x.StartsWith("--", StringComparison.Ordinal)).ToList();
                Console.WriteLine(FlowSvg.FromShots(shots, Opt(a, "--out") ?? Settings.ScreenshotDir));
                return 0;
            }
            default:
                Console.Error.WriteLine("Unknown option " + cmd);
                Console.WriteLine(Help);
                return 4;
        }
    }

    private static async Task<int> CaptureCmd(string[] a)
    {
        string mode = Arg(a, 1);
        string outDir = Opt(a, "--out") ?? Settings.ScreenshotDir;
        Rectangle area; long hwnd;
        switch (mode)
        {
            case "full":
                area = Opt(a, "--rect") is { } fr ? ParseRect(fr) : Native.VirtualScreen();
                hwnd = Native.sc_foreground();
                break;
            case "window":
                (area, hwnd) = Capture.TargetWindow();
                break;
            case "area":
                area = ParseRect(Opt(a, "--rect") ?? throw new ArgumentException("--rect x,y,w,h is required for area."));
                var c = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
                hwnd = Capture.WindowAt(Native.Windows().Where(w => w.Pid != Environment.ProcessId).ToArray(), c);
                break;
            default: throw new ArgumentException("Mode must be full, window or area.");
        }
        area = Capture.Clip(area);
        var img = Capture.Grab(area);
        var src = hwnd != 0 ? Naming.Describe(hwnd) : CaptureSource.Desktop;

        VerifiedOptions? v = null;
        if (Has(a, "--stamped") || Has(a, "--verified"))
        {
            var s = Settings.Load();
            s.UseLocation = Has(a, "--use-location");
            s.TrustedTimestamp = Has(a, "--tsa");
            if (Opt(a, "--name") is { } n) s.DisplayName = n;
            s.Place = Opt(a, "--place") ?? "";
            v = await TrayApp.VerifiedOptionsAsync(s);
            if (Opt(a, "--latlon") is { } ll)
            {
                var p = ll.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                if (p.Length < 2 || Math.Abs(p[0]) > 90 || Math.Abs(p[1]) > 180) throw new ArgumentException("--latlon needs lat,lon in degrees.");
                var fix = new GeoFix(p[0], p[1], p.Length > 2 ? p[2] : 10, "Manual", DateTimeOffset.Now);
                v = v with { Location = fix, Place = v.Place.Length > 0 ? v.Place : Places.Nearest(p[0], p[1]) };
            }
        }

        var r = await Capture.FinishAsync(img, mode, area, src, outDir, v);
        r.Image.Dispose();
        Console.WriteLine(r.Path);
        foreach (var w in r.Warnings) Console.Error.WriteLine("note: " + w);
        return 0;
    }

    private static async Task<int> RecordCmd(string[] a)
    {
        string outDir = Opt(a, "--out") ?? Settings.ScreenshotDir;
        Directory.CreateDirectory(outDir);
        int fps = Opt(a, "--fps") is { } fs && !fs.Equals("match", StringComparison.OrdinalIgnoreCase) ? int.Parse(fs, CultureInfo.InvariantCulture) : 0;
        if (fps != 0 && fps is < 5 or > 120) throw new ArgumentException("--fps must be between 5 and 120.");
        var opt = new RecordOptions(SystemAudio: !Has(a, "--no-audio"), Microphone: !Has(a, "--no-audio") && !Has(a, "--no-mic"), Fps: fps, Track: !Has(a, "--no-track"), BakeCursor: !Has(a, "--separate-cursor"));

        VerifiedOptions? v = null;
        if (Has(a, "--stamped") || Has(a, "--verified"))
        {
            var s = Settings.Load();
            if (Opt(a, "--name") is { } n) s.DisplayName = n;
            s.Place = Opt(a, "--place") ?? "";
            v = await TrayApp.VerifiedOptionsAsync(s);
        }

        var flow = await RecordFlow.StartAsync(outDir, opt);
        Console.Error.WriteLine($"Recording {flow.Recorder.Size.Width}x{flow.Recorder.Size.Height} at {(fps == 0 ? "the display rate" : fps + " fps")}...");
        if (Opt(a, "--seconds") is { } sec) await Task.Delay(TimeSpan.FromSeconds(double.Parse(sec, CultureInfo.InvariantCulture)));
        else { Console.Error.WriteLine("Press Enter to stop."); Console.In.ReadLine(); }
        var (path, rec, note) = await flow.StopAsync(v);
        Console.WriteLine(path);
        Console.Error.WriteLine($"{rec.Width}x{rec.Height}, {rec.Seconds:0.0}s, {rec.Codec}, " + (rec.HasAudio ? "with audio" : "no audio") + (v is null ? ", not signed" : ", signed"));
        if (note is not null) Console.Error.WriteLine("note: " + note);
        return 0;
    }

    private static Rectangle ParseRect(string s)
    {
        var p = s.Split(',').Select(x => int.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
        if (p.Length != 4 || p[2] <= 0 || p[3] <= 0) throw new ArgumentException("--rect must be x,y,w,h with positive size.");
        return new Rectangle(p[0], p[1], p[2], p[3]);
    }

    private static string ReadPassword(string[] a)
    {
        if (!Has(a, "--password-stdin")) throw new ArgumentException("Pass the password on stdin with --password-stdin (never as an argument).");
        return (Console.In.ReadLine() ?? "").TrimEnd('\r', '\n');
    }

    private static string Arg(string[] a, int i) =>
        a.Length > i && !a[i].StartsWith("--", StringComparison.Ordinal) ? a[i] : throw new ArgumentException($"{a[0]} needs a value.");

    private static bool Has(string[] a, string flag) => a.Contains(flag);

    private static string? Opt(string[] a, string flag)
    {
        int i = Array.IndexOf(a, flag);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
