using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Imago;

/// <summary>One thing done in the editor, as written to the signed edit log. Box is in final image pixels.</summary>
internal sealed record EditOp(string Tool, Rectangle Box);

/// <summary>
/// Something drawn on a capture in the editor. Marks never change: moving one makes a new one, so undo can
/// keep plain lists. Everything is in image pixels; the editor scales the graphics to show them.
/// </summary>
internal abstract class Mark
{
    public abstract string Tool { get; }
    public abstract Rectangle Bounds { get; }

    /// <summary>Blur and redact cover the capture itself, under every annotation.</summary>
    public virtual bool Privacy => false;

    public abstract Mark Offset(int dx, int dy);

    /// <summary>Draws onto the capture. Source is the capture, for marks that read it. Step is this mark's number among steps.</summary>
    public abstract void Draw(Graphics g, Bitmap source, int step);

    public virtual bool Hit(Point p, int slop) => Rectangle.Inflate(Bounds, slop, slop).Contains(p);

    protected static Rectangle Box(IEnumerable<PointF> pts, float pad)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var p in pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        return Rectangle.FromLTRB((int)Math.Floor(x0 - pad), (int)Math.Floor(y0 - pad), (int)Math.Ceiling(x1 + pad), (int)Math.Ceiling(y1 + pad));
    }
}

/// <summary>A rounded box in an elite black, fully opaque, with a faint inner edge so it reads as deliberate.</summary>
internal sealed class RedactMark(Rectangle r, bool auto = false) : Mark
{
    public static readonly Color Ink = Color.FromArgb(12, 12, 14);
    public override string Tool => auto ? "auto-redact" : "redact";
    public override Rectangle Bounds => r;
    public override bool Privacy => true;
    public bool Auto => auto;
    public override Mark Offset(int dx, int dy) => new RedactMark(new Rectangle(r.X + dx, r.Y + dy, r.Width, r.Height), auto);

    public override void Draw(Graphics g, Bitmap source, int step)
    {
        int radius = Math.Max(1, Math.Min(8, Math.Min(r.Width, r.Height) / 4));
        using var path = Shape.Round(r, radius);
        using (var b = new SolidBrush(Ink)) g.FillPath(b, path);
        if (r.Width < 6 || r.Height < 6) return;
        using var inner = Shape.Round(Rectangle.Inflate(r, -1, -1), Math.Max(1, radius - 1));
        using var edge = new Pen(Color.FromArgb(18, 255, 255, 255), 1f);
        g.DrawPath(edge, inner);
    }
}

/// <summary>
/// A frosted patch: the area is pixelated, then softened, then grained. The pixelation throws the detail
/// away, so no sharpening can bring the text back.
/// </summary>
internal sealed class BlurMark(Rectangle r, int seed) : Mark
{
    private Bitmap? _patch;
    public override string Tool => "blur";
    public override Rectangle Bounds => r;
    public override bool Privacy => true;
    public override Mark Offset(int dx, int dy) => new BlurMark(new Rectangle(r.X + dx, r.Y + dy, r.Width, r.Height), seed);

    public override void Draw(Graphics g, Bitmap source, int step)
    {
        var area = Rectangle.Intersect(r, new Rectangle(Point.Empty, source.Size));
        if (area.Width < 1 || area.Height < 1) return;
        _patch ??= Frost(source, area, seed);
        int radius = Math.Max(1, Math.Min(8, Math.Min(area.Width, area.Height) / 4));
        using var path = Shape.Round(area, radius);
        var state = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        var mode = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_patch, area);
        g.InterpolationMode = mode;
        g.Restore(state);
    }

    public static unsafe Bitmap Frost(Bitmap source, Rectangle area, int seed)
    {
        int w = area.Width, h = area.Height;
        var px = new float[w * h * 3];
        var data = source.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 3; c++) px[(y * w + x) * 3 + c] = row[x * 4 + c];
            }
        }
        finally { source.UnlockBits(data); }

        // Pixelate: blocks big enough that a word becomes two or three cells.
        int block = Math.Clamp(Math.Min(w, h) / 3, 6, 28);
        for (int by = 0; by < h; by += block)
            for (int bx = 0; bx < w; bx += block)
            {
                int x1 = Math.Min(w, bx + block), y1 = Math.Min(h, by + block), n = (x1 - bx) * (y1 - by);
                for (int c = 0; c < 3; c++)
                {
                    float sum = 0;
                    for (int y = by; y < y1; y++) for (int x = bx; x < x1; x++) sum += px[(y * w + x) * 3 + c];
                    float avg = sum / n;
                    for (int y = by; y < y1; y++) for (int x = bx; x < x1; x++) px[(y * w + x) * 3 + c] = avg;
                }
            }

        // Three box passes each way approximate a gaussian and melt the cell edges.
        int radius = Math.Max(2, block / 2);
        var tmp = new float[px.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxPass(px, tmp, w, h, radius, horizontal: true);
            BoxPass(tmp, px, w, h, radius, horizontal: false);
        }

        // Fine monochrome grain, so the patch looks like frosted glass rather than a smear.
        var rng = new Random(seed);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var outData = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)outData.Scan0 + (long)y * outData.Stride;
                for (int x = 0; x < w; x++)
                {
                    float grain = (float)(rng.NextDouble() * 24 - 12);
                    for (int c = 0; c < 3; c++) row[x * 4 + c] = (byte)Math.Clamp(px[(y * w + x) * 3 + c] + grain, 0, 255);
                    row[x * 4 + 3] = 255;
                }
            }
        }
        finally { bmp.UnlockBits(outData); }
        return bmp;
    }

    private static void BoxPass(float[] src, float[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        Parallel.For(0, lines, line =>
        {
            for (int c = 0; c < 3; c++)
            {
                float sum = 0;
                int Idx(int i) => (horizontal ? line * w + Math.Clamp(i, 0, len - 1) : Math.Clamp(i, 0, len - 1) * w + line) * 3 + c;
                for (int i = -r; i <= r; i++) sum += src[Idx(i)];
                for (int i = 0; i < len; i++)
                {
                    dst[(horizontal ? line * w + i : i * w + line) * 3 + c] = sum / (2 * r + 1);
                    sum += src[Idx(i + r + 1)] - src[Idx(i - r)];
                }
            }
        });
    }
}

