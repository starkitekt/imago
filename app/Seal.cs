using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Imago;

/// <summary>
/// .snapseal: a capture encrypted with a password.
/// AES-256-GCM, key from PBKDF2-HMAC-SHA256 (600,000 rounds, 16-byte random salt), 12-byte random nonce.
/// The header is authenticated too, so no byte of the file can change without the open failing.
/// </summary>
internal static class Seal
{
    public const string Extension = ".snapseal";
    private static readonly byte[] Magic = "SNAPSEAL"u8.ToArray();
    private const byte Version = 1, KdfPbkdf2Sha256 = 1;
    public const int Iterations = 600_000;
    private const int MinIterations = 100_000, MaxIterations = 10_000_000;
    private const int HeaderLen = 8 + 1 + 1 + 4 + 16 + 12;
    private const int TagLen = 16;
    private const long MaxFile = 1L << 30;
    public const int MinPassword = 8;

    public sealed class WrongPasswordException() : Exception("Wrong password, or the file was changed.");

    public static byte[] Encrypt(byte[] png, string fileName, string password)
    {
        if (password.Length < MinPassword) throw new ArgumentException($"Use at least {MinPassword} characters.");
        byte[] name = Encoding.UTF8.GetBytes(Path.GetFileName(fileName));
        if (name.Length > ushort.MaxValue) name = name[..ushort.MaxValue];

        var header = new byte[HeaderLen];
        Magic.CopyTo(header, 0);
        header[8] = Version;
        header[9] = KdfPbkdf2Sha256;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(10), Iterations);
        RandomNumberGenerator.Fill(header.AsSpan(14, 16)); // salt
        RandomNumberGenerator.Fill(header.AsSpan(30, 12)); // nonce

        var plain = new byte[2 + name.Length + png.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(plain, (ushort)name.Length);
        name.CopyTo(plain, 2);
        png.CopyTo(plain, 2 + name.Length);

        var output = new byte[HeaderLen + plain.Length + TagLen];
        header.CopyTo(output, 0);
        byte[] key = DeriveKey(password, header.AsSpan(14, 16), Iterations);
        try
        {
            using var gcm = new AesGcm(key, TagLen);
            gcm.Encrypt(header.AsSpan(30, 12), plain, output.AsSpan(HeaderLen, plain.Length), output.AsSpan(HeaderLen + plain.Length), header);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
        return output;
    }

    public static (string FileName, byte[] Png) Decrypt(byte[] file, string password)
    {
        if (file.Length < HeaderLen + TagLen + 2 || file.Length > MaxFile || !file.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException("Not an Imago sealed file.");
        if (file[8] != Version || file[9] != KdfPbkdf2Sha256)
            throw new InvalidDataException("This sealed file needs a newer Imago.");
        uint iters = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(10));
        if (iters < MinIterations || iters > MaxIterations) throw new InvalidDataException("Sealed file header is invalid.");

        var header = file.AsSpan(0, HeaderLen);
        int ctLen = file.Length - HeaderLen - TagLen;
        var plain = new byte[ctLen];
        byte[] key = DeriveKey(password, header.Slice(14, 16), (int)iters);
        try
        {
            using var gcm = new AesGcm(key, TagLen);
            gcm.Decrypt(header.Slice(30, 12), file.AsSpan(HeaderLen, ctLen), file.AsSpan(HeaderLen + ctLen, TagLen), plain, header);
        }
        catch (AuthenticationTagMismatchException) { throw new WrongPasswordException(); }
        finally { CryptographicOperations.ZeroMemory(key); }

        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(plain);
        if (2 + nameLen > plain.Length) throw new InvalidDataException("Sealed file is damaged.");
        string name = Encoding.UTF8.GetString(plain, 2, nameLen);
        return (name, plain[(2 + nameLen)..]);
    }

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt, int iterations)
    {
        // Normalize so the same password typed on another keyboard layout or IME still matches.
        byte[] pw = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        try { return Rfc2898DeriveBytes.Pbkdf2(pw, salt, iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(pw); }
    }

    /// <summary>Rough password strength in bits, for the hint under the password box.</summary>
    public static int EstimateBits(string pw)
    {
        int pool = 0;
        if (pw.Any(char.IsLower)) pool += 26;
        if (pw.Any(char.IsUpper)) pool += 26;
        if (pw.Any(char.IsDigit)) pool += 10;
        if (pw.Any(c => !char.IsLetterOrDigit(c))) pool += 33;
        int distinct = pw.Distinct().Count();
        return pool == 0 ? 0 : (int)(Math.Min(pw.Length, distinct + 2) * Math.Log2(pool));
    }

    /// <summary>Seals a PNG next to itself. Returns the new path.</summary>
    public static string SealFile(string pngPath, string password, bool deleteOriginal)
    {
        byte[] png = File.ReadAllBytes(pngPath);
        byte[] sealedBytes = Encrypt(png, pngPath, password);
        string dir = Path.GetDirectoryName(pngPath)!;
        string outPath = Naming.Unique(dir, Path.GetFileNameWithoutExtension(pngPath), Extension);
        using (var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write))
            fs.Write(sealedBytes);
        // Check the sealed copy opens before touching the original.
        var (_, back) = Decrypt(File.ReadAllBytes(outPath), password);
        if (!back.AsSpan().SequenceEqual(png)) throw new IOException("Sealed copy did not read back correctly.");
        if (deleteOriginal) File.Delete(pngPath);
        return outPath;
    }
}
