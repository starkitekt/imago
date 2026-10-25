using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Imago;

/// <summary>Fluent system icons, drawn from the icon font that ships with Windows.</summary>
internal static class Glyph
{
    /// <summary>Windows 11 has Segoe Fluent Icons. Windows 10 has Segoe MDL2 Assets, which uses the same code points.</summary>
    public static readonly FontFamily Family =
        FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe Fluent Icons")
        ?? FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe MDL2 Assets")
        ?? FontFamily.GenericSansSerif;

    /// <summary>16px and 20px icons, the two sizes Fluent uses in buttons. 15pt is 20px at 96 DPI.</summary>
    public static readonly Font Small = new(Family, 12f);
    public static readonly Font Regular = new(Family, 15f);
    public static readonly Font Large = new(Family, 18f);

    public const string Dismiss = "", ChevronLeft = "", ChevronRight = "",
        ZoomIn = "", ZoomOut = "", Fit = "", Copy = "", FolderOpen = "",
        Lock = "", Download = "", Info = "", Snip = "", Window = "",
        FullScreen = "", Text = "", Settings = "", Photo = "", Shield = "",
        Check = "", Error = "", Warning = "", Minimize = "", Maximize = "",
        Restore = "", Close = "", Search = "";

    public static void Draw(Graphics g, string glyph, Font font, Rectangle r, Color color) =>
        TextRenderer.DrawText(g, glyph, font, r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
}

/// <summary>Fluent 2 status colors, used by badges and the verification mark.</summary>
internal static class Status
{
    public static readonly Color Success = Color.FromArgb(16, 124, 16);
    public static readonly Color Danger = Color.FromArgb(197, 15, 31);
    public static readonly Color Warning = Color.FromArgb(188, 75, 9);
    public static readonly Color Neutral = Color.FromArgb(97, 97, 97);
}

/// <summary>
/// A square, borderless button that shows one icon: the Fluent "subtle" button. On a dark surface it uses
/// light ink and a translucent hover.
/// </summary>
internal sealed class IconButton : Button
{
    private static readonly ToolTip Tips = new() { InitialDelay = 400 };
    private bool _hot, _down;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Glyph { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Dark { get; set; }

    /// <summary>Used by caption buttons: Close turns red on hover, as in every Windows app.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color? HotFill { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Font GlyphFont { get; set; } = Imago.Glyph.Regular;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Rounding { get; set; } = 4;

    public IconButton(string glyph, string tip)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Glyph = glyph;
        AccessibleName = tip;
        Size = new Size(32, 32);
        Margin = new Padding(0, 0, 8, 0);
        Cursor = Cursors.Hand;
        TabStop = false;
        Tips.SetToolTip(this, tip);
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Shape.Backdrop(this, Ui.Surface));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color ink = !Enabled ? (Dark ? Color.FromArgb(110, 110, 110) : Color.FromArgb(189, 189, 189)) : Dark ? Color.White : Ui.Ink;
        Color? fill = null;
        if (Enabled && (_hot || _down))
        {
            if (HotFill is Color hot) { fill = _down ? ControlPaint.Dark(hot, 0.1f) : hot; ink = Color.White; }
            else if (Dark) fill = Color.FromArgb(_down ? 46 : 26, 255, 255, 255);
            // Translucent, so the hover shows on white and on the grey window backdrop alike.
            else fill = Color.FromArgb(_down ? 25 : 10, 0, 0, 0);
        }
        if (fill is Color c)
        {
            using var b = new SolidBrush(c);
            if (Rounding == 0) g.FillRectangle(b, ClientRectangle);
            else
            {
                using var path = Shape.Round(new Rectangle(0, 0, Width - 1, Height - 1), Rounding);
                g.FillPath(b, path);
            }
        }
        Imago.Glyph.Draw(g, Glyph, GlyphFont, ClientRectangle, ink);
    }
}

