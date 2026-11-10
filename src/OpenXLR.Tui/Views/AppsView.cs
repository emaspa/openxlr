namespace OpenXLR.Tui;

/// <summary>
/// The applications the daemon has placed on channels: every live stream, the
/// channel it plays into, and the keys to move it. A move is remembered for
/// the application, which is what the window's Manage list does. Each has a
/// playback volume too, the one the UI's volume button sets.
/// </summary>
internal sealed class AppsView : View
{
    private readonly RowList _list = new();

    public override string Title => "Apps";

    public override string Keys => "Space toggle  Left/Right channel  -/+ volume  r reset volume  i ignore  f forget";

    public override void Draw(Screen screen, Rect area, App app)
    {
        Snapshot? state = State(app);
        if (state is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "Waiting for the daemon", app.Theme.TextMuted, app.Theme.Window);
            return;
        }
        _list.Draw(screen, area, app.Theme, Build(app, state), labelWidth: 30);
    }

    public override bool Handle(KeyPress key, App app)
    {
        Snapshot? state = State(app);
        return state is not null && _list.Handle(key, Build(app, state));
    }

    private static List<Row> Build(App app, Snapshot state)
    {
        List<Row> rows = [];
        List<ChannelEntry> targets = state.Mixer.Channels.Where(channel => !channel.Hardware).ToList();
        List<StreamEntry> streams = state.Mixer.Streams
            .OrderByDescending(stream => stream.Active)
            .ThenBy(stream => stream.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int playing = streams.Count(stream => stream.Running);
        rows.Add(new HeadingRow($"Applications ({streams.Count} known, {playing} playing)"));
        if (state.Mixer.RouteAppsByDefault is bool automatic)
            rows.Add(new ToggleRow("Auto-route unassigned apps", automatic, value =>
                app.Link.Send("setRouteAppsByDefault", body => body["value"] = value)));
        if (streams.Count == 0) rows.Add(new TextRow("Nothing here yet", "start an application"));
        foreach (StreamEntry stream in streams)
            rows.Add(new AppRow(stream, targets, state, app));

        return rows;
    }

    /// <summary>One stream, with the channel it feeds and the keys that move it.</summary>
    private sealed class AppRow(StreamEntry stream, List<ChannelEntry> targets, Snapshot state, App app)
        : Row(stream.Label.Length > 0 ? stream.Label : stream.Identity)
    {
        public override void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused)
        {
            Widgets.Lamp(screen, x, y, stream.Running, theme, back);
            string channel = stream.ChannelId switch
            {
                null or "" => "the desktop's own routing",
                "ignore" => "left to the desktop",
                _ => state.Mixer.Channels.FirstOrDefault(entry => entry.Id == stream.ChannelId)?.Name ?? stream.ChannelId,
            };
            // The saved level reads like any value; without one, the level
            // the desktop left the application at is shown muted.
            bool levels = state.Mixer.AppVolumeSupported && width >= 40;
            screen.Text(x + 2, y, channel, focused ? theme.TextPrimary : theme.TextDetail, back,
                bold: focused, maxWidth: Math.Max(4, width - (levels ? 26 : 20)));
            if (levels && (stream.Volume ?? stream.LiveVolume) is double level)
                screen.Text(x + width - 24, y, $"{level * 100:0}%".PadLeft(4),
                    stream.Volume is null ? theme.TextMuted : focused ? theme.TextPrimary : theme.TextDetail, back, maxWidth: 4);
            screen.Text(x + Math.Max(6, width - 18), y, stream.Identity, theme.TextMuted, back, maxWidth: 18);
        }

        public override bool Handle(KeyPress key)
        {
            if (state.Mixer.AppVolumeSupported && key.Key == Key.Char)
                switch (key.Char)
                {
                    case '-' or '_': Level(-0.05); return true;
                    case '+' or '=': Level(0.05); return true;
                    case '[': Level(-0.01); return true;
                    case ']': Level(0.01); return true;
                    case 'r':
                        if (stream.Volume is null) return true;
                        app.Link.Send("setAppVolume", body =>
                        {
                            body["identity"] = stream.Identity;
                            body["value"] = null;
                        });
                        app.Say($"{Label} plays at full volume again");
                        return true;
                }
            if (targets.Count == 0) return false;
            int at = targets.FindIndex(channel => channel.Id == stream.ChannelId);

            switch (key.Key)
            {
                case Key.Left:
                    Assign(targets[(Math.Max(at, 0) - 1 + targets.Count) % targets.Count].Id);
                    return true;
                case Key.Right or Key.Enter:
                    Assign(targets[(at + 1 + targets.Count) % targets.Count].Id);
                    return true;
                case Key.Char when key.Char == 'i':
                    Assign("ignore");
                    return true;
                case Key.Char when key.Char == 'f':
                    app.Link.Send("forgetApp", body => body["identity"] = stream.Identity);
                    app.Say($"{Label} is forgotten");
                    return true;
                default:
                    return false;
            }
        }

        // A step starts from the saved level, or from what the application plays at.
        private void Level(double by)
        {
            double from = stream.Volume ?? stream.LiveVolume ?? 1;
            double to = Math.Round(Math.Clamp(from + by, 0, 1), 2);
            if (to == Math.Round(from, 2)) return;
            app.Link.Send("setAppVolume", body =>
            {
                body["identity"] = stream.Identity;
                body["value"] = to;
            });
        }

        private void Assign(string channel) =>
            app.Link.Send("assignApp", body =>
            {
                body["identity"] = stream.Identity;
                body["channel"] = channel;
                body["label"] = stream.Label;
            });
    }
}
