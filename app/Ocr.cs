using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Imago;

/// <summary>Text from pixels, using the OCR engine built into Windows. Runs fully offline.</summary>
internal static class Ocr
{
    public static bool Available => OcrEngine.TryCreateFromUserProfileLanguages() is not null;

    public static async Task<string> ReadAsync(Bitmap img)
    {
        var (lines, _) = await RecognizeAsync(img);
        return string.Join(Environment.NewLine, lines.Select(l => l.Text)).Trim();
    }

    /// <summary>One recognised word and where it sits, in the capture's own pixels.</summary>
    public sealed record Word(string Text, Rectangle Box);

    /// <summary>The words line by line, with boxes, for auto-redact.</summary>
    public static async Task<List<List<Word>>> WordsAsync(Bitmap img)
    {
        var (lines, scale) = await RecognizeAsync(img);
        return lines.Select(l => l.Words.Select(w =>
        {
            var r = w.BoundingRect;
            return new Word(w.Text, Rectangle.FromLTRB((int)Math.Floor(r.X / scale), (int)Math.Floor(r.Y / scale),
                (int)Math.Ceiling((r.X + r.Width) / scale), (int)Math.Ceiling((r.Y + r.Height) / scale)));
        }).ToList()).ToList();
    }

    private static async Task<(IReadOnlyList<OcrLine> Lines, double Scale)> RecognizeAsync(Bitmap img)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("No OCR language is installed. Add one in Settings > Time & language > Language.");

        // Small text reads much better when upscaled; very large images must fit the engine's limit.
        double scale = Math.Min(img.Width, img.Height) < 600 ? 2.0 : 1.0;
        int max = (int)OcrEngine.MaxImageDimension;
        scale = Math.Min(scale, Math.Min((double)max / img.Width, (double)max / img.Height));

        Bitmap src = img;
        if (Math.Abs(scale - 1.0) > 0.001)
        {
            int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
            src = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(src);
            g.InterpolationMode = scale > 1 ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic : System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(img, 0, 0, w, h);
        }
        try
        {
            var px = Pixels.FromBitmap(src);
            using var sb = SoftwareBitmap.CreateCopyFromBuffer(px.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, px.W, px.H, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(sb);
            return (result.Lines, scale);
        }
        finally
        {
            if (!ReferenceEquals(src, img)) src.Dispose();
        }
    }
}
