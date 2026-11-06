namespace Imago;

/// <summary>One recording from start to a signed file; shared by the tray and the command line.</summary>
internal sealed class RecordFlow
{
    public Recorder Recorder { get; }
    private readonly string _path;

    private RecordFlow(Recorder r, string path) { Recorder = r; _path = path; }

    public static async Task<RecordFlow> StartAsync(string dir, RecordOptions opt)
    {
        var monitor = Native.MonitorAt(Cursor.Position, out _);
        string name = Path.GetFullPath(Naming.Unique(dir, "Recording " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss"), ".mp4"));
        if (opt.Fps <= 0) opt = opt with { Fps = Native.RefreshRate(monitor) }; // 0 means match the display
        var r = await Recorder.StartAsync(name, monitor, opt);
        return new RecordFlow(r, name);
    }

    /// <summary>Stops, then signs the file if options are given. Signing runs after the video is closed, so it covers the final bytes.</summary>
    public string? TrackPath => Recorder.Track is null ? null : _path + EventTrack.Ext;

    public async Task<(string Path, RecordingResult Rec, string? Note)> StopAsync(VerifiedOptions? v)
    {
        var rec = await Recorder.StopAsync();
        string? note = null;
        string? trackPath = null;
        if (Recorder.Track is { } tr && tr.Count > 0)
        {
            try
            {
                trackPath = _path + EventTrack.Ext;
                tr.Save(trackPath, Path.GetFileName(_path), Recorder.FirstFrameQpc, rec.Width, rec.Height, rec.Seconds);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { trackPath = null; note = "Could not save the event track: " + e.Message; }
        }
        if (v is not null)
        {
            try { note = await VideoProof.SignAsync(_path, rec, v, null, null, trackPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                note = "Saved, but could not sign it: " + e.Message;
            }
        }
        return (_path, rec, note);
    }
}