/// <summary>
/// A command bar button: an icon and a label, as in the Microsoft 365 ribbon-less toolbars. Subtle by
/// default; Primary fills it with the brand blue for the main action.
/// </summary>
internal sealed class ToolButton : Control
{
    private static readonly ToolTip Tips = new() { InitialDelay = 400 };
    private readonly string _glyph;
    private bool _hot, _down;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; set; }

    public ToolButton(string glyph, string text, string tip)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _glyph = glyph;
        Text = text;
        Font = Ui.Body;
        AccessibleName = text;
        AccessibleRole = AccessibleRole.PushButton;
        Cursor = Cursors.Hand;
        Tips.SetToolTip(this, tip);
    }

    /// <summary>Sizes the button to its label. Takes the window's scale, since a child's own DPI can lag behind.</summary>
    public void FitTo(Control scale) =>
        Size = new Size(scale.LogicalToDeviceUnits(Primary ? 48 : 40) + TextRenderer.MeasureText(Text, Font).Width, scale.LogicalToDeviceUnits(32));

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Shape.Backdrop(this, Ui.Surface));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color ink = Primary ? Color.White : Ui.Ink, icon = Primary ? Color.White : Ui.Brand;
        Color? fill = Primary
            ? (_down ? Color.FromArgb(17, 94, 163) : _hot ? Color.FromArgb(17, 94, 163) : Ui.Brand)
            : _down ? Color.FromArgb(25, 0, 0, 0) : _hot ? Color.FromArgb(10, 0, 0, 0) : null;
        if (fill is Color c)
        {
            using var b = new SolidBrush(c);
            using var path = Shape.Round(new Rectangle(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(4));
            g.FillPath(b, path);
        }
        int pad = LogicalToDeviceUnits(Primary ? 12 : 8), box = LogicalToDeviceUnits(20);
        Glyph.Draw(g, _glyph, Glyph.Small, new Rectangle(pad, (Height - box) / 2, box, box), icon);
        int x = pad + box + LogicalToDeviceUnits(6);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}

/// <summary>A small rounded badge with an icon and a label, such as "Stamped". Filled on dark surfaces, tinted on light ones.</summary>
internal sealed class Badge : Control
{
    private string _glyph = "";
    private Color _tone = Status.Neutral;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Filled { get; set; }

    public Badge()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Ui.Small;
        Height = 24;
    }

    public void Set(string glyph, string text, Color tone)
    {
        _glyph = glyph;
        _tone = tone;
        Text = text;
        Width = TextRenderer.MeasureText(text, Font).Width + (glyph.Length > 0 ? 34 : 18);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Shape.Backdrop(this, Ui.Surface));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Shape.Round(new Rectangle(0, 0, Width - 1, Height - 1), (Height - 1) / 2);
        Color ink;
        if (Filled)
        {
            using var b = new SolidBrush(_tone);
            g.FillPath(b, path);
            ink = Color.White;
        }
        else
        {
            using var b = new SolidBrush(Color.FromArgb(24, _tone));
            using var p = new Pen(Color.FromArgb(90, _tone));
            g.FillPath(b, path);
            g.DrawPath(p, path);
            ink = _tone;
        }
        int x = 9;
        if (_glyph.Length > 0)
        {
            Glyph.Draw(g, _glyph, Glyph.Small, new Rectangle(x, 0, 16, Height), ink);
            x += 20;
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x - 6, Height), ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// The Fluent 2 task dialog: no Windows title bar, a header with the app icon, the app name and what the
/// dialog is for, a close button, the content, and the actions bottom-right. It has a soft shadow and,
/// on Windows 11, rounded corners. Drag the header to move it.
/// </summary>
internal class TaskDialog : Form
{
    private const int CsDropShadow = 0x00020000, WmNcLButtonDown = 0x00A1, HtCaption = 2;
    private const int DwmwaWindowCornerPreference = 33, DwmwcpRound = 2;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static readonly Font AppName = Ui.Caption;

    /// <summary>Where subclasses add their content, under the header.</summary>
    private readonly FlowLayoutPanel _page;

    protected readonly FlowLayoutPanel Body = new()
    {
        FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty,
    };

    protected TaskDialog(string action)
    {
        Text = action;
        Font = Ui.Body;
        BackColor = Ui.Surface;
        Icon = Ui.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(28, 24, 28, 28);
        KeyPreview = true;

        var logo = new PictureBox { Size = new Size(32, 32), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 12, 0) };
        using (var ico = new Icon(Ui.AppIcon, 32, 32)) logo.Image = ico.ToBitmap();
        var names = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        var app = Ui.Text("Imago", AppName);
        app.Margin = Padding.Empty;
        var what = Ui.Text(action, Ui.Small, Ui.Muted);
        what.Margin = Padding.Empty;
        names.Controls.Add(app);
        names.Controls.Add(what);

