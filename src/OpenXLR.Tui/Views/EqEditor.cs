using System.Globalization;
using System.Text.Json;

namespace OpenXLR.Tui;

/// <summary>
/// A microphone's or a mix's bands, using the daemon's model and partial band
/// commands. A microphone is named by its channel id; with <paramref name="mix"/>
/// the id is a mix's, and every command names it as <c>mix</c>.
/// </summary>
internal sealed class EqEditor(string channel, long session, TimeProvider? time = null, bool mix = false)
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web);
    private readonly RowList _rows = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    // Only fields this editor changed are held briefly over older reports.
    // There are at most 81 entries; every edit is already sent, not queued.
    private readonly Dictionary<(int Band, string Field), (object Value, long Since)> _edits = [];
    private long _session = session;
    private long _channelRevision;
    private long _historyRevision;
    private int _bandCount;

    internal string Keys => "Esc back  Up/Down control  -/+ value  [/] fine  Enter exact value  Home/End limits";

    internal static bool CanEdit(ChannelEntry channel) => Settings(channel) is not null;

    /// <summary>A mix's EQ can be edited: a complete one, while hardware mixing does not hold it off.</summary>
    internal static bool CanEdit(MixEntry mix, MixerSnapshot mixer) => Settings(mix, mixer) is not null;

    private static MicEq? Settings(ChannelEntry? channel)
        => channel is { Present: true } ? Settings(channel.Eq) : null;

    private static MicEq? Settings(MixEntry? mix, MixerSnapshot mixer)
        => mix is not null && mixer.Disabled("eq:mix:" + mix.Id) is null ? Settings(mix.Eq) : null;

    private static MicEq? Settings(EqEntry? entry)
    {
        if (entry is not { Bands: { } bands }) return null;
        var eq = new MicEq { Enabled = entry.Enabled, Bands = bands };
        return MicEq.Check(eq) is null ? eq : null;
    }

    // What the daemon's revisions and the state name this EQ by.
    private string Key => mix ? "mix:" + channel : channel;

    private void Target(System.Text.Json.Nodes.JsonObject body) => body[mix ? "mix" : "channel"] = channel;

    private (string Name, EqEntry Entry, MicEq Eq)? Reported(Snapshot state)
    {
        if (mix)
        {
            MixEntry? output = state.Mixer.Mixes.FirstOrDefault(m => m.Id == channel);
            return Settings(output, state.Mixer) is { } eq ? (output!.Name, output.Eq!, eq) : null;
        }
        ChannelEntry? input = state.Mixer.Channels.FirstOrDefault(c => c.Id == channel);
        return Settings(input) is { } micEq ? (input!.Name, input.Eq!, micEq) : null;
    }

    private (string Name, EqEntry Entry, MicEq Eq)? Read(App app)
    {
        if (_session != app.Link.SessionRevision)
        {
            _session = app.Link.SessionRevision;
            _edits.Clear();
        }
        if (app.Link.LastError is not null) _edits.Clear();
        // An undo or redo from here puts values back that the held edits would hide.
        if (_historyRevision != app.HistoryRevision)
        {
            _historyRevision = app.HistoryRevision;
            _edits.Clear();
        }
        long revision = app.Link.ChannelRevision(Key);
        if (_channelRevision != revision)
        {
            _channelRevision = revision;
            _edits.Clear();
        }
        if (!app.Link.Connected || app.Link.State is not { } state) return null;
        if (Reported(state) is not { } found) return null;
        MicEq reported = found.Eq;
        if (_bandCount != reported.Bands.Count) { _edits.Clear(); _bandCount = reported.Bands.Count; }
        MicEq current = reported;
        foreach (var (key, edit) in _edits.ToArray())
        {
            // Equality can be an older echo, especially after on/off/on.
            // Keep the last local intent for the whole short hold window.
            if (_time.GetElapsedTime(edit.Since) >= TimeSpan.FromSeconds(1))
                _edits.Remove(key);
            else current = Apply(current, key.Band, key.Field, edit.Value);
        }
        return (found.Name, found.Entry, current);
    }

    internal bool Draw(Screen screen, Rect area, App app)
    {
        if (!app.Link.Connected || app.Link.State is null)
        {
            _edits.Clear();
            screen.Text(area.X + 2, area.Y + 1, $"Waiting for the daemon. Esc returns to {(mix ? "Outputs" : "Inputs")}.", app.Theme.TextMuted, app.Theme.Card,
                maxWidth: Math.Max(0, area.Width - 4));
            return true;
        }
        if (Read(app) is not { } edit) return false;
        _rows.Draw(screen, area, app.Theme, Build(app, edit.Name, edit.Entry, edit.Eq));
        return true;
    }

    internal bool Handle(KeyPress key, App app)
        => Read(app) is { } edit && _rows.Handle(key, Build(app, edit.Name, edit.Entry, edit.Eq));

    private List<Row> Build(App app, string name, EqEntry entry, MicEq eq)
    {
        List<Row> rows = [new HeadingRow($"{name} / {(mix ? "output" : "microphone")} EQ"), new TextRow("Status", entry.Status),
            new ToggleRow("EQ enabled", eq.Enabled, value => Set(app, 0, "enabled", value))];
        for (int index = 0; index < eq.Bands.Count; index++)
        {
            int number = index + 1;
            EqBand band = eq.Bands[index];
            string label = $"Band {number}";
            rows.Add(new HeadingRow(label));
            rows.Add(new ToggleRow($"{label} enabled", band.Enabled, value => Set(app, number, "enabled", value)));
            rows.Add(new ChoiceRow($"{label} type", MicEq.Types, MicEq.TypeIndex(band.Type),
                selected => Set(app, number, "type", MicEq.Types[selected])));
            rows.Add(new NumberRow($"{label} frequency", band.Frequency, MicEq.MinFrequency, MicEq.MaxFrequency, Octaves(1.0 / 6),
                value => $"{value:0} Hz", value => Set(app, number, "frequency", MicEq.SnapFrequency(value)))
            { FineStep = Octaves(1.0 / 24), Logarithmic = true, Integer = true, Edit = () => Ask(app, number, "frequency", band.Frequency, MicEq.MinFrequency, MicEq.MaxFrequency) });
            rows.Add(new NumberRow($"{label} gain", band.Gain, MicEq.MinGain, MicEq.MaxGain, MicEq.GainStep,
                value => $"{value:+0.##;-0.##;0} dB", value => Set(app, number, "gain", MicEq.SnapGain(value)))
            {
                FineStep = MicEq.GainStep, Enabled = MicEq.UsesGain(band.Type), DisabledText = "not used by this type",
                Edit = () => Ask(app, number, "gain", band.Gain, MicEq.MinGain, MicEq.MaxGain),
            });
            rows.Add(new NumberRow($"{label} Q", MicEq.QLog10(band.Q), -1, 1, MicEq.QLog10Step,
                value => Math.Pow(10, value).ToString("0.####", CultureInfo.InvariantCulture),
                value => Set(app, number, "q", MicEq.SnapQ(Math.Pow(10, value))))
            { FineStep = MicEq.QLog10Step, Edit = () => Ask(app, number, "q", band.Q, MicEq.MinQ, MicEq.MaxQ) });
        }
        rows.Add(new ActionRow("Reset bands", "defaults", () =>
        {
            if (Read(app) is not { } current) return;
            _edits.Clear();
            MicEq defaults = MicEq.Default with { Enabled = current.Eq.Enabled };
            long now = _time.GetTimestamp();
            _edits[(0, "enabled")] = (defaults.Enabled, now);
            for (int number = 1; number <= MicEq.BandCount; number++)
                foreach (string field in new[] { "enabled", "type", "frequency", "gain", "q" })
                    _edits[(number, field)] = (BandValue(defaults.Bands[number - 1], field), now);
            app.Link.Send("setEq", body => { Target(body); body["eq"] = JsonSerializer.SerializeToNode(defaults, WriteOptions); });
        }));
        return rows;
    }

    private void Ask(App app, int band, string field, double value, double minimum, double maximum)
    {
        long revision = app.Link.SessionRevision;
        long channelRevision = app.Link.ChannelRevision(Key);
        int bands = Read(app)?.Eq.Bands.Count ?? 0;
        app.Ask($"Band {band} {field}: {value:0.####} now, {minimum} to {maximum} (decimal point)", "", text =>
        {
            if (app.Link.SessionRevision != revision || app.Link.ChannelRevision(Key) != channelRevision || Read(app) is not { } current || current.Eq.Bands.Count != bands)
            { app.Say($"The {(mix ? "mix" : "microphone")} changed or disconnected. Enter the value again."); return; }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                || !double.IsFinite(number) || number < minimum || number > maximum)
            { app.Say($"Use a finite number from {minimum} to {maximum}, with a decimal point"); return; }
            double snapped = field switch { "frequency" => MicEq.SnapFrequency(number), "gain" => MicEq.SnapGain(number), _ => MicEq.SnapQ(number) };
            Set(app, band, field, snapped);
        });
    }

    private void Set(App app, int band, string field, object value)
    {
        if (Read(app) is not { } current || band > current.Eq.Bands.Count) return;
        _edits[(band, field)] = (value, _time.GetTimestamp());
        app.Link.Send(band == 0 ? "setEqEnabled" : "setEqBand", body =>
        {
            Target(body);
            if (band != 0) body["band"] = band;
            body[band == 0 ? "value" : field] = JsonSerializer.SerializeToNode(value);
        });
    }

    private static object BandValue(EqBand band, string field) => field switch
    {
        "enabled" => band.Enabled, "type" => band.Type,
        "frequency" => band.Frequency, "gain" => band.Gain, _ => band.Q,
    };

    private static MicEq Apply(MicEq eq, int band, string field, object value) => band == 0 ? eq with { Enabled = (bool)value }
        : eq.WithBand(band, new EqBandChange(Enabled: field == "enabled" ? (bool)value : null,
            Type: field == "type" ? (string)value : null, Frequency: field == "frequency" ? (double)value : null,
            Gain: field == "gain" ? (double)value : null, Q: field == "q" ? (double)value : null).ApplyTo(eq.Bands[band - 1]));

    /// <summary>A frequency step of the given fraction of an octave, in the units a logarithmic row moves by.</summary>
    private static double Octaves(double fraction) =>
        (MicEq.MaxFrequency - MicEq.MinFrequency) * fraction / Math.Log2(MicEq.MaxFrequency / MicEq.MinFrequency);
}
