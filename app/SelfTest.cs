using System.Text;
using System.Text.Json.Nodes;

namespace Imago;

/// <summary>Built-in checks: codec, signatures, tamper detection, watermark, review, on-device AI, Lock and naming. Uses this PC's signing key.</summary>
internal static class SelfTest
{
    private static int _pass, _fail;

    public static int Run()
    {
        _pass = _fail = 0;
        Console.WriteLine("Imago " + Native.AppVersion + " self-test");
        Console.WriteLine("Key: " + Keys.Describe());

        // A gradient with detail so filters and hashing get real work.
        using var src = new Bitmap(640, 400);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
                src.SetPixel(x, y, Color.FromArgb((x * 7 + y) & 255, (y * 3) & 255, (x ^ y) & 255));

        // 1. PNG round trip is lossless.
        var px0 = Pixels.FromBitmap(src);
        var back = Png.Decode(Png.Encode(px0)).Pixels;
        Check("PNG round trip is lossless", back.Bgra.AsSpan().SequenceEqual(px0.Bgra));

        // 2. Sign and verify.
        var loc = new GeoFix(18.5204, 73.8567, 12, "Manual", DateTimeOffset.Now);
        var info = StampInfo.Create("area", new CaptureSource("Test page", "Self test", "imago.exe", "Test page - Self test"), "Test Person", "Pune", loc);
        var px = Pixels.FromBitmap(src);
        Watermark.Embed(px, info.Code);
        var blurBox = new Rectangle(24, 24, 120, 40);
        var (chunk, _) = Provenance.SignAsync(Provenance.BuildManifest(info, px, [new EditOp("blur", blurBox)]), null).GetAwaiter().GetResult();
        byte[] good = Png.Encode(px, [chunk]);
        var v = Provenance.Verify(good);
        Check("Signed capture verifies", v.Status == VerifyStatus.Valid, v.Headline);
        Check("Both signatures present", v.Details.Any(d => d.Contains("ML-DSA-65")) || !Keys.PostQuantumAvailable, string.Join(" | ", v.Details));

        // 3. One pixel changed by one level.
        var img = Png.Decode(good);
        img.Pixels.Bgra[(200 * 640 + 100) * 4] ^= 1;
        Check("One-bit pixel edit is caught", Provenance.Verify(Png.Encode(img.Pixels, Extras(img))).Status == VerifyStatus.Tampered);

        // 4. Manifest edited (name changed), signatures kept.
        img = Png.Decode(good);
        var forged = Extras(img).Select(c =>
        {
            var t = Png.ReadITxt(c.Data);
            if (c.Type != "iTXt" || t?.Keyword != Provenance.Keyword) return c;
            var env = JsonNode.Parse(t.Value.Text)!.AsObject();
            string man = Encoding.UTF8.GetString(Convert.FromBase64String((string)env["manifest"]!));
            env["manifest"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(man.Replace("Test Person", "Someone Else")));
            return Png.ITxt(Provenance.Keyword, env.ToJsonString());
        }).ToList();
        Check("Edited name is caught", Provenance.Verify(Png.Encode(img.Pixels, forged)).Status == VerifyStatus.Tampered);

        // 5. Post-quantum signature stripped.
        if (Keys.PostQuantumAvailable)
        {
            var stripped = Extras(img).Select(c =>
            {
                var t = Png.ReadITxt(c.Data);
                if (c.Type != "iTXt" || t?.Keyword != Provenance.Keyword) return c;
                var env = JsonNode.Parse(t.Value.Text)!.AsObject();
                var sigs = env["sigs"]!.AsArray();
                sigs.RemoveAt(1);
                return Png.ITxt(Provenance.Keyword, env.ToJsonString());
            }).ToList();
            Check("Removed post-quantum signature is caught", Provenance.Verify(Png.Encode(img.Pixels, stripped)).Status == VerifyStatus.Tampered);
        }

        // 6. Signature block removed: reads as unsigned, not as valid.
        Check("Stripped signature reads as unsigned", Provenance.Verify(Png.Encode(img.Pixels)).Status == VerifyStatus.Unsigned);

        // 7. A gamma chunk changes how pixels display without changing them.
        var gama = new PngChunk("gAMA", [0, 0, 0x5D, 0xC0]);
        Check("Added display chunk (gAMA) is rejected", Provenance.Verify(Png.Encode(img.Pixels, [.. Extras(img), gama])).Status == VerifyStatus.Tampered);

        // 8. Duplicate signature blocks.
        Check("Second signature block is rejected", Provenance.Verify(Png.Encode(img.Pixels, [.. Extras(img), chunk])).Status == VerifyStatus.Tampered);

        // 9. Cropped by one row, re-signed nowhere.
        var cropped = Pixels.FromBitmap(img.Pixels.ToBitmap(), new Rectangle(0, 0, 640, 399));
        Check("Crop is caught", Provenance.Verify(Png.Encode(cropped, Extras(img))).Status == VerifyStatus.Tampered);

        // 10. Damaged byte in the file.
        var broken = (byte[])good.Clone();
        broken[broken.Length / 2] ^= 0x40;
        Check("Corrupted file reports an error", Provenance.Verify(broken).Status is VerifyStatus.Error or VerifyStatus.Tampered);

        // 11. Seal round trip and wrong password.
        byte[] sealedBytes = Seal.Encrypt(good, "Test.png", "correct horse battery");
        var (name, opened) = Seal.Decrypt(sealedBytes, "correct horse battery");
        Check("Seal opens with the right password", name == "Test.png" && opened.AsSpan().SequenceEqual(good));
        Check("Sealed image still verifies", Provenance.Verify(opened).Status == VerifyStatus.Valid);
        Check("Wrong password is refused", Throws<Seal.WrongPasswordException>(() => Seal.Decrypt(sealedBytes, "correct horse battery!")));
        var flipped = (byte[])sealedBytes.Clone();
        flipped[^20] ^= 1;
        Check("Edited sealed file is refused", Throws<Seal.WrongPasswordException>(() => Seal.Decrypt(flipped, "correct horse battery")));
        var hdr = (byte[])sealedBytes.Clone();
        hdr[20] ^= 1; // inside the salt: header is authenticated
        Check("Edited seal header is refused", Throws<Exception>(() => Seal.Decrypt(hdr, "correct horse battery")));
        Check("Same image seals differently each time", !Seal.Encrypt(good, "Test.png", "correct horse battery").AsSpan().SequenceEqual(sealedBytes));

        // 12. Watermark and review.
        Check("Watermark reads back", Watermark.Detect(Png.Decode(good).Pixels) == info.Code);
        Check("Stamp ID comes back from the watermark", StampInfo.IdFor(info.Code) == info.StampId, StampInfo.IdFor(info.Code));
        var rv = Review.Analyze(good);
        Check("Review: untouched capture is stamped", rv.Verdict == ReviewVerdict.Stamped, rv.Headline);
        Check("Review: Imago edits come from the signed log", rv.ImagoEdits.Count == 1 && rv.ImagoEdits[0].Box == blurBox);

        // 12a. Event track: typed characters are never stored; shortcuts are; the signed manifest pins the file.
        {
            Check("Track: typed letter is counted, not named", EventTrack.Classify(0x41, false, false, false, false) == ("char", null));
            Check("Track: Ctrl+S is kept by name", EventTrack.Classify(0x53, true, false, false, false) == ("key", "Ctrl+S"));
            Check("Track: lone modifier is skipped", EventTrack.Classify(0x11, true, false, false, false).Kind is null);
            Check("Track: Enter is kept by name", EventTrack.Classify(0x0D, false, false, false, false) == ("key", "Enter"));
        }
        {
            string tdir = Path.Combine(Path.GetTempPath(), "imago-track-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tdir);
            string tv = Path.Combine(tdir, "t.mp4");
            try
            {
                var data = new byte[2 * 1024 * 1024];
                new Random(3).NextBytes(data);
                File.WriteAllBytes(tv, data);
                var tr = new EventTrack(new Rectangle(0, 0, 1920, 1080));
                long f = System.Diagnostics.Stopwatch.Frequency;
                tr.AddForTest("win", s: "app.exe", s2: "Before", qpc: 0);
                tr.AddForTest("move", 10, 20, qpc: f / 2);
                tr.AddForTest("key", s: "Ctrl+S", qpc: f);
                tr.AddForTest("char", qpc: f + 10);
                string tp = tv + EventTrack.Ext;
                tr.Save(tp, "t.mp4", f / 4, 1920, 1080, 2);
                string txt = File.ReadAllText(tp);
                Check("Track: window before first frame kept at 0", txt.Contains("\"t\":0,\"e\":\"win\""), txt);
                Check("Track: times start at the first frame", txt.Contains("\"t\":250,\"e\":\"move\""), txt);
                Check("Track: no typed text in the file", !txt.Contains("Before\":") && txt.Contains("\"e\":\"char\"") && !txt.Contains("\"k\":\"A\""));
                var rt = new RecordingResult(tv, 1920, 1080, 60, 2, false, DateTimeOffset.Now.AddSeconds(-2), DateTimeOffset.Now, "H.264");
                VideoProof.SignAsync(tv, rt, new VerifiedOptions("Test", "", null, null), null, null, tp).GetAwaiter().GetResult();
                var ok = VideoProof.Verify(tv);
                Check("Track: signed video with track verifies", ok.Status == VerifyStatus.Valid && ok.Details.Any(d => d.Contains("Event track intact")), string.Join(" | ", ok.Details));
                File.WriteAllText(tp, txt.Replace("\"t\":250", "\"t\":251"));
                var bt = VideoProof.Verify(tv);
                Check("Track: edited track is caught", bt.Status == VerifyStatus.Tampered, bt.Status + " " + bt.Headline + " " + string.Join("|", bt.Details) + " same=" + (txt == File.ReadAllText(tp)));
            }
            finally { try { Directory.Delete(tdir, true); } catch (IOException) { } }
        }

        // 12a. Flow model: steps, smoothing, zoom clamp, ripples, HTML, derived note.
        {
            var moves = Enumerable.Range(0, 100).Select(i => (i * 0.03, 100 + i * 10 + (i % 2 == 0 ? 6 : -6), 500)).ToList();
            var clicks = new List<(double T, int X, int Y)> { (1.0, 400, 500), (1.2, 405, 502), (2.5, 1900, 20) };
            var fp = FlowProject.FromEvents("v.mp4", 1920, 1080, 3.0, true, moves, clicks);
            Check("Flow: nearby clicks merge into one step", fp.Steps.Count == 2, fp.Steps.Count.ToString());
            var raw = fp.RawCursor(1.5); fp.Smooth = true; var sm = fp.Cursor(1.5);
            Check("Flow: smoothing stays within the jitter band", Math.Abs(sm.X - raw.X) < 20 && Math.Abs(sm.Y - 500) < 1);
            var (zs, zcx, zcy) = fp.ZoomAt(2.6);
            Check("Flow: zoom view stays inside the frame", zs > 1 && zcx + 1920 / zs / 2 <= 1920.01 && zcy - 1080 / zs / 2 >= -0.01);
            Check("Flow: no zoom when off", (fp.Zoom = false) == false && fp.ZoomAt(2.6).Scale == 1);
            Check("Flow: ripple lives 0.6 s", fp.RippleAt(1.3).Any() && !fp.RippleAt(1.9).Any(r => r.X == 400));
            fp.Steps[0].Caption = "Tap <b>Pay</b>";
            string html = fp.ToHtml("v.mp4");
            Check("Flow: HTML escapes captions and names the video", html.Contains("v.mp4") && !html.Contains("<b>Pay</b>"));
            string fd = Path.Combine(Path.GetTempPath(), "imago-flow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fd);
            try
            {
                string ov = Path.Combine(fd, "o.mp4"), nv = Path.Combine(fd, "n.mp4");
                File.WriteAllBytes(ov, [1, 2, 3]); File.WriteAllBytes(nv, [4, 5, 6]);
                string note = FlowProject.WriteDerivedNote(nv, ov, fp.Effects);
                string nt = File.ReadAllText(note);
                Check("Flow: derived note says unsigned and names the original", nt.Contains("\"signed\": false") || nt.Contains("\"signed\":false"), nt);
            }
            finally { try { Directory.Delete(fd, true); } catch (IOException) { } }
        }

        // 12b. Video sidecar: sign a dummy file, then catch a one-byte change.
        {
            string vp = Path.Combine(Path.GetTempPath(), "imago-selftest-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                var data = new byte[5 * 1024 * 1024];
                new Random(7).NextBytes(data);
                File.WriteAllBytes(vp, data);
                var recT = new RecordingResult(vp, 1920, 1080, 60, 12.5, true, DateTimeOffset.Now.AddSeconds(-12), DateTimeOffset.Now, "H.264");
                VideoProof.SignAsync(vp, recT, new VerifiedOptions("Test", "", null, null)).GetAwaiter().GetResult();
                Check("Signed recording verifies", VideoProof.Verify(vp).Status == VerifyStatus.Valid, VideoProof.Verify(vp).Headline);
                data[4_900_000] ^= 1;
                File.WriteAllBytes(vp, data);
                var bad = VideoProof.Verify(vp);
                Check("One-byte recording edit is caught, with its place", bad.Status == VerifyStatus.Tampered && bad.Details.Any(d => d.Contains("block")), string.Join(" | ", bad.Details));
                File.Delete(VideoProof.SidecarPath(vp));
                Check("Recording without sidecar reads as unsigned", VideoProof.Verify(vp).Status == VerifyStatus.Unsigned);
            }
            finally
            {
                try { File.Delete(vp); File.Delete(VideoProof.SidecarPath(vp)); } catch (IOException) { }
            }
        }

        var painted = Png.Decode(good);
        var hole = new Rectangle(380, 220, 140, 90);
        for (int y = hole.Top; y < hole.Bottom; y++)
            for (int x = hole.Left; x < hole.Right; x++)
            {
                int o = (y * 640 + x) * 4;
                painted.Pixels.Bgra[o] = painted.Pixels.Bgra[o + 1] = painted.Pixels.Bgra[o + 2] = 20;
            }
        rv = Review.Analyze(Png.Encode(painted.Pixels, Extras(painted)));
        Check("Review: painted box is located", rv.Verdict == ReviewVerdict.Changed && rv.Altered.Any(r => r.IntersectsWith(hole)) && !rv.Altered.Any(r => r.IntersectsWith(blurBox)),
            rv.Headline + " " + string.Join(";", rv.Altered));

        byte[] Jpeg(Pixels p, long q)
        {
            using var bmp = p.ToBitmap();
            using var ms = new MemoryStream();
            var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var ps = new System.Drawing.Imaging.EncoderParameters(1);
            ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
            bmp.Save(ms, enc, ps);
            return ms.ToArray();
        }
        using (var jb = new Bitmap(new MemoryStream(Jpeg(Png.Decode(good).Pixels, 85))))
            Check("Watermark survives JPEG at 85", Watermark.Detect(Pixels.FromBitmap(jb)) == info.Code);
        Check("Plain image has no watermark", Watermark.Detect(Pixels.FromBitmap(src)) is null);

        Ledger.Save(info.StampId, Png.ReadITxt(chunk.Data)!.Value.Text);
        try
        {
            rv = Review.Analyze(Jpeg(Png.Decode(good).Pixels, 85));
            Check("Review: JPEG copy is a stamped copy", rv.Verdict == ReviewVerdict.Resaved && rv.Recompressed, rv.Headline + " " + string.Join(";", rv.Altered));
            rv = Review.Analyze(Jpeg(painted.Pixels, 85));
            Check("Review: change found in a JPEG copy", rv.Verdict == ReviewVerdict.Changed && rv.Altered.Any(r => r.IntersectsWith(hole)),
                rv.Headline + " " + string.Join(";", rv.Altered));
        }
        finally { Ledger.Forget(info.StampId); }
        rv = Review.Analyze(Jpeg(Png.Decode(good).Pixels, 85));
        Check("Review: copy without its record is stamped elsewhere", rv.Verdict == ReviewVerdict.StampedElsewhere, rv.Headline);

        // 13. File names.
        var t = new DateTime(2026, 10, 23, 14, 5, 9);
        Name("Chrome page", new CaptureSource(Naming.Context("(3) Inbox - someone@example.com - Gmail - Google Chrome", "Chrome"), "Chrome", "chrome.exe", ""), t,
            "Inbox - Chrome - 2026-10-23 14.05.09");
        Name("Unsaved VS Code file", new CaptureSource(Naming.Context("● Program.cs - Imago - Visual Studio Code", "Visual Studio Code"), "Visual Studio Code", "Code.exe", ""), t,
            "Program.cs - Visual Studio Code - 2026-10-23 14.05.09");
        Name("Unsafe characters", new CaptureSource("Q3: Plan <draft>? \"final\" / v2*", "Word", "WINWORD.EXE", ""), t,
            "Q3. Plan draft 'final' - v2 - Word - 2026-10-23 14.05.09");
        Name("Reserved device name", new CaptureSource("CON", "Notepad", "notepad.exe", ""), t,
            "_CON - Notepad - 2026-10-23 14.05.09");
        Name("Desktop", CaptureSource.Desktop, t, "Desktop - Windows - 2026-10-23 14.05.09");
        Name("Context equals app", new CaptureSource("Calculator", "Calculator", "calc.exe", ""), t, "Calculator - 2026-10-23 14.05.09");
        string longCtx = Naming.FileName(new CaptureSource(new string('a', 40) + " " + new string('b', 80), "App", "a.exe", ""), t);
        Check("Long titles are cut at a word", longCtx.Length < 120 && !longCtx.Contains('b'), longCtx);

        // 14. Places.
        Check("Nearest place within 25 km", Places.Nearest(18.53, 73.85) == "Pune");
        Check("Open sea has no place", Places.Nearest(0, -140) == "");

        // 15. On-device AI check. A one-node model (average colour per channel) stands in for a real detector.
        string ortPath = Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll");
        string ortWant = File.Exists(ortPath) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(ortPath).ProductVersion ?? "?" : "missing";
        string ort = AiModel.LoadRuntime();
        Check("AI runtime is the bundled one, not Windows' own", ortWant.StartsWith(ort, StringComparison.Ordinal), ort + " vs " + ortWant);
        byte[] Value(string name, params int[] dims) =>
            Pb((1, name), (2, Pb((1, Pb((1, 1), (2, Pb(dims.Select(d => (1, (object)Pb((1, d)))).ToArray())))))));
        byte[] tiny = Pb((1, 8), (8, Pb((1, ""), (2, 13))), (7, Pb(
            (1, Pb((1, "x"), (2, "y"), (4, "GlobalAveragePool"))), (2, "g"), (11, Value("x", 1, 3, 32, 32)), (12, Value("y", 1, 3, 1, 1)))));
        using (var m = AiModel.Open(tiny, new AiModelConfig([0, 0, 0], [1, 1, 1], ["photo", "ai", "other"], null, null), "selftest"))
        {
            var solid = new Pixels(100, 60);
            for (int i = 0; i < solid.Bgra.Length; i += 4) { solid.Bgra[i + 1] = 128; solid.Bgra[i + 2] = 255; solid.Bgra[i + 3] = 255; }
            double g = 128 / 255.0, want = Math.Exp(g) / (Math.E + Math.Exp(g) + 1); // softmax of (R, G, B) = (1, 0.5, 0), "ai" is G
            double got = m.Score(solid, new Rectangle(10, 10, 40, 30));
            Check("AI model runs on this PC and scores the right label", Math.Abs(got - want) < 0.01, $"{got:0.000} vs {want:0.000}");
        }
        Check("Model in the models folder loads, if there is one", AiModel.FindModel() is null || AiModel.Default is not null, AiModel.DefaultError);

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? $"PASS  {_pass} checks" : $"FAIL  {_fail} of {_pass + _fail} checks failed");
        return _fail == 0 ? 0 : 1;
    }

    private static List<PngChunk> Extras(PngImage img) =>
        img.Chunks.Where(c => c.Type is not ("IHDR" or "IDAT" or "IEND")).ToList();

    private static void Name(string label, CaptureSource src, DateTime t, string want)
    {
        string got = Naming.FileName(src, t);
        Check("Name: " + label, got == want, got);
    }

    /// <summary>Minimal protobuf writer: int fields as varints, strings and byte arrays as length-delimited.</summary>
    private static byte[] Pb(params (int Field, object Value)[] fields)
    {
        var o = new List<byte>();
        void Varint(ulong v) { for (; v >= 0x80; v >>= 7) o.Add((byte)(v | 0x80)); o.Add((byte)v); }
        foreach (var (f, v) in fields)
        {
            byte[]? b = v as byte[] ?? (v is string s ? Encoding.UTF8.GetBytes(s) : null);
            Varint((ulong)(f << 3 | (b is null ? 0 : 2)));
            if (b is null) Varint((ulong)(int)v);
            else { Varint((ulong)b.Length); o.AddRange(b); }
        }
        return [.. o];
    }

    private static bool Throws<T>(Action a) where T : Exception
    {
        try { a(); return false; }
        catch (T) { return true; }
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what + (!ok && detail is not null ? "   (" + detail + ")" : ""));
    }
}
