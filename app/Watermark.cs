using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Imago;

/// <summary>
/// Invisible 40-bit watermark that survives JPEG and resizing. Spread spectrum on the luma DCT of 8x8 blocks:
/// every block carries one bit of a 64-bit frame (40-bit code, 16-bit key tag, CRC-8) as a keyed +/-1 chip
/// pattern over a few low/mid frequency coefficients. Detection correlates the same coefficients, sums soft votes
/// per bit and accepts only when the tag and CRC both match.
/// </summary>
internal static class Watermark
{
    /// <summary>Smaller images carry too few blocks for a reliable mark: Embed leaves them alone, Detect returns null.</summary>
    public const int MinSide = 192;

    private const int Bs = 8;            // block size
    private const int Taps = 6;          // coefficients modulated per block
    private const int RefTaps = 10;      // unmarked coefficients used to estimate the noise per block
    private const int MaxDelta = 4;      // hard cap on the change of any single channel value
    private const int Rounds = 3;        // clip/rounding compensation passes while embedding
    private const float Gain = 0.9f;
    private const float MaxBoost = 2.4f; // compensation never raises a coefficient above this multiple of its target

    // Strength (DCT coefficient amplitude) grows with local texture but is never zero.
    private const float FlatStrength = 4.8f;
    private const float TexStrength = 5.4f;
    private const float TexSd = 24f;

    private const float NoiseFloor = 4f; // detector: vote weight = 1 / (block noise + this)
    private const int FlipPool = 10;     // detector: least confident bits that may be flipped to satisfy the CRC

    private const ulong Mask40 = (1UL << 40) - 1;
    private const ulong Mask56 = (1UL << 56) - 1;

    // Candidate scale factors of a copy relative to the original. The input is resampled by 1/f before detecting.
    private static readonly double[] Factors = [1, 0.5, 2.0 / 3.0, 0.75, 0.8, 1.25, 1.5, 2];

    private static readonly byte[] Key =
    [
        0x4B, 0xD1, 0x7E, 0x29, 0xA6, 0x03, 0xF8, 0x5C, 0x91, 0x6A, 0xE2, 0x37, 0x0D, 0xB4, 0x58, 0xCF,
        0x72, 0x1E, 0xA9, 0x84, 0xD3, 0x6F, 0x40, 0xBB, 0x15, 0xE7, 0x9C, 0x2A, 0x68, 0xF1, 0x3D, 0x86,
    ];

    private const ulong Golden = 0x9E3779B97F4A7C15UL;

    private static readonly ulong Seed = MakeSeed();
    private static readonly ulong Tag = Fin(Seed ^ 0x54414721UL) >> 48 | 0x8001; // 16-bit key tag, never 0

    // (vertical, horizontal) frequency of the modulated and of the reference coefficients.
    private static readonly (int U, int V)[] SigPos = [(1, 2), (2, 1), (0, 3), (3, 0), (2, 2), (1, 1)];
    private static readonly (int U, int V)[] RefPos = [(0, 1), (1, 0), (0, 2), (2, 0), (1, 3), (3, 1), (0, 4), (4, 0), (2, 3), (3, 2)];
    private static readonly float[] SigB = MakeBasis(SigPos);
    private static readonly float[] RefB = MakeBasis(RefPos);

    // ---------- public API ----------

    /// <summary>payload is the low 40 bits of <paramref name="code"/>. Modifies px.Bgra in place. Alpha untouched.</summary>
    public static void Embed(Pixels px, ulong code)
    {
        if (Math.Min(px.W, px.H) < MinSide) return;
        int nbx = px.W / Bs, nby = px.H / Bs;
        ulong frame = BuildFrame(code);
        byte[] map = BitMap(nbx, nby);
        byte[] bgra = px.Bgra;
        int w = px.W;
        Parallel.For(0, nby, by =>
        {
            for (int bx = 0; bx < nbx; bx++)
                EmbedBlock(bgra, w, bx, by, ((frame >> map[by * nbx + bx]) & 1) != 0);
        });
    }

