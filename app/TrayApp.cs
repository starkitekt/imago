using System.Diagnostics;

namespace Imago;

/// <summary>The tray icon, global hotkeys and the capture flow.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private enum Mode { Area, Window, Full, Text }

    private readonly NotifyIcon _icon;
    private readonly HotkeyWindow _keys;
    private readonly Settings _s;
    private readonly ToolStripMenuItem _verifiedItem;
    private readonly System.Windows.Forms.Timer _geoTimer = new() { Interval = 5 * 60 * 1000 };
    private MainWindow? _main;
    private bool _busy;
    private RecordFlow? _rec;
    private DateTime _recStart;
    private readonly ToolStripMenuItem _recordItem;
    private readonly System.Windows.Forms.Timer _recTick = new() { Interval = 1000 };
    private string? _last;
    private static TrayApp? _instance;

    public TrayApp()
    {
        _instance = this;
        _s = Settings.Load();
        Directory.CreateDirectory(Settings.ScreenshotDir);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Capture area\tPrtSc", null, (_, _) => Fire(Mode.Area, fromMenu: true));
        menu.Items.Add("Capture window\tAlt+PrtSc", null, (_, _) => Fire(Mode.Window, fromMenu: true));
        menu.Items.Add("Capture full screen\tCtrl+PrtSc", null, (_, _) => Fire(Mode.Full, fromMenu: true));
        menu.Items.Add("Copy text from screen\tCtrl+Alt+PrtSc", null, (_, _) => Fire(Mode.Text, fromMenu: true));
        _recordItem = new ToolStripMenuItem("Record screen\tShift+PrtSc", null, (_, _) => ToggleRecording());
        menu.Items.Add(_recordItem);
        var fpsMenu = new ToolStripMenuItem("Recording frame rate");
        foreach (var (label, val) in new[] { ("Match display", 0), ("30 fps", 30), ("60 fps", 60) })
        {
            var item = new ToolStripMenuItem(label) { Checked = _s.RecordFps == val };
            item.Click += (_, _) =>
            {
                _s.RecordFps = val;
                _s.Save();
                foreach (ToolStripMenuItem o in fpsMenu.DropDownItems) o.Checked = ReferenceEquals(o, item);
            };
            fpsMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(fpsMenu);
        var sepCursor = new ToolStripMenuItem("Record cursor separately (for Flow editor)") { Checked = _s.CursorSeparate, CheckOnClick = true };
        sepCursor.Click += (_, _) => { _s.CursorSeparate = sepCursor.Checked; _s.Save(); };
        menu.Items.Add(sepCursor);
        menu.Items.Add(new ToolStripSeparator());
        _verifiedItem =new ToolStripMenuItem("Stamped screenshots", null, (_, _) => ToggleVerified());
        menu.Items.Add(_verifiedItem);
        menu.Items.Add("Lock last capture with password...", null, (_, _) =>
        {
            if (_last is null || !File.Exists(_last)) { Toast("Nothing to lock yet", "Take a screenshot first."); return; }
            SealWithPrompt(_last);
        });
        menu.Items.Add("Open or review a screenshot...", null, (_, _) => OpenFile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Screenshot folder", null, (_, _) => OpenFolder());
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add("Use Print Screen for Imago...", null, (_, _) => PrintScreenHelp());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Imago", null, (_, _) => ExitThread());

        _icon = new NotifyIcon { Icon = Ui.AppIcon, Text = "Imago", ContextMenuStrip = menu, Visible = true };
        _icon.BalloonTipClicked += (_, _) => { if (_last is not null && File.Exists(_last)) Start("explorer.exe", $"/select,\"{_last}\""); };

        _keys = new HotkeyWindow(OnHotkey);
        // Left-click opens the main window. Right-click still opens the menu.
        // While recording, a left-click stops it, so stopping never depends on finding a button.
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { if (_rec is not null) ToggleRecording(); else ShowMain(); } };
        _recTick.Tick += (_, _) => _icon.Text = "Imago: recording " + (DateTime.Now - _recStart).ToString(@"mm\:ss") + " (clicks and keys tracked)";
        RegisterKeys(firstRun: true);
        UpdateVerifiedItem();

        _geoTimer.Tick += async (_, _) => { if (_s.Verified && _s.UseLocation) await Geo.GetAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10)); };
        _geoTimer.Start();
        if (_s.Verified && _s.UseLocation) _ = Geo.GetAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
    }

    protected override void ExitThreadCore()
    {
        _geoTimer.Dispose();
        _recTick.Dispose();
        _main?.Dispose();
        _keys.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }

    // ---------- hotkeys ----------

    private void RegisterKeys(bool firstRun)
    {
        _keys.UnregisterAll();
        const uint NR = Native.MOD_NOREPEAT;
        bool prt = _keys.Register(1, NR, Native.VK_SNAPSHOT);
        _keys.Register(2, NR | Native.MOD_ALT, Native.VK_SNAPSHOT);
        _keys.Register(3, NR | Native.MOD_CONTROL, Native.VK_SNAPSHOT);
        _keys.Register(4, NR | Native.MOD_CONTROL | Native.MOD_ALT, Native.VK_SNAPSHOT);
        _keys.Register(8, NR | Native.MOD_SHIFT, Native.VK_SNAPSHOT);
        _keys.Register(9, NR | Native.MOD_CONTROL | Native.MOD_SHIFT, '6');
        bool laptop = _s.LaptopKeys || !prt;
        if (laptop)
        {
            uint cs = NR | Native.MOD_CONTROL | Native.MOD_SHIFT;
            _keys.Register(5, cs, '4');
            _keys.Register(6, cs, '3');
            _keys.Register(7, cs, '5');
        }
        if (!prt && firstRun)
            Toast("Print Screen is taken", "Windows or another app is using Print Screen, so Imago uses Ctrl+Shift+4 for now. Right-click the tray icon for help.");
        else if (firstRun)
            Toast("Imago is ready", "PrtSc: area   Alt+PrtSc: window   Ctrl+PrtSc: full screen. Saved to " + Settings.ScreenshotDir);
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case 1 or 5: Fire(Mode.Area); break;
            case 2 or 7: Fire(Mode.Window); break;
            case 3 or 6: Fire(Mode.Full); break;
            case 4: Fire(Mode.Text); break;
            case 8 or 9: ToggleRecording(); break;
        }
    }

    // ---------- recording ----------

    private bool _recBusy;

    private async void ToggleRecording()
    {
        if (_recBusy) return;
        _recBusy = true;
        try
        {
            if (_rec is null) await StartRecordingAsync();
            else await StopRecordingAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                      or System.Runtime.InteropServices.COMException or System.Security.Cryptography.CryptographicException)
        {
            _rec = null;
            _recTick.Stop();
            _icon.Text = "Imago";
            _recordItem.Text = "Record screen\tShift+PrtSc";
            Toast("Recording failed", e.Message, ToolTipIcon.Error);
        }
        finally { _recBusy = false; }
    }

    private async Task StartRecordingAsync()
    {
        Directory.CreateDirectory(Settings.ScreenshotDir);
        await Task.Delay(300); // let a menu fade before the first frame
        _rec = await RecordFlow.StartAsync(Settings.ScreenshotDir, new RecordOptions(Fps: _s.RecordFps, BakeCursor: !_s.CursorSeparate));
        _recStart = DateTime.Now;
        _recordItem.Text = "Stop recording\tShift+PrtSc";
        _recTick.Start();
        Toast("Recording", $"{_rec.Recorder.Size.Width} x {_rec.Recorder.Size.Height}. Clicks and special keys are tracked, typed text is not. Shift+PrtSc stops.");
    }

    private async Task StopRecordingAsync()
    {
        var flow = _rec!;
        _rec = null;
        _recTick.Stop();
        _icon.Text = "Imago";
        _recordItem.Text = "Record screen\tShift+PrtSc";
        _icon.Text = "Imago: saving recording...";
        VerifiedOptions? v = _s.Verified ? await VerifiedOptionsAsync(_s) : null;
        var (path, rec, note) = await flow.StopAsync(v);
        _icon.Text = "Imago";
        _last = path;
        Toast("Recording saved" + (v is null ? "" : " and signed"), Path.GetFileName(path) + (note is null ? "" : "\n" + note));
        var ed = new VideoEditor(path, rec, async (outPath, a, b, parent) =>
        {
            var r2 = rec with { Seconds = (b - a).TotalSeconds };
            return v is null ? null : await VideoProof.SignAsync(outPath, r2, v, [new VideoProof.Edit("trim", a.TotalSeconds, b.TotalSeconds)], parent);
        });
        ed.Show();
        ed.Activate();
    }

    // ---------- capture ----------

    private async void Fire(Mode mode, bool fromMenu = false)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (fromMenu) await Task.Delay(250); // let the menu fade before freezing the screen
            await RunAsync(mode);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            Toast("Screenshot not saved", e.Message, ToolTipIcon.Error);
        }
        finally { _busy = false; }
    }

    private async Task RunAsync(Mode mode)
    {
        Bitmap img;
        Rectangle area;
        long hwnd;
        string modeName;

        if (mode is Mode.Area or Mode.Text)
        {
            var virt = Native.VirtualScreen();
            int me = Environment.ProcessId;
            var wins = Native.Windows().Where(w => w.Pid != me).ToArray();
            using var frozen = Capture.Grab(virt);
            string hint = mode == Mode.Text
                ? "Select text to copy  ·  Esc to cancel"
                : "Click a window, or drag to select  ·  Esc to cancel";
            using var overlay = new Overlay(frozen, virt, wins, hint);
            if (overlay.ShowDialog() != DialogResult.OK) return;
            area = overlay.Result;
            hwnd = overlay.ResultHwnd;
            var local = area; local.Offset(-virt.X, -virt.Y);
            local.Intersect(new Rectangle(Point.Empty, frozen.Size));
            if (local.Width <= 0 || local.Height <= 0) return;
            img = frozen.Clone(local, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            modeName = hwnd != 0 && overlay.Result.Size == Capture.Clip(wins.FirstOrDefault(w => w.Hwnd == hwnd).Rect).Size ? "window" : "area";
        }
        else if (mode == Mode.Window)
        {
            (area, hwnd) = Capture.TargetWindow();
            img = Capture.Grab(area);
            modeName = "window";
        }
        else
        {
            area = Capture.FullRect(_s);
            hwnd = Native.sc_foreground();
            img = Capture.Grab(area);
            modeName = "full";
        }

        if (mode == Mode.Text)
        {
            using (img)
            {
                string text = await Ocr.ReadAsync(img);
                if (text.Length == 0) { Toast("No text found", "Try a larger selection."); return; }
                Clipboard.SetText(text);
                Toast("Text copied", text.Length > 120 ? text[..120] + "..." : text);
            }
            return;
        }

        IReadOnlyList<EditOp>? edits = null;
        if (_s.EditAfterCapture)
        {
            var edit = Editor.Run(img);
            if (edit.Outcome == EditOutcome.Discard) { img.Dispose(); return; }
            if (edit.Image is not null)
            {
                // The unedited capture is dropped here and never reaches disk.
                img.Dispose();
                img = edit.Image;
                edits = edit.Log;
            }
        }

        var src = hwnd != 0 ? Naming.Describe(hwnd) : CaptureSource.Desktop;
        VerifiedOptions? v = _s.Verified ? await VerifiedOptionsAsync(_s) : null;
        var result = await Capture.FinishAsync(img, modeName, area, src, Settings.ScreenshotDir, v, edits);
        using (result.Image)
        {
            _last = result.Path;
            bool copied = Capture.ToClipboard(result.Image, result.Path);
            if (!copied) result.Warnings.Add("Saved, but the clipboard was busy.");
            if (_s.ShowQuickAccess) QuickAccess.Show(result);
            if (result.Warnings.Count > 0) Toast("Saved with a note", string.Join("\n", result.Warnings), ToolTipIcon.Warning);
            else if (!_s.ShowQuickAccess) Toast("Saved", Path.GetFileName(result.Path));
        }
    }

    /// <summary>Name, place, location and timestamp server for a Verified capture.</summary>
    public static async Task<VerifiedOptions> VerifiedOptionsAsync(Settings s)
    {
        GeoFix? loc = s.UseLocation ? await Geo.GetAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(3)) : null;
        string place = s.Place.Trim();
        if (place.Length == 0 && loc is not null) place = Places.Nearest(loc.Lat, loc.Lon);
        return new VerifiedOptions(s.EffectiveName, place, loc, s.TrustedTimestamp ? s.TimestampUrl : null);
    }

    // ---------- verified, seal, settings ----------

    private void ToggleVerified()
    {
        if (!_s.Verified)
        {
            using var f = new SettingsForm(_s, verifiedSetup: true);
            if (f.ShowDialog() != DialogResult.OK) return;
            try { _ = Keys.EcPublicKey(); }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                _s.Verified = false;
                Toast("Could not create a signing key", e.Message, ToolTipIcon.Error);
                return;
            }
        }
        else _s.Verified = false;
        _s.Save();
        UpdateVerifiedItem();
        if (_s.Verified && _s.UseLocation) _ = Geo.GetAsync(TimeSpan.Zero, TimeSpan.FromSeconds(10));
        Toast(_s.Verified ? "Stamped screenshots on" : "Stamped screenshots off",
            _s.Verified ? "Every capture is watermarked and signed. Key: " + Keys.Describe() : "Captures are saved without a stamp.");
    }

    private void UpdateVerifiedItem() => _verifiedItem.Checked = _s.Verified;

    private void ShowSettings()
    {
        bool wasVerified = _s.Verified;
        using var f = new SettingsForm(_s, verifiedSetup: false);
        if (f.ShowDialog() != DialogResult.OK) return;
        _s.Save();
        RegisterKeys(firstRun: false);
        UpdateVerifiedItem();
        if (_s.Verified && !wasVerified) _ = Keys.EcPublicKey();
    }

    /// <summary>Asks for a password and seals the PNG. Returns the sealed path, or null.</summary>
    public static string? SealWithPrompt(string pngPath)
    {
        using var dlg = new PasswordDialog(pngPath, confirm: true);
        if (dlg.ShowDialog() != DialogResult.OK) return null;
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            string outPath = Seal.SealFile(pngPath, dlg.Password, dlg.DeleteOriginal);
            _instance?.Toast("Locked", Path.GetFileName(outPath) + (dlg.DeleteOriginal ? "\nThe unencrypted PNG was deleted." : ""));
            if (_instance is not null && dlg.DeleteOriginal && _instance._last == pngPath) _instance._last = null;
            return outPath;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Alert.Show(null, "Could not lock the file", e.Message, AlertKind.Error, "OK");
            return null;
        }
        finally { Cursor.Current = Cursors.Default; }
    }

    private static void OpenFile()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Screenshots (*.png;*.jpg;*.jpeg;*.snapseal)|*.png;*.jpg;*.jpeg;*.snapseal|All files|*.*",
            InitialDirectory = Settings.ScreenshotDir,
        };
        if (dlg.ShowDialog() == DialogResult.OK) Viewer.Open(dlg.FileName);
    }

    private void ShowMain()
    {
        _main ??= new MainWindow(new MainActions(
            Area: () => CaptureFromWindow(Mode.Area),
            Window: () => CaptureFromWindow(Mode.Window),
            Full: () => CaptureFromWindow(Mode.Full),
            Text: () => CaptureFromWindow(Mode.Text),
            Verified: ToggleVerified,
            IsVerified: () => _s.Verified,
            Settings: ShowSettings,
            Folder: OpenFolder,
            Open: Viewer.Open));
        _main.Reload();
        _main.Show();
        _main.Activate();
        Native.SetForegroundWindow(_main.Handle);
    }

    /// <summary>Hides the window first so it is not in the capture.</summary>
    private void CaptureFromWindow(Mode mode)
    {
        _main?.Hide();
        Fire(mode, fromMenu: true);
    }

    private static void OpenFolder()
    {
        Directory.CreateDirectory(Settings.ScreenshotDir);
        Start("explorer.exe", $"\"{Settings.ScreenshotDir}\"");
    }

    private static void PrintScreenHelp()
    {
        var r = Alert.Show(null, "Print Screen belongs to Windows",
            "Windows can use the Print Screen key to open its own Snipping Tool, which stops Imago from getting it.\n\n" +
            "To give Print Screen to Imago, turn off \"Use the Print screen key to open screen capture\" in Windows Settings, then restart Imago.",
            AlertKind.Info, "Open Settings", "Not now");
        if (r == DialogResult.OK) Start("ms-settings:easeofaccess-keyboard");
    }

    private void Toast(string title, string text, ToolTipIcon icon = ToolTipIcon.Info) =>
        _icon.ShowBalloonTip(4000, title, text.Length > 250 ? text[..250] : text, icon);

    private static void Start(string file, string? args = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = true };
            if (args is not null) psi.Arguments = args;
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>A message-only window that receives WM_HOTKEY.</summary>
    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private readonly Action<int> _onHotkey;
        private readonly HashSet<int> _ids = [];

        public HotkeyWindow(Action<int> onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
        }

        public bool Register(int id, uint mods, uint vk)
        {
            if (!Native.RegisterHotKey(Handle, id, mods, vk)) return false;
            _ids.Add(id);
            return true;
        }

        public void UnregisterAll()
        {
            foreach (int id in _ids) Native.UnregisterHotKey(Handle, id);
            _ids.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) _onHotkey((int)m.WParam);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterAll();
            DestroyHandle();
        }
    }
}
