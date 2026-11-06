using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Security;
using Windows.Media.Editing;
using Windows.Storage;

namespace Imago;

/// <summary>
/// Animated SVG walkthrough: one still per step (taken from the recording), then zoom, cursor, click ripple and
/// caption animated with SMIL. One self-contained file that plays in a browser or an img tag; no video needed.
/// </summary>
internal static class FlowSvg
{
    private const double Scene = 3.2;   // seconds per step
    private const int OutW = 1280;

    /// <summary>The still for each step (or one still at 0 when there are no steps), as JPEG bytes.</summary>
    public static async Task<List<byte[]>> GrabAsync(FlowProject p, string src)
    {
        var file = await StorageFile.GetFileFromPathAsync(src);
        var comp = new MediaComposition();
        comp.Clips.Add(await MediaClip.CreateFromFileAsync(file));
        double dur = comp.Duration.TotalSeconds;
        int w = OutW, h = (int)Math.Round(OutW * (double)p.H / p.W);
        var times = p.Steps.Count > 0 ? p.Steps.Select(s => s.T).ToList() : [0.0];
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var qp = new EncoderParameters(1);
        qp.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
        var list = new List<byte[]>();
        foreach (double t in times)
        {
            using var s = await comp.GetThumbnailAsync(TimeSpan.FromSeconds(Math.Min(t, Math.Max(0, dur - 0.001))), w, h, VideoFramePrecision.NearestFrame);
            using var ms = new MemoryStream();
            await s.AsStreamForRead().CopyToAsync(ms);
            ms.Position = 0;
            using var raw = new Bitmap(ms);
            using var jpg = new MemoryStream();
            raw.Save(jpg, codec, qp);
            list.Add(jpg.ToArray());
        }
        return list;
    }

    /// <summary>
    /// Screenshot-to-flow: each image becomes a step. "shot.png@x,y" adds a click point (image pixels) for zoom, ripple and
    /// cursor. Captions come from the file names. Returns the SVG path; a derived note names and hashes every original.
    /// </summary>
    public static string FromShots(IReadOnlyList<string> specs, string outDir)
    {
        if (specs.Count == 0) throw new ArgumentException("Give at least one screenshot.");
        var files = new List<string>();
        var pts = new List<(int X, int Y)>();
        foreach (string s in specs)
        {
            int at = s.LastIndexOf('@');
            string f = s; int x = -1, y = -1;
            if (at > 0)
            {
                var xy = s[(at + 1)..].Split(',');
                if (xy.Length == 2 && int.TryParse(xy[0], NumberStyles.None, CultureInfo.InvariantCulture, out x) && int.TryParse(xy[1], NumberStyles.None, CultureInfo.InvariantCulture, out y)) f = s[..at];
                else { x = y = -1; }
            }
            if (!File.Exists(f)) throw new FileNotFoundException("Not found: " + f);
            files.Add(Path.GetFullPath(f)); pts.Add((x, y));
        }

        int fw, fh;
        using (var first = Image.FromFile(files[0])) { fw = first.Width; fh = first.Height; }
        int h = (int)Math.Round(OutW * (double)fh / fw);
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var qp = new EncoderParameters(1);
        qp.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        var p = new FlowProject { Video = Path.GetFileName(files[0]), W = fw, H = fh, Duration = files.Count * Scene, CursorSeparate = true };
        var stills = new List<byte[]>();
        for (int i = 0; i < files.Count; i++)
        {
            using var img = Image.FromFile(files[i]);
            using var canvas = new Bitmap(OutW, h);
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                double k = Math.Min((double)OutW / img.Width, (double)h / img.Height);
                int dw = (int)Math.Round(img.Width * k), dh = (int)Math.Round(img.Height * k);
                g.DrawImage(img, (OutW - dw) / 2, (h - dh) / 2, dw, dh);
            }
            using var ms = new MemoryStream();
            canvas.Save(ms, codec, qp);
            stills.Add(ms.ToArray());
            string cap = Path.GetFileNameWithoutExtension(files[i]).Replace('_', ' ').Replace('-', ' ').Trim();
            double sx = (double)fw / img.Width;
            p.Steps.Add(new FlowStep { T = i, X = pts[i].X < 0 ? -1 : (int)(pts[i].X * sx), Y = pts[i].Y < 0 ? -1 : (int)(pts[i].Y * sx), Caption = cap, Manual = true });
        }
        Directory.CreateDirectory(outDir);
        string outPath = Path.GetFullPath(Naming.Unique(outDir, Path.GetFileNameWithoutExtension(files[0]) + "-flow", ".svg"));
        File.WriteAllText(outPath, Build(p, stills), new UTF8Encoding(false));
        FlowProject.WriteDerivedNote(outPath, files, p.Effects);
        return outPath;
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Emits an animate element whose key points are (seconds, value) over the whole loop.</summary>
    private static string Anim(string tag, string attr, double total, IList<(double T, string V)> pts, string extra = "")
    {
        var sb = new StringBuilder();
        var ps = pts.OrderBy(x => x.T).ToList();
        if (ps[0].T > 0) ps.Insert(0, (0, ps[0].V));
        if (ps[^1].T < total) ps.Add((total, ps[^1].V));
        string kt = string.Join(";", ps.Select(x => N(Math.Clamp(x.T / total, 0, 1))));
        string vals = string.Join(";", ps.Select(x => x.V));
        sb.Append($"<{tag} attributeName=\"{attr}\" {extra} dur=\"{N(total)}s\" repeatCount=\"indefinite\" calcMode=\"linear\" keyTimes=\"{kt}\" values=\"{vals}\"/>");
        return sb.ToString();
    }

