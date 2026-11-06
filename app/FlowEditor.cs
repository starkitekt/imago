using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using K = System.Windows.Forms.Keys;

namespace Imago;

/// <summary>Draws one frame with the chosen effects. The preview and the MP4 export both call this, so they always match.</summary>
internal static class FlowRender
{
    public static void Draw(Graphics g, Image frame, FlowProject p, double t, Rectangle dest)
    {
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var (z, cx, cy) = p.ZoomAt(t);
        float vw = (float)(p.W / z), vh = (float)(p.H / z);
        float vx = (float)cx - vw / 2, vy = (float)cy - vh / 2;
        float m = dest.Width / vw; // video pixels to output pixels
        var clip = g.Clip;
        g.SetClip(dest);
        g.DrawImage(frame, dest, vx, vy, vw, vh, GraphicsUnit.Pixel);
        float X(double a) => dest.X + ((float)a - vx) * m;
        float Y(double a) => dest.Y + ((float)a - vy) * m;

        foreach (var (rx, ry, age) in p.RippleAt(t))
        {
            double e = 1 - (1 - age) * (1 - age);
            float r = (float)(10 + 60 * e) * m;
            float fade = (float)Math.Pow(1 - age, 1.4);
            using var gp = new System.Drawing.Drawing2D.GraphicsPath();
            gp.AddEllipse(X(rx) - r, Y(ry) - r, r * 2, r * 2);
            using var pb = new System.Drawing.Drawing2D.PathGradientBrush(gp) { CenterPoint = new PointF(X(rx), Y(ry)) };
            Color Ca(float a) => Color.FromArgb((int)(255 * Math.Clamp(a * fade, 0, 1)), 15, 108, 189);
            pb.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend
            {
                Positions = [0f, 0.45f, 0.8f, 1f],
                Colors = [Ca(0.02f), Ca(0.18f), Ca(0.55f), Ca(0)],
            };
            g.FillEllipse(pb, X(rx) - r, Y(ry) - r, r * 2, r * 2);
        }

        var (marks, ma) = p.MarksAt(t);
        if (ma > 0)
            foreach (var k in marks)
            {
                var r = new RectangleF(X(k.X), Y(k.Y), k.W * m, k.H * m);
                using var path = Shape.Round(Rectangle.Round(r), (int)Math.Max(2, 6 * m));
                using var back = new SolidBrush(Color.FromArgb((int)(36 * ma), 15, 108, 189));
                using var edge = new Pen(Color.FromArgb((int)(255 * ma), 15, 108, 189), Math.Max(2f, 3f * m));
                g.FillPath(back, path);
                g.DrawPath(edge, path);
            }

        if (p.CursorSeparate && p.Moves.Count > 0)
        {
            var (px, py) = p.Cursor(t);
            float u = m * 1.1f, ox = X(px), oy = Y(py);
            PointF[] arrow = [new(0, 0), new(0, 13), new(9, 9)];
            for (int i = 0; i < arrow.Length; i++) arrow[i] = new PointF(ox + arrow[i].X * u, oy + arrow[i].Y * u);
            using var fill = new SolidBrush(Color.FromArgb(15, 108, 189));
            g.FillPolygon(fill, arrow);
            using var outline = new Pen(Color.White, Math.Max(1f, 1.2f * u)) { LineJoin = LineJoin.Round };
            g.DrawPolygon(outline, arrow);
        }

        if (p.CaptionAt(t) is { } cap)
        {
            float fs = Math.Max(10f, dest.Height / 30f);
            using var font = new Font(Ui.Family, fs, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString(cap, font);
            float w = size.Width + fs * 1.6f, h = fs * 2.2f;
            var box = new RectangleF(dest.X + (dest.Width - w) / 2, dest.Bottom - h * 1.5f, w, h);
            using var path = Shape.Round(Rectangle.Round(box), (int)(h / 4));
            using var back = new SolidBrush(Color.FromArgb(184, 0, 0, 0));
            g.FillPath(back, path);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(cap, font, Brushes.White, box, fmt);
        }
        g.Clip = clip;
    }
}

/// <summary>Renders a flow to a new MP4: each frame is drawn with the effects, then the original audio is laid back under it.</summary>
internal static class FlowVideo
{
    public static async Task RenderAsync(FlowProject p, string src, string dst, IProgress<double> progress, CancellationToken ct)
    {
        const int fps = 30;
        int w = p.W & ~1, h = p.H & ~1;
        var file = await StorageFile.GetFileFromPathAsync(src);
        var clip = await MediaClip.CreateFromFileAsync(file);
        bool audio = clip.EmbeddedAudioTracks.Count > 0;
        // The original sound is decoded to PCM and fed to the same encoder, so there is one pass and no second render.
        NAudio.Wave.IWaveProvider? pcm = null;
        NAudio.Wave.MediaFoundationReader? reader = null;
        if (audio)
        {
            try
            {
                reader = new NAudio.Wave.MediaFoundationReader(src);
                pcm = new NAudio.Wave.SampleProviders.SampleToWaveProvider16(NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader));
            }
            catch (Exception e) when (e is COMException or IOException or ArgumentException or InvalidOperationException) { audio = false; pcm = null; }
        }

