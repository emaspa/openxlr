namespace OpenXLR.Tui;

/// <summary>
/// One line in a settings list. The tabs that are a list of controls (the
/// inputs, the outputs, the options) build these from the state on every
/// frame, so a row never holds a copy of what the daemon said.
/// </summary>
internal abstract class Row
{
    protected Row(string label) => Label = label;

    public string Label { get; }

    /// <summary>False for a row the device cannot do, which is drawn faded and skipped.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>A note shown to the right of the value.</summary>
    public string? Note { get; init; }

    public abstract void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused);

    /// <summary>True when the key belonged to this row.</summary>
    public abstract bool Handle(KeyPress key);
}

/// <summary>A control that is on or off.</summary>
internal sealed class ToggleRow(string label, bool value, Action<bool> set) : Row(label)
{
    public override void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused)
    {
        if (!Enabled)
        {
            screen.Text(x, y, " not on this device ", theme.ControlDisabled, back);
            return;
        }
        Widgets.Key(screen, x, y, value ? " on" : "off", value, theme, focused);
    }

    public override bool Handle(KeyPress key)
    {
        if (!Enabled) return false;
        if (key.Key == Key.Char && key.Char == ' ') { set(!value); return true; }
        if (key.Key == Key.Enter) { set(!value); return true; }
        if (key.Key == Key.Left && value) { set(false); return true; }
        if (key.Key == Key.Right && !value) { set(true); return true; }
        return false;
    }
}

/// <summary>A number on a fader, with its own step and range.</summary>
internal sealed class NumberRow(
    string label, double value, double minimum, double maximum, double step,
    Func<double, string> format, Action<double> set) : Row(label)
{
    /// <summary>The finer step, taken with ctrl and an arrow.</summary>
    public double FineStep { get; init; } = step / 5;

    public override void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused)
    {
        if (!Enabled)
        {
            screen.Text(x, y, " not on this device ", theme.ControlDisabled, back);
            return;
        }
        int barWidth = Math.Max(4, width - 10);
        double span = maximum - minimum;
        Widgets.Fader(screen, x, y, barWidth, span <= 0 ? 0 : value - minimum, span <= 0 ? 1 : span, theme, back, focused);
        screen.Text(x + barWidth + 1, y, format(value).PadLeft(8), theme.TextDetail, back);
    }

    public override bool Handle(KeyPress key)
    {
        if (!Enabled) return false;
        double by = key.Ctrl ? FineStep : step;
        switch (key.Key)
        {
            case Key.Left: Apply(-by); return true;
            case Key.Right: Apply(by); return true;
            case Key.Char when key.Char is '-' or '_': Apply(-by); return true;
            case Key.Char when key.Char is '+' or '=': Apply(by); return true;
            case Key.Home: set(minimum); return true;
            case Key.End: set(maximum); return true;
            default: return false;
        }

        void Apply(double delta) => set(Math.Clamp(value + delta, minimum, maximum));
    }
}

/// <summary>One of a few named values.</summary>
internal sealed class ChoiceRow(string label, IReadOnlyList<string> options, int selected, Action<int> set) : Row(label)
{
    public override void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused)
    {
        string text = options.Count == 0 ? "none" : options[Math.Clamp(selected, 0, options.Count - 1)];
        Rgb fore = focused ? theme.TextPrimary : theme.TextDetail;
        screen.Text(x, y, "‹ ", focused ? theme.Accent : theme.TextMuted, back);
        screen.Text(x + 2, y, text, fore, back, bold: focused, maxWidth: Math.Max(1, width - 6));
        screen.Text(x + Math.Max(3, Math.Min(width - 2, text.Length + 3)), y, " ›",
            focused ? theme.Accent : theme.TextMuted, back);
    }

    public override bool Handle(KeyPress key)
    {
        if (options.Count == 0) return false;
        switch (key.Key)
        {
            case Key.Left: set((selected - 1 + options.Count) % options.Count); return true;
            case Key.Right or Key.Enter: set((selected + 1) % options.Count); return true;
            case Key.Char when key.Char == ' ': set((selected + 1) % options.Count); return true;
            default: return false;
        }
    }
}

/// <summary>A reading with no control of its own.</summary>
internal sealed class TextRow(string label, string value) : Row(label)
{
    public override void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused) =>
        screen.Text(x, y, value, theme.TextSecondary, back, maxWidth: width);

    public override bool Handle(KeyPress key) => false;
}