    /// <summary>Returns the 40-bit code, or null when no valid mark is found.</summary>
    public static ulong? Detect(Pixels px)
    {
        if (Math.Min(px.W, px.H) < MinSide / 2) return null; // a 2x upscale must still reach MinSide
        float[] luma = ToLuma(px);
        foreach (double f in Factors)
        {
            ulong? r;
            if (f == 1) r = Math.Min(px.W, px.H) < MinSide ? null : DetectLuma(luma, px.W, px.H);
            else
            {
                int ow = (int)Math.Round(px.W / f), oh = (int)Math.Round(px.H / f);
                if (Math.Min(ow, oh) < MinSide || (long)ow * oh > 60_000_000) continue;
                r = DetectLuma(Resample(luma, px.W, px.H, ow, oh), ow, oh);
            }
            if (r != null) return r;
        }
        return null;
    }

    // ---------- embedding ----------

    private static void EmbedBlock(byte[] bgra, int w, int bx, int by, bool bit)
    {
        Span<float> y = stackalloc float[64];
        Span<float> tgt = stackalloc float[Taps];
        Span<float> d = stackalloc float[Taps];
        Span<float> a = stackalloc float[Taps];
        Span<int> iv = stackalloc int[64];

        int p0 = (by * Bs * w + bx * Bs) * 4;
        float sum = 0, sq = 0;
        for (int r = 0; r < Bs; r++)
            for (int c = 0; c < Bs; c++)
            {
                int i = p0 + (r * w + c) * 4;
                float l = 0.114f * bgra[i] + 0.587f * bgra[i + 1] + 0.299f * bgra[i + 2];
                y[r * Bs + c] = l; sum += l; sq += l * l;
            }
        float sd = MathF.Sqrt(MathF.Max(0, sq / 64 - sum * sum / 4096));
        float s = FlatStrength + (TexStrength - FlatStrength) * MathF.Min(1f, sd / TexSd);

        ulong h = Hash(bx, by, 1);
        for (int j = 0; j < Taps; j++)
        {
            bool pos = (((h >> j) & 1) != 0) == bit;
            tgt[j] = pos ? s : -s;
            d[j] = tgt[j];
        }

        // Synthesize the pattern, then re-measure what survives clipping and rounding and top it up.
        for (int round = 0; ; round++)
        {
            for (int p = 0; p < 64; p++)
            {
                float acc = 0;
                for (int j = 0; j < Taps; j++) acc += d[j] * SigB[j * 64 + p];
                iv[p] = Math.Clamp((int)MathF.Round(acc), -MaxDelta, MaxDelta);
            }
            if (round == Rounds) break;

            a.Clear();
            for (int r = 0; r < Bs; r++)
                for (int c = 0; c < Bs; c++)
                {
                    int q = iv[r * Bs + c];
                    if (q == 0) continue;
                    int i = p0 + (r * w + c) * 4;
                    float dy = 0.114f * (Math.Clamp(bgra[i] + q, 0, 255) - bgra[i])
                             + 0.587f * (Math.Clamp(bgra[i + 1] + q, 0, 255) - bgra[i + 1])
                             + 0.299f * (Math.Clamp(bgra[i + 2] + q, 0, 255) - bgra[i + 2]);
                    for (int j = 0; j < Taps; j++) a[j] += dy * SigB[j * 64 + r * Bs + c];
                }
            for (int j = 0; j < Taps; j++)
            {
                float lim = MaxBoost * s;
                d[j] = Math.Clamp(d[j] + (tgt[j] - a[j]) * Gain, -lim, lim);
            }
        }

        for (int r = 0; r < Bs; r++)
            for (int c = 0; c < Bs; c++)
            {
                int q = iv[r * Bs + c];
                if (q == 0) continue;
                int i = p0 + (r * w + c) * 4;
                bgra[i] = (byte)Math.Clamp(bgra[i] + q, 0, 255);
                bgra[i + 1] = (byte)Math.Clamp(bgra[i + 1] + q, 0, 255);
                bgra[i + 2] = (byte)Math.Clamp(bgra[i + 2] + q, 0, 255);
            }
    }

    // ---------- detection ----------