/// <summary>A freehand line, smoothed. The highlighter is the same line, wide and translucent.</summary>
internal sealed class InkMark(PointF[] pts, Color color, float width, bool highlighter) : Mark
{
    public override string Tool => highlighter ? "highlighter" : "pen";
    public override Rectangle Bounds => Box(pts, width / 2 + 1);
    public PointF[] Points => pts;
    public override Mark Offset(int dx, int dy) => new InkMark([.. pts.Select(p => new PointF(p.X + dx, p.Y + dy))], color, width, highlighter);

    public override void Draw(Graphics g, Bitmap source, int step)
    {
        var c = highlighter ? Color.FromArgb(110, color) : color;
        if (pts.Length == 1)
        {
            using var dot = new SolidBrush(c);
            g.FillEllipse(dot, pts[0].X - width / 2, pts[0].Y - width / 2, width, width);
            return;
        }
        using var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        // One path, one stroke: a translucent highlighter doesn't darken where it crosses itself.
        using var path = new GraphicsPath();
        path.AddCurve(pts, 0.5f);
        g.DrawPath(pen, path);
    }

    public override bool Hit(Point p, int slop)
    {
        float r = width / 2 + slop;
        for (int i = 0; i < pts.Length; i++)
        {
            var a = pts[i];
            var b = pts[Math.Min(i + 1, pts.Length - 1)];
            if (Strokes.DistanceToSegment(p, a, b) <= r) return true;
        }
        return false;
    }
}

/// <summary>An arrow that looks drawn by hand: a slight bow, a stroke that swells toward the tip, uneven barbs.</summary>
internal sealed class ArrowMark(PointF from, PointF to, Color color, float width, int seed) : Mark
{
    public override string Tool => "arrow";
    public PointF From => from;
    public PointF To => to;
    public override Rectangle Bounds => Box([from, to], width * 6);
    public override Mark Offset(int dx, int dy) => new ArrowMark(new PointF(from.X + dx, from.Y + dy), new PointF(to.X + dx, to.Y + dy), color, width, seed);

    public override void Draw(Graphics g, Bitmap source, int step) => Paint(g, from, to, color, width, seed);

    public static void Paint(Graphics g, PointF from, PointF to, Color color, float width, int seed)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 2) return;
        var rng = new Random(seed);
        float ux = dx / len, uy = dy / len, nx = -uy, ny = ux;
        // The bow: a gentle curve to one side, as a wrist draws it.
        float side = rng.Next(2) == 0 ? -1 : 1;
        float bow = len * (0.05f + 0.05f * (float)rng.NextDouble()) * side;
        var ctrl = new PointF((from.X + to.X) / 2 + nx * bow, (from.Y + to.Y) / 2 + ny * bow);
        using var brush = new SolidBrush(color);
        Strokes.Fill(g, brush, from, ctrl, to, width * 0.45f, width * 1.05f, seed);

        // Barbs follow the curve's direction at the tip, not the straight line.
        float tx = to.X - ctrl.X, ty = to.Y - ctrl.Y, tl = MathF.Sqrt(tx * tx + ty * ty);
        tx /= tl; ty /= tl;
        float head = Math.Clamp(len * 0.22f, width * 3.5f, width * 8f);
        for (int k = -1; k <= 1; k += 2)
        {
            float angle = (26f + 6f * (float)rng.NextDouble()) * MathF.PI / 180f * k;
            float l = head * (0.88f + 0.24f * (float)rng.NextDouble());
            // The tip direction rotated by the barb angle, pointing back along the shaft.
            float bx = -(tx * MathF.Cos(angle) - ty * MathF.Sin(angle));
            float by = -(tx * MathF.Sin(angle) + ty * MathF.Cos(angle));
            var end = new PointF(to.X + bx * l, to.Y + by * l);
            float curl = l * 0.08f * (float)(rng.NextDouble() - 0.3);
            var mid = new PointF((to.X + end.X) / 2 - by * curl, (to.Y + end.Y) / 2 + bx * curl);
            Strokes.Fill(g, brush, to, mid, end, width * 1.0f, width * 0.55f, seed + 7 * k);
        }
    }

    public override bool Hit(Point p, int slop) => Strokes.DistanceToSegment(p, from, to) <= width * 2 + slop;
}

