using System.Drawing.Drawing2D;

namespace Imago;

/// <summary>Rounded outline shared by the custom controls.</summary>
internal static class Shape
{
    public static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The colour behind a control: the first ancestor that isn't transparent.</summary>
    public static Color Backdrop(Control c, Color fallback)
    {
        for (var p = c.Parent; p is not null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return fallback;
    }
}

/// <summary>
/// The Fluent text input: a white box with a thin outline and a darker bottom edge that turns into a
/// brand-blue underline while the box has focus.
/// </summary>
internal sealed class Input : Panel
{
    private readonly TextBox _box;

    public Input(TextBox box, int width)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _box = box;
        BackColor = Ui.Surface;
        Size = new Size(width, 32);
        Margin = new Padding(0, 0, 0, 4);
        Cursor = Cursors.IBeam;
        box.BorderStyle = BorderStyle.None;
        box.BackColor = Ui.Surface;
        box.ForeColor = Ui.Ink;
        box.Font = Ui.Body;
        box.Margin = Padding.Empty;
        box.GotFocus += (_, _) => Invalidate();
        box.LostFocus += (_, _) => Invalidate();
        Click += (_, _) => box.Focus();
        Controls.Add(box);
    }

    /// <summary>An optional icon at the start of the box, such as the magnifier in a search box.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Glyph { get; set; }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int side = Height * 10 / 32, left = Glyph is null ? side : Height;
        _box.SetBounds(left, (Height - _box.Height) / 2, Width - left - side, _box.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Shape.Backdrop(this, Ui.Surface));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Shape.Round(new Rectangle(0, 0, Width - 1, Height - 1), 4);
        using (var fill = new SolidBrush(Ui.Surface)) g.FillPath(fill, path);
        using (var line = new Pen(Ui.Stroke)) g.DrawPath(line, path);
        if (Glyph is not null) Imago.Glyph.Draw(g, Glyph, Imago.Glyph.Small, new Rectangle(0, 0, Height, Height), Ui.Muted);
        g.SetClip(path);
        bool focused = _box.Focused;
        int under = focused ? Math.Max(2, Height / 16) : 1;
        using (var bottom = new SolidBrush(focused ? Ui.Brand : Ui.Muted))
            g.FillRectangle(bottom, 0, Height - under, Width, under);
    }
}

/// <summary>A light grey rounded panel with a hairline border. Groups related controls inside a white dialog.</summary>
internal sealed class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Canvas;
        Padding = new Padding(16);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Shape.Round(new Rectangle(0, 0, Width - 1, Height - 1), Ui.Radius);
        using var fill = new SolidBrush(BackColor);
        using var line = new Pen(Ui.Hairline);
        g.FillPath(fill, path);
        g.DrawPath(line, path);
    }
}

/// <summary>An on/off switch in the Mac style. Space and Enter toggle it too.</summary>
internal sealed class Switch : Control
{
    private bool _checked;

    public event EventHandler? CheckedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Switch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(40, 22);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
    }

    /// <summary>A title, an optional muted hint underneath, and the switch on the right.</summary>
    public static Panel Labeled(string title, string? hint, Switch sw, int width)
    {
        int textW = width - sw.Width - 16;
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
        var head = Ui.Text(title);
        head.MaximumSize = new Size(textW, 0);
        text.Controls.Add(head);
        if (hint is not null)
        {
            var sub = Ui.Text(hint, Ui.Small, Ui.Muted);
            sub.MaximumSize = new Size(textW, 0);
            text.Controls.Add(sub);
        }
        return new Row(text, sw, width);
    }

    /// <summary>Keeps the switch on the right and its height in step with the wrapped text, at any DPI.</summary>
    private sealed class Row : Panel
    {
        private readonly Control _text, _sw;

        public Row(Control text, Control sw, int width)
        {
            _text = text;
            _sw = sw;
            BackColor = Color.Transparent;
            AutoSize = true;
            Width = width;
            Controls.Add(text);
            Controls.Add(sw);
        }

        public override Size GetPreferredSize(Size proposedSize) =>
            new(Width, Math.Max(_text.GetPreferredSize(Size.Empty).Height, _sw.Bottom));

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            _text.Location = Point.Empty;
            _sw.Location = new Point(Width - _sw.Width, _sw.Height / 20);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override bool IsInputKey(System.Windows.Forms.Keys keyData) =>
        keyData is System.Windows.Forms.Keys.Space or System.Windows.Forms.Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is System.Windows.Forms.Keys.Space or System.Windows.Forms.Keys.Enter) Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var track = Shape.Round(new Rectangle(0, 0, Width - 1, Height - 1), (Height - 1) / 2);
        using (var tb = new SolidBrush(Checked ? Ui.Brand : Ui.Stroke))
            g.FillPath(tb, track);
        int k = Height - 4;
        int x = Checked ? Width - k - 2 : 2;
        using var knob = new SolidBrush(Color.White);
        g.FillEllipse(knob, x, 2, k, k);
    }
}

/// <summary>A large capture button: a title with its keyboard shortcut underneath, and a soft hover.</summary>
internal sealed class Tile : Button
{
    private bool _hot, _down;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Shortcut { get; set; } = "";

    public Tile()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Size = new Size(192, 64);
        Margin = new Padding(0, 0, 8, 8);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = _down ? Ui.SurfacePressed : _hot ? Ui.SurfaceHover : Ui.Surface;
        using var path = Shape.Round(new Rectangle(0, 0, Width - 1, Height - 1), Ui.Radius);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        using (var p = new Pen(Ui.Hairline)) g.DrawPath(p, path);
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, Text, Ui.Body, new Rectangle(14, 11, Width - 28, 20), Ui.Ink, flags);
        TextRenderer.DrawText(g, Shortcut, Ui.Small, new Rectangle(14, 33, Width - 28, 18), Ui.Muted, flags);
    }
}

/// <summary>
/// A Microsoft 365 style button. Primary is filled with the brand blue and white text; the rest is white with a thin outline.
/// </summary>
internal sealed class Pill : Button
{
    private bool _hot, _down;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; set; }

    /// <summary>For dark surfaces such as the viewer: a charcoal fill, a grey outline and white text.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Dark { get; set; }

    public Pill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Size = new Size(120, 32);
        Cursor = Cursors.Hand;
        Font = Ui.Body;
    }

    /// <summary>A button sized to its label, with a minimum width so short labels still look like buttons.</summary>
    public static Size Fit(string text) =>
        new(Math.Max(96, TextRenderer.MeasureText(text, Ui.Body).Width + 32), 32);

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Shape.Backdrop(this, Ui.Canvas));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Shape.Round(new Rectangle(0, 0, Width - 1, Height - 1), Math.Min(4, (Height - 1) / 2));
        Color fill, ink, outline = Ui.Stroke;
        if (Primary)
        {
            fill = _down ? Color.FromArgb(12, 59, 94) : _hot ? Color.FromArgb(17, 94, 163) : Ui.Brand;
            ink = Color.White;
        }
        else if (Dark)
        {
            fill = _down ? Color.FromArgb(31, 31, 31) : _hot ? Color.FromArgb(61, 61, 61) : Color.FromArgb(41, 41, 41);
            ink = Color.White;
            outline = Color.FromArgb(102, 102, 102);
        }
        else
        {
            fill = _down ? Ui.SurfacePressed : _hot ? Ui.SurfaceHover : Ui.Surface;
            ink = Ui.Ink;
        }
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (!Primary)
        {
            using var line = new Pen(outline);
            g.DrawPath(line, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}
