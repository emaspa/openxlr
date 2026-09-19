namespace OpenXLR.Tui;

/// <summary>Channel sends into the selected mix, with the master bank beside them.</summary>
internal sealed class MixerView : View
{
    private int _row;      // 0 is the mix masters, 1 and up are the channels.
    private int _column;
    private int _channelScroll;
    private int _mixScroll;

    public override string Title => "Mixer";

    public override string Keys =>
        "Up/Down channel  Left/Right mix  Space mute  -/+ [/] level  Home masters  End last  r rename  n/N new  c capture  d delete";

    public override void Draw(Screen screen, Rect area, App app)
    {
        Theme theme = app.Theme;
        Snapshot? state = State(app);
        if (state is null || state.Mixer.Mixes.Count == 0)
        {
            screen.Panel(area.X, area.Y, area.Width, area.Height, theme.Rule, theme.Card, "Mixer");
            screen.Text(area.X + 2, area.Y + 2, state is null ? "Waiting for the daemon" : "The mixer is not built yet",
                theme.TextSecondary, theme.Card, maxWidth: area.Width - 4);
            return;
        }

        List<MixEntry> mixes = state.Mixer.Mixes;
        List<ChannelEntry> channels = state.Mixer.Channels;
        _row = Math.Clamp(_row, 0, channels.Count);
        _column = Math.Clamp(_column, 0, mixes.Count - 1);
        MixEntry selectedMix = mixes[_column];
        bool tall = area.Height >= 26;
        int overviewHeight = tall ? 8 : 4;
        Overview(screen, new Rect(area.X, area.Y, area.Width, overviewHeight), app, selectedMix, tall);

        int bankY = area.Y + overviewHeight + (tall ? 1 : 0);
        int bankHeight = area.Bottom - bankY - 1;
        if (bankHeight < 7) return;
        int masterWidth = area.Width >= 128
            ? Math.Min(mixes.Count * 9 + 2, area.Width / 2)
            : Math.Max(20, area.Width / 3);
        int channelWidth = area.Width - masterWidth - 1;
        Rect channelBank = new(area.X, bankY, channelWidth, bankHeight);
        Rect masterBank = new(channelBank.Right + 1, bankY, masterWidth, bankHeight);
        screen.Panel(channelBank.X, bankY, channelBank.Width, bankHeight, theme.Rule, theme.Card,
            $"Sends to {selectedMix.Name}", theme.TextPrimary);
        screen.Panel(masterBank.X, bankY, masterBank.Width, bankHeight, theme.Rule, theme.Tile,
            "Masters", theme.TextPrimary);

        int channelCount = Math.Min(channels.Count, Math.Max(1, (channelBank.Width - 2) / 9));
        int masterCount = Math.Min(mixes.Count, Math.Max(1, (masterBank.Width - 2) / 9));
        KeepVisible(ref _channelScroll, Math.Max(0, _row - 1), channelCount, channels.Count);
        KeepVisible(ref _mixScroll, _column, masterCount, mixes.Count);
        for (int i = 0; i < channelCount; i++)
        {
            int index = _channelScroll + i;
            ChannelEntry channel = channels[index];
            Rect strip = Slot(channelBank, i, channelCount);
            string kind = channel.Hardware ? "INPUT" : channel.CaptureSource is not null ? "CAPTURE" : "APP";
            // An XLR strip is the interface, so its key is the input's own mute;
            // its send into the chosen mix shows as a word where the level goes.
            string? hardwareMute = HardwareMute(channel);
            bool keyMuted = hardwareMute is null ? channel.IsMuted(selectedMix.Id) : state.Flag(hardwareMute);
            DrawStrip(screen, strip, app, channel.Name, kind, channel.Level(selectedMix.Id), 1,
                keyMuted, app.Link.StereoMeter("ch", channel.Id), _row == index + 1, false,
                sendMuted: hardwareMute is not null && channel.IsMuted(selectedMix.Id), mono: channel.Mono);
        }
        for (int i = 0; i < masterCount; i++)
        {
            MixEntry mix = mixes[_mixScroll + i];
            string kind = mix.Kind == "monitor" ? "MON" : mix.Kind == "virtualMic" ? "MIC" : "AUX";
            DrawStrip(screen, Slot(masterBank, i, masterCount), app, mix.Name, kind, mix.Volume, mix.Ceiling,
                mix.Muted, app.Link.StereoMeter("mix", mix.Id), _row == 0 && _column == _mixScroll + i, true);
        }
        if (channelCount == 0)
            screen.Text(channelBank.X + 2, bankY + 2, "No channels", theme.TextSecondary, theme.Card,
                maxWidth: channelBank.Width - 4);
        Range(screen, channelBank, _channelScroll, channelCount, channels.Count, theme);
        Range(screen, masterBank, _mixScroll, masterCount, mixes.Count, theme);

        ChannelEntry? selectedChannel = _row > 0 ? channels[_row - 1] : null;
        string focus = selectedChannel is null ? $"MASTER / {selectedMix.Name}"
            : $"{selectedChannel.Name} > {selectedMix.Name}";
        double value = selectedChannel?.Level(selectedMix.Id) ?? selectedMix.Volume;
        bool muted = selectedChannel is null ? selectedMix.Muted
            : HardwareMute(selectedChannel) is { } control ? state.Flag(control) : selectedChannel.IsMuted(selectedMix.Id);
        screen.TextPad(area.X + 1, area.Bottom - 1,
            $" {focus}   {Widgets.Percent(value)}   {(muted ? "MUTED" : "ON")}", area.Width - 2,
            theme.TextPrimary, theme.Selection, bold: true);
    }

