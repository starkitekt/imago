using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace Imago;

/// <summary>Draws the visible Verified stamp. It becomes part of the signed pixels.</summary>
internal static class Stamp
{
    private static readonly Color Panel = Color.FromArgb(214, 16, 18, 23);
    private static readonly Color Edge = Color.FromArgb(60, 255, 255, 255);
    private static readonly Color Dim = Color.FromArgb(205, 210, 218);

    public static List<string> Lines(StampInfo s)
    {
        var lines = new List<string> { $"Imago Verified  ·  {s.StampId}" };
        lines.Add(s.Place.Length > 0 ? $"{s.Name}  ·  {s.Place}" : s.Name);
        lines.Add(s.Captured.ToString("yyyy-MM-dd  HH:mm:ss", CultureInfo.InvariantCulture) + "  UTC" + Provenance.FormatOffset(s.Captured.Offset));
        if (s.Location is { } g)
            lines.Add($"{Math.Abs(g.Lat).ToString("0.0000", CultureInfo.InvariantCulture)}° {(g.Lat >= 0 ? 'N' : 'S')}, " +
                      $"{Math.Abs(g.Lon).ToString("0.0000", CultureInfo.InvariantCulture)}° {(g.Lon >= 0 ? 'E' : 'W')}  ±{g.AccuracyM:0} m");
        return lines;
    }

    /// <summary>Returns the stamped image: the same bitmap, or a taller one with a band on top if it is too small.</summary>
    public static Bitmap Apply(Bitmap src, StampInfo s)
    {
        var lines = Lines(s);
        bool band = src.Width < 360 || src.Height < 160;
        int u = band ? 12 : Math.Clamp((int)Math.Round(Math.Min(src.Width, src.Height) / 55.0), 11, 26);
        using var bold = new Font(Ui.Family, u, FontStyle.Bold, GraphicsUnit.Pixel);
        using var reg = new Font(Ui.Family, u, FontStyle.Regular, GraphicsUnit.Pixel);
        int lineH = (int)Math.Ceiling(u * 1.42);
        int pad = (int)Math.Ceiling(u * 0.8);
        int icon = (int)Math.Ceiling(u * 1.1);

        int textW;
        using (var mg = Graphics.FromHwnd(IntPtr.Zero))
        {
            mg.TextRenderingHint = TextRenderingHint.AntiAlias;
            textW = lines.Select((l, i) => (int)Math.Ceiling(mg.MeasureString(l, i == 0 ? bold : reg, int.MaxValue, StringFormat.GenericTypographic).Width)).Max();
        }
        int boxW = pad + icon + (int)(u * 0.6) + textW + pad;
        int boxH = pad + lines.Count * lineH + pad - (lineH - u) / 2;

        Bitmap dst;
        Rectangle box;
        if (band)
        {
            int w = Math.Max(src.Width, boxW);
            dst = new Bitmap(w, src.Height + boxH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g0 = Graphics.FromImage(dst);
            g0.Clear(Color.FromArgb(16, 18, 23));
            g0.DrawImageUnscaled(src, 0, boxH);
            box = new Rectangle(0, 0, w, boxH);
        }
        else
        {
            int margin = u;
            boxW = Math.Min(boxW, src.Width - 2 * margin);
            dst = src;
            box = new Rectangle(src.Width - margin - boxW, margin, boxW, boxH);
        }

        using var g = Graphics.FromImage(dst);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;
        if (!band)
        {
            using var path = Rounded(box, u * 0.7f);
            using var fill = new SolidBrush(Panel);
            using var edge = new Pen(Edge, 1);
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }

        // Check badge: a blue line check on a white disc.
        var ib = new RectangleF(box.X + pad, box.Y + pad + (lineH - icon) / 2f - (lineH - u) / 4f, icon, icon);
        using (var gb = new SolidBrush(Color.White)) g.FillEllipse(gb, ib);
        using (var cp = new Pen(Ui.Brand, Math.Max(1.6f, icon / 7f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(cp, [
                new PointF(ib.X + ib.Width * 0.28f, ib.Y + ib.Height * 0.53f),
                new PointF(ib.X + ib.Width * 0.44f, ib.Y + ib.Height * 0.68f),
                new PointF(ib.X + ib.Width * 0.73f, ib.Y + ib.Height * 0.36f),
            ]);

        float tx = ib.Right + u * 0.6f;
        float maxW = box.Right - pad - tx;
        using var white = new SolidBrush(Color.White);
        using var dim = new SolidBrush(Dim);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        for (int i = 0; i < lines.Count; i++)
        {
            var rect = new RectangleF(tx, box.Y + pad + i * lineH - (lineH - u) / 4f, maxW, lineH);
            g.DrawString(lines[i], i == 0 ? bold : reg, i == 0 ? white : dim, rect, fmt);
        }
        return dst;
    }

    private static GraphicsPath Rounded(Rectangle r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
