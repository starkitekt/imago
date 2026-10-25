namespace Imago;

internal enum AlertKind { Info, Warning, Error }

/// <summary>
/// A Fluent task dialog with a status icon, a heading, a message and one or two buttons.
/// Replaces the system message box so every popup matches the rest of Imago.
/// </summary>
internal sealed class Alert : TaskDialog
{
    private const int IconW = 32, GapW = 12, TextW = Ui.ColW - IconW - GapW;

    private Alert(string heading, string message, AlertKind kind, string primary, string? secondary)
        : base(kind switch { AlertKind.Error => "Something went wrong", AlertKind.Warning => "Check this", _ => "Notice" })
    {
        (string glyph, Color tone) = kind switch
        {
            AlertKind.Warning => (Glyph.Warning, Status.Warning),
            AlertKind.Error => (Glyph.Error, Status.Danger),
            _ => (Glyph.Info, Ui.Brand),
        };
        var icon = new Label
        {
            Text = glyph, Font = Glyph.Large, ForeColor = tone, BackColor = Color.Transparent, UseMnemonic = false,
            Size = new Size(IconW, IconW), TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 0, GapW, 0),
        };

        var head = Ui.Text(heading, Ui.Title);
        head.MaximumSize = new Size(TextW, 0);
        head.Margin = new Padding(0, 4, 0, 4);
        var body = Ui.Text(message, Ui.Body, Ui.Muted);
        body.MaximumSize = new Size(TextW, 0);
        body.Margin = Padding.Empty;

        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        text.Controls.Add(head);
        text.Controls.Add(body);

        var top = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        top.Controls.Add(icon);
        top.Controls.Add(text);

        Body.Controls.Add(top);
        Body.Controls.Add(Footer(primary, secondary));
    }

    /// <summary>Shows the alert and returns the button the person pressed.</summary>
    public static DialogResult Show(IWin32Window? owner, string heading, string message, AlertKind kind, string primary, string? secondary = null)
    {
        using var alert = new Alert(heading, message, kind, primary, secondary);
        return alert.ShowDialog(owner);
    }
}
