using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Imago;

/// <summary>One beat of a flow: a click (or a step the person added) with an optional caption.</summary>
internal sealed class FlowStep
{
    public double T { get; set; }
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public string Caption { get; set; } = "";
    public bool Manual { get; set; }
    /// <summary>Research tag for this moment (Issue, Insight, Quote, Question), or empty.</summary>
    public string Tag { get; set; } = "";
    public bool HasPoint => X >= 0 && Y >= 0;
    /// <summary>Highlight boxes (video pixels) shown while this step is active.</summary>
    public List<FlowMark> Marks { get; } = [];
}

internal readonly record struct FlowMark(int X, int Y, int W, int H);

/// <summary>
/// The editable side of a recording: the event track turned into steps, plus the effects the person chose.
/// Everything here is pure data and maths, so the preview, the MP4 export and the HTML export all agree.
/// </summary>
internal sealed class FlowProject
{
    public const string Ext = ".imago-flow.json";

    public string Video { get; init; } = "";
    public int W { get; init; }
    public int H { get; init; }
    public double Duration { get; init; }
    /// <summary>True when the cursor is not in the video, so it can be redrawn smoothly.</summary>
    public bool CursorSeparate { get; init; }
    public bool Smooth { get; set; } = true;
    public bool Ripples { get; set; } = true;
    public bool Zoom { get; set; } = true;
    public List<FlowStep> Steps { get; } = [];
    public List<(double T, int X, int Y)> Moves { get; } = [];
    public List<(double T, int X, int Y)> Clicks { get; } = [];

    // ---------- load / save ----------

    public static FlowProject FromEvents(string video, int w, int h, double dur, bool cursorSeparate,
        IEnumerable<(double T, int X, int Y)> moves, IEnumerable<(double T, int X, int Y)> clicks)
    {
        var p = new FlowProject { Video = video, W = w, H = h, Duration = dur, CursorSeparate = cursorSeparate };
        p.Moves.AddRange(moves.OrderBy(m => m.T));
        p.Clicks.AddRange(clicks.OrderBy(c => c.T));
        p.DeriveSteps();
        return p;
    }

