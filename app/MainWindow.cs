using System.Drawing.Drawing2D;
using System.Globalization;

namespace Imago;

/// <summary>What the main window can do. TrayApp wires each one up.</summary>
internal sealed record MainActions(
    Action Area, Action Window, Action Full, Action Text,
    Action Verified, Func<bool> IsVerified,
    Action Settings, Action Folder, Action<string> Open);

/// <summary>
/// Opened by a left-click on the tray icon, laid out like a Microsoft 365 app: a title bar with search,
/// a navigation pane, and a white card with the capture commands over every capture, grouped by date.
/// </summary>
internal sealed class MainWindow : FluentWindow
{
    /// <summary>The light neutral behind the title bar and the navigation pane.</summary>
    private static readonly Color Backdrop = Ui.Canvas;
    private const int TitleH = 48, NavW = 232, BarH = 56, CaptionW = 46, SearchW = 400;

    private readonly MainActions _a;
    private readonly Panel _title = new() { Dock = DockStyle.Top, BackColor = Backdrop };
    private readonly PictureBox _logo = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
    private readonly Label _appName = new() { Text = "Imago", AutoSize = true, Font = Ui.Body, ForeColor = Ui.Ink, BackColor = Color.Transparent };
    private readonly TextBox _query = new() { PlaceholderText = "Search captures" };
    private readonly Input _search;
    private readonly IconButton _min = Caption(Glyph.Minimize, "Minimize");
    private readonly IconButton _max = Caption(Glyph.Maximize, "Maximize");
    private readonly IconButton _close = Caption(Glyph.Close, "Close");
    private readonly NavPane _nav = new() { Dock = DockStyle.Left, BackColor = Backdrop };
    private readonly Panel _frame = new() { Dock = DockStyle.Fill, BackColor = Backdrop };
    private readonly RoundCard _card = new() { Dock = DockStyle.Fill };
    private readonly Panel _bar = new() { Dock = DockStyle.Top, BackColor = Ui.Surface };
    private readonly ToolButton[] _commands;
    private readonly Label _verifiedLabel = new() { Text = "Stamped", AutoSize = true, Font = Ui.Body, ForeColor = Ui.Ink, BackColor = Color.Transparent };
    private readonly Switch _verified = new();
    private readonly Gallery _gallery = new() { Dock = DockStyle.Fill, BackColor = Ui.Surface };
    private List<FileInfo> _files = [];

