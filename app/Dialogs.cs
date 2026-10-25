namespace Imago;

/// <summary>Shared look for the small dialogs and the main window, in the Microsoft 365 (Fluent) style.</summary>
internal static class Ui
{
    /// <summary>Segoe UI ships with Windows, so text matches the rest of the system.</summary>
    public static readonly FontFamily Family = new("Segoe UI");

    // The type should feel calm and trustworthy: one family, two weights, a short scale with small steps, and no
    // bold or capitals shouting. Windows 11's Segoe UI Variable has optical sizes, so text sizes get the Text cut
    // and the one large size gets the Display cut. Windows 10 falls back to Segoe UI.
    private static readonly FontFamily Regular = Pick("Segoe UI Variable Text", "Segoe UI");
    private static readonly FontFamily TextSemibold = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
    private static readonly FontFamily DisplaySemibold = Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold");
    /// <summary>Body1 in Fluent 2 is 14px, which is 10.5pt at 96 DPI.</summary>
    public static readonly Font Body = new(Regular, 10.5f);
    public static readonly Font Strong = new(TextSemibold, 10.5f);
    public static readonly Font Small = new(Regular, 9f);
    public static readonly Font Caption = new(TextSemibold, 9f);
    public static readonly Font Title = new(TextSemibold, 12f);
    public static readonly Font Heading = new(DisplaySemibold, 15f);
    /// <summary>Soft, slightly cool greys rather than black: quieter on white, and still well above 4.5:1.</summary>
    public static readonly Color Muted = Color.FromArgb(94, 99, 107);
    public static readonly Color Ink = Color.FromArgb(31, 35, 41);

    private static FontFamily Pick(string name, string fallback) =>
        FontFamily.Families.FirstOrDefault(f => f.Name == name) ?? new FontFamily(fallback);

    /// <summary>Window background: a light neutral grey, with white cards on top.</summary>
    public static readonly Color Canvas = Color.FromArgb(250, 250, 250);
    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceHover = Color.FromArgb(245, 245, 245);
    public static readonly Color SurfacePressed = Color.FromArgb(230, 230, 230);
    public static readonly Color Hairline = Color.FromArgb(224, 224, 224);
    public static readonly Color Stroke = Color.FromArgb(209, 209, 209);
    public const int Radius = 8;

    /// <summary>Column width, and the width inside a card's padding.</summary>
    public const int ColW = 420, InnerW = 388;

    /// <summary>The Microsoft 365 blue: primary buttons, switches on, and the icon-like marks.</summary>
    public static readonly Color Brand = Color.FromArgb(15, 108, 189);

    public static Icon AppIcon
    {
        get
        {
            var s = typeof(Ui).Assembly.GetManifestResourceStream("Imago.ico");
            return s is null ? SystemIcons.Application : new Icon(s);
        }
    }

    public static Label Text(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text, AutoSize = true, Font = font ?? Body, ForeColor = color ?? Ink, BackColor = Color.Transparent,
        MaximumSize = new Size(InnerW, 0), Margin = new Padding(0, 0, 0, 6),
    };

    /// <summary>A white card that holds one group of controls, sized to the column.</summary>
    public static Card Section(Control content)
    {
        var card = new Card { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = ColW, Margin = new Padding(0, 0, 0, 12) };
        card.Controls.Add(content);
        content.Location = new Point(16, 16);
        return card;
    }

    /// <summary>Stacks controls top to bottom at the inner width of a card.</summary>
    public static FlowLayoutPanel Stack(params Control[] items)
    {
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Width = InnerW, MinimumSize = new Size(InnerW, 0), BackColor = Color.Transparent };
        for (int i = 0; i < items.Length; i++)
        {
            items[i].Margin = i < items.Length - 1 ? new Padding(0, 0, 0, 10) : Padding.Empty;
            col.Controls.Add(items[i]);
        }
        return col;
    }

    /// <summary>A label over a field, with an optional muted hint under it.</summary>
    public static FlowLayoutPanel Field(string title, Control field, string? hint = null)
    {
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
        col.Controls.Add(Text(title));
        col.Controls.Add(field);
        if (hint is not null) col.Controls.Add(Text(hint, Small, Muted));
        return col;
    }
}