    private static ulong? DetectLuma(float[] luma, int w, int h)
    {
        int nbx = w / Bs, nby = h / Bs;
        if (nbx < 8 || nby < 8) return null;
        byte[] map = BitMap(nbx, nby);
        var s = new double[64];
        var w2 = new double[64];

        Parallel.For(0, nby,
            () => new double[128],
            (by, _, acc) =>
            {
                Span<float> y = stackalloc float[64];
                for (int bx = 0; bx < nbx; bx++)
                {
                    int p0 = by * Bs * w + bx * Bs;
                    for (int r = 0; r < Bs; r++)
                        luma.AsSpan(p0 + r * w, Bs).CopyTo(y.Slice(r * Bs, Bs));

                    ulong hh = Hash(bx, by, 1);
                    float vote = 0;
                    for (int j = 0; j < Taps; j++)
                    {
                        float z = Dot64(y, SigB.AsSpan(j * 64, 64));
                        vote += ((hh >> j) & 1) != 0 ? z : -z;
                    }
                    float noise = 0;
                    for (int j = 0; j < RefTaps; j++)
                    {
                        float z = Dot64(y, RefB.AsSpan(j * 64, 64));
                        noise += z * z;
                    }
                    noise /= RefTaps;
                    float wt = 1f / (noise + NoiseFloor);
                    int b = map[by * nbx + bx];
                    acc[b] += wt * vote;
                    acc[64 + b] += wt * wt;
                }
                return acc;
            },
            acc =>
            {
                lock (s)
                    for (int b = 0; b < 64; b++) { s[b] += acc[b]; w2[b] += acc[64 + b]; }
            });

        return Decode(s, w2);
    }

    private static ulong? Decode(double[] s, double[] w2)
    {
        var z = new double[64];
        ulong raw = 0;
        for (int b = 0; b < 64; b++)
        {
            z[b] = s[b] / Math.Sqrt(w2[b] + 1e-12);
            if (s[b] > 0) raw |= 1UL << b;
        }
        if (Check(raw, out ulong code)) return code;

        // Try flipping up to two of the least confident bits; tag and CRC must still match.
        int[] ord = Enumerable.Range(0, 64).OrderBy(b => Math.Abs(z[b])).Take(FlipPool).ToArray();
        for (int i = 0; i < ord.Length; i++)
        {
            ulong one = raw ^ (1UL << ord[i]);
            if (Check(one, out code)) return code;
            for (int j = i + 1; j < ord.Length; j++)
                if (Check(one ^ (1UL << ord[j]), out code)) return code;
        }
        return null;
    }

    // ---------- frame ----------

    private static ulong BuildFrame(ulong code)
    {
        ulong fr = (code & Mask40) | (Tag << 40);
        return fr | (ulong)Crc8(fr) << 56;
    }

    private static bool Check(ulong frame, out ulong code)
    {
        code = frame & Mask40;
        return (ushort)(frame >> 40) == (ushort)Tag && (byte)(frame >> 56) == Crc8(frame & Mask56);
    }