    /// <summary>Reads the recording's event track (if any) and any saved flow edits next to it.</summary>
    public static FlowProject Load(string video, int w, int h, double dur)
    {
        var moves = new List<(double, int, int)>();
        var clicks = new List<(double, int, int)>();
        bool separate = false;
        string trackPath = video + EventTrack.Ext;
        if (File.Exists(trackPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(trackPath));
                var root = doc.RootElement;
                separate = root.TryGetProperty("cursor", out var c) && c.GetString() == "separate";
                if (root.TryGetProperty("events", out var evs))
                    foreach (var e in evs.EnumerateArray())
                    {
                        string kind = e.GetProperty("e").GetString() ?? "";
                        double t = e.GetProperty("t").GetDouble() / 1000.0;
                        if (kind == "move") moves.Add((t, e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32()));
                        else if (kind == "down") clicks.Add((t, e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32()));
                    }
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        var p = FromEvents(video, w, h, dur, separate, moves, clicks);
        p.LoadEdits();
        return p;
    }

    private void LoadEdits()
    {
        string path = Video + Ext;
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            if (r.TryGetProperty("smooth", out var s)) Smooth = s.GetBoolean();
            if (r.TryGetProperty("ripples", out var rp)) Ripples = rp.GetBoolean();
            if (r.TryGetProperty("zoom", out var z)) Zoom = z.GetBoolean();
            if (r.TryGetProperty("steps", out var st))
            {
                Steps.Clear();
                foreach (var e in st.EnumerateArray())
                {
                    var ns = new FlowStep
                    {
                        T = e.GetProperty("t").GetDouble(),
                        X = e.TryGetProperty("x", out var x) ? x.GetInt32() : -1,
                        Y = e.TryGetProperty("y", out var y) ? y.GetInt32() : -1,
                        Caption = e.TryGetProperty("caption", out var c) ? c.GetString() ?? "" : "",
                        Manual = e.TryGetProperty("manual", out var m) && m.GetBoolean(),
                        Tag = e.TryGetProperty("tag", out var tg) ? tg.GetString() ?? "" : "",
                    };
                    if (e.TryGetProperty("marks", out var mk))
                        foreach (var a in mk.EnumerateArray())
                            if (a.GetArrayLength() == 4) ns.Marks.Add(new FlowMark(a[0].GetInt32(), a[1].GetInt32(), a[2].GetInt32(), a[3].GetInt32()));
                    Steps.Add(ns);
                }
                Steps.Sort((a, b) => a.T.CompareTo(b.T));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
    }

    public void SaveEdits()
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("kind", "imago-flow");
            w.WriteString("video", Path.GetFileName(Video));
            w.WriteBoolean("smooth", Smooth);
            w.WriteBoolean("ripples", Ripples);
            w.WriteBoolean("zoom", Zoom);
            w.WriteStartArray("steps");
            foreach (var s in Steps)
            {
                w.WriteStartObject();
                w.WriteNumber("t", Math.Round(s.T, 3));
                if (s.HasPoint) { w.WriteNumber("x", s.X); w.WriteNumber("y", s.Y); }
                w.WriteString("caption", s.Caption);
                if (s.Manual) w.WriteBoolean("manual", true);
                if (s.Tag.Length > 0) w.WriteString("tag", s.Tag);
                if (s.Marks.Count > 0)
                {
                    w.WriteStartArray("marks");
                    foreach (var k in s.Marks) { w.WriteStartArray(); w.WriteNumberValue(k.X); w.WriteNumberValue(k.Y); w.WriteNumberValue(k.W); w.WriteNumberValue(k.H); w.WriteEndArray(); }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        File.WriteAllBytes(Video + Ext, ms.ToArray());
    }

    /// <summary>Clicks closer than 0.6 s and 80 px count as one step (a double click is one beat).</summary>
    public void DeriveSteps()
    {
        Steps.Clear();
        FlowStep? last = null;
        foreach (var (t, x, y) in Clicks)
        {
            if (last is not null && t - last.T < 0.6 && Math.Abs(x - last.X) < 80 && Math.Abs(y - last.Y) < 80) continue;
            last = new FlowStep { T = t, X = x, Y = y };
            Steps.Add(last);
        }
    }

    public FlowStep AddStep(double t)
    {
        var (x, y) = RawCursor(t);
        var s = new FlowStep { T = Math.Clamp(t, 0, Duration), X = Moves.Count > 0 ? (int)x : -1, Y = Moves.Count > 0 ? (int)y : -1, Manual = true };
        Steps.Add(s);
        Steps.Sort((a, b) => a.T.CompareTo(b.T));
        return s;
    }

    // ---------- the maths ----------

    /// <summary>Where the pointer was at time t, interpolating between recorded moves.</summary>
    public (double X, double Y) RawCursor(double t)
    {
        if (Moves.Count == 0) return (W / 2.0, H / 2.0);
        int lo = 0, hi = Moves.Count - 1;
        if (t <= Moves[0].T) return (Moves[0].X, Moves[0].Y);
        if (t >= Moves[hi].T) return (Moves[hi].X, Moves[hi].Y);
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (Moves[mid].T <= t) lo = mid; else hi = mid; }
        var a = Moves[lo]; var b = Moves[hi];
        double f = b.T > a.T ? (t - a.T) / (b.T - a.T) : 0;
        return (a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
    }

    /// <summary>The pointer with hand jitter removed: a triangle-weighted average over ±120 ms. Smoothing is offline, so it can look ahead.</summary>
    public (double X, double Y) Cursor(double t)
    {
        if (!Smooth) return RawCursor(t);
        double sx = 0, sy = 0, sw = 0;
        for (int i = -7; i <= 7; i++)
        {
            double wgt = 8 - Math.Abs(i);
            var (x, y) = RawCursor(t + i * 0.017);
            sx += x * wgt; sy += y * wgt; sw += wgt;
        }
        return (sx / sw, sy / sw);
    }

    private static double Smoothstep(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }

    /// <summary>Zoom into each click: ease in 0.4 s before it, hold 1.2 s after, ease out over 0.5 s. Returns scale and the centre of the view.</summary>
    public (double Scale, double CX, double CY) ZoomAt(double t)
    {
        double best = 0, bx = W / 2.0, by = H / 2.0;
        if (Zoom)
            foreach (var s in Steps)
            {
                if (!s.HasPoint) continue;
                double e = t < s.T ? Smoothstep(1 - (s.T - t) / 0.4) : t < s.T + 1.2 ? 1 : Smoothstep(1 - (t - s.T - 1.2) / 0.5);
                if (e > best) { best = e; bx = s.X; by = s.Y; }
            }
        double scale = 1 + 0.8 * best;
        double cx = W / 2.0 + (bx - W / 2.0) * best, cy = H / 2.0 + (by - H / 2.0) * best;
        double vw = W / scale, vh = H / scale;
        cx = Math.Clamp(cx, vw / 2, W - vw / 2);
        cy = Math.Clamp(cy, vh / 2, H - vh / 2);
        return (scale, cx, cy);
    }

    /// <summary>Click rings alive at time t: position and age from 0 to 1.</summary>
    public IEnumerable<(int X, int Y, double Age)> RippleAt(double t)
    {
        if (!Ripples) yield break;
        foreach (var (ct, x, y) in Clicks)
        {
            double age = (t - ct) / 0.6;
            if (age is >= 0 and < 1) yield return (x, y, age);
        }
    }

    /// <summary>Highlight boxes of the step active at t (same window as its caption), with a 0..1 fade factor.</summary>
    public (IReadOnlyList<FlowMark> Marks, double Alpha) MarksAt(double t)
    {
        for (int i = Steps.Count - 1; i >= 0; i--)
        {
            var s = Steps[i];
            if (t < s.T) continue;
            double end = i + 1 < Steps.Count ? Math.Min(Steps[i + 1].T, s.T + 4) : s.T + 4;
            if (t >= end || s.Marks.Count == 0) return ([], 0);
            return (s.Marks, Math.Min(Smoothstep((t - s.T) / 0.25), Smoothstep((end - t) / 0.25)));
        }
        return ([], 0);
    }

    public string? CaptionAt(double t)
    {
        for (int i = Steps.Count - 1; i >= 0; i--)
        {
            var s = Steps[i];
            if (t < s.T) continue;
            double end = i + 1 < Steps.Count ? Math.Min(Steps[i + 1].T, s.T + 4) : s.T + 4;
            return t < end && s.Caption.Length > 0 ? s.Caption : null;
        }
        return null;
    }

    public string Effects => string.Join(", ", new[] { Smooth && CursorSeparate ? "cursor smoothing" : null, Ripples ? "click ripples" : null, Zoom ? "auto-zoom" : null, Steps.Any(s => s.Caption.Length > 0) ? "captions" : null, Steps.Any(s => s.Marks.Count > 0) ? "highlight boxes" : null }.Where(x => x is not null));

    // ---------- export: a derived-work note ----------

    /// <summary>
    /// A stylised export is a derived work, not a raw capture, so it is never signed as one. This sidecar names the
    /// original, hashes it and the new file, and lists the effects, so anyone can see what was changed.
    /// </summary>
    /// <summary>Derived note for an export built from several originals (screenshots): names and hashes each one.</summary>
    public static string WriteDerivedNote(string outPath, IReadOnlyList<string> originals, string effects)
    {
        string note = outPath + ".imago-derived.json";
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("kind", "imago-derived");
            w.WriteString("file", Path.GetFileName(outPath));
            w.WriteString("fileSha256", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(outPath))));
            w.WriteStartArray("originals");
            foreach (string o in originals)
            {
                w.WriteStartObject();
                w.WriteString("file", Path.GetFileName(o));
                w.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(o))));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("effects", effects);
            w.WriteBoolean("signed", false);
            w.WriteString("note", "Stylised export (derived work). It is not a raw capture and carries no signature. The original screenshots and their signatures are the evidence.");
            w.WriteEndObject();
        }
        File.WriteAllBytes(note, ms.ToArray());
        return note;
    }

    public static string WriteDerivedNote(string outPath, string originalPath, string effects)
    {
        string note = outPath + ".imago-derived.json";
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("kind", "imago-derived");
            w.WriteString("file", Path.GetFileName(outPath));
            w.WriteString("fileSha256", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(outPath))));
            w.WriteString("original", Path.GetFileName(originalPath));
            w.WriteString("originalSha256", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(originalPath))));
            w.WriteString("effects", effects);
            w.WriteBoolean("signed", false);
            w.WriteString("note", "Stylised export (derived work). It is not a raw capture and carries no signature. The original recording and its manifest are the evidence.");
            w.WriteEndObject();
        }
        File.WriteAllBytes(note, ms.ToArray());
        return note;
    }

    // ---------- export: animated HTML ----------

    /// <summary>A single HTML file that plays the original video and redraws the effects live on a canvas, with a step list. Needs the video file beside it.</summary>
    public string ToHtml(string videoFileName, double rate = 30)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        int n = (int)Math.Ceiling(Duration * rate) + 1;
        sb.Append('[');
        for (int i = 0; i < n; i++)
        {
            double t = i / rate;
            var (z, cx, cy) = ZoomAt(t);
            var (px, py) = Cursor(t);
            if (i > 0) sb.Append(',');
            sb.Append(string.Create(inv, $"[{z:0.###},{cx:0.#},{cy:0.#},{px:0.#},{py:0.#}]"));
        }
        sb.Append(']');
        var clicks = string.Join(",", Clicks.Select(c => string.Create(inv, $"[{c.T:0.###},{c.X},{c.Y}]")));
        var steps = string.Join(",", Steps.Select(s => string.Create(inv, $"{{\"t\":{s.T:0.###},\"c\":{JsonSerializer.Serialize(s.Caption)},\"m\":[{string.Join(",", s.Marks.Select(k => $"[{k.X},{k.Y},{k.W},{k.H}]"))}]}}")));
        bool cursor = CursorSeparate;
        bool isUri = videoFileName.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        return HtmlTemplate
            .Replace("%TITLE%", System.Net.WebUtility.HtmlEncode(Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(videoFileName))))
            .Replace("%VIDEO%", System.Net.WebUtility.HtmlEncode(isUri ? videoFileName : Uri.EscapeDataString(videoFileName)))
            .Replace("%W%", W.ToString(inv)).Replace("%H%", H.ToString(inv)).Replace("%RATE%", rate.ToString(inv))
            .Replace("%FRAMES%", sb.ToString()).Replace("%CLICKS%", Ripples ? clicks : "")
            .Replace("%STEPS%", steps).Replace("%CURSOR%", cursor ? "true" : "false");
    }

    private const string HtmlTemplate = """
<!doctype html>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>%TITLE%</title>
<style>
:root{--bg:#f3f2f1;--ink:#201f1e;--mute:#605e5c;--line:#e1dfdd;--brand:#0f6cbd;--card:#fff}
@media (prefers-color-scheme:dark){:root{--bg:#1b1a19;--ink:#f3f2f1;--mute:#a19f9d;--line:#3b3a39;--brand:#479ef5;--card:#292827}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.4 "Segoe UI",system-ui,sans-serif;display:flex;gap:16px;padding:16px;flex-wrap:wrap}
main{flex:1 1 640px;min-width:0}canvas{width:100%;height:auto;display:block;border-radius:8px;background:#000}
.bar{display:flex;align-items:center;gap:12px;margin-top:10px}button{font:inherit;color:#fff;background:var(--brand);border:0;border-radius:4px;padding:6px 16px;cursor:pointer}
input[type=range]{flex:1;accent-color:var(--brand)}.t{color:var(--mute);font-variant-numeric:tabular-nums;min-width:90px}
aside{flex:0 0 260px}ol{list-style:none;margin:0;padding:0;background:var(--card);border:1px solid var(--line);border-radius:8px;overflow:hidden}
li{padding:10px 12px;border-bottom:1px solid var(--line);cursor:pointer}li:last-child{border:0}li small{color:var(--mute);display:block}li.on{background:color-mix(in srgb,var(--brand) 14%,transparent)}
video{display:none}
</style>
<main><canvas id="c" width="%W%" height="%H%"></canvas>
<div class="bar"><button id="p">Play</button><input id="s" type="range" min="0" max="1000" value="0"><span class="t" id="t">0:00</span></div></main>
<aside><ol id="l"></ol></aside>
<video id="v" src="%VIDEO%" muted playsinline preload="auto"></video>
<script>
const F=%FRAMES%,CL=[%CLICKS%],ST=[%STEPS%],R=%RATE%,W=%W%,H=%H%,CUR=%CURSOR%;
const v=document.getElementById('v'),c=document.getElementById('c'),x=c.getContext('2d'),p=document.getElementById('p'),s=document.getElementById('s'),tt=document.getElementById('t'),l=document.getElementById('l');
const f2=n=>{n=Math.max(0,n);return Math.floor(n/60)+':'+String(Math.floor(n%60)).padStart(2,'0')};
ST.forEach((q,i)=>{const e=document.createElement('li');e.innerHTML=(i+1)+'. '+(q.c||'Step')+'<small>'+f2(q.t)+'</small>';e.onclick=()=>{v.currentTime=q.t};l.appendChild(e)});
function cap(t){for(let i=ST.length-1;i>=0;i--){if(t<ST[i].t)continue;const e=i+1<ST.length?Math.min(ST[i+1].t,ST[i].t+4):ST[i].t+4;return t<e&&ST[i].c?ST[i].c:null}return null}
function mk(t){for(let i=ST.length-1;i>=0;i--){if(t<ST[i].t)continue;const e=i+1<ST.length?Math.min(ST[i+1].t,ST[i].t+4):ST[i].t+4;if(t>=e||!ST[i].m.length)return null;const sm=a=>{a=Math.max(0,Math.min(1,a));return a*a*(3-2*a)};return[ST[i].m,Math.min(sm((t-ST[i].t)/.25),sm((e-t)/.25))]}return null}
function draw(){const t=v.currentTime,k=F[Math.min(F.length-1,Math.round(t*R))]||[1,W/2,H/2,0,0],z=k[0],vw=W/z,vh=H/z,vx=k[1]-vw/2,vy=k[2]-vh/2,m=W/vw;
x.drawImage(v,vx,vy,vw,vh,0,0,W,H);
const X=a=>(a-vx)*m,Y=a=>(a-vy)*m;
for(const q of CL){const a=(t-q[0])/.6;if(a<0||a>=1)continue;const r=(10+60*(1-(1-a)*(1-a)))*m,f=Math.pow(1-a,1.4),cx=X(q[1]),cy=Y(q[2]),gr=x.createRadialGradient(cx,cy,0,cx,cy,r);gr.addColorStop(0,'rgba(15,108,189,0)');gr.addColorStop(.2,'rgba(15,108,189,'+.55*f+')');gr.addColorStop(.55,'rgba(15,108,189,'+.18*f+')');gr.addColorStop(1,'rgba(15,108,189,'+.02*f+')');x.fillStyle=gr;x.beginPath();x.arc(cx,cy,r,0,7);x.fill()}
const mm=mk(t);if(mm){for(const q of mm[0]){x.globalAlpha=mm[1];x.fillStyle='rgba(15,108,189,.14)';x.strokeStyle='#0f6cbd';x.lineWidth=Math.max(2,3*m);x.beginPath();x.roundRect(X(q[0]),Y(q[1]),q[2]*m,q[3]*m,6*m);x.fill();x.stroke()}x.globalAlpha=1}
if(CUR){const px=X(k[3]),py=Y(k[4]),u=m*1.1;x.beginPath();x.moveTo(px,py);x.lineTo(px,py+13*u);x.lineTo(px+9*u,py+9*u);x.closePath();x.fillStyle='#0f6cbd';x.fill();x.lineWidth=1.2*u;x.lineJoin='round';x.strokeStyle='#fff';x.stroke()}
const cp=cap(t);if(cp){x.font='600 '+Math.round(H/30)+'px Segoe UI,sans-serif';const w=x.measureText(cp).width+H/20,h=H/16;x.fillStyle='rgba(0,0,0,.72)';x.beginPath();x.roundRect((W-w)/2,H-h*1.6,w,h,h/4);x.fill();x.fillStyle='#fff';x.textAlign='center';x.textBaseline='middle';x.fillText(cp,W/2,H-h*1.6+h/2)}
tt.textContent=f2(t)+' / '+f2(v.duration||0);s.value=(v.duration?t/v.duration:0)*1000;
let on=-1;ST.forEach((q,i)=>{if(t>=q.t)on=i});[...l.children].forEach((e,i)=>e.classList.toggle('on',i===on))}
function loop(){draw();requestAnimationFrame(loop)}
p.onclick=()=>{if(v.paused){v.play();p.textContent='Pause'}else{v.pause();p.textContent='Play'}};
s.oninput=()=>{v.currentTime=s.value/1000*(v.duration||0)};v.onended=()=>{p.textContent='Play'};
v.addEventListener('loadeddata',()=>{v.currentTime=0});loop();
</script>
""";
}
