using System.Collections.Specialized;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Imago;

internal sealed record VerifiedOptions(string Name, string Place, GeoFix? Location, string? TsaUrl);

internal sealed class CaptureResult
{
    public required string Path { get; init; }
    public required Bitmap Image { get; init; }
    public StampInfo? Stamp { get; init; }
    public List<string> Warnings { get; } = [];
}

/// <summary>Grabs pixels through snapcore.dll and turns them into a named, optionally signed PNG.</summary>
internal static class Capture
{
    public static unsafe Bitmap Grab(Rectangle r)
    {
        if (r.Width <= 0 || r.Height <= 0) throw new ArgumentException("Nothing to capture.");
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        int rc;
        try { rc = Native.sc_capture(r.X, r.Y, r.Width, r.Height, data.Scan0, data.Stride, (long)data.Stride * r.Height); }
        finally { bmp.UnlockBits(data); }
        if (rc != 0)
        {
            bmp.Dispose();
            throw new InvalidOperationException(rc switch
            {
                -2 => "That area is too large to capture.",
                -3 => "Windows refused the screen copy. Is the session locked?",
                _ => $"Capture failed (code {rc}).",
            });
        }
        return bmp;
    }

    public static Rectangle FullRect(Settings s) =>
        s.FullScreen == "all" ? Native.VirtualScreen() : Native.MonitorAt(Cursor.Position, out _);

    /// <summary>The window to capture for window mode: the foreground window unless it is ours.</summary>
    public static (Rectangle Rect, long Hwnd) TargetWindow()
    {
        int me = Environment.ProcessId;
        long fg = Native.sc_foreground();
        var info = Native.WindowInfo(fg);
        if (info is { } i && i.win.Pid != me && i.win.W > 0 && i.win.H > 0)
            return (Clip(i.win.Rect), fg);
        var top = Native.Windows().FirstOrDefault(w => w.Pid != me);
        return top.Hwnd != 0 ? (Clip(top.Rect), top.Hwnd) : (Native.MonitorAt(Cursor.Position, out _), 0);
    }

    public static Rectangle Clip(Rectangle r)
    {
        r.Intersect(Native.VirtualScreen());
        return r;
    }

    /// <summary>The topmost window under a point, from a list taken before the overlay appeared.</summary>
    public static long WindowAt(ScWindow[] windows, Point p) =>
        windows.FirstOrDefault(w => w.Rect.Contains(p)).Hwnd;

    public static List<string> ProtectedWarnings(Rectangle area)
    {
        var names = Native.Windows()
            .Where(w => w.IsProtected && w.Rect.IntersectsWith(area))
            .Select(w => Naming.Describe(w.Hwnd).App)
            .Distinct()
            .ToList();
        return names.Count == 0 ? [] : [$"{string.Join(", ", names)} blocks screenshots, so that part is black."];
    }

    /// <summary>Stamps and signs (if verified), encodes and saves. Takes ownership of img.</summary>
    public static async Task<CaptureResult> FinishAsync(Bitmap img, string mode, Rectangle area, CaptureSource src, string outDir, VerifiedOptions? v, IReadOnlyList<EditOp>? edits = null)
    {
        var warnings = ProtectedWarnings(area);
        StampInfo? info = null;
        var chunks = new List<PngChunk>();
        var now = DateTime.Now;

        if (v is not null)
        {
            info = StampInfo.Create(mode, src, v.Name, v.Place, v.Location);
            now = info.Captured.LocalDateTime;
        }

        var px = Pixels.FromBitmap(img);
        if (info is not null)
        {
            // Invisible mark first, so the signed hash and tile map describe the pixels as saved.
            await Task.Run(() => Watermark.Embed(px, info.Code));
            var marked = px.ToBitmap();
            img.Dispose();
            img = marked;
            var manifest = await Task.Run(() => Provenance.BuildManifest(info, px, edits));
            var (chunk, tsaErr) = await Provenance.SignAsync(manifest, v!.TsaUrl);
            chunks.Add(chunk);
            if (Png.ReadITxt(chunk.Data) is { } env) Ledger.Save(info.StampId, env.Text);
            if (tsaErr is not null) warnings.Add("Saved without a trusted timestamp: " + tsaErr);
        }
        chunks.Add(new PngChunk("tEXt", [.. "Software\0"u8, .. System.Text.Encoding.Latin1.GetBytes("Imago " + Native.AppVersion)]));
        byte[] png = await Task.Run(() => Png.Encode(px, chunks));

        Directory.CreateDirectory(outDir);
        string baseName = Naming.FileName(src, now);
        string path = Write(outDir, baseName, png);
        var result = new CaptureResult { Path = path, Image = img, Stamp = info };
        result.Warnings.AddRange(warnings);
        return result;
    }

    private static string Write(string dir, string baseName, byte[] bytes)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            string p = Naming.Unique(dir, baseName, ".png");
            try
            {
                using var fs = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                fs.Write(bytes);
                return p;
            }
            catch (IOException) when (File.Exists(p)) { /* lost a race with another save; try the next name */ }
        }
        throw new IOException("Could not find a free file name.");
    }

    /// <summary>Puts the image (bitmap and PNG) and the file on the clipboard.</summary>
    public static bool ToClipboard(Bitmap img, string? path)
    {
        var obj = new DataObject();
        obj.SetData(DataFormats.Bitmap, true, img);
        if (path is not null)
        {
            obj.SetData("PNG", false, new MemoryStream(File.ReadAllBytes(path)));
            obj.SetFileDropList(new StringCollection { path });
        }
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(obj, copy: true, retryTimes: 3, retryDelay: 60); return true; }
            catch (ExternalException) { Thread.Sleep(80); }
        }
        return false;
    }
}
