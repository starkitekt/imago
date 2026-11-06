using System.Runtime.InteropServices;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using K = System.Windows.Forms.Keys;

namespace Imago;


/// <summary>
/// The leanest useful editor for a recording: scrub, mark start and end, save the trimmed copy. It is a normal window
/// (never a modal that can vanish) and works from the keyboard: Left/Right step, Shift = fine, I/O mark, Enter saves.
/// </summary>
internal sealed class VideoEditor : Form
{
    private readonly string _path;
    private readonly RecordingResult _rec;
    private readonly Func<string, TimeSpan, TimeSpan, string?, Task<string?>> _afterSave;
    private MediaComposition? _comp;
    private TimeSpan _dur, _in, _out, _pos;
    private readonly PictureBox _pic = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(32, 32, 32) };
    private readonly Timeline _bar = new();
    private readonly Label _keep = new();
    private readonly Label _info = new() { Font = Ui.Body, ForeColor = Ui.Muted, BackColor = Color.Transparent };
    private readonly ToolButton _save = new(Glyph.Check, "Save copy", "Save a trimmed copy next to the original (Enter)") { Primary = true };
    private readonly ToolButton _play = new("", "Play", "Play or pause (Space)");
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 33 };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private Windows.Media.Playback.MediaPlayer? _audio;
    private TimeSpan _playFrom;
    private bool _playing;
    private bool _loading, _saving;
    private int _seq;

    /// <param name="afterSave">Called with (newPath, start, end, parentSha); signs the copy. Returns a note or null.</param>
    public VideoEditor(string path, RecordingResult rec, Func<string, TimeSpan, TimeSpan, string?, Task<string?>> afterSave)
    {
        _path = path; _rec = rec; _afterSave = afterSave;
        Text = "Imago: " + Path.GetFileName(path);
        Icon = Ui.AppIcon;
        Font = Ui.Body;
        BackColor = Ui.Canvas;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(960, 600);
        MinimumSize = new Size(640, 420);
        KeyPreview = true;

        // One slim strip under the picture: time, the timeline with draggable trim handles, kept length, Save.
        var strip = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(48), ColumnCount = 6, RowCount = 1, BackColor = Ui.Surface, Padding = new Padding(12, 0, 12, 0) };
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var flow = new ToolButton("", "Flow", "Turn this recording into a walkthrough with captions and effects");
        flow.FitTo(this);
        flow.Margin = new Padding(8, 8, 0, 8);
        flow.Click += (_, _) => { Pause(); new FlowEditor(_path).Show(); Close(); };
        strip.Controls.Add(flow, 4, 0);
        _save.FitTo(this);
        _save.Margin = new Padding(8, 8, 0, 8);
        _save.Click += async (_, _) => await SaveAsync();
        _bar.Dock = DockStyle.Fill;
        _info.Dock = DockStyle.Fill;
        _info.AutoSize = true;
        _info.TextAlign = ContentAlignment.MiddleLeft;
        _info.Margin = new Padding(0, 0, 12, 0);
        _keep.AutoSize = true;
        _keep.Font = Ui.Body; _keep.ForeColor = Ui.Muted; _keep.BackColor = Color.Transparent;
        _keep.Margin = new Padding(12, 14, 0, 0);
        _play.FitTo(this);
        _play.Margin = new Padding(0, 8, 8, 8);
        _play.Click += (_, _) => TogglePlay();
        _tick.Tick += (_, _) => OnTick();
        strip.Controls.Add(_play, 0, 0);
        strip.Controls.Add(_info, 1, 0);
        strip.Controls.Add(_bar, 2, 0);
        strip.Controls.Add(_keep, 3, 0);
        strip.Controls.Add(_save, 5, 0);
        strip.Paint += (_, e) => { using var pen = new Pen(Ui.Hairline); e.Graphics.DrawLine(pen, 0, 0, strip.Width, 0); };

        Controls.Add(_pic);
        Controls.Add(strip);
        _bar.Seek += async f => await SeekAsync(TimeSpan.FromTicks((long)(_dur.Ticks * f)));
        _bar.Trim += (from, to) => { _in = TimeSpan.FromTicks((long)(_dur.Ticks * from)); _out = TimeSpan.FromTicks((long)(_dur.Ticks * to)); UpdateInfo(); };
        _bar.ResetTrim += () => { _in = TimeSpan.Zero; _out = _dur; UpdateInfo(); };
        Load += async (_, _) => await OpenAsync();
        FormClosed += (_, _) => { Pause(); _audio?.Dispose(); _tick.Dispose(); _pic.Image?.Dispose(); };
    }

    private void TogglePlay() { if (_playing) Pause(); else Play(); }

    /// <summary>Plays the kept range in real time: frames follow a stopwatch (skipping when busy), sound comes from a player on the same source.</summary>
    private void Play()
    {
        if (_comp is null || _playing) return;
        if (_pos >= _out - TimeSpan.FromMilliseconds(50) || _pos < _in) _pos = _in;
        _playing = true;
        _playFrom = _pos;
        _clock.Restart();
        try
        {
            _audio ??= new Windows.Media.Playback.MediaPlayer
            {
                Source = Windows.Media.Core.MediaSource.CreateFromMediaStreamSource(_comp.GeneratePreviewMediaStreamSource(Math.Max(640, _pic.Width), Math.Max(360, _pic.Height))),
            };
            _audio.PlaybackSession.Position = _pos;
            _audio.Play();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException) { _audio = null; }
        _play.Text = "Pause";
        _play.Icon = "";
        _tick.Start();
    }

    private void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _tick.Stop();
        try { _audio?.Pause(); } catch (Exception e) when (e is COMException or InvalidOperationException) { }
        _play.Text = "Play";
        _play.Icon = "";
    }

    private void OnTick()
    {
        var t = _playFrom + _clock.Elapsed;
        if (t >= _out) { Pause(); _ = SeekAsync(_out); return; }
        _ = SeekAsync(t);
    }

    private async Task OpenAsync()
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_path);
            var clip = await MediaClip.CreateFromFileAsync(file);
            _comp = new MediaComposition();
            _comp.Clips.Add(clip);
            _dur = _comp.Duration;
            _in = TimeSpan.Zero; _out = _dur;
            await SeekAsync(TimeSpan.Zero);
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _info.Text = "Can't open this recording for editing: " + e.Message;
            _save.Enabled = false;
        }
    }

    private async Task SeekAsync(TimeSpan t)
    {
        if (_comp is null) return;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t > _dur) t = _dur;
        _pos = t;
        UpdateInfo();
        int mine = ++_seq;
        if (_loading) return; // the in-flight request will be followed by one for the latest position
        _loading = true;
        try
        {
            while (true)
            {
                int want = _seq;
                var at = _pos;
                int w = Math.Max(160, _pic.Width), h = Math.Max(90, _pic.Height);
                using IRandomAccessStream s = await _comp.GetThumbnailAsync(at, w, h, VideoFramePrecision.NearestFrame);
                using var ms = new MemoryStream();
                await s.AsStreamForRead().CopyToAsync(ms);
                ms.Position = 0;
                var old = _pic.Image;
                using (var tmp = new Bitmap(ms)) _pic.Image = new Bitmap(tmp);
                old?.Dispose();
                if (want == _seq) break;
            }
        }
        catch (Exception e) when (e is COMException or IOException or ArgumentException or InvalidOperationException) { }
        finally { _loading = false; }
        _ = mine;
    }

    private void UpdateInfo()
    {
        _bar.Set(_dur, _in, _out, _pos);
        _info.Text = $"{Fmt(_pos)} / {Fmt(_dur)}";
        _keep.Text = $"keep {(_out - _in).TotalSeconds:0.0} s";
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";

    protected override bool ProcessCmdKey(ref Message msg, System.Windows.Forms.Keys keyData)
    {
        if (_playing && keyData is not (System.Windows.Forms.Keys.Space or System.Windows.Forms.Keys.Escape)) Pause();
        bool shift = (keyData & System.Windows.Forms.Keys.Shift) != 0;
        var k = keyData & System.Windows.Forms.Keys.KeyCode;
        var step = TimeSpan.FromSeconds(shift ? 1.0 / Math.Max(1, _rec.Fps) : 1);
        switch (k)
        {
            case K.Space: TogglePlay(); return true;
            case K.Left: _ = SeekAsync(_pos - step); return true;
            case K.Right: _ = SeekAsync(_pos + step); return true;
            case K.Home: _ = SeekAsync(TimeSpan.Zero); return true;
            case K.End: _ = SeekAsync(_dur); return true;
            case K.I: _in = _pos; if (_out <= _in) _out = _dur; UpdateInfo(); return true;
            case K.O: _out = _pos; if (_in >= _out) _in = TimeSpan.Zero; UpdateInfo(); return true;
            case K.Enter: _ = SaveAsync(); return true;
            case K.Escape: Close(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async Task SaveAsync()
    {
        if (_comp is null || _saving) return;
        if (_out - _in < TimeSpan.FromMilliseconds(200)) { _info.Text = "Pick a start and end at least a moment apart."; return; }
        _saving = true;
        _save.Enabled = false;
        string dir = Settings.ScreenshotDir;
        Directory.CreateDirectory(dir);
        string outPath = Naming.Unique(dir, Path.GetFileNameWithoutExtension(_path) + "-trim", ".mp4");
        try
        {
            var comp = _comp.Clone();
            var clip = comp.Clips[0];
            clip.TrimTimeFromStart = _in;
            clip.TrimTimeFromEnd = _dur - _out;
            var profile = await MediaEncodingProfile.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(_path));
            _info.Text = "Saving...";
            File.WriteAllBytes(outPath, []);
            var target = await StorageFile.GetFileFromPathAsync(outPath);
            var res = await comp.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile).AsTask();
            if (res != Windows.Media.Transcoding.TranscodeFailureReason.None) throw new InvalidOperationException("Windows could not write the trimmed file: " + res);
            string? parent = VideoProof.HashFile(_path).FileSha;
            string? note = await _afterSave(outPath, _in, _out, parent);
            _info.Text = "Saved " + Path.GetFileName(outPath) + (note is null ? "" : ". " + note);
            Start(outPath);
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch (IOException) { }
            _info.Text = "Not saved: " + e.Message;
        }
        finally { _saving = false; _save.Enabled = true; }
    }

    private static void Start(string file)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\""); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

/// <summary>A flat Fluent timeline: thin track, kept range in brand blue with draggable end handles, round playhead.</summary>
internal sealed class Timeline : Control
{
    private TimeSpan _dur, _in, _out, _pos;
    private int _grab; // 0 none, 1 playhead, 2 start handle, 3 end handle

    public event Action<double>? Seek;
    public event Action<double, double>? Trim;
    public event Action? ResetTrim;

    public Timeline()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        TabStop = false;
        AccessibleName = "Timeline";
    }

    public void Set(TimeSpan dur, TimeSpan from, TimeSpan to, TimeSpan pos) { _dur = dur; _in = from; _out = to; _pos = pos; Invalidate(); }

    private int Pad => LogicalToDeviceUnits(8);
    private int X(TimeSpan t) => Pad + (int)((Width - Pad * 2) * (_dur.Ticks == 0 ? 0 : (double)t.Ticks / _dur.Ticks));
    private double Frac(int x) => Math.Clamp((x - Pad) / (double)Math.Max(1, Width - Pad * 2), 0, 1);
    private double F(TimeSpan t) => _dur.Ticks == 0 ? 0 : (double)t.Ticks / _dur.Ticks;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int tol = LogicalToDeviceUnits(8);
        _grab = Math.Abs(e.X - X(_in)) <= tol && Math.Abs(e.X - X(_in)) <= Math.Abs(e.X - X(_out)) ? 2
              : Math.Abs(e.X - X(_out)) <= tol ? 3 : 1;
        if (e.Clicks == 2 && _grab != 1) { ResetTrim?.Invoke(); _grab = 0; return; }
        Drag(e.X);
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_grab != 0) Drag(e.X);
        else
        {
            int tol = LogicalToDeviceUnits(8);
            Cursor = Math.Abs(e.X - X(_in)) <= tol || Math.Abs(e.X - X(_out)) <= tol ? Cursors.SizeWE : Cursors.Hand;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _grab = 0; base.OnMouseUp(e); }

    private void Drag(int x)
    {
        double f = Frac(x), gap = 0.01;
        if (_grab == 1) Seek?.Invoke(f);
        else if (_grab == 2) Trim?.Invoke(Math.Min(f, F(_out) - gap), F(_out));
        else if (_grab == 3) Trim?.Invoke(F(_in), Math.Max(f, F(_in) + gap));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int cy = Height / 2, th = Math.Max(4, LogicalToDeviceUnits(4));
        using (var track = new SolidBrush(Ui.Hairline)) g.FillRectangle(track, Pad, cy - th / 2, Width - Pad * 2, th);
        using (var kept = new SolidBrush(Ui.Brand)) g.FillRectangle(kept, X(_in), cy - th / 2, Math.Max(1, X(_out) - X(_in)), th);
        int hw = LogicalToDeviceUnits(6), hh = LogicalToDeviceUnits(20);
        using (var hb = new SolidBrush(Ui.Brand))
            foreach (int hx in new[] { X(_in) - hw / 2, X(_out) - hw / 2 })
                using (var path = Shape.Round(new Rectangle(hx, cy - hh / 2, hw, hh), 2)) g.FillPath(hb, path);
        int r = LogicalToDeviceUnits(8), px = X(_pos);
        using (var ring = new SolidBrush(Ui.Surface)) g.FillEllipse(ring, px - r, cy - r, r * 2, r * 2);
        using (var pen = new Pen(Ui.Brand, 2)) g.DrawEllipse(pen, px - r + 1, cy - r + 1, r * 2 - 2, r * 2 - 2);
    }
}
