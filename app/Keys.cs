using System.Security.Cryptography;
using System.Text;

namespace Imago;

/// <summary>
/// The device's two signing keys.
/// ES256 (ECDSA P-256) lives in the TPM when there is one, so it cannot be copied off this PC.
/// ML-DSA-65 (post-quantum, FIPS 204) is stored encrypted with Windows DPAPI for this user.
/// </summary>
internal static class Keys
{
    private const string EcName = "Imago Signing Key (P-256)";
    private static readonly byte[] DpapiEntropy = "Imago ML-DSA v1"u8.ToArray();
    private static string PqPath => Path.Combine(Settings.Dir, "mldsa65.key");

    private static readonly Lazy<(ECDsaCng Key, bool Hardware)> Ec = new(OpenOrCreateEc, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<MLDsa?> Pq = new(OpenOrCreatePq, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool HardwareBacked => Ec.Value.Hardware;
    public static bool PostQuantumAvailable => Pq.Value is not null;

    public static byte[] EcPublicKey() => Ec.Value.Key.ExportSubjectPublicKeyInfo();
    public static byte[] EcSign(byte[] data) => Ec.Value.Key.SignData(data, HashAlgorithmName.SHA256);

    public static byte[]? PqPublicKey()
    {
        var k = Pq.Value;
        if (k is null) return null;
        var pub = new byte[MLDsaAlgorithm.MLDsa65.PublicKeySizeInBytes];
        k.ExportMLDsaPublicKey(pub);
        return pub;
    }

    public static byte[]? PqSign(byte[] data, byte[] context) => Pq.Value?.SignData(data, context);

    /// <summary>Short, readable key fingerprint: first 8 bytes of SHA-256, as XXXX-XXXX-XXXX-XXXX.</summary>
    public static string Fingerprint(byte[] publicKey)
    {
        var h = SHA256.HashData(publicKey);
        var hex = Convert.ToHexString(h, 0, 8);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
    }

    /// <summary>This PC's ES256 key fingerprint, without creating a key if there is none yet.</summary>
    public static string? LocalFingerprintIfExists()
    {
        if (Ec.IsValueCreated) return Fingerprint(EcPublicKey());
        foreach (var p in new[] { CngProvider.MicrosoftPlatformCryptoProvider, CngProvider.MicrosoftSoftwareKeyStorageProvider })
        {
            try
            {
                if (!CngKey.Exists(EcName, p)) continue;
                using var k = CngKey.Open(EcName, p);
                using var e = new ECDsaCng(k);
                return Fingerprint(e.ExportSubjectPublicKeyInfo());
            }
            catch (CryptographicException) { }
        }
        return null;
    }

    private static (ECDsaCng, bool) OpenOrCreateEc()
    {
        // Prefer the TPM. Its keys are non-exportable by design.
        try { return (new ECDsaCng(OpenOrCreate(CngProvider.MicrosoftPlatformCryptoProvider)), true); }
        catch (CryptographicException) { }
        return (new ECDsaCng(OpenOrCreate(CngProvider.MicrosoftSoftwareKeyStorageProvider)), false);
    }

    private static CngKey OpenOrCreate(CngProvider provider)
    {
        if (CngKey.Exists(EcName, provider)) return CngKey.Open(EcName, provider);
        var p = new CngKeyCreationParameters
        {
            Provider = provider,
            KeyUsage = CngKeyUsages.Signing,
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.None,
        };
        return CngKey.Create(CngAlgorithm.ECDsaP256, EcName, p);
    }

    private static MLDsa? OpenOrCreatePq()
    {
        if (!MLDsa.IsSupported) return null;
        var alg = MLDsaAlgorithm.MLDsa65;
        byte[]? seed = null;
        try
        {
            if (File.Exists(PqPath))
            {
                try
                {
                    seed = ProtectedData.Unprotect(File.ReadAllBytes(PqPath), DpapiEntropy, DataProtectionScope.CurrentUser);
                    if (seed.Length == alg.PrivateSeedSizeInBytes) return MLDsa.ImportMLDsaPrivateSeed(alg, seed);
                }
                catch (CryptographicException) { }
                // The file is damaged or belongs to another account. Keep it, make a new key.
                File.Move(PqPath, PqPath + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
            }
            var key = MLDsa.GenerateKey(alg);
            seed = new byte[alg.PrivateSeedSizeInBytes];
            key.ExportMLDsaPrivateSeed(seed);
            Directory.CreateDirectory(Settings.Dir);
            string tmp = PqPath + ".tmp";
            File.WriteAllBytes(tmp, ProtectedData.Protect(seed, DpapiEntropy, DataProtectionScope.CurrentUser));
            File.Move(tmp, PqPath, overwrite: true);
            return key;
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
        finally
        {
            if (seed is not null) CryptographicOperations.ZeroMemory(seed);
        }
    }

    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("ES256 key ").Append(Fingerprint(EcPublicKey()))
          .Append(HardwareBacked ? " (in TPM, cannot be copied)" : " (software key, no TPM found)");
        sb.Append(PostQuantumAvailable ? "; ML-DSA-65 post-quantum key on" : "; ML-DSA-65 not available on this Windows build");
        return sb.ToString();
    }
}
