namespace OpenXLR.Tui;

/// <summary>
/// The hardware: the XLR inputs with their processing, the headphone outputs,
/// the direct monitor blend and the USB Aux return. A control the active
/// device does not advertise is drawn faded rather than hidden, so the same
/// page describes every interface.
/// </summary>
internal sealed class InputsView : View
{
    private readonly RowList _list = new();

    public override string Title => "Inputs";

    public override string Keys => "space toggle  left right change  ctrl fine";

    public override void Draw(Screen screen, Rect area, App app)
    {
        Snapshot? state = State(app);
        if (state is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "Waiting for the daemon", app.Theme.TextMuted, app.Theme.Window);
            return;
        }
        _list.Draw(screen, area.Inset(0), app.Theme, Build(app, state));
    }

    public override bool Handle(KeyPress key, App app)
    {
        Snapshot? state = State(app);
        return state is not null && _list.Handle(key, Build(app, state));
    }

    private static List<Row> Build(App app, Snapshot state)
    {
        List<Row> rows = [];
        DaemonLink link = app.Link;
        int inputs = Math.Max(1, state.Count("xlrInputs"));

        for (int input = 1; input <= inputs; input++)
        {
            string suffix = input == 1 ? string.Empty : input.ToString();
            rows.Add(new HeadingRow($"XLR {input}"));
            rows.Add(new NumberRow($"Gain", state.Number($"gain{suffix}Db"), 0, 80, 1,
                value => $"{value:0} dB", value => Set(link, $"gain{suffix}", (int)Math.Round(value)))
            { Enabled = state.Can("gain"), Note = state.Flag("gainLocked") ? "locked" : null });
            rows.Add(new ToggleRow("Mute", state.Flag($"mute{suffix}"),
                value => Set(link, $"mute{suffix}", value)) { Enabled = state.Can("mute") });
            rows.Add(new ToggleRow("Low cut", state.Flag($"lowCut{suffix}"),
                value => Set(link, $"lowCut{suffix}", value)) { Enabled = state.Can("lowCut") });
            rows.Add(new ToggleRow("Expander", state.Flag($"expander{suffix}"),
                value => Set(link, $"expander{suffix}", value)) { Enabled = state.Can("expander") });
            rows.Add(new ToggleRow("Voice tune", state.Flag($"voiceTune{suffix}"),
                value => Set(link, $"voiceTune{suffix}", value)) { Enabled = state.Can("voiceTune") });
            rows.Add(new NumberRow("VT strength", state.Number($"voiceTuneStrength{suffix}"), 0, 100, 5,
                Widgets.Percent0, value => Set(link, $"voiceTuneStrength{suffix}", (int)Math.Round(value)))
            { Enabled = state.Can("voiceTune") });
            rows.Add(new ToggleRow("Phantom power", state.Flag($"phantom{suffix}"),
                value => Set(link, $"phantom{suffix}", value))
            {
                Enabled = state.Can("phantom"),
                Note = state.Flag($"phantomSettling{suffix}") ? "settling" : null,
            });
            rows.Add(new ToggleRow("ClipGuard", state.Flag($"clipGuard{suffix}"),
                value => Set(link, $"clipGuard{suffix}", value)) { Enabled = state.Can("clipGuard") });
            rows.Add(new ToggleRow("Compressor", state.Flag($"compressor{suffix}"),
                value => Set(link, $"compressor{suffix}", value)) { Enabled = state.Can("compressor") });
        }

        rows.Add(new HeadingRow("Software"));
        rows.Add(new ToggleRow("Gain lock", state.Flag("gainLocked"),
            value => Set(link, "gainLock", value)) { Enabled = state.Can("gain") });
        string[] lowCuts = ["off", "80 Hz", "120 Hz"];
        int lowCutAt = state.Mixer.LowCutHz switch { 80 => 1, 120 => 2, _ => 0 };
        rows.Add(new ChoiceRow("Software low cut", lowCuts, lowCutAt, index =>
            link.Send("setLowCutHz", body => body["value"] = index switch { 1 => 80, 2 => 120, _ => 0 })));
        rows.Add(new ToggleRow("Software ClipGuard", state.Mixer.SoftClipGuard,
            value => link.Send("setSoftClipGuard", body => body["value"] = value))
        { Enabled = state.Mixer.SoftClipGuardAvailable, Note = state.Mixer.SoftClipGuardAvailable ? null : "needs swh-plugins" });

        rows.Add(new HeadingRow("Headphones"));
        rows.Add(new NumberRow("Phones 1", Percent(state.Number("hpVolumeDb")), 0, 100, 5, Widgets.Percent0,
            value => Set(link, "hpVolumeDb", Decibels(value))) { Enabled = state.Can("hpVolume") });
        if (state.Count("hpOutputs") > 1)
            rows.Add(new NumberRow("Phones 2", Percent(state.Number("hp2VolumeDb")), 0, 100, 5, Widgets.Percent0,
                value => Set(link, "hp2VolumeDb", Decibels(value))) { Enabled = state.Can("hpVolume") });
        rows.Add(new NumberRow("Mic to PC blend", state.Number("crossfade"), 0, 200, 10,
            value => value <= 0 ? "mic only" : value >= 200 ? "PC only" : $"{value:0}",
            value => Set(link, "crossfade", (int)Math.Round(value))) { Enabled = state.Can("crossfade") });
        rows.Add(new ToggleRow("Low impedance", state.Flag("lowImpedance"),
            value => Set(link, "lowImpedance", value)) { Enabled = state.Can("lowImpedance") });

        if (state.Can("outputRouting"))
        {
            rows.Add(new HeadingRow("Hardware outputs"));
            rows.Add(new ToggleRow("Phones 1 out", state.Flag("outHp1"), value => Set(link, "outHp1", value)));
            rows.Add(new ToggleRow("Phones 2 out", state.Flag("outHp2"), value => Set(link, "outHp2", value))
            { Enabled = state.Count("hpOutputs") > 1 });
            rows.Add(new ToggleRow("USB Aux out", state.Flag("outUsbAux"), value => Set(link, "outUsbAux", value)));
            rows.Add(new ToggleRow("Line out", state.Flag("outLineOut"), value => Set(link, "outLineOut", value)));
        }

        if (state.Can("auxInput"))
        {
            rows.Add(new HeadingRow("USB Aux in"));
            rows.Add(new NumberRow("Level", state.Number("auxLevelDb"), -60, 0, 2,
                value => $"{value:0} dB", value => Set(link, "auxLevelDb", value)));
            rows.Add(new ToggleRow("Level lock", state.Flag("auxLevelLock"),
                value => Set(link, "auxLevelLock", value)));
            rows.Add(new ToggleRow("Aux port feed", state.Mixer.AuxPortEnabled,
                value => link.Send("setAuxPortEnabled", body => body["value"] = value)));
        }

        return rows;
    }

    /// <summary>The headphone scale the window shows: -60 dB is 0% and 0 dB is 100%.</summary>
    private static double Percent(double decibels) => Math.Clamp((60.0 + decibels) / 60.0 * 100.0, 0, 100);

    private static double Decibels(double percent) => -60.0 + Math.Clamp(percent, 0, 100) * 0.6;

    private static void Set(DaemonLink link, string control, bool value) =>
        link.Send("set", body => { body["control"] = control; body["value"] = value; });

    private static void Set(DaemonLink link, string control, int value) =>
        link.Send("set", body => { body["control"] = control; body["value"] = value; });

    private static void Set(DaemonLink link, string control, double value) =>
        link.Send("set", body => { body["control"] = control; body["value"] = Math.Round(value, 2); });
}