    public static string Build(FlowProject p, IReadOnlyList<byte[]> stills)
    {
        int n = Math.Max(1, stills.Count);
        double total = n * Scene;
        int w = OutW, h = (int)Math.Round(OutW * (double)p.H / p.W);
        double m = (double)w / p.W;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 {w} {h}\" width=\"{w}\" height=\"{h}\">");
        sb.Append("<defs><radialGradient id=\"rp\"><stop offset=\"0\" stop-color=\"#0f6cbd\" stop-opacity=\"0\"/><stop offset=\".2\" stop-color=\"#0f6cbd\" stop-opacity=\".55\"/><stop offset=\".55\" stop-color=\"#0f6cbd\" stop-opacity=\".18\"/><stop offset=\"1\" stop-color=\"#0f6cbd\" stop-opacity=\".02\"/></radialGradient></defs>");
        sb.Append($"<rect width=\"{w}\" height=\"{h}\" fill=\"#000\"/>");

        double fade = 0.3;
        for (int i = 0; i < n; i++)
        {
            double t0 = i * Scene, t1 = t0 + Scene;
            var step = p.Steps.Count > i ? p.Steps[i] : null;
            bool point = step is { HasPoint: true };
            double px = point ? step!.X * m : 0, py = point ? step!.Y * m : 0;

            // Scene visibility: fades in, holds, fades out. The loop wraps from the last scene to the first.
            var vis = new List<(double, string)> { (t0, "0"), (t0 + fade, "1"), (t1 - fade, "1"), (t1, "0") };
            sb.Append("<g opacity=\"0\">");
            sb.Append(Anim("animate", "opacity", total, vis));

            // Zoom: ease in before the click, hold, ease out.
            bool zoom = p.Zoom && point;
            double z = 1.8, tx = 0, ty = 0;
            if (zoom)
            {
                double vw = w / z, vh = h / z;
                double cx = Math.Clamp(px, vw / 2, w - vw / 2), cy = Math.Clamp(py, vh / 2, h - vh / 2);
                tx = -(cx - vw / 2) * z; ty = -(cy - vh / 2) * z;
                double a = t0 + 0.7, b = t0 + 1.3, c = t0 + 2.4, d = t0 + 3.0;
                sb.Append("<g>");
                sb.Append(Anim("animateTransform", "transform", total, [(t0, "0 0"), (a, "0 0"), (b, $"{N(tx)} {N(ty)}"), (c, $"{N(tx)} {N(ty)}"), (d, "0 0")], "type=\"translate\" additive=\"replace\""));
                sb.Append("<g>");
                sb.Append(Anim("animateTransform", "transform", total, [(t0, "1"), (a, "1"), (b, N(z)), (c, N(z)), (d, "1")], "type=\"scale\" additive=\"replace\""));
            }
            sb.Append($"<image width=\"{w}\" height=\"{h}\" xlink:href=\"data:image/jpeg;base64,{Convert.ToBase64String(stills[i])}\"/>");

            if (point && p.Ripples)
            {
                double r0 = 10 * m, r1 = 70 * m, ck = t0 + 1.2;
                sb.Append($"<circle cx=\"{N(px)}\" cy=\"{N(py)}\" r=\"0\" fill=\"url(#rp)\" opacity=\"0\">");
                sb.Append(Anim("animate", "r", total, [(ck - 0.001, N(r0)), (ck, N(r0)), (ck + 0.7, N(r1))]));
                sb.Append(Anim("animate", "opacity", total, [(ck - 0.001, "0"), (ck, "1"), (ck + 0.7, "0")]));
                sb.Append("</circle>");
            }

            if (step is { Marks.Count: > 0 })
            {
                var mv = new List<(double, string)> { (t0, "0"), (t0 + 0.25, "1"), (t1 - 0.5, "1"), (t1 - 0.25, "0") };
                foreach (var k in step.Marks)
                {
                    sb.Append($"<rect x=\"{N(k.X * m)}\" y=\"{N(k.Y * m)}\" width=\"{N(k.W * m)}\" height=\"{N(k.H * m)}\" rx=\"{N(6 * m)}\" fill=\"#0f6cbd\" fill-opacity=\".14\" stroke=\"#0f6cbd\" stroke-width=\"{N(Math.Max(2, 3 * m))}\" opacity=\"0\">");
                    sb.Append(Anim("animate", "opacity", total, mv));
                    sb.Append("</rect>");
                }
            }

            if (point && p.CursorSeparate)
            {
                // The pointer glides from where the last step ended to this click.
                double fx = px, fy = py;
                if (i > 0 && p.Steps[i - 1].HasPoint) { fx = p.Steps[i - 1].X * m; fy = p.Steps[i - 1].Y * m; }
                else { fx = px - 120 * m; fy = py + 80 * m; }
                double u = 1.1 * m * 1.4;
                sb.Append("<g>");
                sb.Append(Anim("animateTransform", "transform", total, [(t0, $"{N(fx)} {N(fy)}"), (t0 + 0.2, $"{N(fx)} {N(fy)}"), (t0 + 1.2, $"{N(px)} {N(py)}")], "type=\"translate\" additive=\"replace\""));
                sb.Append($"<path d=\"M0 0L0 {N(13 * u)}L{N(9 * u)} {N(9 * u)}Z\" fill=\"#0f6cbd\" stroke=\"#fff\" stroke-width=\"{N(1.2 * u)}\" stroke-linejoin=\"round\"/>");
                sb.Append("</g>");
            }
            if (zoom) sb.Append("</g></g>");

            string cap = step?.Caption ?? "";
            if (cap.Length > 0)
            {
                double cw = Math.Min(w - 80, cap.Length * 11.5 + 40);
                sb.Append($"<rect x=\"{N((w - cw) / 2)}\" y=\"{h - 78}\" width=\"{N(cw)}\" height=\"46\" rx=\"23\" fill=\"#000\" fill-opacity=\".72\"/>");
                sb.Append($"<text x=\"{N(w / 2.0)}\" y=\"{h - 48}\" fill=\"#fff\" font-family=\"Segoe UI,system-ui,sans-serif\" font-size=\"20\" text-anchor=\"middle\">{SecurityElement.Escape(cap)}</text>");
            }
            sb.Append("</g>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
