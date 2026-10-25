using System.Drawing.Drawing2D;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;

namespace Imago;

/// <summary>
/// Per-tile fingerprint of a capture, signed into the manifest. Brightness mean and spread survive JPEG and
/// resizing well enough to say which part of a later copy differs; the one-byte hash catches small exact edits.
/// </summary>
internal sealed class Tiles
{
    private const int MaxTiles = 12000;

    public int Size { get; }
    public int Cols { get; }
    public int Rows { get; }
    public float[] Mean { get; }
    public float[] Sd { get; }
    public byte[] Hash { get; }

    private Tiles(int size, int cols, int rows)
    {
        Size = size; Cols = cols; Rows = rows;
        Mean = new float[cols * rows];
        Sd = new float[cols * rows];
        Hash = new byte[cols * rows];
    }

    /// <summary>16 px tiles, doubled until a huge capture stays under the tile budget.</summary>
    public static int SizeFor(int w, int h)
    {
        int s = 16;
        while ((long)((w + s - 1) / s) * ((h + s - 1) / s) > MaxTiles) s *= 2;
        return s;
    }

    public static Tiles Measure(Pixels px, int size = 0)
    {
        if (size <= 0) size = SizeFor(px.W, px.H);
        var t = new Tiles(size, (px.W + size - 1) / size, (px.H + size - 1) / size);
        byte[] b = px.Bgra;
        Parallel.For(0, t.Rows, ty =>
        {
            for (int tx = 0; tx < t.Cols; tx++)
            {
                int x0 = tx * size, y0 = ty * size;
                int x1 = Math.Min(px.W, x0 + size), y1 = Math.Min(px.H, y0 + size);
                double sum = 0, sq = 0;
                uint fnv = 2166136261;
                for (int y = y0; y < y1; y++)
                {
                    int i = (y * px.W + x0) * 4;
                    for (int x = x0; x < x1; x++, i += 4)
                    {
                        double l = Luma(b, i);
                        sum += l; sq += l * l;
                        fnv = (fnv ^ b[i]) * 16777619;
                        fnv = (fnv ^ b[i + 1]) * 16777619;
                        fnv = (fnv ^ b[i + 2]) * 16777619;
                    }
                }
                int n = (x1 - x0) * (y1 - y0), k = ty * t.Cols + tx;
                double m = sum / n;
                t.Mean[k] = (float)m;
                t.Sd[k] = (float)Math.Sqrt(Math.Max(0, sq / n - m * m));
                t.Hash[k] = (byte)(fnv ^ (fnv >> 8) ^ (fnv >> 16) ^ (fnv >> 24));
            }
        });
        return t;
    }

    public static double Luma(byte[] bgra, int i) => 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];

    /// <summary>size, cols, rows as ushorts, then mean x256, sd x256 and the hash byte per tile, deflated.</summary>
    public byte[] Pack()
    {
        var raw = new byte[6 + Mean.Length * 5];
        BitConverter.TryWriteBytes(raw.AsSpan(0), (ushort)Size);
        BitConverter.TryWriteBytes(raw.AsSpan(2), (ushort)Cols);
        BitConverter.TryWriteBytes(raw.AsSpan(4), (ushort)Rows);
        for (int k = 0, o = 6; k < Mean.Length; k++, o += 5)
        {
            BitConverter.TryWriteBytes(raw.AsSpan(o), (ushort)Math.Clamp(Math.Round(Mean[k] * 256), 0, 65535));
            BitConverter.TryWriteBytes(raw.AsSpan(o + 2), (ushort)Math.Clamp(Math.Round(Sd[k] * 256), 0, 65535));
            raw[o + 4] = Hash[k];
        }
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal)) z.Write(raw);
        return ms.ToArray();
    }

    public static Tiles? Unpack(byte[] packed)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
            var ms = new MemoryStream();
            z.CopyTo(ms);
            var raw = ms.ToArray();
            if (raw.Length < 6) return null;
            int size = BitConverter.ToUInt16(raw, 0), cols = BitConverter.ToUInt16(raw, 2), rows = BitConverter.ToUInt16(raw, 4);
            if (size == 0 || raw.Length != 6 + cols * rows * 5) return null;
            var t = new Tiles(size, cols, rows);
            for (int k = 0, o = 6; k < t.Mean.Length; k++, o += 5)
            {
                t.Mean[k] = BitConverter.ToUInt16(raw, o) / 256f;
                t.Sd[k] = BitConverter.ToUInt16(raw, o + 2) / 256f;
                t.Hash[k] = raw[o + 4];
            }
            return t;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}

