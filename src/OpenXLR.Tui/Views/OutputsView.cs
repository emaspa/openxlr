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
    private (string Output, string Feed, int By, long Session, long Since, long KeyRevision)? _sumWarning;

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

    private void CycleFeed(App app, string output, string label, int by)
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
        // The arrows choose one mix, so a sum ticked elsewhere is only
        // replaced by the same arrow pressed again while the warning shows.
        string[] summed = feed.Split('+');
        if (summed.Length > 1)
        {
            long now = app.Time.GetTimestamp();
            if (_sumWarning is not { } warned || warned.Output != output || warned.Feed != feed || warned.By != by
                || warned.Session != session || warned.KeyRevision != app.KeyRevision - 1
                || app.Time.GetElapsedTime(warned.Since, now) >= TimeSpan.FromSeconds(4))
            {
                _sumWarning = (output, feed, by, session, now, app.KeyRevision);
                string name = state.Mixer.Mixes.FirstOrDefault(mix => mix.Id == next)?.Name ?? next;
                app.Say($"{label} hears {summed.Length} mixes summed. Press {(by > 0 ? "Right" : "Left")} again to feed it {name} alone.");
                return;
            }
        }
        _sumWarning = null;
        Task<string?> request = app.Link.Request("setMonitorFeed", body =>
        {
            body["device"] = output;
            body["mix"] = next;
        });
        _pendingFeeds[output] = (next, request, session);
    }

    /// <summary>
    /// Each mix's output EQ: what runs it, or why hardware mixing holds it
    /// off, and its band editor, the microphone EQ's own, whose commands name
    /// the mix.
    /// </summary>
    private void OutputEqs(List<Row> rows, App app, Snapshot state)
    {
        List<MixEntry> mixes = [.. state.Mixer.Mixes.Where(mix => mix.Eq is not null)];
        if (mixes.Count == 0) return;
        rows.Add(new HeadingRow("Output EQ"));
        foreach (MixEntry mix in mixes)
        {
            string id = mix.Id;
            rows.Add(new TextRow($"{mix.Name} EQ", state.Mixer.Disabled("eq:mix:" + id) ?? mix.Eq!.Status));
            if (EqEditor.CanEdit(mix, state.Mixer))
                rows.Add(new ActionRow($"{mix.Name} bands", "edit EQ",
                    () => _eqEditor = new EqEditor(id, app.Link.SessionRevision, mix: true)));
        }
    }

    /// <summary>
    /// While hardware mixing runs, the Wave XLR Pro's maximizer on each mix
    /// the unit builds: its amount, and how far it lowers the mix now, or
    /// why the unit runs none on that mix.
    /// </summary>
    private static void Maximizers(List<Row> rows, App app, Snapshot state)
    {
        if (state.Mixer.HardwareMixing is not { Active: true }) return;
        List<MixEntry> mixes = [.. state.Mixer.Mixes.Where(mix => mix.HardwareSection is not null)];
        if (mixes.Count == 0) return;
        rows.Add(new HeadingRow("Maximizer on the unit"));
        foreach (MixEntry mix in mixes)
        {
            string id = mix.Id;
            if (state.Mixer.Disabled("maximizer:" + id) is { } sentence)
            {
                rows.Add(new TextRow($"{mix.Name} maximizer", sentence));
                continue;
            }
            double? reduction = app.Link.MaximizerReduction(id);
            rows.Add(new NumberRow($"{mix.Name} maximizer", mix.Maximizer, 0, 100, 5,
                value => value <= 0 ? "off" : value.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                value => app.Link.Send("setMixMaximizer", body =>
                {
                    body["mix"] = id;
                    body["value"] = (int)Math.Round(value);
                }))
            {
                Integer = true,
                FineStep = 1,
                Note = reduction is { } db ? "-" + db.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB now" : null,
            });
        }
    }

    public override string Title => "Outputs";

    // A mix's output EQ, open over the list until Esc.
    private EqEditor? _eqEditor;

    public override string Keys => _eqEditor?.Keys ?? "Space select  Left/Right feed  Enter main output  -/+ volume  m mute  i idle release";

    public override void Draw(Screen screen, Rect area, App app)
    {
        if (_eqEditor is { } editor)
        {
            if (editor.Draw(screen, area, app)) return;
            _eqEditor = null;
            app.Say("That mix no longer has an editable EQ");
        }
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
        if (_eqEditor is { } editor)
        {
            if (key.Key == Key.Escape) { _eqEditor = null; return true; }
            return editor.Handle(key, app);
        }
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
        // The daemon refuses the shared volume while its first output is
        // locked, so the row shows the held level and sends nothing.
        bool volumeLocked = selected.Count > 0 && state.Mixer.MonitorVolumeLocked;
        rows.Add(new NumberRow("Output volume", state.Mixer.OutputVolume ?? 0, 0, 1.5, 0.05,
            value => Widgets.Percent(value), value => link.Send("setOutputVolume", body => body["value"] = Math.Round(value, 3)))
        {
            Enabled = selected.Count > 0 && !volumeLocked,
            DisabledText = volumeLocked ? $"{Widgets.Percent(state.Mixer.OutputVolume ?? 0)} locked" : Row.Unsupported,
            Note = selected.Count == 0 ? "no output selected" : volumeLocked ? "unlock in the UI's Options, Audio" : null,
        });
        rows.Add(new ToggleRow("Mute monitoring", state.Mixer.MonitorMuted,
            _ => link.Send("toggleMonitorMuted"))
        {
            Note = state.Mixer.MicrophonePausePending ? "mic audio unconfirmed"
                : state.Mixer.MonitorMuteMicrophones ? state.Mixer.MonitorMuted
                    ? "microphone sends paused" : "mic sends also pause"
                : "recording and streaming continue",
        });
        // Each monitor mix's dry switch: on, it hears the microphones before
        // their effects, earlier, and every other mix keeps the processed voice.
        foreach (MixEntry mix in state.Mixer.Mixes.Where(mix => mix.MonitorDry is not null))
        {
            string id = mix.Id;
            rows.Add(new ToggleRow($"{mix.Name} hears mics dry", mix.MonitorDry!.Value, value => link.Send("setMonitorDry", body =>
            {
                body["mix"] = id;
                body["value"] = value;
            })));
        }

        OutputEqs(rows, app, state);
        Maximizers(rows, app, state);

        rows.Add(new HeadingRow("Outputs"));
        if (sinks.Count == 0) rows.Add(new TextRow("None found", "is PipeWire running"));
        foreach (DeviceEntry sink in sinks)
        {
            bool on = selected.Contains(sink.Name);
            string feed = Feed(app, state, sink.Name);
            rows.Add(new OutputRow(sink, on, feed, state, link, app,
                () => ToggleOutput(app, state, sink.Name), by => CycleFeed(app, sink.Name, Describe(sink), by)));
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
    /// selects it, the arrows walk the mixes that can feed it (a summed feed
    /// only after a second press), enter makes it the system default, and i
    /// lets a selected Bluetooth output's link go while its feed is silent.
    /// </summary>
    private sealed class OutputRow(
        DeviceEntry sink, bool selected, string feed, Snapshot state, DaemonLink link, App app, Action toggle, Action<int> cycleFeed)
        : Row(Describe(sink))
    {
        public override void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused)
        {
            Widgets.Lamp(screen, x, y, selected, theme, back);
            string label = !selected ? "not selected" : IdleRelease ? Feeds() + ", released when silent" : Feeds();
            bool locked = state.Mixer.HoldsVolumeOf(sink.Name);
            screen.Text(x + 2, y, label, selected ? theme.TextDetail : theme.TextMuted, back,
                bold: focused, maxWidth: Math.Max(4, width - (locked ? 17 : 12)));

            if (sink.Volume is { } volume)
                screen.Text(x + Math.Max(6, width - 9), y, Widgets.Percent(volume).PadLeft(5),
                    sink.Muted == true ? theme.MuteForeChecked : theme.TextSecondary, back);
            if (locked)
                screen.Text(x + Math.Max(6, width - 14), y, "lock", theme.Accent, back);
            if (state.Mixer.EnforcedDefaultSink == sink.Name)
                screen.Text(x + Math.Max(6, width - 3), y, "def", theme.Accent, back);
        }

        private bool Bluetooth => sink.Name.StartsWith("bluez_output.", StringComparison.Ordinal);
        private bool IdleRelease => Bluetooth && state.Mixer.MonitorIdleRelease.Contains(sink.Name);

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
                    if (state.Mixer.HoldsVolumeOf(sink.Name))
                    {
                        app.Say($"{Describe(sink)} has its volume locked");
                        return true;
                    }
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

                case Key.Char when key.Char == 'i':
                    // A wired output has no stream that stops, so the daemon takes Bluetooth only.
                    if (!selected || !Bluetooth)
                    {
                        app.Say(selected ? "Only a Bluetooth output can be released when silent" : $"Select {Describe(sink)} first");
                        return true;
                    }
                    bool enable = !IdleRelease;
                    link.Send("setMonitorIdleRelease", body =>
                    {
                        body["device"] = sink.Name;
                        body["enabled"] = enable;
                    });
                    app.Say(enable ? $"{Describe(sink)} is released after 10 s of silence; the start of the next sound can be cut"
                        : $"{Describe(sink)} stays linked");
                    return true;

                default: return false;
            }
        }
    }
}
