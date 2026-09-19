namespace OpenXLR.Tui;

/// <summary>
/// The whole submixer on one screen: every channel's send into every mix as a
/// grid, the mix masters across the top, live meters on both. The desk shows
/// one mix's sends at a time; this is where the routing is read as a whole.
/// </summary>
internal sealed class MatrixView : View
{
    private const int NameWidth = 16;
    private const int MinCell = 12;

    private int _row;      // 0 is the mix masters, 1 and up are the channels.
    private int _column;
    private int _scroll;

    public override string Title => "Matrix";

    public override string Keys => "Up/Down channel  Left/Right mix  Space mute  -/+ [/] level";

    public override void Draw(Screen screen, Rect area, App app)
    {
        Theme theme = app.Theme;
        Snapshot? state = State(app);
        if (state is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "Waiting for the daemon", theme.TextMuted, theme.Card);
            return;
        }

        List<MixEntry> mixes = state.Mixer.Mixes;
        List<ChannelEntry> channels = state.Mixer.Channels;
        if (mixes.Count == 0 || channels.Count == 0)
        {
            screen.Text(area.X + 2, area.Y + 1, "The mixer is not built yet", theme.TextMuted, theme.Card);
            return;
        }

        _row = Math.Clamp(_row, 0, channels.Count);
        _column = Math.Clamp(_column, 0, mixes.Count - 1);

        // With height to spare every meter is stereo, on two rows of its own,
        // and a channel takes two rows so its neighbours do not touch. A short
        // terminal keeps one row a channel and one summed bar.
        bool roomy = area.Height - 7 >= channels.Count * 2;
        int nameWidth = roomy ? 22 : NameWidth;
        int masterHeight = roomy ? 5 : 3;