/// <summary>Asks for a Seal password. With confirm on, the person types it twice and sees a strength hint.</summary>
internal sealed class PasswordDialog : TaskDialog
{
    private readonly TextBox _pw = new() { UseSystemPasswordChar = true };
    private readonly TextBox _again = new() { UseSystemPasswordChar = true };
    private readonly Label _hint = Ui.Text("", Ui.Small, Ui.Muted);
    private readonly Switch _delete = new() { Checked = true };
    private readonly bool _confirm;

    public string Password => _pw.Text;
    public bool DeleteOriginal => _delete.Checked;

    public PasswordDialog(string fileName, bool confirm) : base(confirm ? "Lock screenshot" : "Open locked screenshot")
    {
        _confirm = confirm;
        Body.Controls.Add(Wide(Ui.Text(confirm ? "Lock with a password" : "This screenshot is locked", Ui.Heading), 2));
        Body.Controls.Add(Wide(Ui.Text(Path.GetFileName(fileName), Ui.Small, Ui.Muted), 12));
        Body.Controls.Add(Wide(Ui.Text(confirm
            ? "Encrypts the image with AES-256-GCM. Only this password opens it. There is no recovery if you forget it."
            : "Enter the password it was locked with."), 16));
        Body.Controls.Add(Wide(Ui.Field("Password", new Input(_pw, Ui.ColW)), 12));
        if (confirm)
        {
            Body.Controls.Add(Wide(Ui.Field("Type it again", new Input(_again, Ui.ColW)), 0));
            Body.Controls.Add(Wide(_hint, 16));
            Body.Controls.Add(Wide(Switch.Labeled("Delete the unencrypted PNG after locking", null, _delete, Ui.ColW), 0));
            _pw.TextChanged += (_, _) => UpdateHint();
            _again.TextChanged += (_, _) => UpdateHint();
            UpdateHint();
        }
        Body.Controls.Add(Footer(confirm ? "Lock" : "Open"));
        FormClosing += OnClosing;
    }

    /// <summary>Lets a label wrap at the full dialog width and sets the space under it.</summary>
    private static Control Wide(Control c, int below)
    {
        if (c is Label l) l.MaximumSize = new Size(Ui.ColW, 0);
        c.Margin = new Padding(0, 0, 0, below);
        return c;
    }

    private void UpdateHint()
    {
        int bits = Seal.EstimateBits(_pw.Text);
        string strength = _pw.Text.Length < Seal.MinPassword ? $"Use at least {Seal.MinPassword} characters."
            : bits < 45 ? "Weak. Add length, or mix words, numbers and symbols."
            : bits < 70 ? "Fair. Longer is better."
            : "Strong.";
        if (_again.Text.Length > 0 && _again.Text != _pw.Text) strength += "  The two entries don't match.";
        _hint.Text = strength;
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK) return;
        if (_pw.Text.Length == 0) { e.Cancel = true; return; }
        if (!_confirm) return;
        if (_pw.Text.Length < Seal.MinPassword || _pw.Text != _again.Text)
        {
            e.Cancel = true;
            UpdateHint();
            System.Media.SystemSounds.Beep.Play();
        }
    }
}

/// <summary>Settings, including the first-time Verified setup.</summary>
internal sealed class SettingsForm : TaskDialog
{
    private readonly Settings _s;
    private readonly Switch _verified = new();
    private readonly TextBox _name = new();
    private readonly TextBox _place = new();
    private readonly Switch _loc = new();
    private readonly Switch _tsa = new();
    private readonly Switch _laptop = new();
    private readonly Switch _all = new();
    private readonly Switch _quick = new();

