using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Imago;

/// <summary>Where a capture came from: used for its file name and its signed record.</summary>
internal sealed record CaptureSource(string Context, string App, string ExeName, string Title)
{
    public static readonly CaptureSource Desktop = new("Desktop", "Windows", "explorer.exe", "");
}

/// <summary>Builds "{context} - {app} - {yyyy-MM-dd HH.mm.ss}.png" names.</summary>
internal static partial class Naming
{
    private static readonly string[] Separators = [" - ", " — ", " – ", " | ", " · ", " • "];
    private static readonly string[] VendorPrefixes = ["Microsoft ", "Google ", "Mozilla ", "Adobe ", "JetBrains "];
    private static readonly HashSet<string> NoiseSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Personal", "Work", "InPrivate", "Private Browsing", "Incognito", "Guest", "Profile 1", "Profile 2",
        "Microsoft Edge", "Google Chrome", "Mozilla Firefox", "Firefox", "Chrome", "Edge", "Brave", "Opera",
        "Visual Studio Code", "Visual Studio Code - Insiders", "Cursor", "Notepad", "File Explorer", "Explorer",
    };
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [GeneratedRegex(@"^\(\d+\+?\)\s*")] private static partial Regex LeadingCount();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\s*and \d+ more pages?$", RegexOptions.IgnoreCase)] private static partial Regex MorePages();

    public static CaptureSource Describe(long hwnd)
    {
        var info = Native.WindowInfo(hwnd);
        if (info is null) return CaptureSource.Desktop;
        var (_, exe, title) = info.Value;
        string exeName = exe.Length > 0 ? Path.GetFileName(exe) : "";
        if (exeName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(title))
            return CaptureSource.Desktop;
        string app = AppName(exe, title);
        return new CaptureSource(Context(title, app), app, exeName, title);
    }

    public static string AppName(string exePath, string title)
    {
        string exeName = Path.GetFileNameWithoutExtension(exePath);
        // Store apps run inside a frame host; their name is the last title segment.
        if (exeName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            var segs = Split(title);
            return segs.Count > 0 ? Clean(segs[^1]) : "App";
        }
        string? name = null;
        try
        {
            if (File.Exists(exePath))
            {
                var vi = FileVersionInfo.GetVersionInfo(exePath);
                name = Pick(vi.FileDescription) ?? Pick(vi.ProductName);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (exeName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) name = "File Explorer";
        name ??= exeName.Length > 0 ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(exeName) : "Unknown app";
        foreach (var p in VendorPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase) && name.Length > p.Length + 1)
                name = name[p.Length..];
        return Clean(name);

        static string? Pick(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            return s.Equals("Application", StringComparison.OrdinalIgnoreCase) || s.Length < 2 ? null : s;
        }
    }

    /// <summary>The document or page part of a window title, without the app name.</summary>
    public static string Context(string title, string app)
    {
        title = Clean(title);
        title = title.TrimStart('●', '*', ' ').TrimEnd('*', ' '); // unsaved markers
        title = LeadingCount().Replace(title, ""); // "(3) Inbox"
        title = MorePages().Replace(title, "");   // Edge: "Page and 4 more pages"
        if (title.Length == 0) return "";

        var segs = Split(title)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !NoiseSegments.Contains(s) && !SameApp(s, app))
            .ToList();
        if (segs.Count == 0) return "";

        string ctx = segs[0];
        if (ctx.Length < 4 && segs.Count > 1) ctx = ctx + " - " + segs[1];
        return ctx;
    }

    private static bool SameApp(string seg, string app)
    {
        if (seg.Equals(app, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var p in VendorPrefixes)
            if (seg.StartsWith(p, StringComparison.OrdinalIgnoreCase) && seg[p.Length..].Equals(app, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static List<string> Split(string title)
    {
        var parts = new List<string> { title };
        foreach (var sep in Separators)
            parts = parts.SelectMany(p => p.Split(sep)).ToList();
        return parts;
    }

    /// <summary>Removes control and zero-width characters and collapses whitespace.</summary>
    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate)
                continue;
            if (cat is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) { sb.Append(' '); continue; }
            sb.Append(r.ToString());
        }
        return Spaces().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>Makes one part of a file name safe on every Windows file system.</summary>
    public static string SafePart(string s, int max)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var r in Clean(s).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(r) == UnicodeCategory.OtherSymbol) continue; // emoji trip up sync tools
            switch (r.Value)
            {
                case '/': case '\\': sb.Append('-'); break;
                case ':': sb.Append('.'); break;
                case '"': sb.Append('\''); break;
                case '<': case '>': case '|': case '?': case '*': break;
                default: sb.Append(r.ToString()); break;
            }
        }
        string t = Spaces().Replace(sb.ToString(), " ").Trim().Trim('.', ' ', '-');
        if (t.Length > max)
        {
            int cut = t.LastIndexOf(' ', max);
            t = (cut >= max / 2 ? t[..cut] : t[..max]).TrimEnd('.', ' ', '-', ',');
            // Never split a surrogate pair.
            if (t.Length > 0 && char.IsHighSurrogate(t[^1])) t = t[..^1];
        }
        if (Reserved.Contains(t) || (t.Length > 3 && Reserved.Contains(t.Split('.')[0]))) t = "_" + t;
        return t;
    }

    public static string FileName(CaptureSource src, DateTime local)
    {
        string app = SafePart(src.App, 30);
        string ctx = SafePart(src.Context, 60);
        string time = local.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture);
        if (app.Length == 0) app = "Screenshot";
        return ctx.Length == 0 || ctx.Equals(app, StringComparison.OrdinalIgnoreCase)
            ? $"{app} - {time}"
            : $"{ctx} - {app} - {time}";
    }

    /// <summary>A free path in dir for base + ext, adding " (2)", " (3)"… on collisions.</summary>
    public static string Unique(string dir, string baseName, string ext)
    {
        string p = Path.Combine(dir, baseName + ext);
        for (int i = 2; File.Exists(p); i++)
            p = Path.Combine(dir, $"{baseName} ({i}){ext}");
        return p;
    }
}
