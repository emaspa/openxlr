namespace OpenXLR.Tui;

/// <summary>
/// Where the monitor mixes go: which sinks are fed, what feeds each one, the
/// volume and monitoring pause the selected outputs share, and the system defaults the daemon
/// holds.
/// </summary>
internal sealed class OutputsView : View
{
    private readonly RowList _list = new();
    private List<string>? _pendingOutputs;
    private Task<string?>? _selectionRequest;
    private long _selectionSession;
    private readonly Dictionary<string, (string Mix, Task<string?> Request, long Session)> _pendingFeeds = [];

    private List<string> SelectedOutputs(App app, Snapshot state)
    {
        if (_selectionSession != app.Link.SessionRevision || !app.Link.Connected)
        {
            _pendingOutputs = null;
            _selectionRequest = null;
        }
        if (_selectionRequest is { IsCompleted: true } done)
        {
            _pendingOutputs = null;
            _selectionRequest = null;
            if (done.IsCompletedSuccessfully && done.Result is { Length: > 0 } error) app.Say(error);
        }
        // A receive can complete after this frame captured its snapshot.
        return _pendingOutputs ?? app.Link.State?.Mixer.MonitorOutputs ?? state.Mixer.MonitorOutputs;
    }

    private void ToggleOutput(App app, Snapshot state, string name)
    {
        if (!app.Link.Connected) return;
        List<string> outputs = [.. SelectedOutputs(app, state)];
        if (!outputs.Remove(name)) outputs.Add(name);
        _pendingOutputs = outputs;
        _selectionSession = app.Link.SessionRevision;
        // Only the latest request can replace this complete optimistic set.
        // The daemon sends the authoritative state before its acknowledgement.
        _selectionRequest = app.Link.Request("setMonitorOutputs", body =>
            body["devices"] = new System.Text.Json.Nodes.JsonArray(
                outputs.Select(output => (System.Text.Json.Nodes.JsonNode?)output).ToArray()));
    }

    private void SyncPendingFeeds(App app, Snapshot state, List<string> selected)
    {
        foreach (var (output, pending) in _pendingFeeds.ToArray())
            if (!app.Link.Connected || pending.Session != app.Link.SessionRevision || !selected.Contains(output)
                || !state.Mixer.Mixes.Any(mix => mix.Id == pending.Mix)) _pendingFeeds.Remove(output);
            else if (pending.Request.IsCompleted) _ = Feed(app, state, output);
    }

    private string Feed(App app, Snapshot state, string output)
    {
        if (_pendingFeeds.TryGetValue(output, out var pending))
        {
            if (!pending.Request.IsCompleted) return pending.Mix;
            _pendingFeeds.Remove(output);
            if (pending.Request.IsCompletedSuccessfully && pending.Request.Result is { Length: > 0 } error) app.Say(error);
        }
        state = app.Link.State ?? state;
        state.Mixer.MonitorFeeds.TryGetValue(output, out string? value);
        var included = (value?.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        string feed = string.Join('+', state.Mixer.Mixes.Where(mix => included.Contains(mix.Id)).Select(mix => mix.Id));
        if (feed.Length > 0) return feed;
        List<MixEntry> monitors = state.Mixer.Mixes.Where(mix => mix.Kind == "monitor").ToList();
        return monitors.FirstOrDefault(mix => mix.Id == state.Mixer.DefaultMonitorMix)?.Id
            ?? monitors.FirstOrDefault()?.Id ?? "monitor";
    }

    private void CycleFeed(App app, string output, int by)
    {
        if (!app.Link.Connected || app.Link.State is not { } state) return;
        List<string> selected = SelectedOutputs(app, state);
        SyncPendingFeeds(app, state, selected);
        if (!selected.Contains(output)) return;
        string feed = Feed(app, state, output);
        // A reply may have published a newer layout while resolving the feed.
        state = app.Link.State ?? state;
        List<string> ids = state.Mixer.Mixes.Select(mix => mix.Id).ToList();
        if (ids.Count == 0) return;
        int at = ids.IndexOf(feed.Split('+')[0]);
        string next = ids[((at < 0 ? 0 : at) + (by > 0 ? 1 : ids.Count - 1)) % ids.Count];
        long session = app.Link.SessionRevision;
        Task<string?> request = app.Link.Request("setMonitorFeed", body =>
        {
            body["device"] = output;
            body["mix"] = next;
        });
        _pendingFeeds[output] = (next, request, session);
    }

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

    private List<Row> Build(App app, Snapshot state)
    {
        DaemonLink link = app.Link;
        List<Row> rows = [];
        List<DeviceEntry> sinks = state.Devices.Where(device => device.IsSink && !device.IsOwn).ToList();
        List<string> selected = SelectedOutputs(app, state);
        SyncPendingFeeds(app, state, selected);

        rows.Add(new HeadingRow("Monitor"));
        rows.Add(new NumberRow("Output volume", state.Mixer.OutputVolume ?? 0, 0, 1.5, 0.05,
            value => Widgets.Percent(value), value => link.Send("setOutputVolume", body => body["value"] = Math.Round(value, 3)))
        {
            Enabled = selected.Count > 0,
            Note = selected.Count == 0 ? "no output selected" : null,
        });
        rows.Add(new ToggleRow("Mute monitoring", state.Mixer.MonitorMuted,
            _ => link.Send("toggleMonitorMuted"))
        { Note = "recording and streaming continue" });

        rows.Add(new HeadingRow("Outputs"));
        if (sinks.Count == 0) rows.Add(new TextRow("None found", "is PipeWire running"));
        foreach (DeviceEntry sink in sinks)
        {
            bool on = selected.Contains(sink.Name);
            string feed = Feed(app, state, sink.Name);
            rows.Add(new OutputRow(sink, on, feed, state, link, app,
                () => ToggleOutput(app, state, sink.Name), by => CycleFeed(app, sink.Name, by)));
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
        DeviceEntry sink, bool selected, string feed, Snapshot state, DaemonLink link, App app, Action toggle, Action<int> cycleFeed)
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
                    toggle();
                    return true;

                case Key.Left or Key.Right when selected:
                    cycleFeed(key.Key == Key.Right ? 1 : -1);
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
