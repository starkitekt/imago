using System.Collections.Specialized;
using System.Drawing.Drawing2D;

namespace Imago;

/// <summary>A small preview in the bottom-right corner after each capture. Never takes focus.</summary>
internal sealed class QuickAccess : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    private static QuickAccess? _current;

    private readonly string _path;
    private readonly Bitmap _thumb;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 6000 };
    private Point _down;
    private bool _pressed;

    public static void Show(CaptureResult r)
    {
        _current?.Close();
        _current = new QuickAccess(r);
        _current.Show();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    private QuickAccess(CaptureResult r)
    {
        _path = r.Path;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(24, 26, 32);
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;

        float dpi = DeviceDpi / 96f;
        int w = (int)(260 * dpi);
        double ratio = (double)r.Image.Height / r.Image.Width;
        int th = (int)Math.Clamp(w * ratio, 60 * dpi, 220 * dpi);
        _thumb = new Bitmap(w, th);
        using (var g = Graphics.FromImage(_thumb))
        {
            g.Clear(BackColor);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            double s = Math.Min((double)w / r.Image.Width, (double)th / r.Image.Height);
            int dw = (int)(r.Image.Width * s), dh = (int)(r.Image.Height * s);
            g.DrawImage(r.Image, (w - dw) / 2, (th - dh) / 2, dw, dh);
        }

        int barH = (int)(34 * dpi);
        int pad = (int)(8 * dpi);
        ClientSize = new Size(w + 2 * pad, th + barH + 2 * pad);

        var pic = new PictureBox { Image = _thumb, Bounds = new Rectangle(pad, pad, w, th), Cursor = Cursors.Hand };
        pic.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _pressed = true; _down = e.Location; } };
        pic.MouseMove += (_, e) =>
        {
            if (!_pressed || (Math.Abs(e.X - _down.X) < 5 && Math.Abs(e.Y - _down.Y) < 5)) return;
            _pressed = false;
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { _path });
            DoDragDrop(data, DragDropEffects.Copy);
        };
        pic.MouseUp += (_, e) =>
        {
            if (!_pressed) return;
            _pressed = false;
            Shell(_path);
            Close();
        };
        Controls.Add(pic);

        string caption = r.Stamp is null ? "Saved" : "Stamped · " + r.Stamp.StampId;
        if (r.Warnings.Count > 0) caption = "⚠ " + caption;
        var label = new Label
        {
            Text = caption, ForeColor = r.Warnings.Count > 0 ? Color.FromArgb(255, 200, 90) : Color.FromArgb(220, 224, 232),
            Font = Ui.Small, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(pad, pad + th, w - (int)(150 * dpi), barH),
        };
        if (r.Warnings.Count > 0) new ToolTip().SetToolTip(label, string.Join("\n", r.Warnings));
        Controls.Add(label);

        int bx = pad + w;
        foreach (var (text, act) in new (string, Action)[]
        {
            ("✕", Close),
            ("Lock", () => { Close(); TrayApp.SealWithPrompt(_path); }),
            ("Folder", () => { Shell("explorer.exe", $"/select,\"{_path}\""); Close(); }),
        })
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(44, 47, 56),
                Font = Ui.Small, TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            int bw = (int)((text.Length == 1 ? 28 : 54) * dpi);
            bx -= bw + (int)(4 * dpi);
            b.Bounds = new Rectangle(bx + (int)(4 * dpi), pad + th + (int)(5 * dpi), bw, barH - (int)(10 * dpi));
            b.Click += (_, _) => act();
            Controls.Add(b);
        }

        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Right - Width - (int)(16 * dpi), wa.Bottom - Height - (int)(16 * dpi));

        _timer.Tick += (_, _) =>
        {
            if (Bounds.Contains(Cursor.Position)) return;
            Close();
        };
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(70, 255, 255, 255));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        _thumb.Dispose();
        if (ReferenceEquals(_current, this)) _current = null;
        base.OnFormClosed(e);
    }

    private static void Shell(string file, string? args = null)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true };
            if (args is not null) psi.Arguments = args;
            System.Diagnostics.Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception) { /* no app registered for PNG */ }
    }
}