        int cells = Math.Max(1, (area.Width - nameWidth - 2) / MinCell);
        int shown = Math.Min(mixes.Count, cells);
        if (_column < _scroll) _scroll = _column;
        if (_column >= _scroll + shown) _scroll = _column - shown + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, mixes.Count - shown));
        int cellWidth = Math.Max(MinCell, (area.Width - nameWidth - 2) / shown);

        int x0 = area.X + 1;
        int y = area.Y;

        // The mix masters: name, volume, mute and the mix's own meter.
        screen.Text(x0, y, "MIXES", theme.TextSecondary, theme.Card, bold: true, maxWidth: nameWidth);
        for (int index = 0; index < shown; index++)
        {
            MixEntry mix = mixes[_scroll + index];
            int x = x0 + nameWidth + index * cellWidth;
            bool here = _row == 0 && _scroll + index == _column;
            Rgb back = here ? theme.Selection : theme.Tile;
            screen.Fill(x, y, cellWidth - 1, masterHeight, back);
            screen.Text(x + 1, y, mix.Name, theme.TextPrimary, back, bold: true, maxWidth: cellWidth - 3);

            int barWidth = cellWidth - 8;
            Widgets.Fader(screen, x + 1, y + 1, barWidth, mix.Volume, mix.Ceiling, theme, back, here);
            screen.Text(x + barWidth + 2, y + 1, Widgets.Percent(mix.Volume).PadLeft(4),
                theme.TextDetail, back);

            Widgets.MuteKey(screen, x + 1, y + 2, mix.Muted ? "MUTE" : " ON ", mix.Muted, theme, here);
            MeterReading level = app.Link.StereoMeter("mix", mix.Id);
            if (roomy)
            {
                Stereo(screen, x + 1, y + 3, y + 4, cellWidth - 4, level, theme, back);
            }
            else
            {
                Widgets.Meter(screen, x + 8, y + 2, cellWidth - 10, level.Level, theme, back);
            }
        }

        y += masterHeight;
        for (int column = 0; column < area.Width; column++)
            screen.Set(area.X + column, y, '\u2500', theme.Rule, theme.Card);
        y++;

        // Three rows a channel where they fit, which puts its name on the row
        // between its two meters; two rows where only those fit, with the name
        // beside the left one; one row and a summed bar in a short terminal.
        int available = Math.Max(1, area.Bottom - y);
        int step = !roomy ? 1 : channels.Count * 3 <= available ? 3 : 2;
        int rows = Math.Max(1, available / step);
        int first = Math.Max(0, Math.Min(_row - 1 - rows + 2, channels.Count - rows));
        if (_row == 0) first = 0;
        int meterWidth = roomy ? nameWidth - 13 : 4;
        int meterX = x0 + nameWidth - meterWidth - 2;
        for (int index = 0; index < rows && first + index < channels.Count; index++)
        {
            ChannelEntry channel = channels[first + index];
            int line = y + index * step;
            int nameLine = line + (step == 3 ? 1 : 0);
            bool selectedRow = _row == first + index + 1;
            // Every other channel takes a slightly different ground, so the
            // rows of one channel read as its own block rather than pairing a
            // channel's lower meter with the next channel's upper one.
            Rgb rowBack = selectedRow ? theme.SelectedFace
                : step > 1 && (first + index) % 2 == 1 ? theme.Card.Mix(theme.Window, 0.6) : theme.Card;
            screen.Fill(area.X, line, area.Width, step, rowBack);

            Rgb nameColour = channel.Hardware ? theme.TextPrimary : theme.TextDetail;
            screen.Text(x0, nameLine, channel.Name, nameColour, rowBack, bold: selectedRow,
                maxWidth: meterX - x0 - 1);
            MeterReading level = app.Link.StereoMeter("ch", channel.Id);
            if (!roomy) Widgets.Meter(screen, meterX, line, meterWidth, level.Level, theme, rowBack);
            else if (channel.Mono)
                Widgets.Meter(screen, meterX + 2, nameLine, meterWidth, level.Level, theme, rowBack);
            else Stereo(screen, meterX, line, line + step - 1, meterWidth + 2, level, theme, rowBack);

            for (int cell = 0; cell < shown; cell++)
            {
                MixEntry mix = mixes[_scroll + cell];
                int x = x0 + nameWidth + cell * cellWidth;
                bool here = selectedRow && _scroll + cell == _column;
                Rgb back = here ? theme.Selection : rowBack;
                bool muted = channel.IsMuted(mix.Id);
                double value = channel.Level(mix.Id);

                screen.Fill(x, nameLine, cellWidth - 1, 1, back);
                int barWidth = cellWidth - 7;
                if (muted)
                {
                    screen.Text(x + 1, nameLine, "muted".PadRight(barWidth), theme.MuteForeChecked,
                        back.Mix(theme.MuteBackChecked, 0.55), maxWidth: barWidth);
                }
                else
                {
                    Widgets.Fader(screen, x + 1, nameLine, barWidth, value, 1, theme, back, here);
                }
                screen.Text(x + barWidth + 2, nameLine, Widgets.Percent(value).PadLeft(4),
                    muted ? theme.TextMuted : theme.TextDetail, back);
            }
        }
    }

    /// <summary>
    /// Left above right, each lettered, on a row of its own. On neighbouring
    /// rows the two bars take the halves that meet, so the pair reads as one
    /// meter rather than as the same bar twice.
    /// </summary>
    private static void Stereo(Screen screen, int x, int left, int right, int width, MeterReading level, Theme theme, Rgb back)
    {
        if (width < 4) return;
        bool touching = right == left + 1;
        screen.Text(x, left, "L", theme.TextMuted, back);
        screen.Text(x, right, "R", theme.TextMuted, back);
        Widgets.Meter(screen, x + 2, left, width - 2, level.Left, theme, back,
            touching ? Widgets.Align.Lower : Widgets.Align.Alone);
        Widgets.Meter(screen, x + 2, right, width - 2, level.Right, theme, back,
            touching ? Widgets.Align.Upper : Widgets.Align.Alone);
    }

    public override bool Handle(KeyPress key, App app)
    {
        Snapshot? state = State(app);
        if (state is null) return false;
        List<MixEntry> mixes = state.Mixer.Mixes;
        List<ChannelEntry> channels = state.Mixer.Channels;
        if (mixes.Count == 0) return false;

        MixEntry mix = mixes[Math.Clamp(_column, 0, mixes.Count - 1)];
        ChannelEntry? channel = _row > 0 && _row <= channels.Count ? channels[_row - 1] : null;

        switch (key.Key)
        {
            case Key.Up: _row = Move(_row, -1, channels.Count + 1); return true;
            case Key.Down: _row = Move(_row, 1, channels.Count + 1); return true;
            case Key.Left: _column = Move(_column, -1, mixes.Count); return true;
            case Key.Right: _column = Move(_column, 1, mixes.Count); return true;
            case Key.PageUp: _row = Move(_row, -10, channels.Count + 1); return true;
            case Key.PageDown: _row = Move(_row, 10, channels.Count + 1); return true;
            case Key.Home: _row = 0; return true;
            case Key.End: _row = channels.Count; return true;
        }

        // A letter with ctrl held is the frame's, not the grid's.
        if (key.Key != Key.Char || key.Ctrl) return false;
        switch (key.Char)
        {
            case ' ':
                if (channel is null) app.Link.Send("setMixMuted", body => { body["mix"] = mix.Id; body["value"] = !mix.Muted; });
                else app.Link.Send("setChannelMuted", body =>
                {
                    body["channel"] = channel.Id;
                    body["mix"] = mix.Id;
                    body["value"] = !channel.IsMuted(mix.Id);
                });
                return true;
            case '+' or '=': Step(app, mix, channel, 0.05); return true;
            case '-' or '_': Step(app, mix, channel, -0.05); return true;
            case ']': Step(app, mix, channel, 0.01); return true;
            case '[': Step(app, mix, channel, -0.01); return true;
        }
        return false;
    }

    private static void Step(App app, MixEntry mix, ChannelEntry? channel, double by)
    {
        if (channel is null)
            app.Link.Send("setMixVolume", body =>
            {
                body["mix"] = mix.Id;
                body["value"] = Math.Clamp(mix.Volume + by, 0, mix.Ceiling);
            });
        else
            app.Link.Send("setLevel", body =>
            {
                body["channel"] = channel.Id;
                body["mix"] = mix.Id;
                body["value"] = Math.Clamp(channel.Level(mix.Id) + by, 0, 1);
            });
    }
}
