using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Imago;

/// <summary>Top-down BGRA pixels, stride = W * 4, alpha always 255.</summary>
internal sealed class Pixels
{
    public const int MaxDim = 32768;
    public const long MaxBytes = 1L << 30; // 1 GB of pixels

    public int W { get; }
    public int H { get; }
    public byte[] Bgra { get; }

    public Pixels(int w, int h, byte[]? bgra = null)
    {
        if (w <= 0 || h <= 0 || w > MaxDim || h > MaxDim || (long)w * h * 4 > MaxBytes)
            throw new InvalidDataException($"Image size {w} x {h} is out of range.");
        W = w; H = h;
        Bgra = bgra ?? new byte[w * h * 4];
        if (Bgra.Length != w * h * 4) throw new ArgumentException("Pixel buffer has the wrong length.");
    }

    public static unsafe Pixels FromBitmap(Bitmap bmp, Rectangle? crop = null)
    {
        var r = crop ?? new Rectangle(0, 0, bmp.Width, bmp.Height);
        r.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
        var px = new Pixels(r.Width, r.Height);
        var data = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            fixed (byte* dst = px.Bgra)
            {
                for (int y = 0; y < r.Height; y++)
                {
                    byte* src = (byte*)data.Scan0 + (long)y * data.Stride;
                    byte* d = dst + (long)y * r.Width * 4;
                    Buffer.MemoryCopy(src, d, r.Width * 4, r.Width * 4);
                    for (int x = 3; x < r.Width * 4; x += 4) d[x] = 255;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return px;
    }

    public unsafe Bitmap ToBitmap()
    {
        var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            fixed (byte* src = Bgra)
                for (int y = 0; y < H; y++)
                    Buffer.MemoryCopy(src + (long)y * W * 4, (byte*)data.Scan0 + (long)y * data.Stride, W * 4, W * 4);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    /// <summary>SHA-256 over a fixed label, the size and the RGB bytes. Alpha and file encoding don't count.</summary>
    public byte[] CanonicalHash()
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData("IMAGO-PX1\0"u8);
        Span<byte> hdr = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr, (uint)W);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], (uint)H);
        h.AppendData(hdr);
        var row = new byte[W * 3];
        for (int y = 0; y < H; y++)
        {
            int s = y * W * 4;
            for (int x = 0, d = 0; x < W; x++, s += 4, d += 3)
            {
                row[d] = Bgra[s + 2]; row[d + 1] = Bgra[s + 1]; row[d + 2] = Bgra[s];
            }
            h.AppendData(row);
        }
        return h.GetHashAndReset();
    }
}

internal sealed record PngChunk(string Type, byte[] Data);

internal sealed class PngImage
{
    public required Pixels Pixels { get; init; }
    public required List<PngChunk> Chunks { get; init; }
    public bool HadAlpha { get; init; }
}