/// <summary>A box drawn by hand: four slightly bowed strokes that overshoot at the corners.</summary>
internal sealed class BoxMark(Rectangle r, Color color, float width, int seed) : Mark
{
    public override string Tool => "box";
    public override Rectangle Bounds => Rectangle.Inflate(r, (int)(width * 2), (int)(width * 2));
    public override Mark Offset(int dx, int dy) => new BoxMark(new Rectangle(r.X + dx, r.Y + dy, r.Width, r.Height), color, width, seed);

    public override void Draw(Graphics g, Bitmap source, int step) => Paint(g, r, color, width, seed);

    public static void Paint(Graphics g, RectangleF r, Color color, float width, int seed)
    {
        var rng = new Random(seed);
        using var brush = new SolidBrush(color);
        PointF[] c = [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];
        for (int i = 0; i < 4; i++)
        {
            var a = c[i];
            var b = c[(i + 1) % 4];
            float dx = b.X - a.X, dy = b.Y - a.Y, len = MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy));
            float ux = dx / len, uy = dy / len;
            float o0 = width * (0.3f + 1.2f * (float)rng.NextDouble()), o1 = width * (0.3f + 1.2f * (float)rng.NextDouble());
            var s = new PointF(a.X - ux * o0, a.Y - uy * o0);
            var e = new PointF(b.X + ux * o1, b.Y + uy * o1);
            float bow = MathF.Min(len * 0.012f, width * 1.5f) * (float)(rng.NextDouble() * 2 - 1);
            var m = new PointF((s.X + e.X) / 2 - uy * bow, (s.Y + e.Y) / 2 + ux * bow);
            Strokes.Fill(g, brush, s, m, e, width * 0.8f, width * 0.95f, seed + i);
        }
    }

    public override bool Hit(Point p, int slop)
    {
        var outer = Rectangle.Inflate(r, (int)width + slop, (int)width + slop);
        var inner = Rectangle.Inflate(r, -(int)width - slop, -(int)width - slop);
        return outer.Contains(p) && (inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(p));
    }
}

/// <summary>Handwritten text, with a soft white halo so it reads on any background.</summary>
internal sealed class TextMark(PointF at, string text, Color color, float size) : Mark
{
    public override string Tool => "text";
    public string Text => text;
    public PointF At => at;
    public float Size => size;
    public Color Color => color;
    public override Rectangle Bounds => Measure(text.Length == 0 ? "M" : text, size, at);
    public override Mark Offset(int dx, int dy) => new TextMark(new PointF(at.X + dx, at.Y + dy), text, color, size);
    public TextMark With(string t) => new(at, t, color, size);

    public override void Draw(Graphics g, Bitmap source, int step)
    {
        if (text.Length == 0) return;
        using var path = Path(text, size, at);
        using (var halo = new Pen(Color.FromArgb(170, 255, 255, 255), size * 0.16f) { LineJoin = LineJoin.Round })
            g.DrawPath(halo, path);
        using var b = new SolidBrush(color);
        g.FillPath(b, path);
    }

    /// <summary>Where the caret goes: the end of the last line.</summary>
    public RectangleF Caret()
    {
        string last = text.Split('\n')[^1];
        var lines = text.Split('\n').Length;
        float x = last.Length == 0 ? at.X : at.X + Measure(last, size, PointF.Empty).Width - size * 0.15f;
        float lineH = size * 1.25f;
        return new RectangleF(x + size * 0.08f, at.Y + (lines - 1) * lineH + size * 0.12f, Math.Max(1.5f, size / 14f), size * 1.05f);
    }

