namespace OpenXLR.Tui;

/// <summary>
/// The submixer: every channel's send into every mix, the mix masters above
/// them, and the live meters. The grid is the window's strip view laid flat,
/// so a cell is one send and the row above it is the mix that send feeds.
/// </summary>
internal sealed class MixerView : View
{
    private const int NameWidth = 16;
    private const int MinCell = 12;

    private int _row;      // 0 is the mix masters, 1 and up are the channels.
    private int _column;
    private int _scroll;

    public override string Title => "Mixer";

    public override string Keys =>
        "space mute  - + level  [ ] fine  r rename  n new  d delete";

    public override void Draw(Screen screen, Rect area, App app)
    {
        Theme theme = app.Theme;
        Snapshot? state = State(app);
        if (state is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "Waiting for the daemon", theme.TextMuted, theme.Window);
            return;
        }

        List<MixEntry> mixes = state.Mixer.Mixes;
        List<ChannelEntry> channels = state.Mixer.Channels;
        if (mixes.Count == 0 || channels.Count == 0)
        {
            screen.Text(area.X + 2, area.Y + 1, "The mixer is not built yet", theme.TextMuted, theme.Window);
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
        screen.Text(x0, y, "MIXES", theme.TextSecondary, theme.Window, bold: true, maxWidth: NameWidth);
        for (int index = 0; index < shown; index++)
        {
            MixEntry mix = mixes[_scroll + index];
            int x = x0 + NameWidth + index * cellWidth;
            bool here = _row == 0 && _scroll + index == _column;
            Rgb back = here ? theme.Selection : theme.Card;
            screen.Fill(x, y, cellWidth - 1, 3, back);
            screen.Text(x + 1, y, mix.Name, theme.TextPrimary, back, bold: true, maxWidth: cellWidth - 3);

            int barWidth = cellWidth - 8;
            Widgets.Fader(screen, x + 1, y + 1, barWidth, mix.Volume, mix.Ceiling, theme, back, here);
            screen.Text(x + barWidth + 2, y + 1, Widgets.Percent(mix.Volume).PadLeft(4),
                theme.TextDetail, back);

            Widgets.MuteKey(screen, x + 1, y + 2, mix.Muted ? "MUTED" : " ON  ", mix.Muted, theme, here);
            Widgets.Meter(screen, x + 8, y + 2, cellWidth - 10, app.Link.Meter("mix", mix.Id), theme, back);
        }

        y += 3;
        for (int column = 0; column < area.Width; column++)
            screen.Set(area.X + column, y, '─', theme.Divider, theme.Window);
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
            Rgb rowBack = selectedRow ? theme.Card.Mix(theme.Accent, 0.12) : theme.Window;
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
            case Key.Left when key.Ctrl: Reorder(app, state, -1); return true;
            case Key.Right when key.Ctrl: Reorder(app, state, 1); return true;
            case Key.Left: _column = Move(_column, -1, mixes.Count); return true;
            case Key.Right: _column = Move(_column, 1, mixes.Count); return true;
            case Key.PageUp: _row = Move(_row, -10, channels.Count + 1); return true;
            case Key.PageDown: _row = Move(_row, 10, channels.Count + 1); return true;
            case Key.Home: _row = 0; return true;
            case Key.End: _row = channels.Count; return true;
        }

