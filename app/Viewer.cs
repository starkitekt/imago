using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Imago;

/// <summary>
/// The lightbox: one capture on a light stage, with zoom, the other captures from the same folder in a
/// filmstrip, and a badge with the review verdict: stamped, changed, or not stamped. Sealed captures ask for their password
/// when they come up, and stay unlocked until the window closes.
/// </summary>
internal sealed class Viewer : FluentWindow
{
    internal static readonly Color HeaderBack = Ui.Surface;
    internal static readonly Color StageBack = Color.FromArgb(243, 243, 243);
    internal static readonly Color Dim = Ui.Muted;
    private const int DetailsW = 320;
    internal const int ImageRadius = 12;

    private readonly string _dir;
    private readonly Label _title = new()
    {
        AutoSize = true, Font = Ui.Title, ForeColor = Ui.Ink, BackColor = Color.Transparent,
        AutoEllipsis = true, UseMnemonic = false, Margin = new Padding(0, 1, 0, 0),
    };
    private readonly Badge _badge = new() { Margin = new Padding(0, 4, 12, 0), Cursor = Cursors.Hand };
    private readonly ImageCanvas _canvas = new() { Dock = DockStyle.Fill, BackColor = StageBack };
    private readonly Filmstrip _strip = new() { Dock = DockStyle.Bottom, BackColor = StageBack };
    private readonly Label _caption = new()
    {
        Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleCenter, Font = Ui.Small,
        ForeColor = Dim, BackColor = StageBack, UseMnemonic = false,
    };
    private readonly Label _zoom = new()
    {
        Size = new Size(52, 32), TextAlign = ContentAlignment.MiddleCenter, Font = Ui.Small,
        ForeColor = Ui.Ink, BackColor = Color.Transparent, Margin = new Padding(0, 0, 8, 0),
    };
    private readonly Panel _header;
    private readonly IconButton _fit, _zoomOut, _zoomIn, _copy, _folder, _seal, _marks, _save, _info;
    private readonly Panel _details = new() { Dock = DockStyle.Right, Width = DetailsW, BackColor = HeaderBack, Visible = false, AutoScroll = true, Padding = new Padding(28, 24, 24, 28) };
    private readonly FlowLayoutPanel _detailsList = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
    private readonly System.Windows.Forms.Timer _flash = new() { Interval = 1600 };
    private static readonly ToolTip Tip = new();

    private readonly Dictionary<string, (string FileName, byte[] Png)> _unlocked = new(StringComparer.OrdinalIgnoreCase);
    private List<FileInfo> _files;
    private int _index = -1;
    private int _gen;
    private Current? _cur;

    /// <summary>What is on screen: the capture's bytes (decrypted, for a locked file) and its review.</summary>
    private sealed record Current(string Path, string FileName, byte[] Png, ReviewReport Report, Size Pixels);

