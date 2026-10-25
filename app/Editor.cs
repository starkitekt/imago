using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.RegularExpressions;

namespace Imago;

internal enum EditOutcome { Save, Untouched, Discard }

/// <summary>What the editor hands back. Image is set only for Save; the log lists every mark, in final image pixels.</summary>
internal sealed record EditResult(EditOutcome Outcome, Bitmap? Image, IReadOnlyList<EditOp> Log);

internal enum EditTool { Select, Blur, Redact, Pen, Highlighter, Arrow, Box, Text, Step, Crop }

/// <summary>
/// The editor that opens right after a capture: blur and redact first, then pen, highlighter, arrows,
/// boxes, handwritten text, numbered steps and crop. The unedited capture stays in memory; only the
/// edited image leaves this window.
/// </summary>
internal sealed class Editor : FluentWindow
{
    private static readonly Color StageBack = Color.FromArgb(243, 243, 243);
    private static readonly Color Red = Color.FromArgb(230, 46, 46), Yellow = Color.FromArgb(255, 200, 0);
    private static readonly Color[] Palette = [Red, Yellow, Color.FromArgb(16, 163, 74), Ui.Brand, Color.FromArgb(24, 24, 24), Color.White];
    private static readonly string[] WeightNames = ["Thin", "Medium", "Thick"];

    private static readonly Dictionary<EditTool, (string Name, char Key, string Hint)> Info = new()
    {
        [EditTool.Select] = ("Select", 'V', "Click a mark to move it. Delete removes it."),
        [EditTool.Blur] = ("Blur", 'B', "Drag over anything private to blur it."),
        [EditTool.Redact] = ("Redact", 'R', "Drag to cover with a solid black box."),
        [EditTool.Pen] = ("Pen", 'P', "Draw freehand."),
        [EditTool.Highlighter] = ("Highlighter", 'H', "Drag over text to highlight it."),
        [EditTool.Arrow] = ("Arrow", 'A', "Drag to draw an arrow. Hold Shift to keep it straight."),
        [EditTool.Box] = ("Box", 'O', "Drag to draw a box."),
        [EditTool.Text] = ("Text", 'T', "Click and type. Enter finishes, Shift+Enter adds a line."),
        [EditTool.Step] = ("Numbered step", 'N', "Click to place numbered steps in order."),
        [EditTool.Crop] = ("Crop", 'C', "Drag to keep just part of the capture."),
    };

    private readonly Bitmap _capture;
    private readonly EditStage _stage;
    private readonly Panel _header, _bar;
    private readonly Label _hint = new()
    {
        AutoSize = true, Font = Ui.Body, ForeColor = Ui.Muted, BackColor = Color.Transparent,
        UseMnemonic = false, Margin = new Padding(12, 8, 0, 0),
    };
    private readonly IconButton _undo, _redo;
    private readonly ToolButton _auto;
    private readonly Dictionary<EditTool, Chip> _toolChips = [];
    private readonly List<Chip> _swatches = [], _weights = [];
    private readonly List<Control> _styleControls = [];
    private readonly List<Panel> _rules = [];
    private readonly Dictionary<EditTool, Color> _colors = [];
    private readonly System.Windows.Forms.Timer _flash = new() { Interval = 3200 };
    private EditResult? _result;

    public static EditResult Run(Bitmap capture)
    {
        using var editor = new Editor(capture);
        editor.ShowDialog();
        return editor._result ?? new EditResult(EditOutcome.Untouched, null, []);
    }