        // A letter with ctrl held is the frame's, not a strip's.
        if (key.Key == Key.Char && !key.Ctrl)
        {
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

                case 'n': NewChannel(app); return true;
                case 'N': app.Ask("New virtual microphone", string.Empty,
                    name => app.Link.Send("createMix", body => body["name"] = name)); return true;
                case 'c': NewCaptureChannel(app); return true;

                case 'r': Rename(app, mix, channel); return true;
                case 'd': Delete(app, mix, channel); return true;
            }
        }

        return false;
    }

    private static void NewChannel(App app) =>
        app.Ask("New channel", string.Empty, name => app.Link.Send("createChannel", body => body["name"] = name));

    private static void NewCaptureChannel(App app) =>
        app.Ask("Capture channel name", string.Empty, name =>
            app.Ask("PipeWire source name", string.Empty, source =>
                app.Link.Send("createCaptureChannel", body =>
                {
                    body["name"] = name;
                    body["source"] = source;
                })));

    private static void Rename(App app, MixEntry mix, ChannelEntry? channel)
    {
        if (channel is null)
        {
            if (mix.Kind != "virtualMic") { app.Say("Only a virtual microphone can be renamed"); return; }
            app.Ask($"Rename {mix.Name}", mix.Name, name =>
                app.Link.Send("renameMix", body => { body["mix"] = mix.Id; body["name"] = name; }));
            return;
        }
        if (channel.Hardware) { app.Say("A hardware channel keeps its name"); return; }
        app.Ask($"Rename {channel.Name}", channel.Name, name =>
            app.Link.Send("renameChannel", body => { body["channel"] = channel.Id; body["name"] = name; }));
    }

    private static void Delete(App app, MixEntry mix, ChannelEntry? channel)
    {
        if (channel is null)
        {
            if (mix.Kind != "virtualMic") { app.Say("Only a virtual microphone can be removed"); return; }
            app.Ask($"Type yes to delete {mix.Name}", string.Empty, answer =>
            {
                if (answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    app.Link.Send("deleteMix", body => body["mix"] = mix.Id);
            });
            return;
        }
        if (channel.Hardware) { app.Say("A hardware channel cannot be removed"); return; }
        app.Ask($"Type yes to delete {channel.Name}", string.Empty, answer =>
        {
            if (answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
                app.Link.Send("deleteChannel", body => body["channel"] = channel.Id);
        });
    }

    /// <summary>Moves the selected channel or virtual microphone in the saved order.</summary>
    private void Reorder(App app, Snapshot state, int by)
    {
        List<string> channels = state.Mixer.Channels.Where(entry => !entry.Hardware).Select(entry => entry.Id).ToList();
        List<string> mixes = state.Mixer.Mixes.Where(entry => entry.Kind == "virtualMic").Select(entry => entry.Id).ToList();

        if (_row > 0 && _row <= state.Mixer.Channels.Count)
        {
            ChannelEntry channel = state.Mixer.Channels[_row - 1];
            int at = channels.IndexOf(channel.Id);
            if (at < 0) { app.Say("A hardware channel keeps its place"); return; }
            int to = Math.Clamp(at + by, 0, channels.Count - 1);
            if (to == at) return;
            channels.RemoveAt(at);
            channels.Insert(to, channel.Id);
        }
        else
        {
            MixEntry mix = state.Mixer.Mixes[_column];
            int at = mixes.IndexOf(mix.Id);
            if (at < 0) { app.Say("Only a virtual microphone moves"); return; }
            int to = Math.Clamp(at + by, 0, mixes.Count - 1);
            if (to == at) return;
            mixes.RemoveAt(at);
            mixes.Insert(to, mix.Id);
        }

        app.Link.Send("setLayoutOrder", body =>
        {
            body["channels"] = new System.Text.Json.Nodes.JsonArray(channels.Select(id =>
                (System.Text.Json.Nodes.JsonNode?)id).ToArray());
            body["mixes"] = new System.Text.Json.Nodes.JsonArray(mixes.Select(id =>
                (System.Text.Json.Nodes.JsonNode?)id).ToArray());
        });
    }

    private static void Step(App app, MixEntry mix, ChannelEntry? channel, double by)
    {
        if (channel is null) Set(app, mix, null, Math.Clamp(mix.Volume + by, 0, mix.Ceiling));
        else Set(app, mix, channel, Math.Clamp(channel.Level(mix.Id) + by, 0, 1));
    }

    private static void Set(App app, MixEntry mix, ChannelEntry? channel, double value)
    {
        if (channel is null)
            app.Link.Send("setMixVolume", body =>
            {
                body["mix"] = mix.Id;
                body["value"] = Math.Clamp(value, 0, mix.Ceiling);
            });
        else
            app.Link.Send("setLevel", body =>
            {
                body["channel"] = channel.Id;
                body["mix"] = mix.Id;
                body["value"] = Math.Clamp(value, 0, 1);
            });
    }
}