    private static GraphicsPath Path(string text, float size, PointF at)
    {
        var path = new GraphicsPath();
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces };
        path.AddString(text, HandFont.Family, (int)FontStyle.Regular, size, at, fmt);
        return path;
    }

    private static Rectangle Measure(string text, float size, PointF at)
    {
        int lines = text.Split('\n').Length;
        using var path = Path(text, size, at);
        var b = path.GetBounds();
        float w = Math.Max(b.Right - at.X, size * 0.6f);
        float h = Math.Max(lines * size * 1.25f, b.Bottom - at.Y);
        return Rectangle.Inflate(Rectangle.Round(new RectangleF(at.X, at.Y, w, h)), (int)(size * 0.15f) + 2, (int)(size * 0.1f) + 2);
    }
}

/// <summary>A numbered step: a filled circle with a white number. Numbers follow the order steps were placed.</summary>
internal sealed class StepMark(PointF center, Color color, float radius) : Mark
{
    public override string Tool => "step";
    public override Rectangle Bounds => Rectangle.Round(new RectangleF(center.X - radius - 2, center.Y - radius - 2, radius * 2 + 4, radius * 2 + 4));
    public override Mark Offset(int dx, int dy) => new StepMark(new PointF(center.X + dx, center.Y + dy), color, radius);

    public override void Draw(Graphics g, Bitmap source, int step) => Paint(g, center, radius, color, step);

    public static void Paint(Graphics g, PointF center, float radius, Color color, int number)
    {
        var r = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        using (var shadow = new SolidBrush(Color.FromArgb(45, 0, 0, 0)))
            g.FillEllipse(shadow, r.X, r.Y + Math.Max(1, radius / 10), r.Width, r.Height);
        using (var fill = new SolidBrush(color)) g.FillEllipse(fill, r);
        using (var ring = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(1.5f, radius / 7)))
            g.DrawEllipse(ring, r);
        bool light = color.GetBrightness() > 0.7f && color.R + color.G + color.B > 600;
        using var font = new Font(Ui.Strong.FontFamily, radius * (number >= 10 ? 0.95f : 1.15f), FontStyle.Regular, GraphicsUnit.Pixel);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var ink = new SolidBrush(light ? Ui.Ink : Color.White);
        g.DrawString(number.ToString(System.Globalization.CultureInfo.InvariantCulture), font, ink, new RectangleF(r.X, r.Y + radius * 0.04f, r.Width, r.Height), fmt);
    }
}

/// <summary>Tapered strokes: a quadratic curve filled as a polygon whose width changes along its length.</summary>
internal static class Strokes
{
    public static void Fill(Graphics g, Brush brush, PointF a, PointF c, PointF b, float w0, float w1, int seed)
    {
        const int N = 28;
        var left = new PointF[N + 1];
        var right = new PointF[N + 1];
        float phase = seed % 97 * 0.31f;
        for (int i = 0; i <= N; i++)
        {
            float t = i / (float)N, u = 1 - t;
            float x = u * u * a.X + 2 * u * t * c.X + t * t * b.X;
            float y = u * u * a.Y + 2 * u * t * c.Y + t * t * b.Y;
            float tx = 2 * u * (c.X - a.X) + 2 * t * (b.X - c.X), ty = 2 * u * (c.Y - a.Y) + 2 * t * (b.Y - c.Y);
            float tl = MathF.Sqrt(tx * tx + ty * ty);
            if (tl < 1e-4f) { tx = b.X - a.X; ty = b.Y - a.Y; tl = MathF.Max(1e-4f, MathF.Sqrt(tx * tx + ty * ty)); }
            float nx = -ty / tl, ny = tx / tl;
            // Pressure: eases in from w0 to w1, with a faint wobble so it isn't machine-even.
            float hw = (w0 + (w1 - w0) * MathF.Pow(t, 0.7f)) / 2 * (1 + 0.07f * MathF.Sin(t * 9.4f + phase));
            left[i] = new PointF(x + nx * hw, y + ny * hw);
            right[N - i] = new PointF(x - nx * hw, y - ny * hw);
        }
        using var path = new GraphicsPath();
        path.AddPolygon([.. left, .. right]);
        g.FillPath(brush, path);
        g.FillEllipse(brush, a.X - w0 / 2, a.Y - w0 / 2, w0, w0);
        g.FillEllipse(brush, b.X - w1 / 2, b.Y - w1 / 2, w1, w1);
    }

    public static float DistanceToSegment(PointF p, PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        float t = l2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
        float x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return MathF.Sqrt(x * x + y * y);
    }
}

/// <summary>
/// The handwriting face for text marks: Figma Hand if it's installed, otherwise Ink Free, which ships with
/// Windows, otherwise Segoe Print.
/// </summary>
internal static class HandFont
{
    public static readonly FontFamily Family = Find("Figma Hand") ?? Find("Ink Free") ?? Find("Segoe Print") ?? Ui.Family;

    private static FontFamily? Find(string name)
    {
        try { return new FontFamily(name); }
        catch (ArgumentException) { return null; }
    }
}