    public static void Open(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            string name = Path.GetFileName(path);
            if (Thumbs.IsSealed(path))
            {
                if (Unlock(null, path, bytes) is not { } opened) return;
                (name, bytes) = opened;
            }
            new Viewer(path, name, bytes).Show();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Alert.Show(null, "Could not open the file", e.Message, AlertKind.Error, "OK");
        }
    }

    /// <summary>Asks for the password until it opens the file. Null if the person cancels.</summary>
    private static (string FileName, byte[] Png)? Unlock(IWin32Window? owner, string path, byte[] bytes)
    {
        while (true)
        {
            using var dlg = new PasswordDialog(path, confirm: false);
            if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                return Seal.Decrypt(bytes, dlg.Password);
            }
            catch (Seal.WrongPasswordException)
            {
                Alert.Show(owner, "Password not accepted", "That password doesn't open this file.", AlertKind.Warning, "Try again");
            }
            finally { Cursor.Current = Cursors.Default; }
        }
    }

    private Viewer(string path, string name, byte[] png) : base(dark: false)
    {
        Text = name + " - Imago";
        Icon = Ui.AppIcon;
        Font = Ui.Body;
        BackColor = StageBack;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(720, 480);

        // Header: name and verdict on the left; view tools, file actions and Close on the right.
        _fit = Tool(Glyph.Fit, "Fit to window (0)", () => _canvas.FitToView());
        _zoomOut = Tool(Glyph.ZoomOut, "Zoom out (-)", () => _canvas.ZoomBy(1 / 1.25f));
        _zoomIn = Tool(Glyph.ZoomIn, "Zoom in (+)", () => _canvas.ZoomBy(1.25f));
        _copy = Tool(Glyph.Copy, "Copy image (Ctrl+C)", CopyImage);
        _folder = Tool(Glyph.FolderOpen, "Show in folder", ShowInFolder);
        _seal = Tool(Glyph.Lock, "Lock with password", SealCurrent);
        _marks = Tool(Glyph.Shield, "Show review marks (M)", ToggleMarks);
        _save = Tool(Glyph.Download, "Save as PNG (Ctrl+S)", SaveCopy);
        _info = Tool(Glyph.Info, "Details (I)", ToggleDetails);
        var close = new Pill { Text = "Close", Margin = new Padding(16, 0, 0, 0) };
        close.Size = new Size(TextRenderer.MeasureText("Close", Ui.Body).Width + 48, 32);
        close.Click += (_, _) => Close();
        var rule = new Panel { Size = new Size(1, 20), BackColor = Ui.Hairline, Margin = new Padding(4, 6, 12, 0) };

        var tools = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = HeaderBack, Margin = Padding.Empty };
        tools.Controls.AddRange([_fit, _zoomOut, _zoom, _zoomIn, rule, _marks, _copy, _folder, _seal, _save, _info, close]);
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = HeaderBack };
        // Chip first, so it holds its place at the left while names of any length follow it.
        left.Controls.Add(_badge);
        left.Controls.Add(_title);
        left.Resize += (_, _) => _title.MaximumSize = new Size(Math.Max(80, left.Width - _badge.Width - 24), 0);
        _header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = HeaderBack, Padding = new Padding(32, 12, 32, 12) };
        _header.Controls.Add(left);
        _header.Controls.Add(tools);
        DragArea(_header, left, _title, tools);
        _badge.Click += (_, _) => ToggleDetails();

        // Stage: the image, then its caption, then the filmstrip at the bottom.
        var stage = new Panel { Dock = DockStyle.Fill, BackColor = StageBack };
        stage.Controls.Add(_canvas);
        stage.Controls.Add(_caption);
        stage.Controls.Add(_strip);
        _details.Controls.Add(_detailsList);

        Controls.Add(stage);
        Controls.Add(_details);
        Controls.Add(_header);

        _canvas.Prev += (_, _) => Go(-1);
        _canvas.Next += (_, _) => Go(1);
        _canvas.ZoomChanged += (_, _) => _zoom.Text = _canvas.HasImage ? $"{Math.Round(_canvas.Zoom * 100)}%" : "";
        _canvas.UnlockClicked += (_, _) => UnlockCurrent();
        _strip.Picked += ShowAt;
        _flash.Tick += (_, _) => { _flash.Stop(); UpdateCaption(); };

        _dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        if (Thumbs.IsSealed(path)) _unlocked[path] = (name, png);
        _files = Thumbs.List(_dir);
        int i = _files.FindIndex(f => f.FullName.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (i < 0) { _files.Insert(0, new FileInfo(path)); i = 0; }
        _strip.SetFiles(_files);
        ShowAt(i);

        FormClosed += (_, _) => { _canvas.SetImage(null); _flash.Dispose(); };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitBars();
        // Sized after DPI scaling, so it fits the screen it opens on.
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(LogicalToDeviceUnits(1366), work.Width * 9 / 10), Math.Min(LogicalToDeviceUnits(860), work.Height * 9 / 10));
        Location = new Point(work.X + (work.Width - Width) / 2, work.Y + (work.Height - Height) / 2);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitBars();
    }

    /// <summary>The bars have fixed heights from the design, so they follow the screen's scale by hand.</summary>
    private void FitBars()
    {
        _header.Height = LogicalToDeviceUnits(56);
        int side = LogicalToDeviceUnits(32), top = (_header.Height - 32) / 2;
        _header.Padding = new Padding(side, top, side, 0);
        _caption.Height = LogicalToDeviceUnits(28);
        _strip.Height = LogicalToDeviceUnits(Filmstrip.Height96);
    }

    /// <summary>Picks up captures taken while the viewer was in the background.</summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_index >= 0) SyncFiles(null);
    }

    private IconButton Tool(string glyph, string tip, Action onClick)
    {
        var b = new IconButton(glyph, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Go(int step)
    {
        int i = _index + step;
        if (i >= 0 && i < _files.Count) ShowAt(i);
    }

    private void ShowAt(int i)
    {
        if (i < 0 || i >= _files.Count) return;
        _index = i;
        int gen = ++_gen;
        var file = _files[i];
        string path = file.FullName;
        _strip.Highlight(i);
        _canvas.SetNavigation(i > 0, i < _files.Count - 1);
        _title.Text = file.Name;
        Text = file.Name + " - Imago";
        _cur = null;

        bool sealedFile = Thumbs.IsSealed(path);
        if (sealedFile && !_unlocked.ContainsKey(path))
        {
            _canvas.SetImage(null);
            _canvas.SetLocked(true);
            _badge.Set(Glyph.Lock, "Locked", Status.Neutral);
            Tip.SetToolTip(_badge, "Encrypted with a password.");
            UpdateCaption();
            UpdateTools();
            FillDetails();
            return;
        }
        _canvas.SetLocked(false);
        _ = LoadAsync(path, sealedFile, gen);
    }

    /// <summary>Reads, decodes and verifies off the UI thread. A newer selection wins if it arrives first.</summary>
    private async Task LoadAsync(string path, bool sealedFile, int gen)
    {
        (string FileName, byte[] Png) src;
        Bitmap? bmp;
        ReviewReport result;
        try
        {
            src = sealedFile ? _unlocked[path] : (Path.GetFileName(path), await File.ReadAllBytesAsync(path));
            (bmp, result) = await Task.Run(() => (Decode(src.Png), Review.Analyze(src.Png)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (gen != _gen || IsDisposed) return;
            _canvas.SetImage(null);
            _badge.Set(Glyph.Error, "Can't open", Status.Danger);
            _caption.Text = e.Message;
            UpdateTools();
            return;
        }
        if (gen != _gen || IsDisposed) { bmp?.Dispose(); return; }

        _cur = new Current(path, src.FileName, src.Png, result, bmp?.Size ?? Size.Empty);
        _canvas.SetImage(bmp);
        _canvas.SetMarks(result.ImagoEdits.Select(e => e.Box).ToList(), result.Altered);
        var (glyph, text, tone) = result.Verdict switch
        {
            ReviewVerdict.Stamped => (Glyph.Check, result.ImagoEdits.Count > 0 ? "Stamped, edited in Imago" : "Stamped", Status.Success),
            ReviewVerdict.Resaved => (Glyph.Check, "Stamped copy", Status.Success),
            ReviewVerdict.Changed => (Glyph.Error, "Changed", Status.Danger),
            ReviewVerdict.StampedElsewhere => (Glyph.Shield, "Stamped elsewhere", Status.Warning),
            ReviewVerdict.Forged => (Glyph.Error, "Not genuine", Status.Danger),
            ReviewVerdict.Error => (Glyph.Warning, "Can't check", Status.Warning),
            _ => ("", "Not stamped", Status.Neutral),
        };
        _badge.Set(glyph, text, tone);
        Tip.SetToolTip(_badge, result.Headline + "\nClick for details.");
        UpdateCaption();
        UpdateTools();
        FillDetails();
    }

    private static Bitmap? Decode(byte[] png)
    {
        try { return Png.Decode(png).Pixels.ToBitmap(); }
        catch (InvalidDataException)
        {
            try
            {
                // GDI+ needs its stream for the bitmap's whole life, so copy it out.
                using var ms = new MemoryStream(png);
                using var tmp = new Bitmap(ms);
                return new Bitmap(tmp);
            }
            catch (ArgumentException) { return null; }
        }
    }

    private void UpdateCaption()
    {
        if (_index < 0) return;
        string where = $"{_index + 1} of {_files.Count}";
        _caption.Text = _cur is { Pixels.IsEmpty: false } c ? $"{c.Pixels.Width} x {c.Pixels.Height}   ·   {where}"
            : Thumbs.IsSealed(_files[_index].FullName) && _cur is null ? $"Locked   ·   {where}" : where;
    }

    private void Flash(string message)
    {
        _caption.Text = message;
        _flash.Stop();
        _flash.Start();
    }

    private void UpdateTools()
    {
        bool image = _canvas.HasImage;
        bool sealedFile = _index >= 0 && Thumbs.IsSealed(_files[_index].FullName);
        _fit.Enabled = _zoomOut.Enabled = _zoomIn.Enabled = _copy.Enabled = image;
        _seal.Visible = !sealedFile;
        _seal.Enabled = _cur is not null;
        _save.Visible = sealedFile;
        _save.Enabled = _cur is not null;
        _zoom.Text = image ? $"{Math.Round(_canvas.Zoom * 100)}%" : "";
    }

    /// <summary>Refreshes the file list. Keeps the current capture on screen if it is still there.</summary>
    private void SyncFiles(string? select)
    {
        var now = Thumbs.List(_dir);
        string? current = select ?? (_index >= 0 && _index < _files.Count ? _files[_index].FullName : null);
        if (select is null && now.Select(f => f.FullName).SequenceEqual(_files.Select(f => f.FullName), StringComparer.OrdinalIgnoreCase)) return;
        _files = now;
        _strip.SetFiles(now);
        if (now.Count == 0) { Close(); return; }
        int i = current is null ? -1 : now.FindIndex(f => f.FullName.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && select is null && _cur?.Path.Equals(current, StringComparison.OrdinalIgnoreCase) == true)
        {
            _index = i;
            _strip.Highlight(i);
            _canvas.SetNavigation(i > 0, i < now.Count - 1);
            UpdateCaption();
            return;
        }
        ShowAt(i >= 0 ? i : Math.Clamp(_index, 0, now.Count - 1));
    }

    private void UnlockCurrent()
    {
        if (_index < 0) return;
        string path = _files[_index].FullName;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (Unlock(this, path, bytes) is not { } opened) return;
            _unlocked[path] = opened;
            ShowAt(_index);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Alert.Show(this, "Could not open the file", e.Message, AlertKind.Error, "OK");
        }
    }

    private void CopyImage()
    {
        if (_canvas.Image is not { } img) return;
        Clipboard.SetImage(img);
        Flash("Copied to clipboard");
    }

    private void ShowInFolder()
    {
        if (_index < 0) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_files[_index].FullName}\"") { UseShellExecute = true });
    }

    private void SealCurrent()
    {
        if (_cur is not { } cur || Thumbs.IsSealed(cur.Path)) return;
        string? sealedPath = TrayApp.SealWithPrompt(cur.Path);
        if (sealedPath is null) return;
        // The password was just typed here, so the sealed copy opens without asking again.
        _unlocked[sealedPath] = (cur.FileName, cur.Png);
        SyncFiles(sealedPath);
    }

    private void SaveCopy()
    {
        if (_cur is not { } cur || !Thumbs.IsSealed(cur.Path)) return;
        using var dlg = new SaveFileDialog
        {
            FileName = Path.GetFileName(cur.FileName),
            Filter = "PNG image|*.png",
            InitialDirectory = _dir,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllBytes(dlg.FileName, cur.Png);
        Flash("Saved " + Path.GetFileName(dlg.FileName));
    }

    private void ToggleMarks()
    {
        _canvas.ShowMarks = !_canvas.ShowMarks;
        Flash(_canvas.ShowMarks ? "Review marks on" : "Review marks off");
    }

    private void ToggleDetails()
    {
        _details.Visible = !_details.Visible;
        FillDetails();
    }

    /// <summary>The side pane: file facts, then the review: changed or not, where, what kind, and AI involvement.</summary>
    private void FillDetails()
    {
        if (!_details.Visible || _index < 0) return;
        // Measure the pane itself: its width and padding are set before DPI scaling, and the scrollbar takes a share.
        int w = Math.Max(80, _details.ClientSize.Width - _details.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        _detailsList.SuspendLayout();
        var old = _detailsList.Controls.Cast<Control>().ToList();
        _detailsList.Controls.Clear();
        foreach (var c in old) c.Dispose();
        // A panel's padding only moves docked children, so place the list inside it by hand. The bottom padding
        // becomes scroll room, so the last line never sits on the edge.
        _detailsList.Margin = Padding.Empty;
        _detailsList.Location = new Point(_details.Padding.Left, _details.Padding.Top + _details.AutoScrollPosition.Y);
        _details.AutoScrollMargin = new Size(0, _details.Padding.Bottom);

        void Add(string text, Font font, Color color, int top)
        {
            var l = new Label
            {
                Text = text, Font = font, ForeColor = color, AutoSize = true, UseMnemonic = false,
                MaximumSize = new Size(w, 0), Margin = new Padding(0, top, 0, 0), BackColor = Color.Transparent,
            };
            _detailsList.Controls.Add(l);
        }
        void Fact(string label, string value)
        {
            Add(label, Ui.Small, Dim, 10);
            Add(value, Ui.Body, Ui.Ink, 0);
        }

        var file = _files[_index];
        Add("Details", Ui.Title, Ui.Ink, 0);
        Fact("Name", file.Name);
        if (_cur is { Pixels.IsEmpty: false } shown) Fact("Dimensions", $"{shown.Pixels.Width} x {shown.Pixels.Height} pixels");
        Fact("Size", file.Exists ? Bytes(file.Length) : "-");
        Fact("Modified", file.LastWriteTime.ToString("d MMM yyyy, HH:mm"));
        // The folder is a link: it opens Explorer with this capture selected.
        Add("Location", Ui.Small, Dim, 10);
        var link = new Label
        {
            Text = file.FullName, Font = Ui.Body, ForeColor = Ui.Brand, AutoSize = true, UseMnemonic = false,
            MaximumSize = new Size(w, 0), Margin = Padding.Empty, BackColor = Color.Transparent, Cursor = Cursors.Hand,
        };
        var under = new Font(Ui.Body, FontStyle.Underline);
        link.MouseEnter += (_, _) => link.Font = under;
        link.MouseLeave += (_, _) => link.Font = Ui.Body;
        link.Click += (_, _) => ShowInFolder();
        link.Disposed += (_, _) => under.Dispose();
        Tip.SetToolTip(link, "Show in folder");
        _detailsList.Controls.Add(link);

        Add("Review", Ui.Title, Ui.Ink, 24);
        if (_cur is { } cur)
        {
            var r = cur.Report;
            var tone = r.Verdict switch
            {
                ReviewVerdict.Stamped or ReviewVerdict.Resaved => Status.Success,
                ReviewVerdict.Changed or ReviewVerdict.Forged => Status.Danger,
                ReviewVerdict.StampedElsewhere or ReviewVerdict.Error => Status.Warning,
                _ => Ui.Ink,
            };
            Add(r.Headline, Ui.Body, tone, 8);
            if (r.StampId is { } id) Fact("Stamp", id);
            if (r.Altered.Count > 0) Add("Red boxes on the image mark what changed.", Ui.Small, Status.Danger, 10);
            if (r.ImagoEdits.Count > 0) Add("Blue boxes mark edits made in Imago before stamping.", Ui.Small, Ui.Brand, r.Altered.Count > 0 ? 4 : 10);
            foreach (var d in r.Details) Add(d, Ui.Small, Dim, 6);

            Add("AI involvement", Ui.Small, Dim, 16);
            Add(r.Ai switch
            {
                AiLevel.High => "High likelihood",
                AiLevel.Medium => "Medium likelihood",
                AiLevel.Low => "Low likelihood",
                _ => "Not assessed",
            }, Ui.Body, r.Ai switch { AiLevel.High => Status.Danger, AiLevel.Medium => Status.Warning, _ => Ui.Ink }, 0);
            foreach (var why in r.AiReasons) Add(why, Ui.Small, Dim, 6);
            if (Thumbs.IsSealed(cur.Path)) Add("Opened from a locked file. It stays encrypted on disk.", Ui.Small, Dim, 12);
        }
        else Add(Thumbs.IsSealed(file.FullName) && !_unlocked.ContainsKey(file.FullName) ? "Unlock this capture to review it." : "Reviewing...", Ui.Small, Dim, 8);
        _detailsList.ResumeLayout();
    }

    private static string Bytes(long n) =>
        n >= 1 << 20 ? $"{n / 1048576.0:0.0} MB" : n >= 1 << 10 ? $"{n / 1024.0:0} KB" : $"{n} bytes";

    protected override bool ProcessCmdKey(ref Message msg, System.Windows.Forms.Keys keyData)
    {
        switch (keyData)
        {
            case System.Windows.Forms.Keys.Left: Go(-1); return true;
            case System.Windows.Forms.Keys.Right: Go(1); return true;
            case System.Windows.Forms.Keys.Home: ShowAt(0); return true;
            case System.Windows.Forms.Keys.End: ShowAt(_files.Count - 1); return true;
            case System.Windows.Forms.Keys.Escape: Close(); return true;
            case System.Windows.Forms.Keys.Oemplus or System.Windows.Forms.Keys.Add or (System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.Oemplus):
                _canvas.ZoomBy(1.25f); return true;
            case System.Windows.Forms.Keys.OemMinus or System.Windows.Forms.Keys.Subtract: _canvas.ZoomBy(1 / 1.25f); return true;
            case System.Windows.Forms.Keys.D0 or System.Windows.Forms.Keys.NumPad0: _canvas.FitToView(); return true;
            case System.Windows.Forms.Keys.D1 or System.Windows.Forms.Keys.NumPad1: _canvas.ActualSize(null); return true;
            case System.Windows.Forms.Keys.I: ToggleDetails(); return true;
            case System.Windows.Forms.Keys.M: ToggleMarks(); return true;
            case System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.C: CopyImage(); return true;
            case System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.S: SaveCopy(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

/// <summary>
/// The stage: shows the image fitted or zoomed, pans with a drag, zooms with the wheel around the
/// pointer, and has previous and next arrows at the sides. A sealed capture shows a lock instead.
/// </summary>
internal sealed class ImageCanvas : Control
{
    private const int Arrow = 48, Edge = 20;
    private const float MaxZoom = 16f;

    private Bitmap? _image;
    private bool _fit = true, _locked, _hasPrev, _hasNext;
    private float _scale = 1f;
    private PointF _offset;
    private Point? _dragFrom;
    private PointF _dragOffset;
    private int _hot;
    private readonly Pill _unlock = new() { Text = "Unlock...", Visible = false };
    private IReadOnlyList<Rectangle> _edits = [], _altered = [];
    private bool _showMarks = true;
    private static readonly Font LockFont = new(Glyph.Family, 32f);

    public event EventHandler? Prev, Next, ZoomChanged, UnlockClicked;

    public ImageCanvas()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _unlock.Size = Pill.Fit("Unlock...");
        _unlock.Click += (_, _) => UnlockClicked?.Invoke(this, EventArgs.Empty);
        Controls.Add(_unlock);
    }

    public Bitmap? Image => _image;

    /// <summary>Draw the review marks: blue for Imago's own edits, red for changes since capture.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowMarks
    {
        get => _showMarks;
        set { _showMarks = value; Invalidate(); }
    }

    /// <summary>Boxes in image pixels. Cleared whenever the image changes.</summary>
    public void SetMarks(IReadOnlyList<Rectangle> edits, IReadOnlyList<Rectangle> altered)
    {
        _edits = edits;
        _altered = altered;
        Invalidate();
    }
    public bool HasImage => _image is not null && !_locked;
    public float Zoom => _fit ? FitScale() : _scale;

    /// <summary>Takes ownership of the bitmap and disposes the previous one.</summary>
    public void SetImage(Bitmap? image)
    {
        var old = _image;
        _image = image;
        old?.Dispose();
        _edits = _altered = [];
        _fit = true;
        _offset = PointF.Empty;
        Invalidate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        _unlock.Visible = locked;
        PerformLayout();
        Invalidate();
    }

    public void SetNavigation(bool prev, bool next)
    {
        _hasPrev = prev;
        _hasNext = next;
        Invalidate();
    }

    public void FitToView()
    {
        _fit = true;
        _offset = PointF.Empty;
        Invalidate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomBy(float factor, Point? anchor = null)
    {
        if (!HasImage) return;
        float s = Math.Min(MaxZoom, Zoom * factor);
        // Zooming out past "fit" settles on fit, so the image never floats small in the middle.
        if (s <= FitScale()) FitToView();
        else ScaleTo(s, anchor ?? Middle);
    }

    /// <summary>One image pixel per screen pixel. If that is already the fit, zooms to 200% instead.</summary>
    public void ActualSize(Point? anchor)
    {
        if (!HasImage) return;
        ScaleTo(Math.Abs(FitScale() - 1f) < 0.01f ? 2f : 1f, anchor ?? Middle);
    }

    private Point Middle => new(Width / 2, Height / 2);

    private float FitScale()
    {
        if (_image is null) return 1f;
        float side = LogicalToDeviceUnits(Arrow + Edge * 2);
        float aw = Math.Max(1, Width - side * 2), ah = Math.Max(1, Height - LogicalToDeviceUnits(16));
        return Math.Min(1f, Math.Min(aw / _image.Width, ah / _image.Height));
    }

    private RectangleF ImageRect()
    {
        if (_image is null) return RectangleF.Empty;
        float s = Zoom, w = _image.Width * s, h = _image.Height * s;
        return new RectangleF(Width / 2f + _offset.X - w / 2, Height / 2f + _offset.Y - h / 2, w, h);
    }

    /// <summary>Zooms so the image point under the anchor stays under it.</summary>
    private void ScaleTo(float s, PointF anchor)
    {
        var r = ImageRect();
        float old = Zoom;
        float ix = (anchor.X - (r.X + r.Width / 2)) / old, iy = (anchor.Y - (r.Y + r.Height / 2)) / old;
        _fit = false;
        _scale = s;
        _offset = new PointF(anchor.X - ix * s - Width / 2f, anchor.Y - iy * s - Height / 2f);
        Clamp();
        Invalidate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Keeps a zoomed image covering the view instead of sliding off it.</summary>
    private void Clamp()
    {
        if (_image is null) return;
        float mx = Math.Max(0, (_image.Width * Zoom - Width) / 2), my = Math.Max(0, (_image.Height * Zoom - Height) / 2);
        _offset = new PointF(Math.Clamp(_offset.X, -mx, mx), Math.Clamp(_offset.Y, -my, my));
    }

    private bool CanPan => HasImage && !_fit && (_image!.Width * _scale > Width || _image.Height * _scale > Height);

    private Rectangle ArrowRect(int side)
    {
        int a = LogicalToDeviceUnits(Arrow), e = LogicalToDeviceUnits(Edge);
        return new Rectangle(side < 0 ? e : Width - e - a, (Height - a) / 2, a, a);
    }

    private int ArrowAt(Point p) =>
        _hasPrev && ArrowRect(-1).Contains(p) ? -1 : _hasNext && ArrowRect(1).Contains(p) ? 1 : 0;

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        _unlock.Location = new Point((Width - _unlock.Width) / 2, Height / 2 + LogicalToDeviceUnits(44));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_fit) Clamp();
        if (HasImage) ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DrawMarks(Graphics g, RectangleF shown)
    {
        if (_image is null || (_edits.Count == 0 && _altered.Count == 0)) return;
        float k = shown.Width / _image.Width;
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float pad = LogicalToDeviceUnits(3);
        void Box(Rectangle r, Color c, bool dashed)
        {
            var box = new RectangleF(shown.X + r.X * k - pad, shown.Y + r.Y * k - pad, r.Width * k + pad * 2, r.Height * k + pad * 2);
            using var path = Shape.Round(Rectangle.Round(box), LogicalToDeviceUnits(6));
            using (var fill = new SolidBrush(Color.FromArgb(dashed ? 18 : 36, c))) g.FillPath(fill, path);
            using var pen = new Pen(c, LogicalToDeviceUnits(2));
            if (dashed) pen.DashPattern = [4f, 3f];
            g.DrawPath(pen, path);
        }
        foreach (var r in _edits) Box(r, Ui.Brand, dashed: true);
        foreach (var r in _altered) Box(r, Status.Danger, dashed: false);
        g.SmoothingMode = mode;
    }

    /// <summary>
    /// Softens the image's corners by painting the stage colour over them. A clip region would leave jagged
    /// corners, while an anti-aliased fill stays smooth at any zoom.
    /// </summary>
    private void RoundCorners(Graphics g, RectangleF shown)
    {
        float r = Math.Min(LogicalToDeviceUnits(Viewer.ImageRadius), Math.Min(shown.Width, shown.Height) / 4);
        var mode = g.SmoothingMode;
        var offset = g.PixelOffsetMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (r >= 1)
        {
            using var stage = new SolidBrush(BackColor);
            // Each corner is its square minus a quarter circle; the alternate fill mode cuts the circle out.
            foreach (var (x, y, angle) in new[] { (shown.Left, shown.Top, 180f), (shown.Right - r, shown.Top, 270f),
                                                  (shown.Right - r, shown.Bottom - r, 0f), (shown.Left, shown.Bottom - r, 90f) })
            {
                using var corner = new GraphicsPath(FillMode.Alternate);
                corner.AddRectangle(new RectangleF(x, y, r, r));
                float cx = angle is 180f or 90f ? x : x - r, cy = angle is 180f or 270f ? y : y - r;
                corner.AddPie(cx, cy, r * 2, r * 2, angle, 90);
                g.FillPath(stage, corner);
            }
        }
        // A hairline keeps a white capture from melting into the light stage.
        using var edge = new Pen(Color.FromArgb(28, 0, 0, 0));
        var frame = Rectangle.Round(RectangleF.Inflate(shown, 0.5f, 0.5f));
        using var outline = r >= 1 ? Shape.Round(frame, (int)Math.Round(r)) : new GraphicsPath();
        if (r < 1) outline.AddRectangle(frame);
        g.DrawPath(edge, outline);
        g.SmoothingMode = mode;
        g.PixelOffsetMode = offset;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_locked)
        {
            int cy = Height / 2;
            Glyph.Draw(g, Glyph.Lock, LockFont, new Rectangle(0, cy - LogicalToDeviceUnits(96), Width, LogicalToDeviceUnits(56)), Viewer.Dim);
            TextRenderer.DrawText(g, "This capture is locked", Ui.Title, new Rectangle(0, cy - LogicalToDeviceUnits(32), Width, LogicalToDeviceUnits(26)), Ui.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "Enter its password to view it here.", Ui.Small, new Rectangle(0, cy - LogicalToDeviceUnits(4), Width, LogicalToDeviceUnits(22)), Viewer.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else if (_image is not null)
        {
            float s = Zoom;
            g.InterpolationMode = s >= 2f ? InterpolationMode.NearestNeighbor
                : _dragFrom is null ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var shown = ImageRect();
            g.DrawImage(_image, shown);
            RoundCorners(g, shown);
            if (_showMarks) DrawMarks(g, shown);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (int side in new[] { -1, 1 })
        {
            if (side < 0 ? !_hasPrev : !_hasNext) continue;
            var r = ArrowRect(side);
            using (var face = new SolidBrush(_hot == side ? Ui.SurfaceHover : Color.FromArgb(235, 255, 255, 255))) g.FillEllipse(face, r);
            using (var ring = new Pen(Color.FromArgb(30, 0, 0, 0))) g.DrawEllipse(ring, r);
            Glyph.Draw(g, side < 0 ? Glyph.ChevronLeft : Glyph.ChevronRight, Glyph.Regular, r, Ui.Ink);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        int arrow = ArrowAt(e.Location);
        if (arrow != 0) { (arrow < 0 ? Prev : Next)?.Invoke(this, EventArgs.Empty); return; }
        if (CanPan)
        {
            _dragFrom = e.Location;
            _dragOffset = _offset;
            Cursor = Cursors.SizeAll;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragFrom is Point from)
        {
            _offset = new PointF(_dragOffset.X + e.X - from.X, _dragOffset.Y + e.Y - from.Y);
            Clamp();
            Invalidate();
            return;
        }
        int hot = ArrowAt(e.Location);
        if (hot != _hot) { _hot = hot; Invalidate(); }
        Cursor = hot != 0 ? Cursors.Hand : CanPan ? Cursors.SizeAll : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragFrom is null) return;
        _dragFrom = null;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot != 0) { _hot = 0; Invalidate(); }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || ArrowAt(e.Location) != 0 || !HasImage) return;
        if (_fit) ActualSize(e.Location);
        else FitToView();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomBy(e.Delta > 0 ? 1.25f : 1 / 1.25f, e.Location);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _image?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The captures in the folder as a row of square thumbnails. The current one is outlined; click another to show it.</summary>
internal sealed class Filmstrip : Control
{
    private const int Tile = 84, Gap = 16, Above = 8, Below = 28;
    public const int Height96 = Above + Tile + Below;

    private List<FileInfo> _files = [];
    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(3);
    private int _selected = -1, _hot = -1;
    private float _scroll;

    public event Action<int>? Picked;

    public Filmstrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = Height96;
        Cursor = Cursors.Default;
    }

    private int T => LogicalToDeviceUnits(Tile);
    private int G => LogicalToDeviceUnits(Gap);
    private int RowWidth => _files.Count * T + Math.Max(0, _files.Count - 1) * G;

    public void SetFiles(List<FileInfo> files)
    {
        _files = files;
        var keep = files.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _cache.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _cache[key]?.Dispose();
            _cache.Remove(key);
        }
        _hot = -1;
        Invalidate();
    }

    /// <summary>Outlines a tile and scrolls the row so it sits in the middle.</summary>
    public void Highlight(int i)
    {
        _selected = i;
        ScrollTo(G + i * (T + G) + T / 2f - Width / 2f);
    }

    private void ScrollTo(float x)
    {
        _scroll = Math.Clamp(x, 0, Math.Max(0, RowWidth + 2 * G - Width));
        Invalidate();
    }

    private Rectangle ItemRect(int i)
    {
        int x0 = RowWidth + 2 * G <= Width ? (Width - RowWidth) / 2 : G - (int)_scroll;
        return new Rectangle(x0 + i * (T + G), LogicalToDeviceUnits(Above), T, T);
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _files.Count; i++) if (ItemRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_selected >= 0) Highlight(_selected);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int r4 = LogicalToDeviceUnits(4);
        for (int i = 0; i < _files.Count; i++)
        {
            var r = ItemRect(i);
            if (r.Right < 0) continue;
            if (r.Left > Width) break;
            string path = _files[i].FullName;
            using var shape = Shape.Round(r, r4);
            if (Thumbs.IsSealed(path))
            {
                using var b = new SolidBrush(Color.FromArgb(230, 230, 230));
                g.FillPath(b, shape);
                Glyph.Draw(g, Glyph.Lock, Glyph.Regular, r, Viewer.Dim);
            }
            else if (_cache.TryGetValue(path, out var bmp) && bmp is not null)
            {
                g.SetClip(shape);
                Thumbs.Cover(g, bmp, r);
                g.ResetClip();
            }
            else
            {
                using var b = new SolidBrush(Color.FromArgb(232, 232, 232));
                g.FillPath(b, shape);
                if (!_cache.ContainsKey(path) && !_pending.Contains(path)) _ = LoadAsync(path);
            }

            if (i == _selected)
            {
                var ring = Rectangle.Inflate(r, LogicalToDeviceUnits(3), LogicalToDeviceUnits(3));
                using var ringPath = Shape.Round(ring, LogicalToDeviceUnits(6));
                using var pen = new Pen(Ui.Brand, LogicalToDeviceUnits(2));
                g.DrawPath(pen, ringPath);
            }
            else
            {
                // The others sit back under a white wash; the one under the pointer comes forward.
                using (var edge = new Pen(Color.FromArgb(24, 0, 0, 0))) g.DrawPath(edge, shape);
                if (i != _hot)
                {
                    using var veil = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
                    g.FillPath(veil, shape);
                }
            }
        }
    }

    private async Task LoadAsync(string path)
    {
        _pending.Add(path);
        Bitmap? bmp;
        await _gate.WaitAsync();
        try { bmp = await Task.Run(() => Thumbs.Make(path, Tile * 2, Tile * 2)); }
        finally { _gate.Release(); }
        _pending.Remove(path);
        if (IsDisposed) { bmp?.Dispose(); return; }
        if (_cache.TryGetValue(path, out var old)) old?.Dispose();
        _cache[path] = bmp;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hot = HitTest(e.Location);
        Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
        if (hot != _hot) { _hot = hot; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot != -1) { _hot = -1; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int i = HitTest(e.Location);
        if (e.Button == MouseButtons.Left && i >= 0 && i != _selected) Picked?.Invoke(i);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollTo(_scroll - e.Delta);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var b in _cache.Values) b?.Dispose();
            _gate.Dispose();
        }
        base.Dispose(disposing);
    }
}