        var close = new IconButton(Glyph.Dismiss, "Close") { Margin = Padding.Empty, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        close.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var header = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 16) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(names, 1, 0);
        header.Controls.Add(close, 2, 0);
        foreach (Control c in new Control[] { header, logo, names, app, what }) c.MouseDown += DragMove;

        var page = _page = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent,
            Location = new Point(Padding.Left, Padding.Top), Margin = Padding.Empty,
        };
        page.Controls.Add(header);
        page.Controls.Add(Body);
        // The header stretches to the content's width once both are laid out.
        page.Layout += (_, _) => header.Width = Math.Max(Body.Width, header.GetPreferredSize(Size.Empty).Width);
        Controls.Add(page);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = DwmwcpRound;
        _ = DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref round, sizeof(int));
    }

    /// <summary>
    /// Sizes are written at 96 DPI. Fonts already follow the display, so scale everything else once,
    /// after the subclass has added its content, or text outgrows its boxes at 125% and up.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        float f = DeviceDpi / 96f;
        if (f != 1f)
        {
            Scale(new SizeF(f, f));
            Relayout(this);
        }
        // Fit the window to the content exactly; AutoSize leaves the pre-scale size behind.
        AutoSize = false;
        _page.Size = _page.GetPreferredSize(Size.Empty);
        ClientSize = new Size(_page.Right + Padding.Right, _page.Bottom + Padding.Bottom);
        base.OnLoad(e);
    }

    /// <summary>
    /// Lays out children first, so wrapped labels and auto-sized panels measure at the new size. Panels
    /// auto-size as grow-only by default, so they are snapped back to what their content needs.
    /// </summary>
    private static void Relayout(Control c)
    {
        foreach (Control child in c.Controls) Relayout(child);
        c.PerformLayout();
        if (c.AutoSize && c is not Form) c.Size = c.GetPreferredSize(Size.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var line = new Pen(Ui.Stroke);
        e.Graphics.DrawRectangle(line, 0, 0, Width - 1, Height - 1);
    }

    private void DragMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    /// <summary>The footer: primary then secondary, aligned right.</summary>
    protected FlowLayoutPanel Footer(string primary, string? secondary = "Cancel", int width = Ui.ColW)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = false, Size = new Size(width, 32), BackColor = Color.Transparent, Margin = new Padding(0, 12, 0, 0) };
        var ok = new Pill { Text = primary, Primary = true, DialogResult = DialogResult.OK, Size = Pill.Fit(primary), Margin = Padding.Empty };
        AcceptButton = ok;
        CancelButton = ok;
        if (secondary is not null)
        {
            var cancel = new Pill { Text = secondary, DialogResult = DialogResult.Cancel, Size = Pill.Fit(secondary), Margin = new Padding(8, 0, 0, 0) };
            row.Controls.Add(cancel);
            CancelButton = cancel;
        }
        row.Controls.Add(ok);
        return row;
    }
}

/// <summary>
/// A window that draws its own title bar. The sides and bottom keep their frame, so it still resizes by
/// its edges; controls marked as drag areas move it, snap it and maximize it on a double-click, exactly
/// like a Windows title bar.
/// </summary>
internal class FluentWindow : Form
{
    private const int WmNcCalcSize = 0x0083, WmNcHitTest = 0x0084;
    private const int HtTransparent = -1, HtClient = 1, HtCaption = 2, HtTop = 12, HtTopLeft = 13, HtTopRight = 14;
    private const int SmCxSizeFrame = 32, SmCySizeFrame = 33, SmCxPaddedBorder = 92;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const uint SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpFrameChanged = 0x0020;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private readonly List<Control> _dragAreas = [];
    private readonly bool _dark;

    protected FluentWindow(bool dark = false)
    {
        _dark = dark;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
    }