    public MainWindow(MainActions actions)
    {
        _a = actions;
        Text = "Imago";
        Font = Ui.Body;
        Icon = Ui.AppIcon;
        BackColor = Backdrop;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;

        // Title bar: app name on the left, search in the middle, the Windows caption buttons on the right.
        using (var ico = new Icon(Ui.AppIcon, 32, 32)) _logo.Image = ico.ToBitmap();
        _search = new Input(_query, SearchW) { Glyph = Glyph.Search, Margin = Padding.Empty };
        _query.TextChanged += (_, _) => Apply();
        _min.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _max.Click += (_, _) => ToggleMaximize();
        _close.Click += (_, _) => Hide();
        _close.HotFill = Color.FromArgb(196, 43, 28);
        _title.Controls.AddRange([_logo, _appName, _search, _min, _max, _close]);
        _title.Layout += (_, _) => LayoutTitle();
        DragArea(_title, _logo, _appName);

        // Navigation: the two views on top, the folder and Settings at the bottom.
        _nav.Add(Glyph.Photo, "All captures", top: true);
        _nav.Add(Glyph.Lock, "Locked", top: true);
        _nav.Add(Glyph.FolderOpen, "Open folder", top: false);
        _nav.Add(Glyph.Settings, "Settings", top: false);
        _nav.Picked += i =>
        {
            if (i < 2) { _gallery.Sealed = i == 1; Apply(); }
            else if (i == 2) _a.Folder();
            else _a.Settings();
        };

        // Command bar: the four capture modes, and the Verified switch on the right.
        _commands =
        [
            Command(Glyph.Snip, "Area", "Capture an area (PrtSc)", _a.Area, primary: true),
            Command(Glyph.Window, "Window", "Capture a window (Alt+PrtSc)", _a.Window),
            Command(Glyph.FullScreen, "Full screen", "Capture the screen (Ctrl+PrtSc)", _a.Full),
            Command(Glyph.Text, "Copy text", "Copy the text in an area (Ctrl+Alt+PrtSc)", _a.Text),
        ];
        _bar.Controls.AddRange(_commands);
        _bar.Controls.Add(_verifiedLabel);
        _bar.Controls.Add(_verified);
        _bar.Layout += (_, _) => LayoutBar();
        _verified.Click += (_, _) => { _a.Verified(); _verified.Checked = _a.IsVerified(); };
        _verifiedLabel.Click += (_, _) => { _a.Verified(); _verified.Checked = _a.IsVerified(); };
        new ToolTip { InitialDelay = 400 }.SetToolTip(_verified, "Watermark and sign every capture");

        _gallery.Open += path => _a.Open(path);

        _card.Controls.Add(_gallery);
        _card.Controls.Add(_bar);
        _frame.Controls.Add(_card);
        // The frame fills first, then the nav docks left under the title bar.
        Controls.Add(_frame);
        Controls.Add(_nav);
        Controls.Add(_title);

        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>Refreshes the Verified switch and the captures. Thumbnails fill in off the UI thread.</summary>
    public void Reload()
    {
        // A window left minimized would stay minimized when the tray reopens it.
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        _verified.Checked = _a.IsVerified();
        Refresh(force: true);
    }

    private void Refresh(bool force)
    {
        Directory.CreateDirectory(Settings.ScreenshotDir);
        var files = Thumbs.List(Settings.ScreenshotDir);
        if (!force && files.Count == _files.Count && files.Zip(_files).All(p => p.First.FullName == p.Second.FullName && p.First.LastWriteTimeUtc == p.Second.LastWriteTimeUtc))
            return;
        _files = files;
        _nav.SetCount(0, files.Count);
        _nav.SetCount(1, files.Count(f => Thumbs.IsSealed(f.Name)));
        Apply();
    }

    /// <summary>Shows the current view, narrowed by the search box.</summary>
    private void Apply() => _gallery.SetFiles(_files, _query.Text.Trim());

    /// <summary>Picks up captures taken while the window was in the background.</summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (IsHandleCreated) Refresh(force: false);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Fit();
        // Sized after the DPI is known, so it fits the screen it opens on.
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(LogicalToDeviceUnits(1180), work.Width * 9 / 10), Math.Min(LogicalToDeviceUnits(760), work.Height * 9 / 10));
        Location = new Point(work.X + (work.Width - Width) / 2, work.Y + (work.Height - Height) / 2);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Fit();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        bool max = WindowState == FormWindowState.Maximized;
        _max.Glyph = max ? Glyph.Restore : Glyph.Maximize;
        _max.AccessibleName = max ? "Restore" : "Maximize";
        _max.Invalidate();
    }

    /// <summary>The layout is written at 96 DPI; fonts follow the screen on their own, sizes follow it here.</summary>
    private void Fit()
    {
        int D(int v) => LogicalToDeviceUnits(v);
        MinimumSize = new Size(D(760), D(480));
        _title.Height = D(TitleH);
        _nav.Width = D(NavW);
        _frame.Padding = new Padding(0, 0, D(8), D(8));
        _card.Padding = new Padding(D(4));
        _bar.Height = D(BarH);
        _logo.Size = new Size(D(20), D(20));
        _search.Height = D(32);
        foreach (var b in new[] { _min, _max, _close }) b.Size = new Size(D(CaptionW), D(TitleH) - D(8));
        foreach (var c in _commands) c.FitTo(this);
        _verified.Size = new Size(D(40), D(20));
        LayoutTitle();
        LayoutBar();
    }

    private void LayoutTitle()
    {
        int D(int v) => LogicalToDeviceUnits(v);
        int h = _title.Height;
        _logo.Location = new Point(D(16), (h - _logo.Height) / 2);
        _appName.Location = new Point(_logo.Right + D(10), (h - _appName.Height) / 2);
        _close.Location = new Point(_title.Width - _close.Width, 0);
        _max.Location = new Point(_close.Left - _max.Width, 0);
        _min.Location = new Point(_max.Left - _min.Width, 0);
        // Centred on the window when there's room, squeezed between the name and the buttons when not.
        int room = _min.Left - _appName.Right - D(48);
        _search.Width = Math.Max(D(160), Math.Min(D(SearchW), room));
        int x = Math.Clamp((_title.Width - _search.Width) / 2, _appName.Right + D(24), Math.Max(_appName.Right + D(24), _min.Left - D(24) - _search.Width));
        _search.Location = new Point(x, (h - _search.Height) / 2 - D(4) + D(4));
    }

    private void LayoutBar()
    {
        int D(int v) => LogicalToDeviceUnits(v);
        int h = _bar.Height, x = D(16);
        foreach (var c in _commands)
        {
            c.Location = new Point(x, (h - c.Height) / 2);
            x = c.Right + D(c.Primary ? 12 : 4);
        }
        _verified.Location = new Point(_bar.Width - D(20) - _verified.Width, (h - _verified.Height) / 2);
        _verifiedLabel.Location = new Point(_verified.Left - D(10) - _verifiedLabel.Width, (h - _verifiedLabel.Height) / 2);
    }

    protected override bool ProcessCmdKey(ref Message msg, System.Windows.Forms.Keys keyData)
    {
        if (keyData == (System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.F)) { _query.Focus(); _query.SelectAll(); return true; }
        if (keyData == System.Windows.Forms.Keys.Escape && _query.Text.Length > 0) { _query.Clear(); _gallery.Focus(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static IconButton Caption(string glyph, string tip) =>
        new(glyph, tip) { GlyphFont = Glyph.Small, Rounding = 0, Margin = Padding.Empty, Cursor = Cursors.Default };

    private ToolButton Command(string glyph, string text, string tip, Action onClick, bool primary = false)
    {
        var b = new ToolButton(glyph, text, tip) { Primary = primary };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>The white content surface: rounded, with a hairline edge, on the grey backdrop.</summary>
    private sealed class RoundCard : Panel
    {
        public RoundCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Shape.Backdrop(this, Backdrop));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Shape.Round(new Rectangle(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(Ui.Radius));
            using var fill = new SolidBrush(Ui.Surface);
            using var line = new Pen(Ui.Hairline);
            g.FillPath(fill, path);
            g.DrawPath(line, path);
        }
    }
}

/// <summary>
/// The Fluent navigation pane: icon and label rows, a count on the right, and a short brand-blue bar on
/// the selected one. Rows added with top false sit at the bottom and act as plain commands.
/// </summary>
internal sealed class NavPane : Control
{
    private const int RowH = 40, Side = 8;
    private readonly List<(string Glyph, string Text, bool Top, int? Count)> _items = [];
    private int _selected, _hot = -1, _down = -1;

    public event Action<int>? Picked;

    public NavPane()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Ui.Body;
    }

    public void Add(string glyph, string text, bool top) => _items.Add((glyph, text, top, null));

    public void SetCount(int i, int count)
    {
        _items[i] = _items[i] with { Count = count };
        Invalidate();
    }

    private Rectangle RowRect(int i)
    {
        int h = LogicalToDeviceUnits(RowH), s = LogicalToDeviceUnits(Side), gap = LogicalToDeviceUnits(2);
        int topIndex = _items.Take(i).Count(t => t.Top == _items[i].Top);
        if (_items[i].Top) return new Rectangle(s, LogicalToDeviceUnits(4) + topIndex * (h + gap), Width - 2 * s, h);
        int below = _items.Skip(i + 1).Count(t => !t.Top);
        return new Rectangle(s, Height - LogicalToDeviceUnits(12) - (below + 1) * h - below * gap, Width - 2 * s, h);
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _items.Count; i++) if (RowRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int r = LogicalToDeviceUnits(4);
        for (int i = 0; i < _items.Count; i++)
        {
            var (glyph, text, top, count) = _items[i];
            var row = RowRect(i);
            bool selected = top && i == _selected;
            int alpha = i == _down ? 25 : selected ? 14 : i == _hot ? 10 : 0;
            if (alpha > 0)
            {
                using var path = Shape.Round(row, r);
                using var b = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
                g.FillPath(b, path);
            }
            if (selected)
            {
                int bh = LogicalToDeviceUnits(16), bw = LogicalToDeviceUnits(3);
                using var bar = Shape.Round(new Rectangle(row.X, row.Y + (row.Height - bh) / 2, bw, bh), Math.Max(1, bw / 2));
                using var brand = new SolidBrush(Ui.Brand);
                g.FillPath(brand, bar);
            }
            int box = LogicalToDeviceUnits(20), x = row.X + LogicalToDeviceUnits(12);
            Glyph.Draw(g, glyph, Glyph.Small, new Rectangle(x, row.Y + (row.Height - box) / 2, box, box), selected ? Ui.Brand : Ui.Ink);
            x += box + LogicalToDeviceUnits(12);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            int countW = 0;
            if (count is int n)
            {
                string c = n.ToString("N0", CultureInfo.CurrentCulture);
                countW = TextRenderer.MeasureText(c, Ui.Small).Width;
                TextRenderer.DrawText(g, c, Ui.Small, new Rectangle(row.Right - LogicalToDeviceUnits(12) - countW, row.Y, countW, row.Height), Ui.Muted, flags);
            }
            TextRenderer.DrawText(g, text, selected ? Ui.Strong : Font, new Rectangle(x, row.Y, row.Right - x - countW - LogicalToDeviceUnits(16), row.Height), Ui.Ink, flags);
        }
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
        _hot = _down = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _down = HitTest(e.Location); Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = HitTest(e.Location);
        bool click = i >= 0 && i == _down;
        _down = -1;
        if (click && _items[i].Top) _selected = i;
        Invalidate();
        if (click) Picked?.Invoke(i);
    }
}

/// <summary>
/// Every capture as a tile, grouped by date, with a thin Fluent scrollbar. Only the tiles in view are
/// decoded, three at a time off the UI thread, so a folder of thousands opens as fast as one of ten.
/// </summary>
internal sealed class Gallery : Control
{
    private const int Pad = 24, MinTile = 200, Gap = 8, Inset = 8, CacheMax = 240;
    private static readonly Font EmptyGlyph = new(Glyph.Family, 32f);

    private abstract record Item(Rectangle Bounds);
    private sealed record Header(Rectangle Bounds, string Text, Font Font, string? Note) : Item(Bounds);
    private sealed record TileItem(Rectangle Bounds, FileInfo File, bool Recent) : Item(Bounds);

    private List<FileInfo> _all = [];
    private List<FileInfo> _shown = [];
    private string _query = "";
    private readonly List<Item> _items = [];
    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _visible = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(3);
    private readonly System.Windows.Forms.Timer _glide = new() { Interval = 15 };
    private int _height, _hot = -1, _down = -1;
    private float _y, _target;
    private bool _barHot, _dragging;
    private int _dragFrom;
    private float _dragStart;

    public event Action<string>? Open;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Sealed { get; set; }

    public Gallery()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _glide.Tick += (_, _) =>
        {
            float step = (_target - _y) * 0.3f;
            if (Math.Abs(step) < 0.5f) { _y = _target; _glide.Stop(); }
            else _y += step;
            Invalidate();
        };
    }

    public void SetFiles(List<FileInfo> files, string query)
    {
        bool changedView = query != _query;
        _all = files;
        _query = query;
        _shown = files.Where(f => (!Sealed || Thumbs.IsSealed(f.Name))
            && (query.Length == 0 || Path.GetFileNameWithoutExtension(f.Name).Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        if (changedView) _y = _target = 0;
        Relayout();
    }

    /// <summary>Places the title, the date headers and the tiles in content coordinates.</summary>
    private void Relayout()
    {
        _items.Clear();
        int D(int v) => LogicalToDeviceUnits(v);
        int pad = D(Pad), gap = D(Gap), inset = D(Inset);
        int inner = Math.Max(D(MinTile), Width - 2 * pad);
        int cols = Math.Max(1, (inner + gap) / (D(MinTile) + gap));
        int cw = (inner - (cols - 1) * gap) / cols;
        int th = (cw - 2 * inset) * 10 / 16;
        int ch = inset + th + D(10) + D(20) + D(18) + inset;

        string title = _query.Length > 0 ? $"Results for “{_query}”" : Sealed ? "Locked" : "All captures";
        string note = _shown.Count == 1 ? "1 capture" : $"{_shown.Count.ToString("N0", CultureInfo.CurrentCulture)} captures";
        int y = D(8);
        _items.Add(new Header(new Rectangle(pad, y, inner, D(36)), title, Ui.Heading, note));
        y += D(36) + D(4);

        string? group = null;
        int col = 0;
        foreach (var f in _shown)
        {
            var (name, recent) = Group(f.LastWriteTime);
            if (name != group)
            {
                if (col > 0) y += ch + gap;
                col = 0;
                y += D(12);
                _items.Add(new Header(new Rectangle(pad + inset, y, inner, D(28)), name, Ui.Title, null));
                y += D(28) + D(4);
                group = name;
            }
            _items.Add(new TileItem(new Rectangle(pad + col * (cw + gap), y, cw, ch), f, recent));
            if (++col == cols) { col = 0; y += ch + gap; }
        }
        if (col > 0) y += ch + gap;
        _height = y + pad;
        _y = _target = Math.Clamp(_target, 0, MaxScroll);
        _hot = -1;
        Invalidate();
    }

    /// <summary>Today, Yesterday, This week, This month, then the month and year.</summary>
    private static (string Name, bool Recent) Group(DateTime t)
    {
        var today = DateTime.Today;
        if (t.Date == today) return ("Today", true);
        if (t.Date == today.AddDays(-1)) return ("Yesterday", true);
        if (t.Date > today.AddDays(-7)) return ("This week", false);
        if (t.Year == today.Year && t.Month == today.Month) return ("This month", false);
        return (t.ToString("MMMM yyyy", CultureInfo.CurrentCulture), false);
    }

    private int MaxScroll => Math.Max(0, _height - Height);

    private Rectangle Thumb(Rectangle cell)
    {
        int inset = LogicalToDeviceUnits(Inset);
        int w = cell.Width - 2 * inset;
        return new Rectangle(cell.X + inset, cell.Y + inset, w, w * 10 / 16);
    }

    private Rectangle Track => new(Width - LogicalToDeviceUnits(12), LogicalToDeviceUnits(4), LogicalToDeviceUnits(12), Height - LogicalToDeviceUnits(8));

    private Rectangle BarThumb()
    {
        var t = Track;
        int len = Math.Max(LogicalToDeviceUnits(40), (int)((long)t.Height * Height / Math.Max(1, _height)));
        int top = t.Y + (int)((t.Height - len) * (MaxScroll == 0 ? 0 : _y / MaxScroll));
        int w = LogicalToDeviceUnits(_barHot || _dragging ? 6 : 3);
        return new Rectangle(t.Right - LogicalToDeviceUnits(4) - w, top, w, len);
    }

    private int HitTest(Point p)
    {
        var c = new Point(p.X, p.Y + (int)_y);
        for (int i = 0; i < _items.Count; i++)
            if (_items[i] is TileItem t && t.Bounds.Contains(c)) return i;
        return -1;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Relayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_shown.Count == 0) { PaintEmpty(g); return; }

        int D(int v) => LogicalToDeviceUnits(v);
        int top = (int)_y;
        var flags = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        _visible.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            var b = _items[i].Bounds;
            b.Offset(0, -top);
            if (b.Bottom < 0) continue;
            if (b.Top > Height) break;

            if (_items[i] is Header h)
            {
                TextRenderer.DrawText(g, h.Text, h.Font, b, Ui.Ink, flags | TextFormatFlags.VerticalCenter);
                if (h.Note is not null)
                {
                    int w = TextRenderer.MeasureText(h.Text, h.Font).Width;
                    TextRenderer.DrawText(g, h.Note, Ui.Body, new Rectangle(b.X + w + D(12), b.Y + D(3), b.Width - w, b.Height), Ui.Muted, flags | TextFormatFlags.VerticalCenter);
                }
                continue;
            }

            var tile = (TileItem)_items[i];
            int alpha = i == _down ? 18 : i == _hot ? 9 : 0;
            if (alpha > 0)
            {
                using var cellPath = Shape.Round(b, D(Ui.Radius));
                using var veil = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
                g.FillPath(veil, cellPath);
            }

            var thumb = Thumb(b);
            string path = tile.File.FullName;
            using var shape = Shape.Round(thumb, D(6));
            if (Thumbs.IsSealed(path))
            {
                using var fill = new SolidBrush(Color.FromArgb(240, 240, 240));
                g.FillPath(fill, shape);
                Glyph.Draw(g, Glyph.Lock, Glyph.Large, new Rectangle(thumb.X, thumb.Y + thumb.Height / 2 - D(30), thumb.Width, D(32)), Ui.Muted);
                TextRenderer.DrawText(g, "Locked", Ui.Small, new Rectangle(thumb.X, thumb.Y + thumb.Height / 2 + D(6), thumb.Width, D(20)), Ui.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            else
            {
                string key = Key(tile.File);
                _visible.Add(key);
                if (_cache.TryGetValue(key, out var bmp) && bmp is not null)
                {
                    g.SetClip(shape);
                    Thumbs.Cover(g, bmp, thumb);
                    g.ResetClip();
                }
                else
                {
                    using var fill = new SolidBrush(Color.FromArgb(243, 243, 243));
                    g.FillPath(fill, shape);
                    if (!_cache.ContainsKey(key) && !_pending.Contains(key)) _ = LoadAsync(key, path);
                }
            }
            // A hairline keeps white screenshots from melting into the white card.
            using (var line = new Pen(Color.FromArgb(20, 0, 0, 0))) g.DrawPath(line, shape);

            var when = tile.File.LastWriteTime;
            string stamp = tile.Recent ? when.ToString("t", CultureInfo.CurrentCulture) : when.ToString("d MMM yyyy, t", CultureInfo.CurrentCulture);
            int tx = thumb.X + D(2), tw = thumb.Width - D(4);
            TextRenderer.DrawText(g, Path.GetFileNameWithoutExtension(tile.File.Name), Ui.Body, new Rectangle(tx, thumb.Bottom + D(10), tw, D(20)), Ui.Ink, flags);
            TextRenderer.DrawText(g, stamp, Ui.Small, new Rectangle(tx, thumb.Bottom + D(30), tw, D(18)), Ui.Muted, flags);
        }

        if (MaxScroll > 0)
        {
            using var bar = Shape.Round(BarThumb(), Math.Max(1, BarThumb().Width / 2));
            using var brush = new SolidBrush(Color.FromArgb(_barHot || _dragging ? 130 : 90, 0, 0, 0));
            g.FillPath(brush, bar);
        }
    }

    private void PaintEmpty(Graphics g)
    {
        int D(int v) => LogicalToDeviceUnits(v);
        (string glyph, string head, string hint) = _query.Length > 0
            ? (Glyph.Search, $"No captures match “{_query}”", "Try part of a file name, such as a date.")
            : Sealed
                ? (Glyph.Lock, "No locked captures", "Open a capture and choose Lock to protect it with a password.")
                : (Glyph.Photo, "No captures yet", "Press Print Screen to capture an area. Captures show up here.");
        int cy = Height / 2 - D(40);
        var center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        Glyph.Draw(g, glyph, EmptyGlyph, new Rectangle(0, cy - D(56), Width, D(56)), Ui.Brand);
        TextRenderer.DrawText(g, head, Ui.Title, new Rectangle(0, cy + D(8), Width, D(26)), Ui.Ink, center);
        TextRenderer.DrawText(g, hint, Ui.Body, new Rectangle(0, cy + D(38), Width, D(22)), Ui.Muted, center);
    }

    private static string Key(FileInfo f) => f.FullName + "|" + f.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);

    private async Task LoadAsync(string key, string path)
    {
        _pending.Add(key);
        Bitmap? bmp;
        await _gate.WaitAsync();
        try
        {
            // Scrolled past while waiting: skip it, it loads again when it comes back into view.
            if (!_visible.Contains(key)) { _pending.Remove(key); return; }
            bmp = await Task.Run(() => Thumbs.Make(path, 200, 125));
        }
        finally { _gate.Release(); }
        _pending.Remove(key);
        if (IsDisposed) { bmp?.Dispose(); return; }
        if (_cache.TryGetValue(key, out var old)) old?.Dispose();
        _cache[key] = bmp;
        if (_cache.Count > CacheMax)
            foreach (var k in _cache.Keys.Where(k => !_visible.Contains(k)).Take(_cache.Count - CacheMax).ToList())
            {
                _cache[k]?.Dispose();
                _cache.Remove(k);
            }
        Invalidate();
    }

    private void ScrollTo(float y, bool glide)
    {
        _target = Math.Clamp(y, 0, MaxScroll);
        if (glide) _glide.Start();
        else { _y = _target; _glide.Stop(); Invalidate(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollTo(_target - e.Delta * LogicalToDeviceUnits(120) / 120f, glide: true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            var t = Track;
            float span = Math.Max(1, t.Height - BarThumb().Height);
            ScrollTo(_dragStart + (e.Y - _dragFrom) * MaxScroll / span, glide: false);
            return;
        }
        bool barHot = MaxScroll > 0 && Track.Contains(e.Location);
        int hot = barHot ? -1 : HitTest(e.Location);
        Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
        if (hot != _hot || barHot != _barHot) { _hot = hot; _barHot = barHot; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging) return;
        _hot = _down = -1;
        _barHot = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        if (MaxScroll > 0 && Track.Contains(e.Location))
        {
            var thumb = BarThumb();
            // A click on the track jumps a page, a press on the thumb drags it.
            if (thumb.Contains(e.Location)) { _dragging = true; _dragFrom = e.Y; _dragStart = _y; Capture = true; }
            else ScrollTo(_target + (e.Y < thumb.Top ? -Height : Height) * 0.9f, glide: true);
            Invalidate();
            return;
        }
        _down = HitTest(e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging) { _dragging = false; Capture = false; Invalidate(); return; }
        int i = HitTest(e.Location);
        bool click = i >= 0 && i == _down;
        _down = -1;
        Invalidate();
        if (click && _items[i] is TileItem t) Open?.Invoke(t.File.FullName);
    }

    protected override bool IsInputKey(System.Windows.Forms.Keys keyData) =>
        keyData is System.Windows.Forms.Keys.Up or System.Windows.Forms.Keys.Down or System.Windows.Forms.Keys.PageUp
            or System.Windows.Forms.Keys.PageDown or System.Windows.Forms.Keys.Home or System.Windows.Forms.Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        float line = LogicalToDeviceUnits(60);
        switch (e.KeyCode)
        {
            case System.Windows.Forms.Keys.Up: ScrollTo(_target - line, true); break;
            case System.Windows.Forms.Keys.Down: ScrollTo(_target + line, true); break;
            case System.Windows.Forms.Keys.PageUp: ScrollTo(_target - Height * 0.9f, true); break;
            case System.Windows.Forms.Keys.PageDown: ScrollTo(_target + Height * 0.9f, true); break;
            case System.Windows.Forms.Keys.Home: ScrollTo(0, true); break;
            case System.Windows.Forms.Keys.End: ScrollTo(MaxScroll, true); break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var b in _cache.Values) b?.Dispose();
            _gate.Dispose();
            _glide.Dispose();
        }
        base.Dispose(disposing);
    }
}
