using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Imago;

/// <summary>Full-screen selection over a frozen screenshot: hover snaps to windows, drag selects an area.</summary>
internal sealed class Overlay : Form
{
    private const int WM_DPICHANGED = 0x02E0;
    private static readonly Color Accent = Color.FromArgb(47, 111, 235);
    private const int LoupeSrc = 15, LoupeZoom = 8, LoupeSize = LoupeSrc * LoupeZoom;

    private readonly Bitmap _shot, _dim;
    private readonly Rectangle _virt;
    private readonly ScWindow[] _wins;
    private readonly string _hint;
    private Point _down, _mouse = new(-1000, -1000);
    private bool _pressed, _dragging;
    private Rectangle _sel, _hover;
    private long _hoverHwnd;
    private readonly List<Rectangle> _painted = [];

    public Rectangle Result { get; private set; }
    public long ResultHwnd { get; private set; }

    public Overlay(Bitmap shot, Rectangle virt, ScWindow[] wins, string hint)
    {
        _shot = shot; _virt = virt; _wins = wins; _hint = hint;
        _dim = new Bitmap(shot);
        using (var g = Graphics.FromImage(_dim))
        using (var b = new SolidBrush(Color.FromArgb(105, 0, 0, 0)))
            g.FillRectangle(b, 0, 0, _dim.Width, _dim.Height);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        Bounds = virt;
        Text = "Imago selection";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED) { m.Result = IntPtr.Zero; return; } // stay exactly on the virtual screen
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _virt;
        Activate();
        Native.SetForegroundWindow(Handle);
        _mouse = PointToClient(Cursor.Position);
        UpdateHover();
        Refresh();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dim.Dispose();
        base.Dispose(disposing);
    }

    // ---------- input ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { Cancel(); return; }
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        _down = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.Location;
        if (_pressed && !_dragging && (Math.Abs(e.X - _down.X) > 3 || Math.Abs(e.Y - _down.Y) > 3)) _dragging = true;
        if (_dragging) _sel = Normalize(_down, e.Location);
        else UpdateHover();
        Repaint();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_pressed) return;
        _pressed = false;
        if (_dragging)
        {
            _sel = Normalize(_down, e.Location);
            if (_sel.Width < 2 || _sel.Height < 2) { _dragging = false; Repaint(); return; }
            var c = new Point(_sel.X + _sel.Width / 2 + _virt.X, _sel.Y + _sel.Height / 2 + _virt.Y);
            Accept(_sel, Imago.Capture.WindowAt(_wins, c));
        }
        else Accept(_hover, _hoverHwnd);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case System.Windows.Forms.Keys.Escape: Cancel(); break;
            case System.Windows.Forms.Keys.Enter:
                if (_dragging) Accept(_sel, 0); else Accept(_hover, _hoverHwnd);
                break;
            case System.Windows.Forms.Keys.Left or System.Windows.Forms.Keys.Right or System.Windows.Forms.Keys.Up or System.Windows.Forms.Keys.Down:
                int step = e.Shift ? 10 : 1;
                var p = Cursor.Position;
                Cursor.Position = e.KeyCode switch
                {
                    System.Windows.Forms.Keys.Left => p with { X = p.X - step },
                    System.Windows.Forms.Keys.Right => p with { X = p.X + step },
                    System.Windows.Forms.Keys.Up => p with { Y = p.Y - step },
                    _ => p with { Y = p.Y + step },
                };
                e.Handled = true;
                break;
        }
    }

    private void Accept(Rectangle clientRect, long hwnd)
    {
        if (clientRect.Width <= 0 || clientRect.Height <= 0) { Cancel(); return; }
        Result = new Rectangle(clientRect.X + _virt.X, clientRect.Y + _virt.Y, clientRect.Width, clientRect.Height);
        ResultHwnd = hwnd;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Cancel()
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void UpdateHover()
    {
        var screen = new Point(_mouse.X + _virt.X, _mouse.Y + _virt.Y);
        var w = _wins.FirstOrDefault(x => x.Rect.Contains(screen));
        Rectangle r;
        if (w.Hwnd != 0) { r = w.Rect; _hoverHwnd = w.Hwnd; }
        else { r = Native.MonitorAt(screen, out _); _hoverHwnd = 0; }
        r.Offset(-_virt.X, -_virt.Y);
        r.Intersect(ClientRectangle);
        _hover = r;
    }

    private static Rectangle Normalize(Point a, Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);

    // ---------- painting ----------

    private Rectangle Active => _dragging ? _sel : _hover;

    private List<Rectangle> Decorations()
    {
        var list = new List<Rectangle>();
        var a = Active;
        if (a.Width > 0)
        {
            var ring = a; ring.Inflate(3, 3);
            list.Add(ring);
            list.Add(LabelRect(a));
        }
        list.Add(LoupeRect());
        list.Add(HintRect());
        return list;
    }

    private void Repaint()
    {
        var now = Decorations();
        using var region = new Region(Rectangle.Empty);
        foreach (var r in _painted) region.Union(r);
        foreach (var r in now) region.Union(r);
        // Moving a hover or selection edge also uncovers the dimmed area it left.
        Invalidate(region);
        _painted.Clear();
        _painted.AddRange(now);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_dim, clip, clip, GraphicsUnit.Pixel);

        var a = Active;
        if (a.Width > 0)
        {
            var bright = Rectangle.Intersect(a, clip);
            if (bright.Width > 0) g.DrawImage(_shot, bright, bright, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;
            using (var pen = new Pen(Accent, 2) { Alignment = PenAlignment.Inset })
                g.DrawRectangle(pen, a);
            DrawLabel(g, a);
        }
        g.CompositingMode = CompositingMode.SourceOver;
        DrawHint(g);
        DrawLoupe(g);
        if (_painted.Count == 0) _painted.AddRange(Decorations());
    }

    private Rectangle LabelRect(Rectangle a)
    {
        var size = new Size(108, 24);
        int y = a.Bottom + 8;
        if (y + size.Height > ClientSize.Height) y = a.Y - size.Height - 8;
        if (y < 0) y = a.Y + 8;
        int x = Math.Clamp(a.X, 0, Math.Max(0, ClientSize.Width - size.Width));
        return new Rectangle(new Point(x, y), size);
    }

    private void DrawLabel(Graphics g, Rectangle a)
    {
        var r = LabelRect(a);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var bg = new SolidBrush(Color.FromArgb(225, 20, 22, 28));
        using var path = Pill(r);
        g.FillPath(bg, path);
        using var f = new Font(Ui.Family, 11, FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, $"{a.Width} × {a.Height}", f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private Rectangle HintRect()
    {
        var mon = Native.MonitorAt(new Point(_mouse.X + _virt.X, _mouse.Y + _virt.Y), out _);
        mon.Offset(-_virt.X, -_virt.Y);
        var size = new Size(Math.Min(460, mon.Width - 20), 30);
        return new Rectangle(mon.X + (mon.Width - size.Width) / 2, mon.Y + 18, size.Width, size.Height);
    }

    private void DrawHint(Graphics g)
    {
        if (_dragging) return;
        var r = HintRect();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bg = new SolidBrush(Color.FromArgb(225, 20, 22, 28));
        using var path = Pill(r);
        g.FillPath(bg, path);
        using var f = new Font(Ui.Family, 12, FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, _hint, f, r, Color.FromArgb(230, 232, 238), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private Rectangle LoupeRect()
    {
        int x = _mouse.X + 24, y = _mouse.Y + 24;
        if (x + LoupeSize + 4 > ClientSize.Width) x = _mouse.X - 24 - LoupeSize;
        if (y + LoupeSize + 28 > ClientSize.Height) y = _mouse.Y - 24 - LoupeSize - 24;
        return new Rectangle(x - 2, y - 2, LoupeSize + 4, LoupeSize + 28);
    }

    private void DrawLoupe(Graphics g)
    {
        if (_mouse.X < 0 || _mouse.Y < 0 || _mouse.X >= _shot.Width || _mouse.Y >= _shot.Height) return;
        var outer = LoupeRect();
        var view = new Rectangle(outer.X + 2, outer.Y + 2, LoupeSize, LoupeSize);
        var src = new Rectangle(_mouse.X - LoupeSrc / 2, _mouse.Y - LoupeSrc / 2, LoupeSrc, LoupeSrc);

        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using (var bg = new SolidBrush(Color.FromArgb(20, 22, 28))) g.FillRectangle(bg, outer);
        g.DrawImage(_shot, view, src, GraphicsUnit.Pixel);
        using (var center = new Pen(Color.FromArgb(230, 255, 255, 255), 1))
            g.DrawRectangle(center, view.X + LoupeSrc / 2 * LoupeZoom, view.Y + LoupeSrc / 2 * LoupeZoom, LoupeZoom - 1, LoupeZoom - 1);
        using (var edge = new Pen(Accent, 2) { Alignment = PenAlignment.Inset }) g.DrawRectangle(edge, view);

        var c = _shot.GetPixel(_mouse.X, _mouse.Y);
        var text = new Rectangle(view.X, view.Bottom + 2, view.Width, 22);
        using var f = new Font(Ui.Family, 11, FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, $"{_mouse.X + _virt.X},{_mouse.Y + _virt.Y}  #{c.R:X2}{c.G:X2}{c.B:X2}", f, text, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static GraphicsPath Pill(Rectangle r)
    {
        int d = r.Height;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }
}