    /// <summary>Makes these controls behave as the title bar. Buttons placed on them still get their clicks.</summary>
    protected void DragArea(params Control[] controls)
    {
        foreach (var c in controls)
        {
            _dragAreas.Add(c);
            // Clicks fall through the control to the window, which answers "title bar".
            if (c.IsHandleCreated) _ = new PassThrough(c);
            else c.HandleCreated += (_, _) => _ = new PassThrough(c);
        }
    }

    public void ToggleMaximize() =>
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_dark)
        {
            int on = 1;
            _ = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
        }
        // Windows only re-runs WM_NCCALCSIZE when the frame changes, so ask for it now.
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcCalcSize && m.WParam != IntPtr.Zero)
        {
            int fx = GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder);
            int fy = GetSystemMetrics(SmCySizeFrame) + GetSystemMetrics(SmCxPaddedBorder);
            // NCCALCSIZE_PARAMS starts with the proposed client rectangle: left, top, right, bottom.
            IntPtr rc = m.LParam;
            Marshal.WriteInt32(rc, 0, Marshal.ReadInt32(rc, 0) + fx);
            Marshal.WriteInt32(rc, 8, Marshal.ReadInt32(rc, 8) - fx);
            Marshal.WriteInt32(rc, 12, Marshal.ReadInt32(rc, 12) - fy);
            // A maximized window hangs its frame off the monitor edge, so the top needs the same inset.
            if (WindowState == FormWindowState.Maximized) Marshal.WriteInt32(rc, 4, Marshal.ReadInt32(rc, 4) + fy);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
        if (m.Msg == WmNcHitTest && (int)m.Result == HtClient)
        {
            var p = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
            var client = PointToClient(p);
            int edge = LogicalToDeviceUnits(5);
            if (WindowState == FormWindowState.Normal && client.Y < edge)
                m.Result = client.X < edge * 2 ? HtTopLeft : client.X >= ClientSize.Width - edge * 2 ? HtTopRight : HtTop;
            else if (_dragAreas.Any(c => c.Visible && c.RectangleToScreen(c.ClientRectangle).Contains(p)))
                m.Result = HtCaption;
        }
    }

    /// <summary>Lets mouse hit-tests pass through a control to the window behind it.</summary>
    private sealed class PassThrough : NativeWindow
    {
        public PassThrough(Control c)
        {
            AssignHandle(c.Handle);
            c.HandleDestroyed += (_, _) => ReleaseHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNcHitTest) { m.Result = HtTransparent; return; }
            base.WndProc(ref m);
        }
    }
}

/// <summary>Small previews of captures, shared by the main window and the viewer's filmstrip.</summary>
internal static class Thumbs
{
    public static bool IsSealed(string path) => path.EndsWith(Seal.Extension, StringComparison.OrdinalIgnoreCase);

    public static bool IsCapture(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || IsSealed(path);

    /// <summary>Captures in a folder, newest first.</summary>
    public static List<FileInfo> List(string dir) =>
        Directory.Exists(dir)
            ? new DirectoryInfo(dir).EnumerateFiles().Where(f => IsCapture(f.Name)).OrderByDescending(f => f.LastWriteTimeUtc).ToList()
            : [];

    /// <summary>A PNG scaled to fit w x h at twice the size, so it stays sharp on high-DPI screens. Null if it can't be read.</summary>
    public static Bitmap? Make(string path, int w, int h)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var src = Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);
            double s = Math.Min(1.0, Math.Min((double)w * 2 / src.Width, (double)h * 2 / src.Height));
            var bmp = new Bitmap(Math.Max(1, (int)(src.Width * s)), Math.Max(1, (int)(src.Height * s)));
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, bmp.Width, bmp.Height);
            return bmp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Draws a bitmap to cover a rectangle, cropping the overflow, like CSS object-fit: cover.</summary>
    public static void Cover(Graphics g, Image img, Rectangle r)
    {
        double s = Math.Max((double)r.Width / img.Width, (double)r.Height / img.Height);
        double sw = r.Width / s, sh = r.Height / s;
        var src = new RectangleF((float)((img.Width - sw) / 2), (float)((img.Height - sh) / 2), (float)sw, (float)sh);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(img, r, src, GraphicsUnit.Pixel);
    }
}