    private static Rect Slot(Rect bank, int index, int count)
    {
        int width = bank.Width - 2;
        int left = index * width / count;
        int right = (index + 1) * width / count;
        return new Rect(bank.X + 1 + left, bank.Y + 1, right - left, bank.Height - 2);
    }

    private static void KeepVisible(ref int scroll, int selected, int shown, int total)
    {
        if (selected < scroll) scroll = selected;
        if (selected >= scroll + shown) scroll = selected - shown + 1;
        scroll = Math.Clamp(scroll, 0, Math.Max(0, total - shown));
    }

    private static void Range(Screen screen, Rect bank, int first, int shown, int total, Theme theme)
    {
        string text = $" {first + (total == 0 ? 0 : 1)}-{first + shown}/{total} ";
        screen.Text(bank.Right - text.Length - 2, bank.Bottom - 1, text, theme.TextSecondary, theme.Card,
            maxWidth: bank.Width - 4);
    }

    /// <summary>The device control behind an XLR input's mute, or null for any other channel.</summary>
    private static string? HardwareMute(ChannelEntry channel) =>
        channel.Id == "xlr1" ? "mute" : channel.Id == "xlr2" ? "mute2" : null;

    private static void DrawStrip(Screen screen, Rect area, App app, string name, string kind,
        double value, double ceiling, bool muted, MeterReading meter, bool focused, bool master,
        bool sendMuted = false, bool mono = false)
    {
        Theme theme = app.Theme;
        Rgb back = focused ? theme.SelectedFace : master ? theme.Tile : theme.Card;
        screen.Fill(area.X, area.Y, area.Width, area.Height, back);
        for (int y = area.Y; y < area.Bottom; y++)
            screen.Set(area.Right - 1, y, '│', theme.Rule, back);
        int width = area.Width - 2;
        string first = name, second = string.Empty;
        if (name.Length > width)
        {
            int space = name.LastIndexOf(' ', Math.Min(width, name.Length - 1));
            int split = space > 0 ? space : width;
            first = name[..split];
            second = name[split..].TrimStart();
        }
        Rgb plate = focused ? theme.Selection : back;
        screen.Fill(area.X, area.Y, area.Width - 1, 2, plate);
        Widgets.Center(screen, area.X, area.Y, area.Width - 1, first, theme.TextPrimary, plate, true);
        Widgets.Center(screen, area.X, area.Y + 1, area.Width - 1, second.Length > 0 ? second : kind,
            second.Length > 0 ? theme.TextPrimary : theme.TextMuted, plate);
        if (sendMuted)
            Widgets.Center(screen, area.X, area.Y + 2, area.Width - 1, "muted", theme.MuteBackChecked, back, true);
        else
            Widgets.Center(screen, area.X, area.Y + 2, area.Width - 1, Widgets.Percent(value),
                focused ? theme.Accent : theme.TextDetail, back, true);

        bool compact = area.Height < 14;
        int top = area.Y + (compact ? 3 : 4);
        int height = Math.Max(1, area.Height - (compact ? 5 : 7));
        int faderX = area.X + Math.Max(1, (area.Width - 8) / 2 + 1);
        int meterX = area.Right - 4;
        Widgets.VerticalFader(screen, faderX, top, height, value, ceiling, theme, back, focused);
        if (mono)
        {
            // One reading, so one bar, as wide as the pair it replaces.
            Widgets.VerticalMeter(screen, meterX + 1, top, height, meter.Level, meter.Hold, theme, back, width: 2);
        }
        else
        {
            Widgets.VerticalMeter(screen, meterX, top, height, meter.Left, meter.HoldLeft, theme, back);
            // A blank column between the two bars, so left and right read apart
            // even when both are full.
            Widgets.VerticalMeter(screen, meterX + 2, top, height, meter.Right, meter.HoldRight, theme, back);
            screen.Text(meterX, compact ? area.Bottom - 2 : top - 1, "L R", theme.TextMuted, back);
        }
        if (area.Width >= 14)
        {
            screen.Text(area.X + 1, top, $"{ceiling * 100:0}", theme.TextMuted, back, maxWidth: 3);
            screen.Text(area.X + 1, top + height - 1, "  0", theme.TextMuted, back);
        }
        // Four letters or two: both sit centred between the brackets.
        string mute = muted ? "MUTE" : " ON ";
        Widgets.MuteKey(screen, area.X + Math.Max(0, (area.Width - 7) / 2), area.Bottom - (compact ? 1 : 2),
            mute, muted, theme, focused: false);
        if (focused && !compact)
            screen.Set(area.X, area.Bottom - 1, '━', theme.Accent, back);
    }