    public SettingsForm(Settings s, bool verifiedSetup) : base(verifiedSetup ? "Turn on Stamped screenshots" : "Settings")
    {
        _s = s;

        // Two columns side by side, so the whole dialog fits a laptop screen without scrolling.
        var left = Column();
        var right = Column();
        if (verifiedSetup)
        {
            left.Controls.Add(Ui.Section(Ui.Stack(
                Ui.Text("Stamped screenshots", Ui.Title),
                Ui.Text("Each capture gets an invisible watermark and two digital signatures (P-256 and post-quantum ML-DSA-65) " +
                    "made with a key that never leaves this PC. A review can then tell whether a copy changed, where, " +
                    "and how, even after it was recompressed."))));
        }
        else
        {
            left.Controls.Add(Group("Capture"));
            left.Controls.Add(Ui.Section(Ui.Stack(
                Switch.Labeled("Stamped screenshots", "Watermark and sign every capture.", _verified, Ui.InnerW),
                Switch.Labeled("Also use Ctrl+Shift+3 / 4 / 5", "For keyboards without Print Screen.", _laptop, Ui.InnerW),
                Switch.Labeled("Full screen captures every display", "Otherwise only the display under the cursor.", _all, Ui.InnerW),
                Switch.Labeled("Show a preview after each capture", null, _quick, Ui.InnerW))));
            left.Controls.Add(Group("Storage"));
            left.Controls.Add(Ui.Section(Ui.Stack(
                Ui.Text("Screenshots are saved to " + Settings.ScreenshotDir, Ui.Body),
                Ui.Text("Signing key: " + Keys.Describe(), Ui.Small, Ui.Muted))));
        }

        string host = Uri.TryCreate(s.TimestampUrl, UriKind.Absolute, out var tu) ? tu.Host : "the timestamp server";
        right.Controls.Add(Group("Stamp"));
        right.Controls.Add(Ui.Section(Ui.Stack(
            Ui.Field("Name on the stamp", new Input(_name, Ui.InnerW), "Leave empty to use your Windows name (" + Native.DisplayName() + ")."),
            Ui.Field("Place", new Input(_place, Ui.InnerW), "Leave empty to use the nearest city from your location, if location is on. Found offline."),
            Switch.Labeled("Add my location (latitude and longitude) to the stamp",
                "Off by default. Uses Windows location and puts your coordinates in the image. Anyone you share it with can see them.", _loc, Ui.InnerW),
            Switch.Labeled("Get a trusted timestamp from a public server",
                "Proves the capture existed at that time, even if this PC's clock is wrong. Sends only a hash of the signature, never the image, to " + host + ".",
                _tsa, Ui.InnerW))));

        var columns = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        left.Margin = new Padding(0, 0, Gutter, 0);
        columns.Controls.Add(left);
        columns.Controls.Add(right);
        Body.Controls.Add(columns);
        Body.Controls.Add(Footer(verifiedSetup ? "Turn on" : "Save", width: 2 * Ui.ColW + Gutter));

        _verified.Checked = s.Verified;
        _name.Text = s.DisplayName;
        _place.Text = s.Place;
        _loc.Checked = s.UseLocation;
        _tsa.Checked = s.TrustedTimestamp;
        _laptop.Checked = s.LaptopKeys;
        _all.Checked = s.FullScreen == "all";
        _quick.Checked = s.ShowQuickAccess;
        _name.PlaceholderText = Native.DisplayName();

        FormClosing += (_, _) =>
        {
            if (DialogResult != DialogResult.OK) return;
            _s.Verified = verifiedSetup || _verified.Checked;
            _s.DisplayName = _name.Text.Trim();
            _s.Place = _place.Text.Trim();
            _s.UseLocation = _loc.Checked;
            _s.TrustedTimestamp = _tsa.Checked;
            if (!verifiedSetup)
            {
                _s.LaptopKeys = _laptop.Checked;
                _s.FullScreen = _all.Checked ? "all" : "display";
                _s.ShowQuickAccess = _quick.Checked;
            }
        };
    }

    private const int Gutter = 24;

    private static FlowLayoutPanel Column() =>
        new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Width = Ui.ColW, Margin = Padding.Empty };

    /// <summary>A group name above a card, as in Windows Settings.</summary>
    private static Label Group(string name)
    {
        var l = Ui.Text(name, Ui.Title);
        l.Margin = new Padding(0, 0, 0, 8);
        return l;
    }
}
