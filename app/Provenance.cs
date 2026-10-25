using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Imago;

internal sealed record GeoFix(double Lat, double Lon, double AccuracyM, string Source, DateTimeOffset Time);

/// <summary>What the stamp shows and the manifest records.</summary>
internal sealed class StampInfo
{
    public required string Id { get; init; }          // uuid
    public required string StampId { get; init; }     // IMG-XXXX-XXXX, shown on the image
    public required DateTimeOffset Captured { get; init; }
    public required string Name { get; init; }
    public string Place { get; init; } = "";
    public GeoFix? Location { get; init; }
    public required string Mode { get; init; }
    public required CaptureSource Source { get; init; }

    /// <summary>The 40 bits behind the stamp ID, woven into the pixels as the invisible watermark.</summary>
    public ulong Code { get; init; }

    public static StampInfo Create(string mode, CaptureSource src, string name, string place, GeoFix? loc)
    {
        Span<byte> r = stackalloc byte[5];
        RandomNumberGenerator.Fill(r);
        string code = Base32(r);
        ulong bits = 0;
        foreach (var x in r) bits = (bits << 8) | x;
        return new StampInfo
        {
            Code = bits,
            Id = Guid.NewGuid().ToString("D"),
            StampId = $"IMG-{code[..4]}-{code[4..8]}",
            Captured = DateTimeOffset.Now,
            Name = name, Place = place, Location = loc, Mode = mode, Source = src,
        };
    }

    // Crockford base32: no I, L, O, U, so codes read aloud cleanly.
    private static string Base32(ReadOnlySpan<byte> b)
    {
        const string A = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        ulong v = 0;
        foreach (var x in b) v = (v << 8) | x;
        var c = new char[8];
        for (int i = 7; i >= 0; i--) { c[i] = A[(int)(v & 31)]; v >>= 5; }
        return new string(c);
    }

    /// <summary>The stamp ID a watermark code stands for.</summary>
    public static string IdFor(ulong code)
    {
        Span<byte> b = stackalloc byte[5];
        for (int i = 4; i >= 0; i--) { b[i] = (byte)code; code >>= 8; }
        string c = Base32(b);
        return $"IMG-{c[..4]}-{c[4..8]}";
    }
}

internal enum VerifyStatus { Valid = 0, Tampered = 2, Unsigned = 3, Error = 4 }

internal sealed class VerifyResult
{
    public VerifyStatus Status { get; set; }
    public string Headline { get; set; } = "";
    public List<string> Details { get; } = [];
    public JsonObject? Manifest { get; set; }

    /// <summary>True once the signatures check out, so the manifest can be trusted even when the pixels changed.</summary>
    public bool SignatureValid { get; set; }

    public override string ToString() => Headline + Environment.NewLine + string.Join(Environment.NewLine, Details.Select(d => "  " + d));
}

/// <summary>Signs captures and checks signed captures.</summary>
internal static class Provenance
{
    public const string Keyword = "imago";
    private static readonly byte[] SigLabel = "IMAGO-SIG1\0"u8.ToArray();
    private static readonly byte[] PqContext = "imago-v1"u8.ToArray();
    private static readonly HashSet<string> AllowedChunks = ["IHDR", "IDAT", "IEND", "iTXt", "tEXt", "zTXt", "pHYs", "tIME"];

    // ---------- signing ----------