        // Seeking is the slow part, so several decoders each fetch their own frames ahead of the encoder.
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        var comps = new MediaComposition[workers];
        for (int i = 0; i < workers; i++)
        {
            comps[i] = new MediaComposition();
            comps[i].Clips.Add(i == 0 ? clip : await MediaClip.CreateFromFileAsync(file));
        }
        double dur = comps[0].Duration.TotalSeconds;
        int total = (int)Math.Floor(dur * fps);

        var vp = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)w, (uint)h);
        vp.FrameRate.Numerator = fps; vp.FrameRate.Denominator = 1;
        var mss = pcm is null
            ? new MediaStreamSource(new VideoStreamDescriptor(vp))
            : new MediaStreamSource(new VideoStreamDescriptor(vp), new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm((uint)pcm.WaveFormat.SampleRate, (uint)pcm.WaveFormat.Channels, 16)));
        mss.CanSeek = false; mss.BufferTime = TimeSpan.Zero;
        long audioBytes = 0;
        static void Log(string m) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "imago-flow-error.txt"), DateTime.Now.ToString("HH:mm:ss.fff ") + m + Environment.NewLine); } catch (IOException) { } }
        mss.Closed += (_, a) => Log("closed: " + a.Request.Reason);
        var audioLock = new object();

        async Task<byte[]> MakeFrame(int i)
        {
            double t = i / (double)fps;
            var comp = comps[i % workers];
            MediaComposition c = comp;
            using var s = await c.GetThumbnailAsync(TimeSpan.FromSeconds(Math.Min(t, Math.Max(0, dur - 0.001))), w, h, VideoFramePrecision.NearestFrame);
            using var ms = new MemoryStream();
            await s.AsStreamForRead().CopyToAsync(ms, ct);
            ms.Position = 0;
            using var raw = new Bitmap(ms);
            using var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(outBmp)) FlowRender.Draw(g, raw, p, t, new Rectangle(0, 0, w, h));
            var data = outBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var bytes = new byte[data.Stride * h];
            for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + (h - 1 - y) * data.Stride, bytes, y * data.Stride, data.Stride); // Windows reads uncompressed BGRA bottom-up
            outBmp.UnlockBits(data);
            return bytes;
        }

        // A decoder handles one frame at a time (frame i goes to decoder i mod N), so the window never exceeds N tasks.
        var window = new Dictionary<int, Task<byte[]>>();
        int idx = 0;
        mss.SampleRequested += (sender, a) =>
        {
            var req = a.Request;
            var def = req.GetDeferral();
            if (req.StreamDescriptor is AudioStreamDescriptor && pcm is not null)
            {
                try
                {
                    lock (audioLock)
                    {
                        int rate = pcm.WaveFormat.SampleRate, ch = pcm.WaveFormat.Channels;
                        var buf = new byte[rate * ch * 2 / 10];
                        int n = pcm.Read(buf, 0, buf.Length);
                        if (n <= 0) req.Sample = null;
                        else
                        {
                            var smp = MediaStreamSample.CreateFromBuffer(buf.AsBuffer(0, n), TimeSpan.FromSeconds(audioBytes / (double)(rate * ch * 2)));
                            smp.Duration = TimeSpan.FromSeconds(n / (double)(rate * ch * 2));
                            audioBytes += n;
                            req.Sample = smp;
                        }
                    }
                }
                catch (Exception e) { req.Sample = null; try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "imago-flow-error.txt"), "audio: " + e + Environment.NewLine); } catch (IOException) { } }
                finally { def.Complete(); }
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    if (idx >= total || ct.IsCancellationRequested) { req.Sample = null; return; }
                    for (int k = idx; k < Math.Min(total, idx + workers); k++)
                        if (!window.ContainsKey(k)) { int kk = k; window[kk] = Task.Run(() => MakeFrame(kk), ct); }
                    var bytes = await window[idx];
                    window.Remove(idx);
                    var sample = MediaStreamSample.CreateFromBuffer(bytes.AsBuffer(), TimeSpan.FromSeconds(idx / (double)fps));
                    sample.Duration = TimeSpan.FromSeconds(1.0 / fps);
                    req.Sample = sample;
                    progress.Report(Math.Min(1, idx / (double)total) * (audio ? 0.9 : 1));
                    idx++;
                }
                catch (Exception e)
                {
                    Log("video frame " + idx + ": " + e);
                    req.Sample = null;
                }
                finally { def.Complete(); }
            });
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        SetVideo(profile, w, h, fps);
        profile.Audio = pcm is null ? null : AudioEncodingProperties.CreateAac((uint)pcm.WaveFormat.SampleRate, (uint)pcm.WaveFormat.Channels, 192_000);
        File.WriteAllBytes(dst, []);
        try
        {
            using var outStream = await FileRandomAccessStream.OpenAsync(dst, FileAccessMode.ReadWrite).AsTask(ct);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(mss, outStream, profile).AsTask(ct);
            if (!prep.CanTranscode) throw new InvalidOperationException("Windows can't render this size: " + prep.FailureReason);
            await prep.TranscodeAsync().AsTask(ct);
        }
        finally { reader?.Dispose(); }
        progress.Report(1);
    }

    private static void SetVideo(MediaEncodingProfile pr, int w, int h, int fps)
    {
        var v = pr.Video!;
        v.Width = (uint)w; v.Height = (uint)h;
        v.FrameRate.Numerator = (uint)fps; v.FrameRate.Denominator = 1;
        v.Bitrate = (uint)Math.Clamp((double)w * h * fps * 0.25, 8_000_000, 120_000_000);
    }
}

