namespace OpenXLR.Tui;

/// <summary>
/// Where the monitor mixes go: which sinks are fed, what feeds each one, the
/// volume the selected outputs share, and the system defaults the daemon
/// holds.
/// </summary>
internal sealed class OutputsView : View
{
    private readonly RowList _list = new();

    public override string Title => "Outputs";

    public override string Keys => "Space select  Left/Right feed  Enter main output  -/+ volume  m mute";

    public override void Draw(Screen screen, Rect area, App app)
    {
        Snapshot? state = State(app);
        if (state is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "Waiting for the daemon", app.Theme.TextMuted, app.Theme.Window);
            return;
        }
        _list.Draw(screen, area, app.Theme, Build(app, state), labelWidth: 34);
    }

    public override bool Handle(KeyPress key, App app)
    {
        Snapshot? state = State(app);
        return state is not null && _list.Handle(key, Build(app, state));
    }

    private static List<Row> Build(App app, Snapshot state)
    {
        DaemonLink link = app.Link;
        List<Row> rows = [];
        List<DeviceEntry> sinks = state.Devices.Where(device => device.IsSink && !device.IsOwn).ToList();
        List<string> selected = state.Mixer.MonitorOutputs;

        rows.Add(new HeadingRow("Monitor"));
        rows.Add(new NumberRow("Output volume", state.Mixer.OutputVolume ?? 0, 0, 1.5, 0.05,
            value => Widgets.Percent(value), value => link.Send("setOutputVolume", body => body["value"] = Math.Round(value, 3)))
        {
            Enabled = selected.Count > 0,
            Note = selected.Count == 0 ? "no output selected" : null,
        });

        rows.Add(new HeadingRow("Outputs"));
        if (sinks.Count == 0) rows.Add(new TextRow("None found", "is PipeWire running"));
        foreach (DeviceEntry sink in sinks)
        {
            bool on = selected.Contains(sink.Name);
            string feed = state.Mixer.MonitorFeeds.TryGetValue(sink.Name, out string? value) && value.Length > 0
                ? value
                : state.Mixer.Mixes.FirstOrDefault(mix => mix.Kind == "monitor")?.Id ?? "monitor";
            rows.Add(new OutputRow(sink, on, feed, state, link, app));
        }

        rows.Add(new HeadingRow("System defaults"));
        List<string> sinkNames = ["@monitor", .. sinks.Select(sink => sink.Name)];
        List<string> sinkLabels = ["the first monitor output", .. sinks.Select(Describe)];
        int sinkAt = Math.Max(0, sinkNames.IndexOf(state.Mixer.EnforcedDefaultSink ?? "@monitor"));
        rows.Add(new ChoiceRow("Default output", sinkLabels, sinkAt, index =>
            link.Send("setEnforcedDefaults", body =>
            {
                body["sink"] = sinkNames[index];
                body["source"] = state.Mixer.EnforcedDefaultSource ?? string.Empty;
            })));

        List<DeviceEntry> sources = state.Devices.Where(device => !device.IsSink).ToList();
        List<string> sourceNames = [.. sources.Select(source => source.Name)];
        List<string> sourceLabels = [.. sources.Select(Describe)];
        int sourceAt = Math.Max(0, sourceNames.IndexOf(state.Mixer.EnforcedDefaultSource ?? string.Empty));
        if (sourceNames.Count > 0)
            rows.Add(new ChoiceRow("Default input", sourceLabels, sourceAt, index =>
                link.Send("setEnforcedDefaults", body =>
                {
                    body["sink"] = state.Mixer.EnforcedDefaultSink ?? "@monitor";
                    body["source"] = sourceNames[index];
                })));

        return rows;
    }

    private static string Describe(DeviceEntry device) =>
        device.Description is { Length: > 0 } text ? text : device.Name;

    /// <summary>
    /// One sink: selected or not, what feeds it, and its own volume. Space
    /// selects it, the arrows walk the mixes that can feed it, and enter makes
    /// it the system default.
    /// </summary>
    private sealed class OutputRow(
        DeviceEntry sink, bool selected, string feed, Snapshot state, DaemonLink link, App app)
        : Row(Describe(sink))
    {
        public override void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused)
        {
            Widgets.Lamp(screen, x, y, selected, theme, back);
            string label = selected ? Feeds() : "not selected";
            screen.Text(x + 2, y, label, selected ? theme.TextDetail : theme.TextMuted, back,
                bold: focused, maxWidth: Math.Max(4, width - 12));

            if (sink.Volume is { } volume)
                screen.Text(x + Math.Max(6, width - 9), y, Widgets.Percent(volume).PadLeft(5),
                    sink.Muted == true ? theme.MuteForeChecked : theme.TextSecondary, back);
            if (state.Mixer.EnforcedDefaultSink == sink.Name)
                screen.Text(x + Math.Max(6, width - 3), y, "def", theme.Accent, back);
        }

        private string Feeds()
        {
            IEnumerable<string> names = feed.Split('+', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => state.Mixer.Mixes.FirstOrDefault(mix => mix.Id == id)?.Name ?? id);
            return string.Join(" + ", names);
        }

        public override bool Handle(KeyPress key)
        {
            switch (key.Key)
            {
                case Key.Char when key.Char == ' ':
                    List<string> outputs = [.. state.Mixer.MonitorOutputs];
                    if (selected) outputs.Remove(sink.Name); else outputs.Add(sink.Name);
                    link.Send("setMonitorOutputs", body =>
                        body["devices"] = new System.Text.Json.Nodes.JsonArray(
                            outputs.Select(name => (System.Text.Json.Nodes.JsonNode?)name).ToArray()));
                    return true;

                case Key.Left or Key.Right when selected:
                    List<string> ids = state.Mixer.Mixes.Select(mix => mix.Id).ToList();
                    if (ids.Count == 0) return true;
                    int at = ids.IndexOf(feed.Split('+')[0]);
                    int next = ((at < 0 ? 0 : at) + (key.Key == Key.Right ? 1 : ids.Count - 1)) % ids.Count;
                    link.Send("setMonitorFeed", body =>
                    {
                        body["device"] = sink.Name;
                        body["mix"] = ids[next];
                    });
                    return true;

                case Key.Enter:
                    link.Send("setMainOutput", body => body["device"] = sink.Name);
                    app.Say($"{Describe(sink)} is the system default");
                    return true;

                case Key.Char when key.Char is '+' or '=' or '-' or '_':
                    double by = key.Char is '+' or '=' ? 0.05 : -0.05;
                    link.Send("adjustOutputVolume", body =>
                    {
                        body["device"] = sink.Name;
                        body["value"] = by;
                    });
                    return true;

                case Key.Char when key.Char == 'm':
                    link.Send("toggleOutputMute", body => body["device"] = sink.Name);
                    return true;

                default: return false;
            }
        }
    }
}
