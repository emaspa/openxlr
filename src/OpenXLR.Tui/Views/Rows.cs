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

    public void DrawDial(Screen screen, int x, int y, Theme theme, Rgb back, bool focused) =>
        Widgets.Dial(screen, x, y, maximum <= minimum ? 0 : (value - minimum) / (maximum - minimum),
            Enabled ? format(value) : "n/a", theme, back, focused);

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
            case Key.Char when key.Char == '[': Apply(-FineStep); return true;
            case Key.Char when key.Char == ']': Apply(FineStep); return true;
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
        screen.Text(x, y, "< ", focused ? theme.Accent : theme.TextMuted, back);
        screen.Text(x + 2, y, text, fore, back, bold: focused, maxWidth: Math.Max(1, width - 6));
        screen.Text(x + Math.Max(3, Math.Min(width - 2, text.Length + 3)), y, " >",
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

    /// <summary>Hardware groups share the desk, preserving the same control order and selection.</summary>
    public void DrawGroups(Screen screen, Rect area, Theme theme, IReadOnlyList<Row> rows)
    {
        if (area.Width < 90 || area.Height < 26) { Draw(screen, area, theme, rows); return; }
        Settle(rows);
        List<(int Start, int End, Rect Box, bool Dial)> groups = [];
        int[] bottoms = [0, 0];
        for (int start = 0; start < rows.Count;)
        {
            int end = start + 1;
            while (end < rows.Count && rows[end] is not HeadingRow) end++;
            bool dial = rows[start].Label.StartsWith("XLR", StringComparison.Ordinal) && rows[start + 1] is NumberRow;
            int height = end - start + 2 + (dial ? 4 : 0);
            int column = bottoms[0] <= bottoms[1] ? 0 : 1;
            int x = area.X + column * ((area.Width + 1) / 2);
            int width = column == 0 ? area.Width / 2 : area.Width - (area.Width + 1) / 2;
            groups.Add((start, end, new Rect(x, area.Y + bottoms[column], width, height), dial));
            bottoms[column] += height;
            start = end;
        }
        var selected = groups.First(group => _index >= group.Start && _index < group.End);
        int offset = Math.Max(0, selected.Box.Bottom - area.Bottom);
        foreach (var group in groups)
        {
            Rect box = group.Box with { Y = group.Box.Y - offset };
            // Whole cards move together when the hardware needs more than one screen.
            if (box.Y < area.Y || box.Bottom > area.Bottom) continue;
            screen.Panel(box.X, box.Y, box.Width, box.Height, theme.Rule, theme.Card,
                rows[group.Start].Label, theme.TextPrimary);
            int y = box.Y + 1;
            int first = group.Start + 1;
            if (group.Dial && rows[first] is NumberRow gain)
            {
                Rgb back = first == _index ? theme.SelectedFace : theme.Card;
                screen.Fill(box.X + 1, y, box.Width - 2, 5, back);
                gain.DrawDial(screen, box.X + 3, y, theme, back, first == _index);
                screen.Text(box.X + 19, y + 1, "GAIN", theme.TextSecondary, back);
                screen.Text(box.X + 19, y + 3, gain.Note ?? (first == _index ? "- / +" : "0 to 80 dB"),
                    first == _index ? theme.Accent : theme.TextMuted, back, maxWidth: box.Width - 21);
                y += 5;
                first++;
            }
            for (int at = first; at < group.End; at++, y++)
                DrawRow(screen, new Rect(box.X + 1, y, box.Width - 2, 1), theme, rows[at], at == _index,
                    box.Width >= 55 ? 23 : 19);
        }
        if (bottoms.Max() > area.Height)
            screen.Text(area.Right - 25, area.Bottom - 1, " Up/Down reveals controls ", theme.TextSecondary, theme.Card);
    }

    private void Settle(IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0) return;
        _index = Math.Clamp(_index, 0, rows.Count - 1);
        if (rows[_index] is HeadingRow) Step(rows, 1);
    }

    public void Draw(Screen screen, Rect area, Theme theme, IReadOnlyList<Row> rows, int labelWidth = 26)
    {
        if (rows.Count == 0) return;
        Settle(rows);

        int height = Math.Max(1, area.Height);
        if (_index < _scroll) _scroll = _index;
        if (_index >= _scroll + height) _scroll = _index - height + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, rows.Count - height));

        for (int line = 0; line < height && _scroll + line < rows.Count; line++)
        {
            int at = _scroll + line;
            int y = area.Y + line;
            DrawRow(screen, new Rect(area.X, y, area.Width, 1), theme, rows[at], at == _index, labelWidth);
        }
    }

    private static void DrawRow(Screen screen, Rect area, Theme theme, Row row, bool focused, int labelWidth)
    {
        Rgb back = focused ? theme.Selection : theme.Card;
        screen.Fill(area.X, area.Y, area.Width, 1, back);
        if (row is HeadingRow)
        {
            screen.Text(area.X + 1, area.Y, row.Label.ToUpperInvariant(), theme.TextSecondary, back, true,
                maxWidth: area.Width - 2);
            return;
        }
        Rgb label = row.Enabled ? focused ? theme.TextPrimary : theme.TextDetail : theme.ControlDisabled;
        screen.Text(area.X + 2, area.Y, row.Label, label, back, focused, maxWidth: labelWidth - 3);
        if (focused) screen.Set(area.X, area.Y, '┃', theme.Accent, back);
        int valueX = area.X + labelWidth;
        int valueWidth = Math.Max(4, area.Right - valueX - 2);
        int noteWidth = row.Note is { Length: > 0 } note ? Math.Min(note.Length + 1, Math.Max(0, valueWidth - 14)) : 0;
        row.DrawValue(screen, valueX, area.Y, valueWidth - noteWidth, theme, back, focused);
        if (noteWidth > 0)
            screen.Text(area.Right - noteWidth - 1, area.Y, row.Note!, theme.TextAlert, back, maxWidth: noteWidth);
    }

    /// <summary>Moves or hands the key to the selected row.</summary>
    public bool Handle(KeyPress key, IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0) return false;
        Settle(rows);
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
