using System.Text.Json;
using System.Text.Json.Serialization;

namespace Imago;

/// <summary>User settings, stored in %LOCALAPPDATA%\Imago\settings.json.</summary>
internal sealed class Settings
{
    /// <summary>Stamp and sign every capture.</summary>
    public bool Verified { get; set; }

    /// <summary>Name on the stamp. Empty means the Windows display name.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Place on the stamp. Empty means the nearest city from the location fix.</summary>
    public string Place { get; set; } = "";

    /// <summary>Ask Windows for the device location. Off until the person turns it on.</summary>
    public bool UseLocation { get; set; }

    /// <summary>Send the signature's hash to a public timestamp server (RFC 3161).</summary>
    public bool TrustedTimestamp { get; set; }

    public string TimestampUrl { get; set; } = "http://timestamp.digicert.com";

    /// <summary>Also bind Ctrl+Shift+3/4/5 for keyboards without Print Screen.</summary>
    public bool LaptopKeys { get; set; }

    /// <summary>"display" captures the display under the cursor, "all" every display.</summary>
    public string FullScreen { get; set; } = "display";

    public bool ShowQuickAccess { get; set; } = true;

    /// <summary>Open the editor right after each capture, so private details can be blurred before anything is saved.</summary>
    public bool EditAfterCapture { get; set; } = true;

    [JsonIgnore] public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Imago");
    [JsonIgnore] private static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>Every image and video capture goes to %USERPROFILE%\Screenshots.</summary>
    public static string ScreenshotDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Screenshots");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file falls back to defaults rather than blocking captures.
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public string EffectiveName => string.IsNullOrWhiteSpace(DisplayName) ? Native.DisplayName() : DisplayName.Trim();
}