    private static void Overview(Screen screen, Rect area, App app, MixEntry mix, bool tall)
    {
        Theme theme = app.Theme;
        MeterReading meter = app.Link.StereoMeter("mix", mix.Id);
        screen.Panel(area.X, area.Y, area.Width, area.Height, theme.Rule, theme.Card,
            $"{mix.Name} / live RMS", theme.TextPrimary);
        if (!tall)
        {
            screen.Text(area.X + 2, area.Y + 1, $"L {Widgets.Rms(meter.Left),4}  R {Widgets.Rms(meter.Right),4} dBFS",
                theme.TextDetail, theme.Card, maxWidth: 27);
            Widgets.History(screen, new Rect(area.X + 29, area.Y + 1, Math.Max(1, area.Width - 31), 2),
                app.Link.MeterHistory("mix", mix.Id), theme, theme.Card);
            screen.Text(area.X + 2, area.Y + 2, $"Hold {Widgets.Rms(meter.Hold)} dBFS / 15 s", theme.TextSecondary,
                theme.Card, maxWidth: 27);
            return;
        }
        string held = $"{Math.Round(meter.Hold * 60 - 60):0}";
        Widgets.BigNumber(screen, area.X + 3, area.Y + 2, held, theme, theme.Card);
        screen.Text(area.X + 3, area.Y + 6, "HOLD / dBFS", theme.TextSecondary, theme.Card);
        int meterX = area.X + 20;
        int meterWidth = Math.Max(12, Math.Min(30, area.Width / 4));
        screen.Text(meterX, area.Y + 1, "STEREO / RMS", theme.TextMuted, theme.Card);
        screen.Text(meterX, area.Y + 3, "L", theme.TextSecondary, theme.Card);
        screen.Text(meterX, area.Y + 4, "R", theme.TextSecondary, theme.Card);
        Widgets.Meter(screen, meterX + 2, area.Y + 3, meterWidth, meter.Left, theme, theme.Card);
        Widgets.Meter(screen, meterX + 2, area.Y + 4, meterWidth, meter.Right, theme, theme.Card);
        screen.Text(meterX + 2, area.Y + 5, "-60", theme.TextMuted, theme.Card);
        screen.Text(meterX + meterWidth / 2, area.Y + 5, "-30", theme.TextMuted, theme.Card);
        screen.Text(meterX + meterWidth, area.Y + 5, "0", theme.TextMuted, theme.Card);
        int historyX = meterX + meterWidth + 5;
        int historyWidth = area.Right - historyX - 2;
        screen.Text(historyX, area.Y + 1, "LEVEL HISTORY / 15 s", theme.TextMuted, theme.Card, maxWidth: historyWidth);
        Widgets.History(screen, new Rect(historyX, area.Y + 2, historyWidth, 4),
            app.Link.MeterHistory("mix", mix.Id), theme, theme.Card);
        screen.Text(historyX, area.Y + 6, "-15 s", theme.TextMuted, theme.Card);
        screen.Text(area.Right - 5, area.Y + 6, "now", theme.TextMuted, theme.Card);
    }

    public override bool Handle(KeyPress key, App app)
    {
        Snapshot? state = State(app);
        if (state is null) return false;
        List<MixEntry> mixes = state.Mixer.Mixes;
        List<ChannelEntry> channels = state.Mixer.Channels;
        if (mixes.Count == 0) return false;

        _row = Math.Clamp(_row, 0, channels.Count);
        _column = Math.Clamp(_column, 0, mixes.Count - 1);
        MixEntry mix = mixes[_column];
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
                    else if (HardwareMute(channel) is { } control)
                        app.Link.Send("set", body => { body["control"] = control; body["value"] = !state.Flag(control); });
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