/// <summary>
/// The flow editor: a recording turned into a walkthrough. Clicks become steps; each step takes a caption; the
/// person picks the effects (cursor smoothing, click ripples, auto-zoom) and exports an MP4 or a self-playing HTML page.
/// </summary>
internal sealed class FlowEditor : Form
{
    private readonly string _path;
    private readonly FlowProject _p;
    private MediaComposition? _comp;
    private TimeSpan _dur, _pos;
    private Bitmap? _frame;
    private readonly Canvas _view = new();
    private readonly Timeline _bar = new();
    private readonly Label _info = new() { Font = Ui.Body, ForeColor = Ui.Muted, BackColor = Color.Transparent, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolButton _play = new("", "Play", "Play or pause (Space)");
    private readonly ToolButton _html = new("", "HTML", "Export a page that replays the walkthrough in any browser");
    private readonly ToolButton _mp4 = new("", "Export MP4", "Render a video with the effects burned in") { Primary = true };
    private readonly ToolButton _svg = new("", "SVG", "Export a looping animated SVG, one picture per step");
    private readonly ListBox _steps = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = Ui.Body, IntegralHeight = false, BackColor = Ui.Surface };
    private readonly TextBox _caption = new() { Dock = DockStyle.Fill, Multiline = true, Font = Ui.Body, BorderStyle = BorderStyle.FixedSingle, AcceptsReturn = false };
    private readonly CheckBox _smooth = new() { Text = "Smooth cursor", AutoSize = true, Font = Ui.Body };
    private readonly CheckBox _ripples = new() { Text = "Click ripples", AutoSize = true, Font = Ui.Body };
    private readonly CheckBox _zoom = new() { Text = "Auto-zoom on clicks", AutoSize = true, Font = Ui.Body };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 33 };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private TimeSpan _playFrom;
    private bool _playing, _loading, _busy, _updating;
    private int _seq;
    private CancellationTokenSource? _cts;

    /// <summary>Render the MP4 as soon as the editor opens, then close (for scripted checks).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AutoExport { get; init; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AutoHtml { get; init; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AutoSvg { get; init; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AutoReel { get; init; }

    private static (int W, int H, double Sec) Probe(string path) => Task.Run(async () =>
    {
        var f = await StorageFile.GetFileFromPathAsync(path);
        var vp = await f.Properties.GetVideoPropertiesAsync();
        return ((int)vp.Width, (int)vp.Height, vp.Duration.TotalSeconds);
    }).GetAwaiter().GetResult();

    public FlowEditor(string path)
    {
        _path = path;
        var (pw, ph, ps) = Probe(path);
        _p = FlowProject.Load(path, pw, ph, ps);
        Text = "Imago Flow: " + Path.GetFileName(path);
        Icon = Ui.AppIcon;
        Font = Ui.Body;
        BackColor = Ui.Canvas;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1120, 640);
        MinimumSize = new Size(760, 460);
        KeyPreview = true;

        // Bottom strip, same shape as the trim editor: play, time, timeline, exports.
        var strip = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(48), ColumnCount = 6, RowCount = 1, BackColor = Ui.Surface, Padding = new Padding(12, 0, 12, 0) };
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var b in new[] { _play, _html, _svg, _mp4 }) b.FitTo(this);
        _play.Margin = new Padding(0, 8, 8, 8);
        _html.Margin = new Padding(8, 8, 0, 8);
        _svg.Margin = new Padding(8, 8, 0, 8);
        _mp4.Margin = new Padding(8, 8, 0, 8);
        _info.Margin = new Padding(0, 14, 12, 0);
        _bar.Dock = DockStyle.Fill;
        strip.Controls.Add(_play, 0, 0);
        strip.Controls.Add(_info, 1, 0);
        strip.Controls.Add(_bar, 2, 0);
        strip.Controls.Add(_html, 3, 0);
        strip.Controls.Add(_svg, 4, 0);
        strip.Controls.Add(_mp4, 5, 0);
        strip.Paint += (_, e) => { using var pen = new Pen(Ui.Hairline); e.Graphics.DrawLine(pen, 0, 0, strip.Width, 0); };

        // Right panel: steps, caption, effects.
        var side = new TableLayoutPanel { Dock = DockStyle.Right, Width = LogicalToDeviceUnits(300), ColumnCount = 1, BackColor = Ui.Surface, Padding = new Padding(12) };
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(64)));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var head = new Label { Text = "Steps", Font = Ui.Strong, AutoSize = true, ForeColor = Ui.Ink, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) };
        var addRm = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 6), BackColor = Color.Transparent };
        var add = new ToolButton("", "Add step", "Add a step at the playhead (N)");
        var rm = new ToolButton("", "Remove", "Remove the selected step (Delete)");
        add.FitTo(this); rm.FitTo(this);
        add.Click += (_, _) => AddStep();
        rm.Click += (_, _) => RemoveStep();
        var clr = new ToolButton("", "Clear boxes", "Drag on the preview to box something for this step; clear them here");
        clr.FitTo(this);
        clr.Click += (_, _) =>
        {
            if (_steps.SelectedIndex < 0) return;
            _p.Steps[_steps.SelectedIndex].Marks.Clear();
            _view.Invalidate();
        };
        var tag = new ToolButton("", "Tag", "Cycle the research tag on this step: Issue, Insight, Quote, Question (T)");
        var notes = new ToolButton("", "Notes", "Export a tagged transcript of the steps as Markdown");
        var reel = new ToolButton("", "Reel", "Cut the tagged moments (or every step) into one highlight reel");
        tag.FitTo(this); notes.FitTo(this); reel.FitTo(this);
        tag.Click += (_, _) => CycleTag();
        notes.Click += (_, _) => ExportNotes();
        reel.Click += async (_, _) => await ExportReelAsync();
        addRm.Controls.Add(add); addRm.Controls.Add(rm); addRm.Controls.Add(clr);
        addRm.Controls.Add(tag); addRm.Controls.Add(notes); addRm.Controls.Add(reel);
        _view.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && _steps.SelectedIndex >= 0) { _dragA = e.Location; _dragB = e.Location; } };
        _view.MouseMove += (_, e) => { if (_dragA is not null) { _dragB = e.Location; _view.Invalidate(); } };
        _view.MouseUp += (_, _) => CommitBox();
        var fx = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0), BackColor = Color.Transparent };
        fx.Controls.Add(_smooth); fx.Controls.Add(_ripples); fx.Controls.Add(_zoom);
        side.Controls.Add(head, 0, 0);
        side.Controls.Add(_steps, 0, 1);
        side.Controls.Add(addRm, 0, 2);
        side.Controls.Add(_caption, 0, 3);
        side.Controls.Add(fx, 0, 4);

        _view.Dock = DockStyle.Fill;
        Controls.Add(_view);
        Controls.Add(side);
        Controls.Add(strip);
        _view.BringToFront();

        _smooth.Checked = _p.Smooth && _p.CursorSeparate;
        _smooth.Enabled = _p.CursorSeparate;
        _ripples.Checked = _p.Ripples;
        _zoom.Checked = _p.Zoom;
        _smooth.CheckedChanged += (_, _) => { _p.Smooth = _smooth.Checked; _view.Invalidate(); };
        _ripples.CheckedChanged += (_, _) => { _p.Ripples = _ripples.Checked; _view.Invalidate(); };
        _zoom.CheckedChanged += (_, _) => { _p.Zoom = _zoom.Checked; _view.Invalidate(); };
        _view.Paint += OnPaintView;
        _steps.SelectedIndexChanged += async (_, _) => await OnStepSelected();
        _caption.TextChanged += (_, _) =>
        {
            if (_updating || _steps.SelectedIndex < 0) return;
            _p.Steps[_steps.SelectedIndex].Caption = _caption.Text;
            RefreshList(_steps.SelectedIndex);
            _view.Invalidate();
        };
        _play.Click += (_, _) => TogglePlay();
        _tick.Tick += (_, _) => OnTick();
        _bar.Seek += async f => { Pause(); await SeekAsync(TimeSpan.FromTicks((long)(_dur.Ticks * f))); };
        _bar.Trim += (_, _) => _bar.Set(_dur, TimeSpan.Zero, _dur, _pos);
        _bar.ResetTrim += () => { };
        _html.Click += (_, _) => ExportHtml();
        _mp4.Click += async (_, _) => await ExportMp4Async();
        _svg.Click += async (_, _) => await ExportSvgAsync();
        Load += async (_, _) =>
        {
            await OpenAsync();
            if (AutoReel) { await ExportReelAsync(); Close(); }
            else if (AutoSvg) { await ExportSvgAsync(); Close(); }
            else if (AutoHtml) { ExportHtml(); Close(); }
            else if (AutoExport) { await ExportMp4Async(); Close(); }
        };
        FormClosing += (_, _) => { _cts?.Cancel(); try { _p.SaveEdits(); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
        FormClosed += (_, _) => { Pause(); _tick.Dispose(); _frame?.Dispose(); };
        RefreshList(-1);
        UpdateHint();
    }

    private void UpdateHint()
    {
        if (_busy) return;
        _info.Text = _p.Steps.Count == 0
            ? "No clicks were tracked. Press N to add steps."
            : $"{_p.Steps.Count} steps  {Fmt(_pos)} / {Fmt(_dur)}";
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    private static string Fmt(double s) => Fmt(TimeSpan.FromSeconds(s));

    private void RefreshList(int select)
    {
        _updating = true;
        _steps.BeginUpdate();
        _steps.Items.Clear();
        for (int i = 0; i < _p.Steps.Count; i++)
        {
            var s = _p.Steps[i];
            _steps.Items.Add($"{i + 1}   {Fmt(s.T)}   {(s.Tag.Length > 0 ? "[" + s.Tag + "] " : "")}{(s.Caption.Length > 0 ? s.Caption : (s.Manual ? "Step" : "Click"))}");
        }
        if (select >= 0 && select < _steps.Items.Count) _steps.SelectedIndex = select;
        _steps.EndUpdate();
        _updating = false;
    }

    private async Task OnStepSelected()
    {
        if (_updating || _steps.SelectedIndex < 0) return;
        var s = _p.Steps[_steps.SelectedIndex];
        _updating = true; _caption.Text = s.Caption; _updating = false;
        Pause();
        await SeekAsync(TimeSpan.FromSeconds(Math.Max(0, s.T - 0.2)));
    }

    private void AddStep()
    {
        var s = _p.AddStep(_pos.TotalSeconds);
        RefreshList(_p.Steps.IndexOf(s));
        _caption.Focus();
        UpdateHint();
    }

    private void RemoveStep()
    {
        int i = _steps.SelectedIndex;
        if (i < 0) return;
        _p.Steps.RemoveAt(i);
        RefreshList(Math.Min(i, _p.Steps.Count - 1));
        if (_p.Steps.Count == 0) { _updating = true; _caption.Text = ""; _updating = false; }
        _view.Invalidate();
        UpdateHint();
    }

    // ---------- preview ----------

    private Point? _dragA;
    private Point _dragB;

    private Rectangle Dest()
    {
        double sc = Math.Min(_view.Width / (double)_p.W, _view.Height / (double)_p.H);
        int dw = Math.Max(1, (int)(_p.W * sc)), dh = Math.Max(1, (int)(_p.H * sc));
        return new Rectangle((_view.Width - dw) / 2, (_view.Height - dh) / 2, dw, dh);
    }

    /// <summary>A drag on the preview becomes a highlight box on the selected step, converted back to video pixels through the current zoom.</summary>
    private void CommitBox()
    {
        if (_dragA is not { } a) return;
        _dragA = null;
        int i = _steps.SelectedIndex;
        var d = Dest();
        var (z, cx, cy) = _p.ZoomAt(_pos.TotalSeconds);
        double vw = _p.W / z, vh = _p.H / z, vx = cx - vw / 2, vy = cy - vh / 2, m = d.Width / vw;
        int Vx(int px) => (int)Math.Clamp(vx + (px - d.X) / m, 0, _p.W);
        int Vy(int py) => (int)Math.Clamp(vy + (py - d.Y) / m, 0, _p.H);
        int x0 = Vx(Math.Min(a.X, _dragB.X)), x1 = Vx(Math.Max(a.X, _dragB.X)), y0 = Vy(Math.Min(a.Y, _dragB.Y)), y1 = Vy(Math.Max(a.Y, _dragB.Y));
        if (i >= 0 && x1 - x0 > 12 && y1 - y0 > 12) _p.Steps[i].Marks.Add(new FlowMark(x0, y0, x1 - x0, y1 - y0));
        _view.Invalidate();
    }

    private void OnPaintView(object? sender, PaintEventArgs e)
    {
        e.Graphics.Clear(Color.FromArgb(32, 32, 32));
        if (_frame is null) return;
        FlowRender.Draw(e.Graphics, _frame, _p, _pos.TotalSeconds, Dest());
        if (_dragA is { } a)
        {
            using var pen = new Pen(Color.FromArgb(15, 108, 189), 2) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawRectangle(pen, Math.Min(a.X, _dragB.X), Math.Min(a.Y, _dragB.Y), Math.Abs(a.X - _dragB.X), Math.Abs(a.Y - _dragB.Y));
        }
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
            await SeekAsync(TimeSpan.Zero);
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _info.Text = "Can't open this recording: " + e.Message;
            _mp4.Enabled = _html.Enabled = _play.Enabled = false;
        }
    }

    private async Task SeekAsync(TimeSpan t)
    {
        if (_comp is null) return;
        _pos = t < TimeSpan.Zero ? TimeSpan.Zero : t > _dur ? _dur : t;
        _bar.Set(_dur, TimeSpan.Zero, _dur, _pos);
        UpdateHint();
        _view.Invalidate();
        _seq++;
        if (_loading) return;
        _loading = true;
        try
        {
            while (true)
            {
                int want = _seq;
                var at = _pos;
                using IRandomAccessStream s = await _comp.GetThumbnailAsync(at, _p.W, _p.H, VideoFramePrecision.NearestFrame);
                using var ms = new MemoryStream();
                await s.AsStreamForRead().CopyToAsync(ms);
                ms.Position = 0;
                var old = _frame;
                using (var tmp = new Bitmap(ms)) _frame = new Bitmap(tmp);
                old?.Dispose();
                _view.Invalidate();
                if (want == _seq) break;
            }
        }
        catch (Exception e) when (e is COMException or IOException or ArgumentException or InvalidOperationException) { }
        finally { _loading = false; }
    }

    private void TogglePlay() { if (_playing) Pause(); else Play(); }

    private void Play()
    {
        if (_comp is null || _playing || _busy) return;
        if (_pos >= _dur - TimeSpan.FromMilliseconds(50)) _pos = TimeSpan.Zero;
        _playing = true;
        _playFrom = _pos;
        _clock.Restart();
        _play.Text = "Pause"; _play.Icon = ""; _play.FitTo(this);
        _tick.Start();
    }

    private void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _tick.Stop();
        _play.Text = "Play"; _play.Icon = ""; _play.FitTo(this);
    }

    private async void OnTick()
    {
        var t = _playFrom + _clock.Elapsed;
        if (t >= _dur) { Pause(); await SeekAsync(_dur); return; }
        await SeekAsync(t);
    }

    protected override bool ProcessCmdKey(ref Message msg, K keyData)
    {
        if (_caption.Focused || _busy) return base.ProcessCmdKey(ref msg, keyData);
        if (keyData == K.Space) { TogglePlay(); return true; }
        if (keyData == K.N && !_caption.Focused) { AddStep(); return true; }
        if (keyData == K.T && !_caption.Focused) { CycleTag(); return true; }
        if (keyData == K.Delete && _steps.Focused) { RemoveStep(); return true; }
        if (keyData == K.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------- export ----------

    private string OutPath(string suffix, string ext)
    {
        string dir = Settings.ScreenshotDir;
        Directory.CreateDirectory(dir);
        return Path.GetFullPath(Naming.Unique(dir, Path.GetFileNameWithoutExtension(_path) + suffix, ext));
    }

    private void ExportHtml()
    {
        try
        {
            Pause();
            _p.SaveEdits();
            string outPath = OutPath("-flow", ".html");
            File.WriteAllText(outPath, _p.ToHtml(string.Equals(Path.GetDirectoryName(outPath), Path.GetDirectoryName(_path), StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(_path) : new Uri(_path).AbsoluteUri), new System.Text.UTF8Encoding(false));
            _info.Text = "Saved " + Path.GetFileName(outPath) + " (keep it beside the video)";
            if (!AutoHtml) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + outPath + "\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _info.Text = "Could not save: " + e.Message; }
    }

    private static readonly string[] Tags = ["", "Issue", "Insight", "Quote", "Question"];

    private void CycleTag()
    {
        int i = _steps.SelectedIndex;
        if (i < 0) return;
        var s = _p.Steps[i];
        s.Tag = Tags[(Array.IndexOf(Tags, s.Tag) + 1) % Tags.Length];
        RefreshList(i);
    }

    /// <summary>Tagged transcript: time, tag and note per step, plus a header naming the recording and its hash.</summary>
    private void ExportNotes()
    {
        try
        {
            Pause();
            _p.SaveEdits();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# " + Path.GetFileNameWithoutExtension(_path)).AppendLine();
            sb.AppendLine($"Recording: {Path.GetFileName(_path)}  ");
            sb.AppendLine("SHA-256: " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(_path))).ToLowerInvariant()).AppendLine();
            foreach (var g in _p.Steps.Where(s => s.Tag.Length > 0).GroupBy(s => s.Tag))
                sb.AppendLine($"- {g.Key}: {g.Count()}");
            sb.AppendLine().AppendLine("| Time | Tag | Note |").AppendLine("|---|---|---|");
            foreach (var s in _p.Steps)
                sb.AppendLine($"| {Fmt(s.T)} | {s.Tag} | {s.Caption.Replace("|", "\\|")} |");
            string outPath = OutPath("-notes", ".md");
            File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false));
            _info.Text = "Saved " + Path.GetFileName(outPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + outPath + "\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _info.Text = "Could not save: " + e.Message; }
    }

    /// <summary>Highlight reel: 2 s before to 4 s after each tagged step (every step when none are tagged), cut from the original frames.</summary>
    private async Task ExportReelAsync()
    {
        if (_busy) return;
        Pause();
        var picks = _p.Steps.Where(s => s.Tag.Length > 0).ToList();
        if (picks.Count == 0) picks = _p.Steps.ToList();
        if (picks.Count == 0) { _info.Text = "Add a step first."; return; }
        _busy = true;
        _info.Text = "Cutting reel...";
        string outPath = OutPath("-reel", ".mp4");
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_path);
            var comp = new MediaComposition();
            double end = _dur.TotalSeconds, last = 0;
            foreach (var s in picks)
            {
                double a = Math.Max(last, s.T - 2), b = Math.Min(end, s.T + 4);
                if (b - a < 0.5) continue;
                var c = await MediaClip.CreateFromFileAsync(file);
                c.TrimTimeFromStart = TimeSpan.FromSeconds(a);
                c.TrimTimeFromEnd = TimeSpan.FromSeconds(end - b);
                comp.Clips.Add(c);
                last = b;
            }
            var dst = await StorageFile.GetFileFromPathAsync(await CreateEmptyAsync(outPath));
            var res = await comp.RenderToFileAsync(dst, MediaTrimmingPreference.Fast, MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p));
            if (res != Windows.Media.Transcoding.TranscodeFailureReason.None) throw new InvalidOperationException(res.ToString());
            FlowProject.WriteDerivedNote(outPath, _path, "highlight reel (cut from original frames)");
            _info.Text = "Saved " + Path.GetFileName(outPath) + ". Cut copy: not signed as a raw capture.";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + outPath + "\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _info.Text = "Reel failed: " + e.Message;
            try { File.Delete(outPath); } catch (IOException) { }
        }
        finally { _busy = false; }
    }

    private static Task<string> CreateEmptyAsync(string path) { File.WriteAllBytes(path, []); return Task.FromResult(path); }

    private async Task ExportSvgAsync()
    {
        if (_busy) return;
        Pause();
        _busy = true;
        _mp4.Enabled = _html.Enabled = _svg.Enabled = _play.Enabled = false;
        try
        {
            _p.SaveEdits();
            _info.Text = "Building SVG...";
            var stills = await FlowSvg.GrabAsync(_p, _path);
            string outPath = OutPath("-flow", ".svg");
            await File.WriteAllTextAsync(outPath, FlowSvg.Build(_p, stills), new System.Text.UTF8Encoding(false));
            FlowProject.WriteDerivedNote(outPath, _path, _p.Effects);
            _info.Text = "Saved " + Path.GetFileName(outPath) + ". Stylised copy: not signed as a raw capture.";
            if (!AutoSvg) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + outPath + "\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _info.Text = "SVG failed: " + e.Message;
            if (AutoSvg) Environment.ExitCode = 1;
        }
        finally { _busy = false; _mp4.Enabled = _html.Enabled = _svg.Enabled = _play.Enabled = true; }
    }

    private async Task ExportMp4Async()
    {
        if (_busy) return;
        Pause();
        _busy = true;
        _mp4.Enabled = _html.Enabled = _play.Enabled = false;
        _cts = new CancellationTokenSource();
        string outPath = OutPath("-flow", ".mp4");
        try
        {
            _p.SaveEdits();
            var progress = new Progress<double>(f => _info.Text = $"Rendering {f:P0}");
            await FlowVideo.RenderAsync(_p, _path, outPath, progress, _cts.Token);
            FlowProject.WriteDerivedNote(outPath, _path, _p.Effects);
            _busy = false;
            _info.Text = "Saved " + Path.GetFileName(outPath) + ". Stylised copy: not signed as a raw capture.";
            if (!AutoExport) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + outPath + "\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            _busy = false;
            _info.Text = "Render failed: " + e.Message;
            if (AutoExport)
            {
                Environment.ExitCode = 1;
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "imago-flow-error.txt"), e.ToString()); } catch (IOException) { }
            }
            try { File.Delete(outPath); File.Delete(outPath + ".tmp.mp4"); } catch (IOException) { }
        }
        finally { _busy = false; _mp4.Enabled = _html.Enabled = _play.Enabled = true; }
    }

    /// <summary>The picture, double buffered so playback does not flicker.</summary>
    private sealed class Canvas : Control
    {
        public Canvas() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
}