/// <summary>Signature blocks of every Stamped capture made on this PC, so a copy stripped of its metadata can still be checked.</summary>
internal static class Ledger
{
    public static string Dir => Path.Combine(Settings.Dir, "ledger");

    public static void Save(string stampId, string envelope)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, stampId + ".json"), envelope);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The file itself still carries the signature; the ledger only helps with stripped copies.
        }
    }

    public static void Forget(string stampId)
    {
        try { File.Delete(Path.Combine(Dir, stampId + ".json")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static string? Load(string stampId)
    {
        string p = Path.Combine(Dir, stampId + ".json");
        try { return File.Exists(p) ? File.ReadAllText(p) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

internal enum ReviewVerdict { Stamped, Resaved, Changed, StampedElsewhere, Forged, NotStamped, Error }

internal enum AiLevel { NotAssessed, Low, Medium, High }

internal sealed class ReviewReport
{
    public ReviewVerdict Verdict { get; set; }
    public string Headline { get; set; } = "";
    public List<string> Details { get; } = [];
    public string? StampId { get; set; }
    /// <summary>Edits made in Imago before signing, in this file's pixel coordinates.</summary>
    public List<EditOp> ImagoEdits { get; } = [];
    /// <summary>Areas that differ from the stamped original, in this file's pixel coordinates.</summary>
    public List<Rectangle> Altered { get; } = [];
    public bool Recompressed { get; set; }
    public AiLevel Ai { get; set; }
    public List<string> AiReasons { get; } = [];
    public VerifyResult? Signature { get; set; }
}

/// <summary>Answers four questions about an image: changed or not, where, what kind of change, and whether AI was likely involved.</summary>
internal static class Review
{
    public static string ModelDir => Path.Combine(Settings.Dir, "models");

    public static ReviewReport Analyze(byte[] file)
    {
        var rep = new ReviewReport();
        Pixels px;
        PngImage? png = null;
        try { png = Png.Decode(file); }
        catch (InvalidDataException) { /* not a PNG we can read: try the other formats below */ }

        if (png is not null)
        {
            px = png.Pixels;
            var v = Provenance.Verify(png);
            rep.Signature = v;
            if (v.Status == VerifyStatus.Valid)
            {
                rep.Verdict = ReviewVerdict.Stamped;
                rep.StampId = (string?)v.Manifest?["stamp"];
                rep.Headline = "Stamped and unchanged since capture.";
                ReadEdits(rep, v.Manifest!, px);
                rep.Ai = AiLevel.Low;
                rep.AiReasons.Add("Every pixel matches the signed original.");
                rep.Details.AddRange(v.Details);
                return rep;
            }
            if (v.Status == VerifyStatus.Tampered && v.SignatureValid && v.Manifest is not null)
            {
                rep.StampId = (string?)v.Manifest["stamp"];
                Compare(rep, v.Manifest, px, lossless: true);
                rep.Details.AddRange(v.Details);
                return rep;
            }
            if (v.Status == VerifyStatus.Tampered)
            {
                // The signature itself doesn't check out. The watermark may still say which capture this was.
                rep.Verdict = ReviewVerdict.Forged;
                rep.Headline = v.Headline;
            }
        }
        else
        {
            try
            {
                using var ms = new MemoryStream(file);
                using var img = Image.FromStream(ms);
                using var bmp = new Bitmap(img);
                px = Pixels.FromBitmap(bmp);
            }
            catch (Exception e) when (e is ArgumentException or OutOfMemoryException or ExternalException)
            {
                rep.Verdict = ReviewVerdict.Error;
                rep.Headline = "Can't read this file as an image.";
                return rep;
            }
        }

        ulong? code = Watermark.Detect(px);
        if (code is null)
        {
            if (rep.Verdict != ReviewVerdict.Forged)
            {
                rep.Verdict = ReviewVerdict.NotStamped;
                rep.Headline = "No Imago stamp found.";
                rep.Details.Add("It was not captured with Stamped on, or it was cropped, heavily resized or rebuilt.");
            }
            AssessAiWhole(rep, px, "a Stamped original");
            return rep;
        }

        string id = StampInfo.IdFor(code.Value);
        rep.StampId = id;
        rep.Recompressed = true;
        string? record = Ledger.Load(id);
        if (record is null)
        {
            rep.Verdict = ReviewVerdict.StampedElsewhere;
            rep.Headline = $"Stamped by Imago as {id}.";
            rep.Details.Add("The watermark is intact, but the signed record isn't on this PC, so changes can't be located. Ask the person who captured it to review it.");
            AssessAiWhole(rep, px, "the signed record");
            return rep;
        }
        var rv = Provenance.VerifyRecord(record);
        if (rv.Status != VerifyStatus.Valid || rv.Manifest is null)
        {
            rep.Verdict = ReviewVerdict.Forged;
            rep.Headline = rv.Headline;
            return rep;
        }
        rep.Signature = rv;
        Compare(rep, rv.Manifest, px, lossless: false);
        rep.Details.Add($"Matched by its invisible watermark to {id} in this PC's records.");
        rep.Details.AddRange(rv.Details);
        return rep;
    }

    private static void ReadEdits(ReviewReport rep, JsonObject man, Pixels px)
    {
        if (man["edits"] is not JsonArray arr) return;
        int ow = (int?)man["width"] ?? px.W, oh = (int?)man["height"] ?? px.H;
        double sx = (double)px.W / ow, sy = (double)px.H / oh;
        foreach (var e in arr)
        {
            if (e is not JsonObject o) continue;
            var r = new Rectangle((int?)o["x"] ?? 0, (int?)o["y"] ?? 0, (int?)o["w"] ?? 0, (int?)o["h"] ?? 0);
            rep.ImagoEdits.Add(new EditOp((string?)o["tool"] ?? "edit", Scale(r, sx, sy)));
        }
        if (rep.ImagoEdits.Count > 0)
        {
            var counts = rep.ImagoEdits.GroupBy(e => e.Tool).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key);
            rep.Details.Insert(0, "Edited in Imago before stamping: " + string.Join(", ", counts) + ".");
        }
    }

    /// <summary>Signed tile grid against this copy's pixels: where it differs, and what kind of change it looks like.</summary>
    private static void Compare(ReviewReport rep, JsonObject man, Pixels px, bool lossless)
    {
        ReadEdits(rep, man, px);
        int ow = (int?)man["width"] ?? 0, oh = (int?)man["height"] ?? 0;
        Tiles? orig = null;
        try { if (man["tiles"]?["data"] is JsonValue d) orig = Tiles.Unpack(Convert.FromBase64String((string)d!)); }
        catch (Exception e) when (e is FormatException or InvalidOperationException or JsonException) { orig = null; }

        if (orig is null || ow <= 0 || oh <= 0)
        {
            rep.Verdict = ReviewVerdict.Changed;
            rep.Headline = "Changed since capture.";
            rep.Details.Add("This capture predates change maps, so the changed area can't be shown.");
            rep.Ai = AiLevel.NotAssessed;
            return;
        }

        double aspect = (double)px.W / px.H, want = (double)ow / oh;
        if (Math.Abs(aspect / want - 1) > 0.02)
        {
            rep.Verdict = ReviewVerdict.Changed;
            rep.Headline = "Cropped or reshaped since capture.";
            rep.Details.Add($"Captured at {ow} x {oh}, now {px.W} x {px.H}.");
            rep.Ai = AiLevel.NotAssessed;
            return;
        }

        bool sameSize = px.W == ow && px.H == oh;
        Pixels cp = sameSize ? px : Resample(px, ow, oh);
        if (!sameSize) { rep.Recompressed = true; rep.Details.Add($"Resized from {ow} x {oh} to {px.W} x {px.H}."); }
        var now = Tiles.Measure(cp, orig.Size);
        if (now.Cols != orig.Cols || now.Rows != orig.Rows)
        {
            rep.Verdict = ReviewVerdict.Changed;
            rep.Headline = "Changed since capture.";
            rep.Ai = AiLevel.NotAssessed;
            return;
        }

        int n = orig.Mean.Length;
        var changed = new bool[n];
        bool exact = lossless && sameSize;
        if (exact)
        {
            int diff = 0;
            for (int k = 0; k < n; k++)
            {
                changed[k] = now.Hash[k] != orig.Hash[k] || Math.Abs(now.Mean[k] - orig.Mean[k]) > 0.01 || Math.Abs(now.Sd[k] - orig.Sd[k]) > 0.01;
                if (changed[k]) diff++;
            }
            // A JPEG saved back to PNG changes every tile slightly: judge that by tolerance instead.
            if (diff > n * 0.6) { exact = false; rep.Recompressed = true; }
        }
        if (!exact)
        {
            var dist = new float[n];
            for (int k = 0; k < n; k++) dist[k] = Math.Abs(now.Mean[k] - orig.Mean[k]) + 0.5f * Math.Abs(now.Sd[k] - orig.Sd[k]);
            var sorted = (float[])dist.Clone();
            Array.Sort(sorted);
            float med = sorted[n / 2];
            var dev = sorted.Select(x => Math.Abs(x - med)).ToArray();
            Array.Sort(dev);
            float mad = dev[n / 2] * 1.4826f;
            float limit = Math.Max(3f, med + 6 * mad);
            for (int k = 0; k < n; k++) changed[k] = dist[k] > limit;
            // Re-encoding rings around sharp text can push a lone tile just past the limit. Keep a lone tile only if it is far past.
            var lone = new List<int>();
            for (int k = 0; k < n; k++)
            {
                if (!changed[k] || dist[k] > 2 * limit) continue;
                int tx = k % orig.Cols, ty = k / orig.Cols;
                bool near = false;
                for (int dy = -2; dy <= 2 && !near; dy++)
                    for (int dx = -2; dx <= 2 && !near; dx++)
                    {
                        int nx = tx + dx, ny = ty + dy;
                        near = (dx != 0 || dy != 0) && nx >= 0 && ny >= 0 && nx < orig.Cols && ny < orig.Rows && changed[ny * orig.Cols + nx];
                    }
                if (!near) lone.Add(k);
            }
            foreach (int k in lone) changed[k] = false;
            if (med > 0.05f) rep.Recompressed = true;
        }

        // Group changed tiles into boxes, in original coordinates, then map to this file.
        var boxes = Group(changed, orig.Cols, orig.Rows, orig.Size, ow, oh);
        double sx = (double)px.W / ow, sy = (double)px.H / oh;
        foreach (var b in boxes) rep.Altered.Add(Scale(b, sx, sy));

        int changedCount = changed.Count(c => c);
        if (rep.Altered.Count == 0)
        {
            rep.Verdict = ReviewVerdict.Resaved;
            rep.Headline = rep.Recompressed ? "Re-saved, content unchanged." : "Unchanged content, file details differ.";
            rep.Details.Insert(0, rep.Recompressed
                ? "The pixels were re-encoded (for example JPEG or a chat app), but nothing in the picture was altered."
                : "The picture matches the signed original; only the file's extra data changed.");
            rep.Ai = AiLevel.Low;
            rep.AiReasons.Add("No region differs from the signed original.");
            return;
        }

        rep.Verdict = ReviewVerdict.Changed;
        double pct = 100.0 * changedCount / n;
        rep.Headline = rep.Altered.Count == 1 ? "Changed in one area since capture." : $"Changed in {rep.Altered.Count} areas since capture.";
        rep.Details.Insert(0, $"About {pct:0.#}% of the picture differs from the signed original" + (rep.Recompressed ? ", beyond normal re-encoding." : "."));
        rep.Details.Insert(1, "Kind of change: " + Kind(orig, now, changed) + ".");
        AssessAi(rep, cp, changed, orig, boxes);
    }

    private static string Kind(Tiles orig, Tiles now, bool[] changed)
    {
        int covered = 0, revealed = 0, recolored = 0;
        for (int k = 0; k < changed.Length; k++)
        {
            if (!changed[k]) continue;
            if (now.Sd[k] < orig.Sd[k] * 0.5f) covered++;          // detail went away: painted over, blurred or erased
            else if (now.Sd[k] > orig.Sd[k] * 1.5f + 2) revealed++; // detail appeared: text or objects added
            else recolored++;                                        // similar detail, different values: replaced or retouched
        }
        var parts = new List<(int N, string What)>
        {
            (covered, "content covered, blurred or erased"),
            (revealed, "content added"),
            (recolored, "content replaced or retouched"),
        };
        return string.Join("; ", parts.Where(p => p.N > 0).OrderByDescending(p => p.N).Select(p => p.What));
    }

    /// <summary>
    /// On-device heuristics, comparing the changed tiles with the untouched rest of the same capture. Screens are
    /// flat colours and pixel-sharp edges; generated fills tend to bring many colours, soft edges and fine grain.
    /// A model in the models folder, when there is one, adds its own score for the changed areas.
    /// </summary>
    private static void AssessAi(ReviewReport rep, Pixels cp, bool[] changed, Tiles grid, List<Rectangle> boxes)
    {
        var a = new Features();
        var b = new Features();
        int baseline = 0;
        for (int k = 0; k < changed.Length; k++)
        {
            if (changed[k]) a.Add(cp, k % grid.Cols * grid.Size, k / grid.Cols * grid.Size, grid.Size);
            else if (k % 7 == 0 && baseline++ < 600) b.Add(cp, k % grid.Cols * grid.Size, k / grid.Cols * grid.Size, grid.Size);
        }
        int score = 0;
        if (a.Colours > 0.15 && a.Colours > 2 * b.Colours) { score++; rep.AiReasons.Add("Far more distinct colours than the rest of the screen, typical of generated fills."); }
        if (a.Sharp < 0.5 * b.Sharp && b.Sharp > 0.05) { score++; rep.AiReasons.Add("Softer edges than the rest of the capture, where text and UI are pixel-sharp."); }
        if (a.Noise > 2 * b.Noise + 0.5) { score++; rep.AiReasons.Add("Fine grain where the screen is otherwise clean."); }
        // The model counts for up to two of the three heuristic points, and a confident "not AI" takes one away.
        double? p = ModelScore(rep, cp, boxes.OrderByDescending(r => r.Width * r.Height).Take(3));
        if (p >= 0.85) score += 2;
        else if (p >= 0.6) score++;
        else if (p < 0.15 && score > 0) score--;
        rep.Ai = score switch { >= 3 => AiLevel.High, 2 => AiLevel.Medium, _ => AiLevel.Low };
        if (score == 0) rep.AiReasons.Add("The changed area looks like ordinary screen content or a manual edit.");
        if (score >= 2) rep.AiReasons.Add("A pasted photo can look the same; treat this as a lead, not proof.");
        rep.AiReasons.Add(ModelNote(p is not null));
    }

    /// <summary>With no stamp to compare against, only a model can judge, and only the whole picture.</summary>
    private static void AssessAiWhole(ReviewReport rep, Pixels px, string need)
    {
        double? p = ModelScore(rep, px, [new Rectangle(0, 0, px.W, px.H)]);
        if (p is null)
        {
            rep.Ai = AiLevel.NotAssessed;
            rep.AiReasons.Add($"Needs {need} to compare against, or a detector model in the models folder.");
            if (AiModel.DefaultError is not null) rep.AiReasons.Add(ModelNote(false));
            return;
        }
        rep.Ai = p switch { >= 0.85 => AiLevel.High, >= 0.6 => AiLevel.Medium, _ => AiLevel.Low };
        rep.AiReasons.Add($"Judged by the model alone; {need} would show where it changed.");
        rep.AiReasons.Add(ModelNote(true));
    }

    /// <summary>Highest model score over the areas, or null without a working model.</summary>
    private static double? ModelScore(ReviewReport rep, Pixels px, IEnumerable<Rectangle> areas)
    {
        var model = AiModel.Default;
        if (model is null) return null;
        try
        {
            double p = areas.Select(r => model.Score(px, r)).DefaultIfEmpty(0).Max();
            rep.AiReasons.Add($"On-device model {model.Name} rates it {p:P0} likely AI-generated.");
            return p;
        }
        catch (Exception e) when (e is OnnxRuntimeException or InvalidDataException or ArgumentException or OutOfMemoryException)
        {
            rep.AiReasons.Add($"The model {model.Name} could not run: {e.Message}");
            return null;
        }
    }

    private static string ModelNote(bool ran) =>
        ran ? "Checked on this PC with heuristics and the model. Nothing was uploaded."
        : AiModel.DefaultError is { } err ? $"The model in the models folder could not load ({err}), so only heuristics ran. Nothing was uploaded."
        : "Checked on this PC with heuristics. Nothing was uploaded.";

    private sealed class Features
    {
        private long _px, _strong, _soft;
        private double _noise;
        private long _noiseN;
        private double _colourRatio;
        private int _tiles;

        public double Colours => _tiles == 0 ? 0 : _colourRatio / _tiles;
        public double Sharp => _strong + _soft == 0 ? 0 : (double)_strong / (_strong + _soft);
        public double Noise => _noiseN == 0 ? 0 : _noise / _noiseN;

        public void Add(Pixels p, int x0, int y0, int size)
        {
            int x1 = Math.Min(p.W, x0 + size), y1 = Math.Min(p.H, y0 + size);
            var seen = new HashSet<int>();
            byte[] b = p.Bgra;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * p.W + x) * 4;
                    seen.Add((b[i] >> 3) | (b[i + 1] >> 3) << 5 | (b[i + 2] >> 3) << 10);
                    _px++;
                    double l = Tiles.Luma(b, i);
                    if (x + 1 < p.W)
                    {
                        double d = Math.Abs(Tiles.Luma(b, i + 4) - l);
                        if (d >= 48) _strong++; else if (d >= 6) _soft++;
                    }
                    if (x > 0 && y > 0 && x + 1 < p.W && y + 1 < p.H)
                    {
                        double lap = 4 * l - Tiles.Luma(b, i - 4) - Tiles.Luma(b, i + 4) - Tiles.Luma(b, i - p.W * 4) - Tiles.Luma(b, i + p.W * 4);
                        if (Math.Abs(lap) < 24) { _noise += Math.Abs(lap); _noiseN++; }
                    }
                }
            }
            int n = (x1 - x0) * (y1 - y0);
            if (n > 0) { _colourRatio += (double)seen.Count / n; _tiles++; }
        }
    }

    private static List<Rectangle> Group(bool[] changed, int cols, int rows, int size, int w, int h)
    {
        var seen = new bool[changed.Length];
        var boxes = new List<Rectangle>();
        var stack = new Stack<int>();
        for (int s = 0; s < changed.Length; s++)
        {
            if (!changed[s] || seen[s]) continue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
            stack.Push(s);
            seen[s] = true;
            while (stack.Count > 0)
            {
                int k = stack.Pop(), tx = k % cols, ty = k / cols;
                count++;
                minX = Math.Min(minX, tx); maxX = Math.Max(maxX, tx);
                minY = Math.Min(minY, ty); maxY = Math.Max(maxY, ty);
                // 8-connected, with a one-tile gap allowed so a single edit doesn't splinter.
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = tx + dx, ny = ty + dy;
                        if (nx < 0 || ny < 0 || nx >= cols || ny >= rows) continue;
                        int nk = ny * cols + nx;
                        if (changed[nk] && !seen[nk]) { seen[nk] = true; stack.Push(nk); }
                    }
            }
            var r = Rectangle.FromLTRB(minX * size, minY * size, Math.Min(w, (maxX + 1) * size), Math.Min(h, (maxY + 1) * size));
            boxes.Add(r);
        }
        return boxes;
    }

    private static Rectangle Scale(Rectangle r, double sx, double sy) =>
        Rectangle.FromLTRB((int)Math.Floor(r.Left * sx), (int)Math.Floor(r.Top * sy), (int)Math.Ceiling(r.Right * sx), (int)Math.Ceiling(r.Bottom * sy));

    private static Pixels Resample(Pixels px, int w, int h)
    {
        using var src = px.ToBitmap();
        using var dst = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        }
        return Pixels.FromBitmap(dst);
    }
}
