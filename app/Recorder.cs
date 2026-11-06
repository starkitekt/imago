using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace Imago;

internal sealed record RecordOptions(bool SystemAudio = true, bool Microphone = true, int Fps = 0, bool Track = true, bool BakeCursor = true);

internal sealed record RecordingResult(string Path, int Width, int Height, int Fps, double Seconds, bool HasAudio,
    DateTimeOffset Started, DateTimeOffset Ended, string Codec);

/// <summary>
/// Records one display at its full physical resolution to MP4.
/// Video: Windows.Graphics.Capture frames go straight to the GPU encoder (H.264, or HEVC above 4096 px) with no
/// CPU readback. Audio: WASAPI loopback (what you hear) mixed with the default microphone into one AAC track.
/// </summary>
internal sealed class Recorder
{
    private const int ChunkMs = 20;
    private const int AudioRate = 48000;

    private readonly string _path;
    private readonly RecordOptions _opt;
    private readonly Rectangle _monitor;

    private IntPtr _d3dDevice, _d3dContext;
    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private readonly BlockingCollection<Direct3D11CaptureFrame> _frames = new(boundedCapacity: 8);
    private Windows.Graphics.SizeInt32 _size;
    private TimeSpan _firstFrame = TimeSpan.MinValue;
    private TimeSpan _lastFrame;
    private readonly ManualResetEventSlim _gotFirst = new(false);
    private volatile bool _stopping;
    private long _stopAudioMs = long.MaxValue;

    private MediaStreamSource? _mss;
    private Task? _transcode;
    private IRandomAccessStream? _out;

    private WasapiCapture? _loop, _mic;
    private BufferedWaveProvider? _loopBuf, _micBuf;
    private MixingSampleProvider? _mixer;
    private long _audioChunks;
    private Stopwatch? _audioClock;

    private DateTimeOffset _started;
    private long _firstFrameQpc;
    private string _codec = "H.264";
    private bool _hasAudio;

    private Recorder(string path, RecordOptions opt, Rectangle monitor)
    {
        _path = path;
        _opt = opt;
        _monitor = monitor;
    }

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();
    public string Path => _path;
    public Windows.Graphics.SizeInt32 Size => _size;

    /// <summary>What the person did during the recording; null when tracking is off.</summary>
    public EventTrack? Track { get; private set; }

    /// <summary>Stopwatch time of the first video frame, the zero point for the track.</summary>
    public long FirstFrameQpc => _firstFrameQpc;

    public static async Task<Recorder> StartAsync(string path, Rectangle monitor, RecordOptions opt)
    {
        if (!IsSupported) throw new InvalidOperationException("This version of Windows can't record the screen (needs Windows 10 1903 or later).");
        var r = new Recorder(path, opt, monitor);
        try { await r.BeginAsync(); }
        catch { r.Cleanup(); throw; }
        return r;
    }

    // ---------- start ----------

    private async Task BeginAsync()
    {
        _device = CreateDevice();
        _item = CreateItemForMonitor(_monitor);
        // H.264 needs even sizes.
        _size = new Windows.Graphics.SizeInt32 { Width = _item.Size.Width & ~1, Height = _item.Size.Height & ~1 };
        if (_size.Width < 16 || _size.Height < 16) throw new InvalidOperationException("That display is too small to record.");

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 10, _item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = _opt.BakeCursor || !_opt.Track; } catch (Exception e) when (e is InvalidOperationException or COMException or UnauthorizedAccessException) { }
        try { _session.IsBorderRequired = false; } catch (Exception e) when (e is InvalidOperationException or COMException or UnauthorizedAccessException or NotSupportedException) { }