    public static byte[] BuildManifest(StampInfo s, Pixels px, IReadOnlyList<EditOp>? edits = null)
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 2);
            w.WriteString("app", "Imago " + Native.AppVersion);
            w.WriteString("id", s.Id);
            w.WriteString("stamp", s.StampId);
            w.WriteString("mode", s.Mode);
            w.WriteString("capturedUtc", s.Captured.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            w.WriteString("tzOffset", FormatOffset(s.Captured.Offset));
            w.WriteString("name", s.Name);
            if (s.Place.Length > 0) w.WriteString("place", s.Place);
            if (s.Location is { } g)
            {
                w.WriteStartObject("location");
                w.WriteNumber("lat", Math.Round(g.Lat, 6));
                w.WriteNumber("lon", Math.Round(g.Lon, 6));
                w.WriteNumber("accuracyM", Math.Round(g.AccuracyM, 1));
                w.WriteString("source", g.Source);
                w.WriteNumber("ageS", Math.Max(0, (int)(s.Captured - g.Time).TotalSeconds));
                w.WriteEndObject();
            }
            w.WriteStartObject("source");
            w.WriteString("app", s.Source.App);
            w.WriteString("exe", s.Source.ExeName);
            w.WriteEndObject();
            w.WriteNumber("width", px.W);
            w.WriteNumber("height", px.H);
            w.WriteString("pixelSha256", Convert.ToHexStringLower(px.CanonicalHash()));
            w.WriteString("watermark", "dct-ss-1");
            // The tile grid lets a review say where a later copy differs, even after recompression.
            var grid = Tiles.Measure(px);
            w.WriteStartObject("tiles");
            w.WriteNumber("size", grid.Size);
            w.WriteString("data", Convert.ToBase64String(grid.Pack()));
            w.WriteEndObject();
            // What was done in Imago's editor before signing: tool and box, never the hidden content.
            w.WriteStartArray("edits");
            foreach (var e in edits ?? [])
            {
                w.WriteStartObject();
                w.WriteString("tool", e.Tool);
                w.WriteNumber("x", e.Box.X);
                w.WriteNumber("y", e.Box.Y);
                w.WriteNumber("w", e.Box.Width);
                w.WriteNumber("h", e.Box.Height);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("device");
            w.WriteString("keyId", Keys.Fingerprint(Keys.EcPublicKey()));
            w.WriteBoolean("hardwareKey", Keys.HardwareBacked);
            if (Keys.PqPublicKey() is { } pq) w.WriteString("pqKeyId", Keys.Fingerprint(pq));
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>Signs the manifest with both keys and, if asked, gets an RFC 3161 timestamp. Returns the iTXt chunk.</summary>
    public static async Task<(PngChunk Chunk, string? TsaError)> SignAsync(byte[] manifest, string? tsaUrl, CancellationToken ct = default)
    {
        byte[] toSign = [.. SigLabel, .. manifest];
        byte[] ecSig = Keys.EcSign(toSign);

        var sigs = new JsonArray
        {
            new JsonObject { ["alg"] = "ES256", ["key"] = Convert.ToBase64String(Keys.EcPublicKey()), ["sig"] = Convert.ToBase64String(ecSig) },
        };
        if (Keys.PqSign(toSign, PqContext) is { } pqSig)
            sigs.Add(new JsonObject { ["alg"] = "ML-DSA-65", ["key"] = Convert.ToBase64String(Keys.PqPublicKey()!), ["sig"] = Convert.ToBase64String(pqSig) });

        var env = new JsonObject { ["manifest"] = Convert.ToBase64String(manifest), ["sigs"] = sigs };

        string? tsaError = null;
        if (!string.IsNullOrWhiteSpace(tsaUrl))
        {
            try
            {
                byte[] token = await TimestampAsync(ecSig, tsaUrl, ct);
                env["tsa"] = new JsonObject { ["token"] = Convert.ToBase64String(token), ["over"] = "ES256" };
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or CryptographicException or InvalidDataException or UriFormatException)
            {
                tsaError = e.Message;
            }
        }
        return (Png.ITxt(Keyword, env.ToJsonString()), tsaError);
    }

    private static async Task<byte[]> TimestampAsync(byte[] data, string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Timestamp URL must be http or https.");
        var req = Rfc3161TimestampRequest.CreateFromData(data, HashAlgorithmName.SHA256, nonce: RandomNumberGenerator.GetBytes(16), requestSignerCertificates: true);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Imago/" + Native.AppVersion);
        using var content = new ByteArrayContent(req.Encode());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
        using var resp = await http.PostAsync(uri, content, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsByteArrayAsync(ct);
        if (body.Length > 64 * 1024) throw new InvalidDataException("Timestamp reply is too large.");
        var token = req.ProcessResponse(body, out _); // checks status, nonce and that it covers our hash
        return token.AsSignedCms().Encode();
    }

    // ---------- verifying ----------

    public static VerifyResult Verify(byte[] file)
    {
        var r = new VerifyResult();
        PngImage img;
        try { img = Png.Decode(file); }
        catch (InvalidDataException e)
        {
            r.Status = VerifyStatus.Error;
            r.Headline = "Can't read this file: " + e.Message;
            return r;
        }
        return Verify(img);
    }

    public static VerifyResult Verify(PngImage img)
    {
        var r = new VerifyResult();
        var ours = img.Chunks.Where(c => c.Type == "iTXt").Select(c => Png.ReadITxt(c.Data)).Where(t => t?.Keyword == Keyword).ToList();
        if (ours.Count == 0)
        {
            r.Status = VerifyStatus.Unsigned;
            r.Headline = "No Imago signature in this file.";
            return r;
        }

        void Fail(string why) { r.Status = VerifyStatus.Tampered; r.Headline = "Changed since capture: " + why; }

        if (ours.Count > 1) { Fail("it carries more than one signature block."); return r; }
        var odd = img.Chunks.Select(c => c.Type).Where(t => !AllowedChunks.Contains(t)).Distinct().ToList();
        if (odd.Count > 0) { Fail($"it contains data that can change how it looks ({string.Join(", ", odd)})."); return r; }
        if (img.HadAlpha) { Fail("transparency was added."); return r; }

        if (!CheckEnvelope(ours[0]!.Value.Text, r, Fail, out var env, out var man, out var ecKey, out var ecSig, out bool pqChecked)) return r;

        // The manifest is authentic. Now check it describes these exact pixels.
        if ((int?)man["width"] != img.Pixels.W || (int?)man["height"] != img.Pixels.H) { Fail("the image was resized or cropped."); return r; }
        string want = (string?)man["pixelSha256"] ?? "";
        string have = Convert.ToHexStringLower(img.Pixels.CanonicalHash());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(want), Encoding.ASCII.GetBytes(have))) { Fail("pixels were edited."); return r; }

        r.Status = VerifyStatus.Valid;
        r.Headline = $"Stamped: unchanged since capture ({(string?)man["stamp"] ?? "?"}).";
        Describe(r, env, man, ecKey, ecSig, pqChecked);
        return r;
    }

    /// <summary>Checks a signature block on its own: both signatures, and that their keys match the manifest.</summary>
    private static bool CheckEnvelope(string text, VerifyResult r, Action<string> Fail,
        out JsonObject env, out JsonObject man, out byte[] ecKey, out byte[] ecSig, out bool pqChecked)
    {
        env = man = null!;
        ecKey = ecSig = null!;
        pqChecked = false;
        byte[] manifest;
        try
        {
            env = JsonNode.Parse(text)!.AsObject();
            manifest = Convert.FromBase64String((string)env["manifest"]!);
            man = JsonNode.Parse(manifest)!.AsObject();
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NullReferenceException or InvalidCastException)
        {
            Fail("the signature block is damaged.");
            return false;
        }
        r.Manifest = man;

        byte[] signed = [.. SigLabel, .. manifest];
        byte[]? foundKey = null, foundSig = null;
        try
        {
            foreach (var s in env["sigs"]!.AsArray())
            {
                string alg = (string)s!["alg"]!;
                byte[] key = Convert.FromBase64String((string)s["key"]!);
                byte[] sig = Convert.FromBase64String((string)s["sig"]!);
                if (alg == "ES256")
                {
                    using var e = ECDsa.Create();
                    e.ImportSubjectPublicKeyInfo(key, out _);
                    if (e.KeySize != 256 || !e.VerifyData(signed, sig, HashAlgorithmName.SHA256)) { Fail("the ES256 signature does not match."); return false; }
                    foundKey = key; foundSig = sig;
                    if ((string?)man["device"]?["keyId"] != Keys.Fingerprint(key)) { Fail("the signing key was swapped."); return false; }
                }
                else if (alg == "ML-DSA-65")
                {
                    if ((string?)man["device"]?["pqKeyId"] != Keys.Fingerprint(key)) { Fail("the post-quantum key was swapped."); return false; }
                    if (!MLDsa.IsSupported) { r.Details.Add("Post-quantum signature present, not checked (this Windows build has no ML-DSA)."); continue; }
                    using var k = MLDsa.ImportMLDsaPublicKey(MLDsaAlgorithm.MLDsa65, key);
                    if (!k.VerifyData(signed, sig, PqContext)) { Fail("the ML-DSA-65 signature does not match."); return false; }
                    pqChecked = true;
                }
            }
        }
        catch (Exception e) when (e is FormatException or CryptographicException or InvalidOperationException or NullReferenceException or InvalidCastException)
        {
            Fail("a signature is malformed.");
            return false;
        }
        if (foundKey is null || foundSig is null) { Fail("the ES256 signature is missing."); return false; }
        if (man["device"]?["pqKeyId"] is not null && !pqChecked && MLDsa.IsSupported) { Fail("the post-quantum signature was removed."); return false; }
        ecKey = foundKey;
        ecSig = foundSig;
        r.SignatureValid = true;
        return true;
    }

    /// <summary>Checks a signature block kept in this PC's ledger. Valid means the record is genuine; it says nothing about a copy's pixels.</summary>
    public static VerifyResult VerifyRecord(string text)
    {
        var r = new VerifyResult();
        void Fail(string why) { r.Status = VerifyStatus.Tampered; r.Headline = "The ledger record is not genuine: " + why; }
        if (!CheckEnvelope(text, r, Fail, out var env, out var man, out var ecKey, out var ecSig, out bool pqChecked)) return r;
        r.Status = VerifyStatus.Valid;
        r.Headline = $"Ledger record for {(string?)man["stamp"] ?? "?"} is genuine.";
        Describe(r, env, man, ecKey, ecSig, pqChecked);
        return r;
    }

    private static void Describe(VerifyResult r, JsonObject env, JsonObject man, byte[] ecKey, byte[] ecSig, bool pqChecked)
    {
        string local = Keys.LocalFingerprintIfExists() == Keys.Fingerprint(ecKey) ? "this PC" : "another device";
        r.Details.Add($"Signed on {local}, key {Keys.Fingerprint(ecKey)}" + ((bool?)man["device"]?["hardwareKey"] == true ? " (TPM)" : " (software key)"));
        r.Details.Add(pqChecked ? "Signatures: ES256 + ML-DSA-65 (post-quantum), both valid" : "Signature: ES256 valid");
        if (DateTimeOffset.TryParse((string?)man["capturedUtc"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            r.Details.Add($"Captured {t.ToLocalTime():yyyy-MM-dd HH:mm:ss} (device clock, {man["tzOffset"]})");
        r.Details.Add($"Name: {man["name"]}" + (man["place"] is { } p ? $", place: {p}" : ""));
        if (man["location"] is JsonObject loc)
            r.Details.Add($"Location: {(double)loc["lat"]!:0.0000}, {(double)loc["lon"]!:0.0000} ±{(double)loc["accuracyM"]!:0} m, " + ((string?)loc["source"] == "Manual" ? "entered by hand" : $"from {loc["source"]} (device reported)"));
        r.Details.Add($"Source: {man["source"]?["app"]}, {man["width"]} x {man["height"]} px, {man["mode"]} capture");
        if (env["tsa"] is JsonObject tsa) r.Details.Add(CheckTimestamp(tsa, ecSig));
        else r.Details.Add("No trusted timestamp: the time comes from this device's clock.");
    }

    private static string CheckTimestamp(JsonObject tsa, byte[] ecSig)
    {
        try
        {
            byte[] tok = Convert.FromBase64String((string)tsa["token"]!);
            if (!Rfc3161TimestampToken.TryDecode(tok, out var token, out _))
                return "Timestamp present but unreadable.";
            if (!token.VerifySignatureForData(ecSig, out var signer) || signer is null)
                return "Timestamp present but does not match this signature.";
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.8")); // timestamping
            chain.ChainPolicy.VerificationTime = token.TokenInfo.Timestamp.UtcDateTime;
            bool trusted = chain.Build(signer);
            string who = signer.GetNameInfo(X509NameType.SimpleName, false);
            return trusted
                ? $"Trusted timestamp {token.TokenInfo.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss} from {who}: existed no later than this."
                : $"Timestamp {token.TokenInfo.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss} from {who}, but its certificate isn't trusted here.";
        }
        catch (Exception e) when (e is FormatException or CryptographicException or NullReferenceException or InvalidCastException)
        {
            return "Timestamp present but damaged.";
        }
    }

    public static string FormatOffset(TimeSpan o) =>
        (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString("hh\\:mm", CultureInfo.InvariantCulture);
}