/// <summary>A small, strict PNG encoder and decoder (8-bit RGB/RGBA, no interlace).</summary>
internal static class Png
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private const int IdatSize = 256 * 1024;
    private const int MaxChunk = 64 * 1024 * 1024;

    // ---------- encode ----------

    /// <summary>Encodes RGB with adaptive per-row filters. Extra chunks go between IHDR and IDAT.</summary>
    public static byte[] Encode(Pixels px, IEnumerable<PngChunk>? extra = null, CompressionLevel level = CompressionLevel.Optimal)
    {
        var ms = new MemoryStream(px.W * px.H + 4096);
        ms.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)px.W);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)px.H);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit truecolor
        WriteChunk(ms, "IHDR", ihdr);
        foreach (var c in extra ?? []) WriteChunk(ms, c.Type, c.Data);

        byte[] z = Deflate(px, level);
        for (int off = 0; off < z.Length; off += IdatSize)
            WriteChunk(ms, "IDAT", z.AsSpan(off, Math.Min(IdatSize, z.Length - off)));
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>Writes an already parsed chunk list back out (used by tests to build tampered files).</summary>
    public static byte[] Write(IEnumerable<PngChunk> chunks)
    {
        var ms = new MemoryStream();
        ms.Write(Signature);
        foreach (var c in chunks) WriteChunk(ms, c.Type, c.Data);
        return ms.ToArray();
    }

    private static byte[] Deflate(Pixels px, CompressionLevel level)
    {
        int w = px.W, rowLen = w * 3;
        var outMs = new MemoryStream(px.W * px.H);
        using (var z = new ZLibStream(outMs, level, leaveOpen: true))
        {
            var prev = new byte[rowLen];
            var cur = new byte[rowLen];
            var cand = new byte[5][];
            for (int f = 0; f < 5; f++) cand[f] = new byte[rowLen + 1];

            for (int y = 0; y < px.H; y++)
            {
                int s = y * w * 4;
                for (int x = 0, d = 0; x < w; x++, s += 4, d += 3)
                {
                    cur[d] = px.Bgra[s + 2]; cur[d + 1] = px.Bgra[s + 1]; cur[d + 2] = px.Bgra[s];
                }
                int best = 0; long bestSum = long.MaxValue;
                for (int f = 0; f < 5; f++)
                {
                    var o = cand[f];
                    o[0] = (byte)f;
                    long sum = 0;
                    for (int i = 0; i < rowLen; i++)
                    {
                        int a = i >= 3 ? cur[i - 3] : 0, b = prev[i], c = i >= 3 ? prev[i - 3] : 0;
                        int p = f switch
                        {
                            0 => 0,
                            1 => a,
                            2 => b,
                            3 => (a + b) >> 1,
                            _ => Paeth(a, b, c),
                        };
                        byte v = (byte)(cur[i] - p);
                        o[i + 1] = v;
                        sum += v < 128 ? v : 256 - v;
                        if (sum >= bestSum) break;
                    }
                    if (sum < bestSum) { bestSum = sum; best = f; }
                }
                z.Write(cand[best]);
                (prev, cur) = (cur, prev);
            }
        }
        return outMs.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        Span<byte> t = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, t);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFF, t), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    /// <summary>An iTXt chunk with an uncompressed UTF-8 value.</summary>
    public static PngChunk ITxt(string keyword, string text)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.Latin1.GetBytes(keyword));
        ms.Write([0, 0, 0, 0, 0]); // keyword end, not compressed, method, empty language, empty translation
        ms.Write(Encoding.UTF8.GetBytes(text));
        return new PngChunk("iTXt", ms.ToArray());
    }

    /// <summary>Reads an iTXt chunk. Returns null if it is malformed.</summary>
    public static (string Keyword, string Text)? ReadITxt(byte[] d)
    {
        int k = Array.IndexOf(d, (byte)0);
        if (k < 1 || k > 79 || k + 3 > d.Length) return null;
        string keyword = Encoding.Latin1.GetString(d, 0, k);
        bool compressed = d[k + 1] == 1;
        int lang = Array.IndexOf(d, (byte)0, k + 3);
        if (lang < 0) return null;
        int trans = Array.IndexOf(d, (byte)0, lang + 1);
        if (trans < 0) return null;
        var body = d.AsSpan(trans + 1);
        try
        {
            if (!compressed) return (keyword, new UTF8Encoding(false, true).GetString(body));
            using var z = new ZLibStream(new MemoryStream(body.ToArray()), CompressionMode.Decompress);
            var buf = new byte[4 * 1024 * 1024];
            int n = 0, r;
            while ((r = z.Read(buf, n, buf.Length - n)) > 0) { n += r; if (n == buf.Length) return null; }
            return (keyword, new UTF8Encoding(false, true).GetString(buf, 0, n));
        }
        catch (Exception e) when (e is DecoderFallbackException or InvalidDataException) { return null; }
    }

    // ---------- decode ----------

    public static PngImage Decode(byte[] file)
    {
        if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("Not a PNG file.");
        var chunks = new List<PngChunk>();
        int pos = 8;
        bool end = false;
        while (!end)
        {
            if (pos + 12 > file.Length) throw new InvalidDataException("PNG is cut short.");
            uint len = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
            if (len > MaxChunk || pos + 12 + len > file.Length) throw new InvalidDataException("PNG chunk length is invalid.");
            var typeSpan = file.AsSpan(pos + 4, 4);
            foreach (byte b in typeSpan)
                if (!((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z'))) throw new InvalidDataException("PNG chunk type is invalid.");
            string type = Encoding.ASCII.GetString(typeSpan);
            var data = file.AsSpan(pos + 8, (int)len);
            uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFF, typeSpan), data) ^ 0xFFFFFFFF;
            if (crc != BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos + 8 + (int)len)))
                throw new InvalidDataException($"PNG chunk {type} is damaged (CRC mismatch).");
            chunks.Add(new PngChunk(type, data.ToArray()));
            pos += 12 + (int)len;
            if (type == "IEND") end = true;
        }
        if (pos != file.Length) throw new InvalidDataException("PNG has data after its end.");
        if (chunks.Count < 3 || chunks[0].Type != "IHDR" || chunks[0].Data.Length != 13)
            throw new InvalidDataException("PNG header is missing.");

        var h = chunks[0].Data;
        int w = (int)BinaryPrimitives.ReadUInt32BigEndian(h), ht = (int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4));
        if (w <= 0 || ht <= 0 || w > Pixels.MaxDim || ht > Pixels.MaxDim) throw new InvalidDataException("PNG size is out of range.");
        if (h[8] != 8 || (h[9] != 2 && h[9] != 6) || h[10] != 0 || h[11] != 0 || h[12] != 0)
            throw new InvalidDataException("Only 8-bit RGB or RGBA, non-interlaced PNGs are supported.");
        int bpp = h[9] == 6 ? 4 : 3;

        // Concatenate IDAT (must be consecutive) and inflate with a hard size limit.
        int first = chunks.FindIndex(c => c.Type == "IDAT");
        if (first < 0) throw new InvalidDataException("PNG has no image data.");
        int last = chunks.FindLastIndex(c => c.Type == "IDAT");
        if (chunks.Skip(first).Take(last - first + 1).Any(c => c.Type != "IDAT"))
            throw new InvalidDataException("PNG image data is split.");
        var zms = new MemoryStream();
        for (int i = first; i <= last; i++) zms.Write(chunks[i].Data);
        zms.Position = 0;

        long rowLen = (long)w * bpp;
        long expected = (rowLen + 1) * ht;
        if (expected > Pixels.MaxBytes + ht) throw new InvalidDataException("PNG is too large.");
        var raw = new byte[expected];
        using (var z = new ZLibStream(zms, CompressionMode.Decompress))
        {
            int n = 0, r;
            while (n < raw.Length && (r = z.Read(raw, n, raw.Length - n)) > 0) n += r;
            if (n != raw.Length) throw new InvalidDataException("PNG image data is cut short.");
            if (z.Read(new byte[1], 0, 1) != 0) throw new InvalidDataException("PNG image data is longer than its size.");
        }

        var px = new Pixels(w, ht);
        var prev = new byte[rowLen];
        var cur = new byte[rowLen];
        bool hadAlpha = false;
        for (int y = 0; y < ht; y++)
        {
            long o = y * (rowLen + 1);
            byte f = raw[o];
            if (f > 4) throw new InvalidDataException("PNG filter is invalid.");
            for (int i = 0; i < rowLen; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int p = f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, _ => Paeth(a, b, c) };
                cur[i] = (byte)(raw[o + 1 + i] + p);
            }
            int d = y * w * 4;
            for (int x = 0, s = 0; x < w; x++, s += bpp, d += 4)
            {
                px.Bgra[d] = cur[s + 2]; px.Bgra[d + 1] = cur[s + 1]; px.Bgra[d + 2] = cur[s];
                px.Bgra[d + 3] = 255;
                if (bpp == 4 && cur[s + 3] != 255) hadAlpha = true;
            }
            (prev, cur) = (cur, prev);
        }
        return new PngImage { Pixels = px, Chunks = chunks, HadAlpha = hadAlpha };
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