        var vprops = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_size.Width, (uint)_size.Height);
        vprops.FrameRate.Numerator = (uint)_opt.Fps;
        vprops.FrameRate.Denominator = 1;
        _mss = new MediaStreamSource(new VideoStreamDescriptor(vprops));

        _hasAudio = TryStartAudio();
        if (_hasAudio)
        {
            var aprops = AudioEncodingProperties.CreatePcm(AudioRate, 2, 16);
            _mss.AddStreamDescriptor(new AudioStreamDescriptor(aprops));
        }
        _mss.BufferTime = TimeSpan.Zero;
        _mss.CanSeek = false;
        _mss.SampleRequested += OnSampleRequested;

        var profile = BuildProfile();
        File.WriteAllBytes(_path, []);
        _out = await FileRandomAccessStream.OpenAsync(_path, Windows.Storage.FileAccessMode.ReadWrite).AsTask();
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, VideoProcessingAlgorithm = MediaVideoProcessingAlgorithm.Default };
        var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(_mss, _out, profile).AsTask();
        if (!prep.CanTranscode) throw new InvalidOperationException("Windows can't encode this display's size: " + prep.FailureReason);

        _started = DateTimeOffset.Now;
        if (_opt.Track) { Track = EventTrack.Start(_monitor); Track.CursorBaked = _opt.BakeCursor; }
        _session.StartCapture();
        _transcode = prep.TranscodeAsync().AsTask();
    }

    private MediaEncodingProfile BuildProfile()
    {
        bool huge = _size.Width > 4096 || _size.Height > 4096;
        var profile = huge
            ? MediaEncodingProfile.CreateHevc(VideoEncodingQuality.Uhd2160p)
            : MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Uhd2160p);
        _codec = huge ? "HEVC" : "H.264";
        var v = profile.Video!;
        v.Width = (uint)_size.Width;
        v.Height = (uint)_size.Height;
        v.FrameRate.Numerator = (uint)_opt.Fps;
        v.FrameRate.Denominator = 1;
        // About 0.35 bits per pixel per frame: visually lossless for screen content (4K60 is roughly 175 Mbps).
        double bps = (double)_size.Width * _size.Height * _opt.Fps * 0.35;
        v.Bitrate = (uint)Math.Clamp(bps, 20_000_000, 300_000_000);
        if (_hasAudio)
        {
            var a = profile.Audio ?? AudioEncodingProperties.CreateAac(AudioRate, 2, 192_000);
            a.SampleRate = AudioRate;
            a.ChannelCount = 2;
            a.Bitrate = 192_000;
            profile.Audio = a;
        }
        else profile.Audio = null;
        return profile;
    }

    // ---------- video ----------

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        Direct3D11CaptureFrame? f;
        while ((f = pool.TryGetNextFrame()) is not null)
        {
            // A display mode change mid-recording would change the frame size; those frames are skipped.
            if (_stopping || f.ContentSize.Width < _size.Width || f.ContentSize.Height < _size.Height || !_frames.TryAdd(f))
                f.Dispose();
        }
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        if (args.Request.StreamDescriptor is VideoStreamDescriptor) NextVideo(args.Request);
        else NextAudio(args.Request);
    }

    /// <summary>A captured frame that can back several samples; disposed when the last one is done.</summary>
    private sealed class Held(Direct3D11CaptureFrame frame)
    {
        public Direct3D11CaptureFrame Frame { get; } = frame;
        private int _refs = 1; // owned by the recorder until replaced
        public void Add() => Interlocked.Increment(ref _refs);
        public void Release() { if (Interlocked.Decrement(ref _refs) == 0) Frame.Dispose(); }
    }

    private Held? _held;
    private bool _sentFirst;

    /// <summary>
    /// Constant frame rate. Windows only sends a frame when the screen changes, so a still screen (a menu held open,
    /// a modal sitting there) would leave gaps and a video shorter than the real time. When no new frame arrives
    /// within one frame time, the last one is shown again, so the timeline always matches the wall clock.
    /// </summary>
    private void NextVideo(MediaStreamSourceSampleRequest req)
    {
        int waitMs = Math.Max(1, 1000 / _opt.Fps);
        while (true)
        {
            TimeSpan ts;
            if (_frames.TryTake(out var f, waitMs))
            {
                if (_firstFrame == TimeSpan.MinValue)
                {
                    _firstFrame = f.SystemRelativeTime;
                    // SystemRelativeTime and Stopwatch both count QPC ticks, so the track lines up with the video.
                    _firstFrameQpc = (long)(_firstFrame.Ticks * (double)System.Diagnostics.Stopwatch.Frequency / TimeSpan.TicksPerSecond);
                    _loopBuf?.ClearBuffer();
                    _micBuf?.ClearBuffer();
                    _audioClock = Stopwatch.StartNew();
                    _gotFirst.Set();
                }
                ts = f.SystemRelativeTime - _firstFrame;
                _held?.Release();
                _held = new Held(f);
            }
            else if (_stopping && _frames.Count == 0)
            {
                _held?.Release(); _held = null;
                req.Sample = null;
                return;
            }
            else if (_held is not null && _audioClock is not null) ts = _audioClock.Elapsed;
            else continue;

            if (_sentFirst) { var floor = _lastFrame + TimeSpan.FromMilliseconds(1); if (ts < floor) ts = floor; }
            else if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
            _sentFirst = true;
            _lastFrame = ts;
            var h = _held!;
            h.Add();
            var sample = MediaStreamSample.CreateFromDirect3D11Surface(h.Frame.Surface, ts);
            sample.Duration = TimeSpan.FromSeconds(1.0 / _opt.Fps);
            sample.Processed += (_, _) => h.Release();
            req.Sample = sample;
            return;
        }
    }

    // ---------- audio ----------

    private bool TryStartAudio()
    {
        if (!_opt.SystemAudio && !_opt.Microphone) return false;
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(AudioRate, 2);
        _mixer = new MixingSampleProvider(fmt) { ReadFully = true };
        try
        {
            if (_opt.SystemAudio)
            {
                _loop = new WasapiLoopbackCapture();
                _loopBuf = new BufferedWaveProvider(_loop.WaveFormat) { ReadFully = true, DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(5) };
                _loop.DataAvailable += (_, e) => _loopBuf.AddSamples(e.Buffer, 0, e.BytesRecorded);
                _mixer.AddMixerInput(To48kStereo(_loopBuf));
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            _loop?.Dispose(); _loop = null; _loopBuf = null;
        }
        try
        {
            if (_opt.Microphone)
            {
                using var en = new MMDeviceEnumerator();
                if (en.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
                {
                    var dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    _mic = new WasapiCapture(dev);
                    _micBuf = new BufferedWaveProvider(_mic.WaveFormat) { ReadFully = true, DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(5) };
                    _mic.DataAvailable += (_, e) => _micBuf.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    _mixer.AddMixerInput(To48kStereo(_micBuf));
                }
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            _mic?.Dispose(); _mic = null; _micBuf = null;
        }
        if (_loop is null && _mic is null) { _mixer = null; return false; }
        _loop?.StartRecording();
        _mic?.StartRecording();
        return true;
    }

    private static ISampleProvider To48kStereo(IWaveProvider src)
    {
        ISampleProvider sp = src.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2) sp = new MultiplexingSampleProvider([sp], 2);
        if (sp.WaveFormat.SampleRate != AudioRate) sp = new WdlResamplingSampleProvider(sp, AudioRate);
        return sp;
    }

    private void NextAudio(MediaStreamSourceSampleRequest req)
    {
        // Audio runs on its own clock that starts with the first video frame, paced in real time.
        _gotFirst.Wait();
        long idx = Interlocked.Increment(ref _audioChunks) - 1;
        long startMs = idx * ChunkMs;
        if (startMs >= Volatile.Read(ref _stopAudioMs)) { req.Sample = null; return; }
        while (_audioClock!.ElapsedMilliseconds < startMs + ChunkMs)
        {
            if (_stopping && startMs >= Volatile.Read(ref _stopAudioMs)) { req.Sample = null; return; }
            Thread.Sleep(2);
        }
        int frames = AudioRate * ChunkMs / 1000;
        var f32 = new float[frames * 2];
        _mixer!.Read(f32, 0, f32.Length);
        var pcm = new byte[f32.Length * 2];
        for (int i = 0; i < f32.Length; i++)
        {
            short s = (short)Math.Clamp((int)MathF.Round(f32[i] * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)s;
            pcm[i * 2 + 1] = (byte)(s >> 8);
        }
        req.Sample = MediaStreamSample.CreateFromBuffer(Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(pcm), TimeSpan.FromMilliseconds(startMs));
    }

    // ---------- stop ----------

    public async Task<RecordingResult> StopAsync()
    {
        var ended = DateTimeOffset.Now;
        _stopping = true;
        long elapsedMs = (long)(ended - _started).TotalMilliseconds;
        try { _session?.Dispose(); } catch (Exception e) when (e is COMException or ObjectDisposedException) { }
        _session = null;
        if (_pool is not null) _pool.FrameArrived -= OnFrameArrived;
        // Make sure a waiting audio request can finish even if no video frame ever arrived.
        _gotFirst.Set();
        Volatile.Write(ref _stopAudioMs, Math.Max(0, (long)_lastFrame.TotalMilliseconds));
        try { _loop?.StopRecording(); _mic?.StopRecording(); } catch (Exception e) when (e is COMException or InvalidOperationException) { }

        Exception? failure = null;
        try { if (_transcode is not null) await _transcode; }
        catch (Exception e) { failure = e; }
        var seconds = _lastFrame.TotalSeconds > 0 ? _lastFrame.TotalSeconds : elapsedMs / 1000.0;
        var w = _size.Width; var h = _size.Height;
        Cleanup();
        if (failure is not null) throw new InvalidOperationException("Recording failed: " + failure.Message, failure);
        if (new FileInfo(_path).Length < 1024) throw new InvalidOperationException("Nothing was recorded (the screen did not change).");
        return new RecordingResult(_path, w, h, _opt.Fps, seconds, _hasAudio, _started, ended, _codec);
    }

    private void Cleanup()
    {
        _stopping = true;
        _gotFirst.Set();
        try { _session?.Dispose(); } catch (Exception e) when (e is COMException or ObjectDisposedException) { }
        try { _pool?.Dispose(); } catch (Exception e) when (e is COMException or ObjectDisposedException) { }
        _loop?.Dispose(); _mic?.Dispose();
        _loop = null; _mic = null;
        Track?.Stop();
        while (_frames.TryTake(out var f)) f.Dispose();
        try { _out?.Dispose(); } catch (Exception e) when (e is COMException or ObjectDisposedException) { }
        _out = null;
        if (_d3dContext != IntPtr.Zero) { Marshal.Release(_d3dContext); _d3dContext = IntPtr.Zero; }
        if (_d3dDevice != IntPtr.Zero) { Marshal.Release(_d3dDevice); _d3dDevice = IntPtr.Zero; }
        (_device as IDisposable)?.Dispose();
        _device = null;
    }

    // ---------- Direct3D and capture interop ----------

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint nLevels,
        uint sdk, out IntPtr device, out int level, out IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string src, int len, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, [In] ref Guid iid);
    }

    private IDirect3DDevice CreateDevice()
    {
        const int D3D_DRIVER_TYPE_HARDWARE = 1;
        const uint BGRA_SUPPORT = 0x20, VIDEO_SUPPORT = 0x800;
        int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, BGRA_SUPPORT | VIDEO_SUPPORT, IntPtr.Zero, 0, 7,
            out _d3dDevice, out _, out _d3dContext);
        if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, BGRA_SUPPORT, IntPtr.Zero, 0, 7,
            out _d3dDevice, out _, out _d3dContext);
        if (hr < 0) throw new InvalidOperationException($"No usable GPU for screen recording (0x{hr:X8}).");

        var dxgiIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_d3dDevice, in dxgiIid, out IntPtr dxgi));
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out IntPtr abi));
            try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(abi); }
            finally { Marshal.Release(abi); }
        }
        finally { Marshal.Release(dxgi); }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(Rectangle monitor)
    {
        var center = new Point(monitor.X + monitor.Width / 2, monitor.Y + monitor.Height / 2);
        IntPtr hmon = MonitorFromPoint(center, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (hmon == IntPtr.Zero) throw new InvalidOperationException("Could not find that display.");

        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out IntPtr hs));
        try
        {
            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, ref interopIid, out IntPtr factory));
            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
                var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
                IntPtr abi = interop.CreateForMonitor(hmon, ref itemIid);
                try { return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(abi); }
                finally { Marshal.Release(abi); }
            }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(hs); }
    }
}
