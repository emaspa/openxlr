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

        int cells = Math.Max(1, (area.Width - NameWidth - 2) / MinCell);
        int shown = Math.Min(mixes.Count, cells);
        if (_column < _scroll) _scroll = _column;
        if (_column >= _scroll + shown) _scroll = _column - shown + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, mixes.Count - shown));
        int cellWidth = Math.Max(MinCell, (area.Width - NameWidth - 2) / shown);

        int x0 = area.X + 1;
        int y = area.Y;

        // The mix masters: name, volume, mute and the mix's own meter.
        screen.Text(x0, y, "MIXES", theme.TextSecondary, theme.Card, bold: true, maxWidth: NameWidth);
        for (int index = 0; index < shown; index++)
        {
            MixEntry mix = mixes[_scroll + index];
            int x = x0 + NameWidth + index * cellWidth;
            bool here = _row == 0 && _scroll + index == _column;
            Rgb back = here ? theme.Selection : theme.Tile;
            screen.Fill(x, y, cellWidth - 1, 3, back);
            screen.Text(x + 1, y, mix.Name, theme.TextPrimary, back, bold: true, maxWidth: cellWidth - 3);

            int barWidth = cellWidth - 8;
            Widgets.Fader(screen, x + 1, y + 1, barWidth, mix.Volume, mix.Ceiling, theme, back, here);
            screen.Text(x + barWidth + 2, y + 1, Widgets.Percent(mix.Volume).PadLeft(4),
                theme.TextDetail, back);

            Widgets.MuteKey(screen, x + 1, y + 2, mix.Muted ? "MUTE" : " ON ", mix.Muted, theme, here);
            Widgets.Meter(screen, x + 8, y + 2, cellWidth - 10, app.Link.Meter("mix", mix.Id), theme, back);
        }

        y += 3;
        for (int column = 0; column < area.Width; column++)
            screen.Set(area.X + column, y, '─', theme.Rule, theme.Card);
        y++;

        // One row per channel: its name, its meter, and its send into each mix.
        int rows = Math.Max(1, area.Bottom - y);
        int first = Math.Max(0, Math.Min(_row - 1 - rows + 2, channels.Count - rows));
        if (_row == 0) first = 0;
        for (int index = 0; index < rows && first + index < channels.Count; index++)
        {
            ChannelEntry channel = channels[first + index];
            int line = y + index;
            bool selectedRow = _row == first + index + 1;
            Rgb rowBack = selectedRow ? theme.SelectedFace : theme.Card;
            screen.Fill(area.X, line, area.Width, 1, rowBack);

            Rgb nameColour = channel.Hardware ? theme.TextPrimary : theme.TextDetail;
            screen.Text(x0, line, channel.Name, nameColour, rowBack, bold: selectedRow, maxWidth: NameWidth - 6);
            Widgets.Meter(screen, x0 + NameWidth - 5, line, 4, app.Link.Meter("ch", channel.Id), theme, rowBack);

            for (int cell = 0; cell < shown; cell++)
            {
                MixEntry mix = mixes[_scroll + cell];
                int x = x0 + NameWidth + cell * cellWidth;
                bool here = selectedRow && _scroll + cell == _column;
                Rgb back = here ? theme.Selection : rowBack;
                bool muted = channel.IsMuted(mix.Id);
                double level = channel.Level(mix.Id);

                screen.Fill(x, line, cellWidth - 1, 1, back);
                int barWidth = cellWidth - 7;
                if (muted)
                {
                    screen.Text(x + 1, line, "muted".PadRight(barWidth), theme.MuteForeChecked,
                        back.Mix(theme.MuteBackChecked, 0.55), maxWidth: barWidth);
                }
                else
                {
                    Widgets.Fader(screen, x + 1, line, barWidth, level, 1, theme, back, here);
                }
                screen.Text(x + barWidth + 2, line, Widgets.Percent(level).PadLeft(4),
                    muted ? theme.TextMuted : theme.TextDetail, back);
            }
        }
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
