using Windows.Devices.Geolocation;

namespace Imago;

/// <summary>Windows location, used only when the person turns it on in Verified mode.</summary>
internal static class Geo
{
    private static GeoFix? _last;
    private static string _status = "Off";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string Status => _status;
    public static GeoFix? Last => _last;

    /// <summary>A fix no older than maxAge, waiting up to timeout for a new one. Null if location is off or denied.</summary>
    public static async Task<GeoFix?> GetAsync(TimeSpan maxAge, TimeSpan timeout)
    {
        if (_last is { } f && DateTimeOffset.Now - f.Time <= maxAge) return f;
        if (!await Gate.WaitAsync(timeout)) return _last;
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                _status = access == GeolocationAccessStatus.Denied
                    ? "Blocked in Windows Settings > Privacy & security > Location"
                    : "Not available";
                return null;
            }
            var g = new Geolocator { DesiredAccuracy = PositionAccuracy.High };
            var op = g.GetGeopositionAsync(maxAge, timeout).AsTask();
            var done = await Task.WhenAny(op, Task.Delay(timeout + TimeSpan.FromMilliseconds(500)));
            if (done != op) { _status = "No fix yet"; return _last; }
            var c = (await op).Coordinate;
            var p = c.Point.Position;
            _last = new GeoFix(p.Latitude, p.Longitude, c.Accuracy, c.PositionSource.ToString(), c.Timestamp);
            _status = $"Fix from {c.PositionSource}, ±{c.Accuracy:0} m";
            return _last;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            _status = "Not available: " + e.Message;
            return _last;
        }
        finally { Gate.Release(); }
    }
}