/// <summary>Something that happens when enter is pressed.</summary>
internal sealed class ActionRow(string label, string button, Action run) : Row(label)
{
    /// <summary>True for an action that undoes something, which is lettered as such.</summary>
    public bool Destructive { get; init; }

    public override void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused)
    {
        Rgb face = Destructive ? theme.DangerBack : theme.ControlBack;
        Rgb fore = Destructive ? theme.DangerFore : theme.ControlFore;
        if (focused) face = face.Mix(theme.Accent, 0.35);
        screen.Text(x, y, $" {button} ", fore, face, bold: focused);
    }

    public override bool Handle(KeyPress key)
    {
        if (key.Key == Key.Enter || (key.Key == Key.Char && key.Char == ' ')) { run(); return true; }
        return false;
    }
}

/// <summary>A heading between groups of rows.</summary>
internal sealed class HeadingRow(string label) : Row(label)
{
    public override void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused) { }

    public override bool Handle(KeyPress key) => false;
}

/// <summary>
/// Draws a list of rows and moves through it. A heading is drawn but never
/// selected, so the arrows step between controls rather than stopping on a
/// title.
/// </summary>
internal sealed class RowList
{
    private int _index;
    private int _scroll;

    public void Draw(Screen screen, Rect area, Theme theme, IReadOnlyList<Row> rows, int labelWidth = 26)
    {
        if (rows.Count == 0) return;
        _index = Math.Clamp(_index, 0, rows.Count - 1);
        if (rows[_index] is HeadingRow) Step(rows, 1);

        int height = Math.Max(1, area.Height);
        if (_index < _scroll) _scroll = _index;
        if (_index >= _scroll + height) _scroll = _index - height + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, rows.Count - height));

        for (int line = 0; line < height && _scroll + line < rows.Count; line++)
        {
            int at = _scroll + line;
            Row row = rows[at];
            int y = area.Y + line;
            bool focused = at == _index;
            Rgb back = focused ? theme.Selection : theme.Window;
            screen.Fill(area.X, y, area.Width, 1, back);

            if (row is HeadingRow)
            {
                screen.Text(area.X + 1, y, row.Label.ToUpperInvariant(), theme.TextSecondary, back, bold: true,
                    maxWidth: area.Width - 2);
                continue;
            }

            Rgb label = row.Enabled ? (focused ? theme.TextPrimary : theme.TextDetail) : theme.ControlDisabled;
            // The label stops short of the value column, so a name that runs
            // long is cut with a gap rather than touching the control.
            screen.Text(area.X + 2, y, row.Label, label, back, bold: focused, maxWidth: labelWidth - 4);
            int valueX = area.X + labelWidth;
            int valueWidth = Math.Max(4, area.Right - valueX - 2);
            row.DrawValue(screen, valueX, y, valueWidth, theme, back, focused);
            if (row.Note is { Length: > 0 } note)
                screen.Text(Math.Min(valueX + valueWidth + 1, area.Right - 1), y, note, theme.TextAlert, back,
                    maxWidth: Math.Max(0, area.Right - valueX - valueWidth - 1));
        }
    }

    /// <summary>Moves or hands the key to the selected row.</summary>
    public bool Handle(KeyPress key, IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0) return false;
        _index = Math.Clamp(_index, 0, rows.Count - 1);
        switch (key.Key)
        {
            // A plain arrow walks the list; ctrl and an arrow belong to the
            // row, which is how an insert moves in its chain.
            case Key.Up when !key.Ctrl: Step(rows, -1); return true;
            case Key.Down when !key.Ctrl: Step(rows, 1); return true;
            case Key.PageUp: for (int i = 0; i < 8; i++) Step(rows, -1); return true;
            case Key.PageDown: for (int i = 0; i < 8; i++) Step(rows, 1); return true;
        }
        return rows[_index].Handle(key);
    }

    private void Step(IReadOnlyList<Row> rows, int by)
    {
        int at = _index;
        for (int tries = 0; tries < rows.Count; tries++)
        {
            at += by;
            if (at < 0 || at >= rows.Count) return;
            if (rows[at] is not HeadingRow) { _index = at; return; }
        }
    }
}