    /// <summary>CRC-8 (poly 0x07) over the low 56 bits, little-endian bytes.</summary>
    private static byte Crc8(ulong v)
    {
        byte crc = 0;
        for (int i = 0; i < 7; i++)
        {
            crc ^= (byte)(v >> (8 * i));
            for (int k = 0; k < 8; k++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
        }
        return crc;
    }

    // ---------- keyed layout ----------

    /// <summary>Frame bit index per block. Every 8x8 tile of blocks holds a keyed permutation, so each bit gets one
    /// block per tile and the layout does not depend on the image size.</summary>
    private static byte[] BitMap(int nbx, int nby)
    {
        var map = new byte[nbx * nby];
        Span<byte> perm = stackalloc byte[64];
        for (int ty = 0; ty * 8 < nby; ty++)
            for (int tx = 0; tx * 8 < nbx; tx++)
            {
                ulong st = Hash(tx, ty, 2);
                for (int i = 0; i < 64; i++) perm[i] = (byte)i;
                for (int i = 63; i > 0; i--)
                {
                    int j = (int)(Next(ref st) % (ulong)(i + 1));
                    (perm[i], perm[j]) = (perm[j], perm[i]);
                }
                for (int ly = 0; ly < 8; ly++)
                    for (int lx = 0; lx < 8; lx++)
                    {
                        int bx = tx * 8 + lx, by = ty * 8 + ly;
                        if (bx < nbx && by < nby) map[by * nbx + bx] = perm[ly * 8 + lx];
                    }
            }
        return map;
    }

    private static ulong MakeSeed()
    {
        ulong s = Golden;
        for (int i = 0; i < Key.Length; i += 8)
            s = Fin(s ^ BinaryPrimitives.ReadUInt64LittleEndian(Key.AsSpan(i, 8))) + Golden;
        return s;
    }

    private static ulong Fin(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Next(ref ulong state)
    {
        state += Golden;
        return Fin(state);
    }

    private static ulong Hash(int a, int b, int salt)
    {
        ulong z = Fin(Seed ^ ((ulong)salt * 0xD6E8FEB86659FD93UL));
        z = Fin(z + (uint)a * Golden);
        return Fin(z + (uint)b * 0xC2B2AE3D27D4EB4FUL);
    }

    // ---------- DCT and resampling ----------

    private static float[] MakeBasis((int U, int V)[] pos)
    {
        var t = new float[pos.Length * 64];
        for (int k = 0; k < pos.Length; k++)
        {
            var (u, v) = pos[k];
            double au = u == 0 ? Math.Sqrt(1.0 / 8) : 0.5, av = v == 0 ? Math.Sqrt(1.0 / 8) : 0.5;
            for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                    t[k * 64 + r * 8 + c] = (float)(au * av
                        * Math.Cos((2 * r + 1) * u * Math.PI / 16) * Math.Cos((2 * c + 1) * v * Math.PI / 16));
        }
        return t;
    }

    private static float Dot64(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var acc = Vector<float>.Zero;
        for (int i = 0; i < va.Length; i++) acc += va[i] * vb[i];
        return Vector.Sum(acc);
    }

    private static float[] ToLuma(Pixels px)
    {
        var l = new float[px.W * px.H];
        byte[] s = px.Bgra;
        int w = px.W;
        Parallel.For(0, px.H, y =>
        {
            for (int x = 0, i = y * w * 4, o = y * w; x < w; x++, i += 4, o++)
                l[o] = 0.114f * s[i] + 0.587f * s[i + 1] + 0.299f * s[i + 2];
        });
        return l;
    }

    private sealed class Kernel(int[] idx, float[] w, int taps)
    {
        public readonly int[] Idx = idx;
        public readonly float[] W = w;
        public readonly int Taps = taps;
    }

    /// <summary>Catmull-Rom weights, stretched when shrinking so the result is band limited.</summary>
    private static Kernel MakeKernel(int sn, int on)
    {
        double ratio = (double)sn / on, scale = Math.Max(1, ratio), radius = 2 * scale;
        int taps = (int)Math.Ceiling(radius * 2) + 1;
        var idx = new int[on * taps];
        var wt = new float[on * taps];
        for (int o = 0; o < on; o++)
        {
            double center = (o + 0.5) * ratio - 0.5;
            int i0 = (int)Math.Ceiling(center - radius);
            double total = 0;
            for (int t = 0; t < taps; t++)
            {
                double k = Cubic(Math.Abs((i0 + t - center) / scale));
                wt[o * taps + t] = (float)k;
                idx[o * taps + t] = Math.Clamp(i0 + t, 0, sn - 1);
                total += k;
            }
            for (int t = 0; t < taps; t++) wt[o * taps + t] = (float)(wt[o * taps + t] / total);
        }
        return new Kernel(idx, wt, taps);
    }

    private static double Cubic(double x) =>
        x < 1 ? 1.5 * x * x * x - 2.5 * x * x + 1
        : x < 2 ? -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2
        : 0;

    private static float[] Resample(float[] src, int sw, int sh, int ow, int oh)
    {
        Kernel kx = MakeKernel(sw, ow), ky = MakeKernel(sh, oh);
        var tmp = new float[ow * sh];
        Parallel.For(0, sh, y =>
        {
            int row = y * sw, orow = y * ow;
            for (int x = 0; x < ow; x++)
            {
                float acc = 0;
                int k0 = x * kx.Taps;
                for (int t = 0; t < kx.Taps; t++) acc += src[row + kx.Idx[k0 + t]] * kx.W[k0 + t];
                tmp[orow + x] = acc;
            }
        });
        var dst = new float[ow * oh];
        Parallel.For(0, oh, y =>
        {
            var d = dst.AsSpan(y * ow, ow);
            int k0 = y * ky.Taps;
            for (int t = 0; t < ky.Taps; t++)
            {
                var s = tmp.AsSpan(ky.Idx[k0 + t] * ow, ow);
                float wgt = ky.W[k0 + t];
                for (int x = 0; x < d.Length; x++) d[x] += s[x] * wgt;
            }
        });
        return dst;
    }
}
