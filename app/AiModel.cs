using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Imago;

/// <summary>
/// Optional settings beside a model, as <c>name.json</c> next to <c>name.onnx</c>. Every field has a default, so a
/// plain image classifier works without one.
/// </summary>
internal sealed record AiModelConfig(float[] Mean, float[] Std, string[]? Labels, int? AiIndex, int? Size)
{
    public static readonly AiModelConfig Default = new([0.485f, 0.456f, 0.406f], [0.229f, 0.224f, 0.225f], null, null, null);

    public static AiModelConfig Read(string path)
    {
        if (!File.Exists(path)) return Default;
        var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("The model settings are not a JSON object.");
        float[] Three(string key, float[] fallback)
        {
            var v = o[key]?.AsArray().Select(x => (float)x!).ToArray() ?? fallback;
            return v.Length == 3 ? v : throw new InvalidDataException($"\"{key}\" needs three numbers.");
        }
        return new(Three("mean", Default.Mean), Three("std", Default.Std),
            o["labels"]?.AsArray().Select(x => (string)x!).ToArray(), (int?)o["ai"], (int?)o["size"]);
    }
}

/// <summary>
/// Runs an image classifier on this PC with ONNX Runtime and returns how likely an area is AI-generated. Nothing is
/// uploaded. The model is whatever <c>.onnx</c> file the user puts in the models folder; without one the review
/// relies on its heuristics alone.
/// </summary>
internal sealed partial class AiModel : IDisposable
{
    private const string Runtime = "onnxruntime.dll";

    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly bool _nhwc;
    private readonly int _size;
    private readonly int _aiIndex;
    private readonly AiModelConfig _cfg;

    public string Name { get; }

    private static readonly Lazy<(AiModel? Model, string? Error)> Shared = new(OpenDefault, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The model from the models folder, or null when there is none or it failed to load.</summary>
    public static AiModel? Default => Shared.Value.Model;

    /// <summary>Why the model in the models folder could not be used, or null.</summary>
    public static string? DefaultError => Shared.Value.Error;

    public static string? FindModel()
    {
        try { return Directory.Exists(Review.ModelDir) ? Directory.EnumerateFiles(Review.ModelDir, "*.onnx").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static (AiModel?, string?) OpenDefault()
    {
        string? path = FindModel();
        if (path is null) return (null, null);
        try { return (Open(File.ReadAllBytes(path), AiModelConfig.Read(Path.ChangeExtension(path, ".json")), Path.GetFileNameWithoutExtension(path)), null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException
                                  or FormatException or OnnxRuntimeException or DllNotFoundException or BadImageFormatException)
        {
            return (null, e.Message);
        }
    }

    /// <summary>
    /// Loads the ONNX Runtime that ships with Imago by absolute path. Windows has an older onnxruntime.dll in
    /// System32, and Imago's hardening prefers System32 images, so a lookup by name would find the wrong one.
    /// </summary>
    public static string LoadRuntime()
    {
        string path = Path.Combine(AppContext.BaseDirectory, Runtime);
        if (!File.Exists(path)) throw new DllNotFoundException($"{Runtime} is missing. Keep it in the same folder as Imago ({AppContext.BaseDirectory}).");
        // Official builds send usage telemetry by default. The review promises nothing leaves this PC, so stop it
        // before the runtime starts, and again through the API for the events the variable doesn't cover.
        Environment.SetEnvironmentVariable("ORT_DISABLE_TELEMETRY", "1");
        NativeLibrary.Load(path);
        return Start();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string Start()
    {
        var env = OrtEnv.Instance();
        env.DisableTelemetryEvents();
        return env.GetVersionString();
    }

    public static AiModel Open(byte[] model, AiModelConfig cfg, string name)
    {
        LoadRuntime();
        return Create(model, cfg, name);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AiModel Create(byte[] model, AiModelConfig cfg, string name)
    {
        using var opts = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
        var session = new InferenceSession(model, opts);
        AiModel? m = null;
        try { return m = new AiModel(session, cfg, name); }
        finally { if (m is null) session.Dispose(); }
    }

    private AiModel(InferenceSession session, AiModelConfig cfg, string name)
    {
        _session = session;
        _cfg = cfg;
        Name = name;
        var (input, meta) = session.InputMetadata.First();
        if (meta.ElementType != typeof(float) || meta.Dimensions.Length != 4)
            throw new InvalidDataException("The model must take one image as a float tensor of four dimensions.");
        _input = input;
        int[] d = meta.Dimensions;
        _nhwc = d[3] == 3 && d[1] != 3;
        int side = _nhwc ? d[1] : d[2];
        _size = side > 0 ? side : cfg.Size ?? 224;
        if (_size is < 16 or > 1024) throw new InvalidDataException($"Model input size {_size} is out of range.");
        _aiIndex = cfg.AiIndex ?? Math.Max(0, Array.FindIndex(cfg.Labels ?? [], l => AiLabel().IsMatch(l)));
        if (_aiIndex < 0) throw new InvalidDataException("\"ai\" must be a label index.");
    }

    [GeneratedRegex(@"\b(ai|artificial|fake|generated|synthetic)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AiLabel();

    /// <summary>Probability, from 0 to 1, that <paramref name="area"/> of <paramref name="px"/> is AI-generated.</summary>
    public double Score(Pixels px, Rectangle area)
    {
        // Give small areas some surrounding context, as classifiers see whole pictures in training.
        area.Inflate(Math.Max(0, 64 - area.Width) / 2, Math.Max(0, 64 - area.Height) / 2);
        area.Intersect(new Rectangle(0, 0, px.W, px.H));
        if (area.Width < 2 || area.Height < 2) area = new Rectangle(0, 0, px.W, px.H);

        using var src = px.ToBitmap();
        using var dst = new Bitmap(_size, _size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        using (var wrap = new System.Drawing.Imaging.ImageAttributes())
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            wrap.SetWrapMode(WrapMode.TileFlipXY); // no faded border from sampling past the edge
            g.DrawImage(src, new Rectangle(0, 0, _size, _size), area.X, area.Y, area.Width, area.Height, GraphicsUnit.Pixel, wrap);
        }
        byte[] bgra = Pixels.FromBitmap(dst).Bgra;

        int plane = _size * _size;
        var data = new float[3 * plane];
        for (int p = 0; p < plane; p++)
            for (int c = 0; c < 3; c++)
            {
                float v = (bgra[p * 4 + 2 - c] / 255f - _cfg.Mean[c]) / _cfg.Std[c]; // BGRA in memory, RGB to the model
                data[_nhwc ? p * 3 + c : c * plane + p] = v;
            }
        int[] shape = _nhwc ? [1, _size, _size, 3] : [1, 3, _size, _size];
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, new DenseTensor<float>(data, shape))]);
        float[] logits = results[0].AsEnumerable<float>().ToArray();
        if (logits.Length == 0) throw new InvalidDataException("The model returned nothing.");
        if (logits.Length == 1) return 1 / (1 + Math.Exp(-logits[0]));
        if (_aiIndex >= logits.Length) throw new InvalidDataException($"The model has {logits.Length} outputs, but \"ai\" is {_aiIndex}.");
        // Already probabilities when they are all in [0, 1] and sum to 1; otherwise softmax the logits.
        if (logits.All(v => v >= 0 && v <= 1) && Math.Abs(logits.Sum() - 1) < 1e-3) return logits[_aiIndex];
        float max = logits.Max();
        double sum = logits.Sum(v => Math.Exp(v - max));
        return Math.Exp(logits[_aiIndex] - max) / sum;
    }

    public void Dispose() => _session.Dispose();
}