    private Editor(Bitmap capture) : base(dark: false)
    {
        _capture = capture;
        Text = "Edit capture - Imago";
        Icon = Ui.AppIcon;
        Font = Ui.Body;
        BackColor = StageBack;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(720, 480);

        _stage = new EditStage(capture) { Dock = DockStyle.Fill, BackColor = StageBack };
        _stage.Changed += (_, _) => SyncHistory();

        // Header: what this is on the left; history and the two ways out on the right.
        var title = new Label
        {
            AutoSize = true, Text = "Edit", Font = Ui.Title, ForeColor = Ui.Ink, BackColor = Color.Transparent,
            UseMnemonic = false, Margin = new Padding(0, 5, 0, 0),
        };
        _undo = new IconButton("", "Undo (Ctrl+Z)");
        _undo.Click += (_, _) => _stage.Undo();
        _redo = new IconButton("", "Redo (Ctrl+Y)");
        _redo.Click += (_, _) => _stage.Redo();
        var discard = new Pill { Text = "Don't save", Size = Pill.Fit("Don't save"), Margin = new Padding(8, 0, 0, 0) };
        discard.Click += (_, _) => Finish(EditOutcome.Discard);
        var done = new Pill { Text = "Done", Primary = true, Size = Pill.Fit("Done"), Margin = new Padding(8, 0, 0, 0) };
        done.Click += (_, _) => Finish(EditOutcome.Save);

        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Ui.Surface, Margin = Padding.Empty };
        right.Controls.AddRange([_undo, _redo, Rule(), discard, done]);
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Ui.Surface };
        left.Controls.Add(title);
        left.Controls.Add(_hint);
        _header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Ui.Surface };
        _header.Controls.Add(left);
        _header.Controls.Add(right);
        DragArea(_header, left, title, _hint, right);

        // Tool strip: privacy tools first, because that is what a fresh capture needs most.
        var strip = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Ui.Surface };
        strip.Controls.Add(ToolChip(EditTool.Select, Icons.Select));
        strip.Controls.Add(Rule());
        strip.Controls.Add(ToolChip(EditTool.Blur, Icons.Blur));
        strip.Controls.Add(ToolChip(EditTool.Redact, Icons.Redact));
        _auto = new ToolButton(Glyph.Shield, "Auto-redact", "Find emails, phone numbers, card numbers and names, and cover them");
        _auto.Click += async (_, _) => await AutoRedactAsync();
        strip.Controls.Add(_auto);
        strip.Controls.Add(Rule());
        strip.Controls.Add(ToolChip(EditTool.Pen, Icons.Pen));
        strip.Controls.Add(ToolChip(EditTool.Highlighter, Icons.Highlighter));
        strip.Controls.Add(ToolChip(EditTool.Arrow, Icons.Arrow));
        strip.Controls.Add(ToolChip(EditTool.Box, Icons.Box));
        strip.Controls.Add(ToolChip(EditTool.Text, Icons.Text));
        strip.Controls.Add(ToolChip(EditTool.Step, Icons.Step));
        strip.Controls.Add(Rule());
        strip.Controls.Add(ToolChip(EditTool.Crop, Icons.Crop));

        var styleRule = Rule();
        strip.Controls.Add(styleRule);
        _styleControls.Add(styleRule);
        foreach (var color in Palette)
        {
            var c = color;
            var chip = new Chip((g, r, _, on) => Icons.Swatch(g, r, c, on), Swatch.Name(c), 28) { Plain = true, Tag = c };
            chip.Click += (_, _) => PickColor(c);
            _swatches.Add(chip);
            _styleControls.Add(chip);
            strip.Controls.Add(chip);
        }
        var weightRule = Rule();
        strip.Controls.Add(weightRule);
        _styleControls.Add(weightRule);
        for (int i = 0; i < 3; i++)
        {
            int w = i;
            var chip = new Chip((g, r, ink, _) => Icons.Weight(g, r, ink, w), WeightNames[w], 28);
            chip.Click += (_, _) => PickWeight(w);
            _weights.Add(chip);
            _styleControls.Add(chip);
            strip.Controls.Add(chip);
        }

        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Ui.Hairline };
        _bar = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Ui.Surface };
        _bar.Controls.Add(strip);
        _bar.Controls.Add(line);

        Controls.Add(_stage);
        Controls.Add(_bar);
        Controls.Add(_header);

        _flash.Tick += (_, _) => { _flash.Stop(); _hint.Text = Info[_stage.Tool].Hint; };
        PickWeight(1);
        SetTool(EditTool.Blur);
        SyncHistory();
        FormClosed += (_, _) => _flash.Dispose();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitBars();
        // Big enough to show the capture at full size when the screen allows it.
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = Math.Clamp(_capture.Width + LogicalToDeviceUnits(96), LogicalToDeviceUnits(960), work.Width * 9 / 10);
        int h = Math.Clamp(_capture.Height + LogicalToDeviceUnits(56 + 52 + 96), LogicalToDeviceUnits(600), work.Height * 9 / 10);
        Size = new Size(w, h);
        Location = new Point(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _stage.Focus();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitBars();
    }

    /// <summary>The bars have fixed sizes from the design, so they follow the screen's scale by hand.</summary>
    private void FitBars()
    {
        _header.Height = LogicalToDeviceUnits(56);
        int side = LogicalToDeviceUnits(32), top = (_header.Height - 32) / 2;
        _header.Padding = new Padding(side, top, side, 0);
        _bar.Height = LogicalToDeviceUnits(52);
        _bar.Padding = new Padding(side - LogicalToDeviceUnits(8), (_bar.Height - LogicalToDeviceUnits(36)) / 2, side, 0);
        foreach (var chip in _toolChips.Values.Concat(_swatches).Concat(_weights))
        {
            int s = LogicalToDeviceUnits(chip.Logical), m = (LogicalToDeviceUnits(36) - s) / 2;
            chip.Size = new Size(s, s);
            chip.Margin = new Padding(LogicalToDeviceUnits(1), m, LogicalToDeviceUnits(1), 0);
        }
        _auto.FitTo(this);
        _auto.Margin = new Padding(LogicalToDeviceUnits(4), LogicalToDeviceUnits(2), 0, 0);
        foreach (var rule in _rules)
        {
            rule.Size = new Size(1, LogicalToDeviceUnits(20));
            rule.Margin = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(8), LogicalToDeviceUnits(10), 0);
        }
    }

    private Panel Rule()
    {
        var rule = new Panel { Size = new Size(1, 20), BackColor = Ui.Hairline, Margin = new Padding(4, 6, 12, 0) };
        _rules.Add(rule);
        return rule;
    }

    private Chip ToolChip(EditTool tool, Action<Graphics, Rectangle, Color, bool> icon)
    {
        var (name, key, _) = Info[tool];
        var chip = new Chip(icon, $"{name} ({key})", 36);
        chip.Click += (_, _) => SetTool(tool);
        _toolChips[tool] = chip;
        return chip;
    }

    private static bool Styled(EditTool t) => t is EditTool.Pen or EditTool.Highlighter or EditTool.Arrow or EditTool.Box or EditTool.Text or EditTool.Step;

    private void SetTool(EditTool tool)
    {
        _stage.Tool = tool;
        foreach (var (t, chip) in _toolChips) chip.Selected = t == tool;
        foreach (var c in _styleControls) c.Visible = Styled(tool);
        if (Styled(tool)) PickColor(_colors.TryGetValue(tool, out var c) ? c : tool == EditTool.Highlighter ? Yellow : Red);
        _flash.Stop();
        _hint.Text = Info[tool].Hint;
    }

    private void PickColor(Color c)
    {
        _colors[_stage.Tool] = c;
        _stage.Ink = c;
        foreach (var s in _swatches) s.Selected = s.Tag is Color sc && sc.ToArgb() == c.ToArgb();
    }

    private void PickWeight(int w)
    {
        _stage.Weight = w;
        for (int i = 0; i < _weights.Count; i++) _weights[i].Selected = i == w;
    }

    private void SyncHistory()
    {
        _undo.Enabled = _stage.CanUndo;
        _redo.Enabled = _stage.CanRedo;
    }

    private void Flash(string text, bool sticky = false)
    {
        _hint.Text = text;
        _flash.Stop();
        if (!sticky) _flash.Start();
    }

    private async Task AutoRedactAsync()
    {
        if (!Ocr.Available)
        {
            Alert.Show(this, "Auto-redact needs a text recognition language",
                "Add a language with text recognition in Settings > Time & language > Language, then try again.", AlertKind.Warning, "OK");
            return;
        }
        _auto.Enabled = false;
        Flash("Looking for private details...", sticky: true);
        try
        {
            var words = await Ocr.WordsAsync(_capture);
            int n = _stage.AddRedactions(Sensitive.Find(words));
            SetTool(EditTool.Redact);
            Flash(n switch
            {
                0 => "Found nothing that looks private. Check by eye.",
                1 => "Covered 1 detail. Check the rest by eye.",
                _ => $"Covered {n} details. Check the rest by eye.",
            });
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Flash("Auto-redact didn't work: " + e.Message);
        }
        finally { _auto.Enabled = true; }
    }

    private void Finish(EditOutcome outcome)
    {
        _stage.CommitText();
        if (outcome == EditOutcome.Save && !_stage.Dirty) outcome = EditOutcome.Untouched;
        if (outcome == EditOutcome.Save)
        {
            var (image, log) = _stage.Compose();
            _result = new EditResult(EditOutcome.Save, image, log);
        }
        else _result = new EditResult(outcome, null, []);
        Close();
    }

    /// <summary>Esc, or closing the window. With no edits that saves the capture as it is; with edits it asks, so a blur is never lost by accident.</summary>
    private void AskToLeave()
    {
        if (!_stage.Dirty) { Finish(EditOutcome.Untouched); return; }
        var answer = Alert.Show(this, "Save with your edits?",
            "Your blurs and marks are kept only if you save them. The unedited capture is never saved.",
            AlertKind.Info, "Save with edits", "Keep editing");
        if (answer == DialogResult.OK) Finish(EditOutcome.Save);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_result is null && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            BeginInvoke(AskToLeave);
        }
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, System.Windows.Forms.Keys keyData)
    {
        if (_stage.Typing)
            return _stage.TypeKey(keyData) || base.ProcessCmdKey(ref msg, keyData);

        const System.Windows.Forms.Keys Ctrl = System.Windows.Forms.Keys.Control, Shift = System.Windows.Forms.Keys.Shift;
        switch (keyData)
        {
            case Ctrl | System.Windows.Forms.Keys.Z: _stage.Undo(); return true;
            case Ctrl | System.Windows.Forms.Keys.Y or Ctrl | Shift | System.Windows.Forms.Keys.Z: _stage.Redo(); return true;
            case System.Windows.Forms.Keys.Delete or System.Windows.Forms.Keys.Back: _stage.DeleteSelected(); return true;
            case System.Windows.Forms.Keys.Enter: Finish(EditOutcome.Save); return true;
            case System.Windows.Forms.Keys.Escape:
                if (_stage.HasSelection) _stage.ClearSelection();
                else AskToLeave();
                return true;
        }
        if ((keyData & (Ctrl | System.Windows.Forms.Keys.Alt)) == 0)
        {
            var key = keyData & System.Windows.Forms.Keys.KeyCode;
            foreach (var (tool, info) in Info)
                if ((int)key == info.Key) { SetTool(tool); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

/// <summary>
/// The canvas: shows the capture fitted to the window and turns drags into marks. Keeps the marks and
/// the crop, with undo and redo as whole snapshots.
/// </summary>
internal sealed class EditStage : Control
{
    private sealed record Snap(Mark[] Marks, Rectangle? Crop);

    private static readonly float[] Strokes = [2f, 4f, 7f], Markers = [12f, 18f, 26f], TextSizes = [16f, 22f, 32f], Radii = [10f, 13f, 17f];

    private readonly Bitmap _capture;
    private readonly Stack<Snap> _undo = new(), _redo = new();
    private readonly Random _rng = new();
    private readonly System.Windows.Forms.Timer _blink = new() { Interval = 530 };
    private List<Mark> _marks = [];
    private Rectangle? _crop;
    private EditTool _tool;

    // The capture as shown, scaled once per size rather than on every paint.
    private Bitmap? _view;
    private float _scale = 1;
    private Point _origin;

    // The drag in progress.
    private bool _down;
    private PointF _from, _to;
    private List<PointF>? _ink;
    private int _seed;
    private int _sel = -1;
    private Mark? _grabbed;
    private Snap? _beforeMove;

    // Text being typed, and the state to return to if it's cancelled.
    private TextMark? _text;
    private Snap? _beforeText;
    private bool _caretOn = true;

    public event EventHandler? Changed;

    public EditStage(Bitmap capture)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _capture = capture;
        TabStop = true;
        _blink.Tick += (_, _) => { _caretOn = !_caretOn; Invalidate(); };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public EditTool Tool
    {
        get => _tool;
        set { CommitText(); _tool = value; _sel = -1; Cursor = CursorFor(value); Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Ink { get; set; } = Color.Red;

    /// <summary>Thin, medium or thick: 0, 1 or 2.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Weight { get; set; } = 1;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool Typing => _text is not null;
    public bool HasSelection => _sel >= 0;
    public bool Dirty => _marks.Count > 0 || _crop is not null || _text is { Text.Length: > 0 };

    // Sizes are in screen pixels at this display's scale, and the capture's pixels are screen pixels.
    private float Dpi => DeviceDpi / 96f;
    private Rectangle Whole => new(Point.Empty, _capture.Size);

    private static Cursor CursorFor(EditTool t) => t switch
    {
        EditTool.Select => Cursors.Default,
        EditTool.Text => Cursors.IBeam,
        EditTool.Step => Cursors.Hand,
        _ => Cursors.Cross,
    };

    public void Undo()
    {
        CommitText();
        if (_undo.Count == 0) return;
        _redo.Push(Now());
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        CommitText();
        if (_redo.Count == 0) return;
        _undo.Push(Now());
        Restore(_redo.Pop());
    }

    public void DeleteSelected()
    {
        if (_sel < 0) return;
        Push();
        _marks.RemoveAt(_sel);
        _sel = -1;
        Touched();
    }

    public void ClearSelection()
    {
        _sel = -1;
        Invalidate();
    }

    /// <summary>Adds auto-redact boxes, skipping any already covered. Returns how many were added.</summary>
    public int AddRedactions(IEnumerable<Rectangle> boxes)
    {
        var add = boxes.Select(b => Rectangle.Intersect(b, Whole))
            .Where(b => b.Width > 0 && b.Height > 0 && !_marks.Any(m => m.Privacy && m.Bounds.Contains(b)))
            .Distinct().ToList();
        if (add.Count == 0) return 0;
        Push();
        foreach (var b in add) _marks.Add(new RedactMark(b, auto: true));
        Touched();
        return add.Count;
    }

    /// <summary>The edited image at full resolution, cropped, and the edit log in its pixels.</summary>
    public (Bitmap Image, List<EditOp> Log) Compose()
    {
        var crop = _crop ?? Whole;
        var bmp = new Bitmap(crop.Width, crop.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TranslateTransform(-crop.X, -crop.Y);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(_capture, Whole, Whole, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            Render(g, _capture, _marks);
        }
        var frame = new Rectangle(Point.Empty, crop.Size);
        var log = new List<EditOp>();
        foreach (var m in _marks)
        {
            var b = m.Bounds;
            b.Offset(-crop.X, -crop.Y);
            b.Intersect(frame);
            if (b.Width > 0 && b.Height > 0) log.Add(new EditOp(m.Tool, b));
        }
        if (_crop is { } c) log.Add(new EditOp("crop", c));
        return (bmp, log);
    }

    /// <summary>Privacy marks first, so nothing drawn on top can be hidden under a blur; steps numbered in the order placed.</summary>
    public static void Render(Graphics g, Bitmap source, IReadOnlyList<Mark> marks)
    {
        foreach (var m in marks) if (m.Privacy) m.Draw(g, source, 0);
        int step = 0;
        foreach (var m in marks) if (!m.Privacy) m.Draw(g, source, m is StepMark ? ++step : 0);
    }

    private Snap Now() => new([.. _marks], _crop);

    private void Push()
    {
        _undo.Push(Now());
        _redo.Clear();
    }

    private void Restore(Snap s)
    {
        _marks = [.. s.Marks];
        _crop = s.Crop;
        _sel = -1;
        Touched();
    }

    private void Touched()
    {
        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- Text ----

    private void BeginText(TextMark mark, int existing)
    {
        _beforeText = Now();
        if (existing >= 0) _marks.RemoveAt(existing);
        _sel = -1;
        _text = mark;
        _caretOn = true;
        _blink.Start();
        Invalidate();
    }

    public void CommitText()
    {
        if (_text is null || _beforeText is null) return;
        var text = _text.Text.TrimEnd();
        bool removedOld = _beforeText.Marks.Length != _marks.Count;
        var mark = _text;
        _text = null;
        _blink.Stop();
        if (text.Length > 0 || removedOld)
        {
            _undo.Push(_beforeText);
            _redo.Clear();
        }
        if (text.Length > 0) _marks.Add(mark.With(text));
        _beforeText = null;
        Touched();
    }

    private void CancelText()
    {
        if (_text is null || _beforeText is null) return;
        _marks = [.. _beforeText.Marks];
        _text = null;
        _beforeText = null;
        _blink.Stop();
        Touched();
    }

    private void Type(string s)
    {
        if (_text is null) return;
        _text = _text.With(_text.Text + s);
        _caretOn = true;
        _blink.Stop();
        _blink.Start();
        Invalidate();
    }

    /// <summary>Keys that mean something while typing. Everything else arrives as characters.</summary>
    public bool TypeKey(System.Windows.Forms.Keys key)
    {
        if (_text is null) return false;
        switch (key)
        {
            case System.Windows.Forms.Keys.Enter: CommitText(); return true;
            case System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.Enter: Type("\n"); return true;
            case System.Windows.Forms.Keys.Escape: CancelText(); return true;
            case System.Windows.Forms.Keys.Back:
                var t = _text.Text;
                if (t.Length > 0)
                {
                    int cut = t.Length >= 2 && char.IsSurrogatePair(t[^2], t[^1]) ? 2 : 1;
                    _text = _text.With(t[..^cut]);
                    _caretOn = true;
                    Invalidate();
                }
                return true;
            case System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.V:
                if (Clipboard.ContainsText()) Type(Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n'));
                return true;
        }
        return false;
    }

    protected override bool IsInputKey(System.Windows.Forms.Keys keyData) => _text is not null || base.IsInputKey(keyData);

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (_text is null || char.IsControl(e.KeyChar)) return;
        Type(e.KeyChar.ToString());
        e.Handled = true;
    }

    // ---- Layout ----

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Fit();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Fit();
    }

    private void Fit()
    {
        if (Width < 2 || Height < 2) return;
        int pad = LogicalToDeviceUnits(32);
        float aw = Math.Max(1, Width - 2 * pad), ah = Math.Max(1, Height - 2 * pad);
        // Never enlarge, except a tiny capture, which is easier to mark up at twice the size.
        float max = _capture.Width < 480 && _capture.Height < 320 ? 2f : 1f;
        _scale = Math.Min(max, Math.Min(aw / _capture.Width, ah / _capture.Height));
        int w = Math.Max(1, (int)Math.Round(_capture.Width * _scale)), h = Math.Max(1, (int)Math.Round(_capture.Height * _scale));
        _origin = new Point((Width - w) / 2, (Height - h) / 2);
        if (_view is null || _view.Size != new Size(w, h))
        {
            _view?.Dispose();
            _view = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(_view);
            g.InterpolationMode = _scale < 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_capture, new Rectangle(0, 0, w, h), Whole, GraphicsUnit.Pixel);
        }
        Invalidate();
    }

    private PointF ToImage(Point p) => new((p.X - _origin.X) / _scale, (p.Y - _origin.Y) / _scale);

    private Rectangle DragRect(bool clamp)
    {
        var r = Rectangle.FromLTRB(
            (int)Math.Floor(Math.Min(_from.X, _to.X)), (int)Math.Floor(Math.Min(_from.Y, _to.Y)),
            (int)Math.Ceiling(Math.Max(_from.X, _to.X)), (int)Math.Ceiling(Math.Max(_from.Y, _to.Y)));
        return clamp ? Rectangle.Intersect(r, Whole) : r;
    }

    /// <summary>The arrow's end; with Shift held, snapped to the nearest 45 degrees.</summary>
    private PointF ArrowEnd()
    {
        if ((ModifierKeys & System.Windows.Forms.Keys.Shift) == 0) return _to;
        float dx = _to.X - _from.X, dy = _to.Y - _from.Y, len = MathF.Sqrt(dx * dx + dy * dy);
        float a = MathF.Round(MathF.Atan2(dy, dx) / (MathF.PI / 4)) * (MathF.PI / 4);
        return new PointF(_from.X + MathF.Cos(a) * len, _from.Y + MathF.Sin(a) * len);
    }

    private int HitTest(PointF p)
    {
        var pt = Point.Round(p);
        int slop = (int)Math.Ceiling(6 / _scale);
        // Annotations sit on top of privacy marks, so they're found first.
        for (int i = _marks.Count - 1; i >= 0; i--) if (!_marks[i].Privacy && _marks[i].Hit(pt, slop)) return i;
        for (int i = _marks.Count - 1; i >= 0; i--) if (_marks[i].Privacy && _marks[i].Hit(pt, slop)) return i;
        return -1;
    }

    // ---- Mouse ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        var p = ToImage(e.Location);
        // A click away from the text being typed finishes it.
        if (_text is not null) { CommitText(); return; }

        switch (_tool)
        {
            case EditTool.Select:
                _sel = HitTest(p);
                _grabbed = _sel >= 0 ? _marks[_sel] : null;
                _beforeMove = _grabbed is null ? null : Now();
                _from = p;
                Invalidate();
                return;
            case EditTool.Step:
                Push();
                _marks.Add(new StepMark(p, Ink, Radii[Weight] * Dpi));
                Touched();
                return;
            case EditTool.Text:
                int hit = HitTest(p);
                if (hit >= 0 && _marks[hit] is TextMark old) BeginText(old, hit);
                else
                {
                    float size = TextSizes[Weight] * Dpi;
                    BeginText(new TextMark(new PointF(p.X, p.Y - size * 0.6f), "", Ink, size), -1);
                }
                return;
        }
        _down = true;
        _from = _to = p;
        _seed = _rng.Next();
        if (_tool is EditTool.Pen or EditTool.Highlighter) _ink = [p];
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = ToImage(e.Location);
        if (_grabbed is not null && _beforeMove is not null && e.Button == MouseButtons.Left)
        {
            int dx = (int)Math.Round(p.X - _from.X), dy = (int)Math.Round(p.Y - _from.Y);
            if (dx == 0 && dy == 0 && ReferenceEquals(_marks[_sel], _grabbed)) return;
            if (ReferenceEquals(_marks[_sel], _grabbed)) { _undo.Push(_beforeMove); _redo.Clear(); }
            _marks[_sel] = _grabbed.Offset(dx, dy);
            Invalidate();
            return;
        }
        if (_down)
        {
            if (_ink is not null)
            {
                var last = _ink[^1];
                float d = MathF.Sqrt((p.X - last.X) * (p.X - last.X) + (p.Y - last.Y) * (p.Y - last.Y));
                if (d * _scale >= 2) _ink.Add(p);
            }
            _to = p;
            Invalidate();
            return;
        }
        if (_tool == EditTool.Select) Cursor = HitTest(p) >= 0 ? Cursors.SizeAll : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_grabbed is not null)
        {
            bool moved = !ReferenceEquals(_marks[_sel], _grabbed);
            _grabbed = null;
            _beforeMove = null;
            if (moved) Touched();
            return;
        }
        if (!_down) return;
        _down = false;
        _to = ToImage(e.Location);
        var box = DragRect(clamp: _tool is EditTool.Blur or EditTool.Redact or EditTool.Crop);
        Mark? add = _tool switch
        {
            EditTool.Blur when box.Width >= 4 && box.Height >= 4 => new BlurMark(box, _seed),
            EditTool.Redact when box.Width >= 4 && box.Height >= 4 => new RedactMark(box),
            EditTool.Box when box.Width >= 4 && box.Height >= 4 => new BoxMark(box, Ink, Strokes[Weight] * Dpi, _seed),
            EditTool.Arrow when Dist(_from, ArrowEnd()) * _scale >= 8 => new ArrowMark(_from, ArrowEnd(), Ink, Strokes[Weight] * Dpi, _seed),
            EditTool.Pen when _ink is not null => new InkMark([.. _ink], Ink, Strokes[Weight] * Dpi, highlighter: false),
            EditTool.Highlighter when _ink is not null => new InkMark([.. _ink], Ink, Markers[Weight] * Dpi, highlighter: true),
            _ => null,
        };
        _ink = null;
        if (add is not null)
        {
            Push();
            _marks.Add(add);
            Touched();
        }
        else if (_tool == EditTool.Crop && box.Width >= 8 && box.Height >= 8)
        {
            Push();
            _crop = box == Whole ? null : box;
            Touched();
        }
        else Invalidate();
    }

    private static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---- Paint ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_view is null) return;
        var frame = new Rectangle(_origin, _view.Size);

        // A soft lift off the stage, then the capture at its fitted size.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 3; i >= 1; i--)
        {
            using var shadow = new SolidBrush(Color.FromArgb(7, 0, 0, 0));
            g.FillRectangle(shadow, Rectangle.Inflate(new Rectangle(frame.X, frame.Y + i, frame.Width, frame.Height), i, i));
        }
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_view, frame);

        var state = g.Save();
        g.SetClip(frame);
        g.TranslateTransform(_origin.X, _origin.Y);
        g.ScaleTransform(_scale, _scale);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        Render(g, _capture, _marks);
        PaintLive(g);
        PaintText(g);
        PaintCrop(g);
        PaintSelection(g);
        g.Restore(state);
    }

    private void PaintLive(Graphics g)
    {
        if (!_down) return;
        float stroke = Strokes[Weight] * Dpi;
        switch (_tool)
        {
            case EditTool.Blur:
                var r = DragRect(clamp: true);
                if (r.Width < 1 || r.Height < 1) return;
                using (var path = Shape.Round(r, Math.Max(1, Math.Min(8, Math.Min(r.Width, r.Height) / 4))))
                {
                    using var frost = new SolidBrush(Color.FromArgb(150, 255, 255, 255));
                    g.FillPath(frost, path);
                    using var edge = new Pen(Ui.Brand, 1.5f / _scale) { DashStyle = DashStyle.Dash };
                    g.DrawPath(edge, path);
                }
                return;
            case EditTool.Redact:
                var rr = DragRect(clamp: true);
                if (rr.Width > 0 && rr.Height > 0) new RedactMark(rr).Draw(g, _capture, 0);
                return;
            case EditTool.Box:
                var br = DragRect(clamp: false);
                if (br.Width > 0 && br.Height > 0) BoxMark.Paint(g, br, Ink, stroke, _seed);
                return;
            case EditTool.Arrow:
                ArrowMark.Paint(g, _from, ArrowEnd(), Ink, stroke, _seed);
                return;
            case EditTool.Pen or EditTool.Highlighter when _ink is not null:
                bool hl = _tool == EditTool.Highlighter;
                new InkMark([.. _ink], Ink, hl ? Markers[Weight] * Dpi : stroke, hl).Draw(g, _capture, 0);
                return;
            case EditTool.Crop:
                // Drawn by PaintCrop from the live rectangle.
                return;
        }
    }

    private void PaintText(Graphics g)
    {
        if (_text is null) return;
        _text.Draw(g, _capture, 0);
        var b = _text.Bounds;
        using (var frame = new Pen(Color.FromArgb(140, Ui.Brand), 1f / _scale) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(frame, b);
        if (!_caretOn) return;
        using var caret = new SolidBrush(_text.Color);
        g.FillRectangle(caret, _text.Caret());
    }

    private void PaintCrop(Graphics g)
    {
        Rectangle? crop = _down && _tool == EditTool.Crop ? DragRect(clamp: true) : _crop;
        if (crop is not { Width: > 0, Height: > 0 } c) return;
        using (var outside = new Region(Whole))
        {
            outside.Exclude(c);
            using var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
            g.FillRegion(dim, outside);
        }
        float px = 1f / _scale;
        using (var dark = new Pen(Color.FromArgb(90, 0, 0, 0), 3 * px)) g.DrawRectangle(dark, c);
        using (var light = new Pen(Color.White, 1.25f * px)) g.DrawRectangle(light, c);
        if (_tool != EditTool.Crop) return;
        // Corner ticks, so it reads as a crop frame.
        float t = 14 * px * Dpi, w = 3 * px;
        using var tick = new Pen(Color.White, w) { StartCap = LineCap.Square, EndCap = LineCap.Square };
        foreach (var (x, y, sx, sy) in new[] { (c.Left, c.Top, 1, 1), (c.Right, c.Top, -1, 1), (c.Right, c.Bottom, -1, -1), (c.Left, c.Bottom, 1, -1) })
        {
            g.DrawLine(tick, x, y, x + sx * t, y);
            g.DrawLine(tick, x, y, x, y + sy * t);
        }
    }

    private void PaintSelection(Graphics g)
    {
        if (_sel < 0 || _sel >= _marks.Count) return;
        var b = _marks[_sel].Bounds;
        b.Inflate((int)Math.Ceiling(3 / _scale), (int)Math.Ceiling(3 / _scale));
        using var pen = new Pen(Ui.Brand, 1.5f / _scale) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(pen, b);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _blink.Dispose();
            _view?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>A square button with a drawn icon, for the tool strip. Selected tools get a brand tint.</summary>
internal sealed class Chip : Control
{
    private static readonly ToolTip Tips = new() { InitialDelay = 400 };
    private readonly Action<Graphics, Rectangle, Color, bool> _icon;
    private bool _hot, _selected;

    /// <summary>The size at 100% scale.</summary>
    public int Logical { get; }

    /// <summary>Plain chips show selection in their icon (a ring on a swatch) rather than with a tint.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Plain { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Invalidate(); }
    }

    public Chip(Action<Graphics, Rectangle, Color, bool> icon, string tip, int logical)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _icon = icon;
        Logical = logical;
        Size = new Size(logical, logical);
        Cursor = Cursors.Hand;
        AccessibleName = tip;
        AccessibleRole = AccessibleRole.PushButton;
        Tips.SetToolTip(this, tip);
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Shape.Backdrop(this, Ui.Surface));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        bool tint = _selected && !Plain;
        if (tint || _hot)
        {
            using var path = Shape.Round(new Rectangle(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(6));
            using var fill = new SolidBrush(tint ? Color.FromArgb(30, Ui.Brand) : Ui.SurfaceHover);
            g.FillPath(fill, path);
        }
        int box = LogicalToDeviceUnits(20);
        _icon(g, new Rectangle((Width - box) / 2, (Height - box) / 2, box, box), tint ? Ui.Brand : Ui.Ink, _selected);
    }
}

/// <summary>Tool icons, drawn rather than taken from the icon font so the arrow and box show the same hand as the marks.</summary>
internal static class Icons
{
    public static void Select(Graphics g, Rectangle r, Color ink, bool on)
    {
        PointF P(float x, float y) => new(r.X + x * r.Width, r.Y + y * r.Height);
        using var b = new SolidBrush(ink);
        g.FillPolygon(b, [P(0.26f, 0.08f), P(0.26f, 0.84f), P(0.44f, 0.67f), P(0.57f, 0.94f), P(0.68f, 0.89f), P(0.55f, 0.62f), P(0.80f, 0.62f)]);
    }

    public static void Blur(Graphics g, Rectangle r, Color ink, bool on)
    {
        var area = Rectangle.Inflate(r, -1, -1);
        using var clip = Shape.Round(area, Math.Max(2, r.Width / 5));
        var state = g.Save();
        g.SetClip(clip);
        int[] alpha = [70, 150, 100, 190, 60, 130, 110, 80, 170];
        float cw = area.Width / 3f, ch = area.Height / 3f;
        for (int i = 0; i < 9; i++)
        {
            using var b = new SolidBrush(Color.FromArgb(alpha[i], ink));
            g.FillRectangle(b, area.X + i % 3 * cw, area.Y + i / 3 * ch, cw + 0.5f, ch + 0.5f);
        }
        g.Restore(state);
    }

    public static void Redact(Graphics g, Rectangle r, Color ink, bool on)
    {
        var bar = new Rectangle(r.X, r.Y + r.Height / 4, r.Width, r.Height / 2);
        using var path = Shape.Round(bar, Math.Max(2, r.Height / 7));
        using var b = new SolidBrush(on ? ink : RedactMark.Ink);
        g.FillPath(b, path);
    }

    public static void Pen(Graphics g, Rectangle r, Color ink, bool on) => Glyph.Draw(g, "", Glyph.Regular, Rectangle.Inflate(r, 4, 4), ink);
    public static void Highlighter(Graphics g, Rectangle r, Color ink, bool on) => Glyph.Draw(g, "", Glyph.Regular, Rectangle.Inflate(r, 4, 4), ink);
    public static void Crop(Graphics g, Rectangle r, Color ink, bool on) => Glyph.Draw(g, "", Glyph.Regular, Rectangle.Inflate(r, 4, 4), ink);

    public static void Arrow(Graphics g, Rectangle r, Color ink, bool on) =>
        ArrowMark.Paint(g, new PointF(r.X + 2, r.Bottom - 2), new PointF(r.Right - 2, r.Y + 2), ink, r.Width / 12f, 11);

    public static void Box(Graphics g, Rectangle r, Color ink, bool on) =>
        BoxMark.Paint(g, RectangleF.Inflate(r, -2.5f, -3.5f), ink, r.Width / 14f, 5);

    public static void Text(Graphics g, Rectangle r, Color ink, bool on)
    {
        using var font = new Font(HandFont.Family, r.Height * 0.72f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var b = new SolidBrush(ink);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.DrawString("Aa", font, b, new RectangleF(r.X - 4, r.Y - 2, r.Width + 8, r.Height + 4), fmt);
    }

    public static void Step(Graphics g, Rectangle r, Color ink, bool on) =>
        StepMark.Paint(g, new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f), r.Width * 0.44f, ink, 1);

    public static void Swatch(Graphics g, Rectangle r, Color c, bool on)
    {
        var dot = Rectangle.Inflate(r, -2, -2);
        if (on)
        {
            using var ring = new Pen(Ui.Brand, Math.Max(2f, r.Width / 10f));
            g.DrawEllipse(ring, Rectangle.Inflate(r, 1, 1));
            dot = Rectangle.Inflate(r, -3, -3);
        }
        using (var fill = new SolidBrush(c)) g.FillEllipse(fill, dot);
        using var edge = new Pen(Color.FromArgb(c.GetBrightness() > 0.9f ? 70 : 30, 0, 0, 0), 1f);
        g.DrawEllipse(edge, dot);
    }

    public static void Weight(Graphics g, Rectangle r, Color ink, int weight)
    {
        float d = r.Width * (weight switch { 0 => 0.22f, 1 => 0.38f, _ => 0.58f });
        using var b = new SolidBrush(ink);
        g.FillEllipse(b, r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
    }
}

/// <summary>Names for the swatches, for tooltips and screen readers.</summary>
internal static class Swatch
{
    public static string Name(Color c) => c.ToArgb() switch
    {
        var x when x == Color.FromArgb(230, 46, 46).ToArgb() => "Red",
        var x when x == Color.FromArgb(255, 200, 0).ToArgb() => "Yellow",
        var x when x == Color.FromArgb(16, 163, 74).ToArgb() => "Green",
        var x when x == Ui.Brand.ToArgb() => "Blue",
        var x when x == Color.White.ToArgb() => "White",
        _ => "Black",
    };
}

/// <summary>
/// Finds text that looks private in OCR output: email addresses, phone numbers, card numbers that pass
/// the Luhn check, IP addresses, long keys and tokens, and names after greetings or "Name:", "From:", "To:".
/// Returns boxes in the capture's pixels, padded a little. It's a helper, not a guarantee.
/// </summary>
internal static partial class Sensitive
{
    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")] private static partial Regex Email();
    [GeneratedRegex(@"(?<![\w.])\+?\(?\d[\d ().-]{6,}\d(?![\w])")] private static partial Regex Phone();
    [GeneratedRegex(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)")] private static partial Regex Card();
    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")] private static partial Regex Ip();
    [GeneratedRegex(@"(?<![\w-])[A-Za-z0-9_-]{20,}(?![\w-])")] private static partial Regex Token();
    [GeneratedRegex(@"(?:\b(?i:name|from|to|cc|attn)\s*:|\b(?i:dear|hi|hello|hey)\b,?)\s*(?<n>[A-Z][a-z]+(?: [A-Z][a-z]+){0,2})")] private static partial Regex Person();
    [GeneratedRegex(@"^\d{4}[-./]\d{1,2}[-./]\d{1,2}$|^\d{1,2}[-./]\d{1,2}[-./]\d{2,4}$")] private static partial Regex DateLike();

    public static List<Rectangle> Find(List<List<Ocr.Word>> lines)
    {
        var found = new List<Rectangle>();
        foreach (var line in lines)
        {
            var sb = new StringBuilder();
            var spans = new List<(int Start, int End, Rectangle Box)>();
            foreach (var w in line)
            {
                if (sb.Length > 0) sb.Append(' ');
                int start = sb.Length;
                sb.Append(w.Text);
                spans.Add((start, sb.Length, w.Box));
            }
            string text = sb.ToString();

            void Cover(int start, int length)
            {
                Rectangle? box = null;
                foreach (var s in spans)
                    if (s.Start < start + length && s.End > start) box = box is { } b ? Rectangle.Union(b, s.Box) : s.Box;
                if (box is { } r) found.Add(Rectangle.Inflate(r, 3, 3));
            }

            foreach (Match m in Email().Matches(text)) Cover(m.Index, m.Length);
            foreach (Match m in Card().Matches(text))
                if (Luhn(m.Value)) Cover(m.Index, m.Length);
            foreach (Match m in Phone().Matches(text))
            {
                int digits = m.Value.Count(char.IsAsciiDigit);
                if (digits is >= 9 and <= 15 && !DateLike().IsMatch(m.Value.Trim()) && !Luhn(m.Value)) Cover(m.Index, m.Length);
            }
            foreach (Match m in Ip().Matches(text)) Cover(m.Index, m.Length);
            foreach (Match m in Token().Matches(text))
                if (m.Value.Any(char.IsAsciiDigit) && m.Value.Any(char.IsAsciiLetter)) Cover(m.Index, m.Length);
            foreach (Match m in Person().Matches(text))
            {
                var n = m.Groups["n"];
                Cover(n.Index, n.Length);
            }
        }
        return found;
    }

    private static bool Luhn(string s)
    {
        var digits = s.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int d = digits[digits.Length - 1 - i];
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }
}
