using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Imago;

/// <summary>Signs a recording with a sidecar file and checks it later. The signature covers the whole file's bytes.</summary>
internal static class VideoProof
{
    public const string SidecarExt = ".imago.json";
    private const int BlockSize = 4 * 1024 * 1024;

    public static string SidecarPath(string video) => video + SidecarExt;

    public sealed record Edit(string Tool, double StartS, double EndS);

    public static (string FileSha, List<string> Blocks) HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var all = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var blocks = new List<string>();
        var buf = new byte[BlockSize];
        int n;
        while ((n = ReadFull(fs, buf)) > 0)
        {
            all.AppendData(buf, 0, n);
            blocks.Add(Convert.ToHexStringLower(SHA256.HashData(buf.AsSpan(0, n))));
        }
        return (Convert.ToHexStringLower(all.GetHashAndReset()), blocks);
    }

    private static int ReadFull(Stream s, byte[] buf)
    {
        int total = 0, n;
        while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
        return total;
    }

    public static byte[] BuildManifest(string path, RecordingResult rec, StampInfo s, IReadOnlyList<Edit>? edits = null, string? parentSha = null, string? trackPath = null)
    {
        var (sha, blocks) = HashFile(path);
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 2);
            w.WriteString("app", "Imago " + Native.AppVersion);
            w.WriteString("kind", "video");
            w.WriteString("id", s.Id);
            w.WriteString("stamp", s.StampId);
            w.WriteString("mode", "recording");
            w.WriteString("capturedUtc", s.Captured.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            w.WriteString("tzOffset", Provenance.FormatOffset(s.Captured.Offset));
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
            w.WriteString("app", "Screen recording");
            w.WriteEndObject();
            w.WriteNumber("width", rec.Width);
            w.WriteNumber("height", rec.Height);
            w.WriteNumber("fps", rec.Fps);
            w.WriteNumber("durationS", Math.Round(rec.Seconds, 2));
            w.WriteString("codec", rec.Codec);
            w.WriteBoolean("audio", rec.HasAudio);
            w.WriteString("startedUtc", rec.Started.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            w.WriteString("endedUtc", rec.Ended.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            w.WriteString("file", Path.GetFileName(path));
            w.WriteString("fileSha256", sha);
            w.WriteNumber("blockSize", BlockSize);
            w.WriteStartArray("blockHashes");
            foreach (var b in blocks) w.WriteStringValue(b);
            w.WriteEndArray();
            if (parentSha is not null) w.WriteString("parentSha256", parentSha);
            if (trackPath is not null && File.Exists(trackPath))
            {
                var (tsha, tn) = EventTrack.Describe(trackPath);
                w.WriteStartObject("track");
                w.WriteString("file", Path.GetFileName(trackPath));
                w.WriteString("sha256", tsha);
                w.WriteNumber("events", tn);
                w.WriteString("keys", EventTrack.KeysPolicy);
                w.WriteEndObject();
            }
            w.WriteStartArray("edits");
            foreach (var e in edits ?? [])
            {
                w.WriteStartObject();
                w.WriteString("tool", e.Tool);
                w.WriteNumber("startS", Math.Round(e.StartS, 3));
                w.WriteNumber("endS", Math.Round(e.EndS, 3));
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

    /// <summary>Writes the sidecar next to the video. Returns a TSA warning, if any.</summary>
    public static async Task<string?> SignAsync(string path, RecordingResult rec, VerifiedOptions v,
        IReadOnlyList<Edit>? edits = null, string? parentSha = null, string? trackPath = null)
    {
        var stamp = StampInfo.Create("recording", CaptureSource.Desktop, v.Name, v.Place, v.Location);
        // Capture time is when recording began, not when signing ran.
        stamp = new StampInfo
        {
            Id = stamp.Id, StampId = stamp.StampId, Captured = rec.Started, Name = stamp.Name, Place = stamp.Place,
            Location = stamp.Location, Mode = stamp.Mode, Source = stamp.Source, Code = stamp.Code,
        };
        var manifest = await Task.Run(() => BuildManifest(path, rec, stamp, edits, parentSha, trackPath));
        var (json, tsaError) = await Provenance.SignEnvelopeAsync(manifest, v.TsaUrl);
        await File.WriteAllTextAsync(SidecarPath(path), json, new UTF8Encoding(false));
        return tsaError;
    }

    public static VerifyResult Verify(string path)
    {
        var r = new VerifyResult();
        string side = SidecarPath(path);
        if (!File.Exists(path)) { r.Status = VerifyStatus.Error; r.Headline = "Can't find the video."; return r; }
        if (!File.Exists(side)) { r.Status = VerifyStatus.Unsigned; r.Headline = "No Imago signature next to this video."; return r; }

        var rec = Provenance.VerifyRecord(File.ReadAllText(side));
        if (rec.Status != VerifyStatus.Valid)
        {
            rec.Headline = rec.Headline.Replace("The ledger record is not genuine", "The signature file is not genuine");
            return rec;
        }
        var man = rec.Manifest!;
        r.Manifest = man;
        r.SignatureValid = true;

        var (sha, blocks) = HashFile(path);
        string want = (string?)man["fileSha256"] ?? "";
        if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(want), Encoding.ASCII.GetBytes(sha)))
        {
            r.Status = VerifyStatus.Valid;
            r.Headline = $"Recording unchanged since capture ({(string?)man["stamp"] ?? "?"}).";
            r.Details.AddRange(rec.Details.Select(d => d.Replace("capture", "recording", StringComparison.Ordinal)));
            r.Details.Add($"{man["width"]} x {man["height"]} px, {man["fps"]} fps, {man["durationS"]} s, {man["codec"]}" + ((bool?)man["audio"] == true ? ", with audio" : ", no audio"));
            if (man["edits"] is JsonArray ed && ed.Count > 0)
                r.Details.Add("Edited in Imago: " + string.Join("; ", ed.Select(e => $"{e!["tool"]} {e["startS"]}s to {e["endS"]}s")));
            if (man["track"] is JsonObject tr)
            {
                string tp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", (string?)tr["file"] ?? "");
                if (!File.Exists(tp)) r.Details.Add("Event track listed in the signature is not next to the video.");
                else if (EventTrack.Describe(tp).Sha != (string?)tr["sha256"])
                {
                    r.Status = VerifyStatus.Tampered;
                    r.Headline = "The event track (clicks, keys, windows) was changed since recording.";
                }
                else r.Details.Add($"Event track intact: {tr["events"]} events.");
            }
            return r;
        }

        r.Status = VerifyStatus.Tampered;
        r.Headline = "Changed since recording: the file no longer matches its signature.";
        var bh = man["blockHashes"]?.AsArray().Select(x => (string?)x ?? "").ToList() ?? [];
        int bs = (int?)man["blockSize"] ?? BlockSize;
        var changed = new List<int>();
        for (int i = 0; i < Math.Max(bh.Count, blocks.Count); i++)
            if (i >= bh.Count || i >= blocks.Count || bh[i] != blocks[i]) changed.Add(i);
        if (blocks.Count != bh.Count) r.Details.Add("The file is " + (blocks.Count > bh.Count ? "longer" : "shorter") + " than the signed recording.");
        if (changed.Count > 0)
        {
            double total = (double?)man["durationS"] ?? 0;
            long size = new FileInfo(path).Length;
            var spans = changed.Where(i => i < blocks.Count).Take(6).Select(i =>
            {
                double a = total * Math.Min(1.0, (double)i * bs / Math.Max(1, size));
                double b = total * Math.Min(1.0, (double)(i + 1) * bs / Math.Max(1, size));
                return $"about {a:0.#}s to {b:0.#}s";
            });
            r.Details.Add($"{changed.Count} of {Math.Max(bh.Count, blocks.Count)} data blocks differ, roughly at: {string.Join(", ", spans)}.");
        }
        return r;
    }
}
