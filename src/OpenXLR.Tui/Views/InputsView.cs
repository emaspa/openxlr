using System.Text.Json.Nodes;

namespace OpenXLR.Tui;

/// <summary>
/// The hardware: the interface's microphone inputs with their processing,
/// what feeds XLR 1 and XLR 2, where each microphone's EQ runs, the
/// headphone outputs, the direct monitor blend, ducking, the USB Aux return, the Line In gain and each capture input's gain. A control the active device does not
/// advertise is drawn faded rather than hidden, so the same page describes
/// every interface.
///
/// An input's group is headed by the channel that carries it, which is its
/// own strip until that strip is bound to another microphone. A binding
/// choice is a draft until Apply sends it, because each change mutes the
/// strip in the monitor mixes; stepping through the sources never rewires
/// the microphone on the way.
/// </summary>
internal sealed class InputsView : View
{
    private readonly RowList _list = new();
    private readonly Dictionary<string, (string? Source, int Port, bool Drive)> _drafts = [];
    // The request in flight, which is the link's own task, so a reply has
    // completed it by the time the link reports the change and a frame is
    // drawn. Driving a unit is a second request, sent once the binding is in.
    private Task<string?>? _applying;
    private string _applyingStrip = string.Empty;
    private string _applyingLabel = string.Empty;
    // The ducking strips' cursors, by id, so a key survives a list that changed under it.
    private string? _duckChannel;
    private string? _duckMix;
    private string? _driveAfter;
    private bool _driving;
    private EqEditor? _eqEditor;

    public override string Title => "Inputs";

    public override string Keys => _eqEditor?.Keys ?? "Up/Down control  Space toggle  Left/Right or -/+ change  [/] fine  Enter apply";

    public override void Draw(Screen screen, Rect area, App app)
    {
        if (_eqEditor is { } editor)
        {
            if (editor.Draw(screen, area, app)) return;
            _eqEditor = null;
            app.Say("That microphone no longer has an editable EQ");
        }
        if (_applying is { IsCompleted: true } done)
        {
            string? error = done.IsCompletedSuccessfully ? done.Result : "The daemon did not answer";
            _applying = null;
            if (error is { Length: > 0 })
            {
                app.Say(_driving ? $"The strip is bound, but the interface was not driven: {error}" : error);
                _driveAfter = null;
            }
            else if (_driveAfter is { } unit)
            {
                _driveAfter = null;
                _driving = true;
                _applying = app.Link.Request("setWaveInterfaceEnabled", body =>
                {
                    body["device"] = unit;
                    body["value"] = true;
                });
            }
            else app.Say(_applyingLabel);
            // The binding is in once its own request succeeded, whatever driving the unit says.
            if (_driving || error is null) _drafts.Remove(_applyingStrip);
        }
        Snapshot? state = State(app);
        if (state is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "Waiting for the daemon", app.Theme.TextMuted, app.Theme.Window);
            return;
        }
        _list.DrawGroups(screen, area, app.Theme, Build(app, state));
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
        List<Row> rows = [];
        DaemonLink link = app.Link;
        int inputs = Math.Max(1, state.Count("xlrInputs"));
        const string off = "not on this device";

        for (int input = 1; input <= inputs; input++)
        {
            if (state.Carrier(input) is { CaptureSource: not null, Mono: true }) continue;
            rows.Add(new HeadingRow(state.InputHeading(input)) { Dial = true });
            PrimaryInput(rows, link, state, input);
        }

        AdditionalInputs(rows, link, state);
        Bindings(rows, app, state);
        Equalisers(rows, app, state);
        NoiseRemovals(rows, app.Link, state);
        Dynamics(rows, app.Link, state);
        VoiceChangers(rows, app.Link, state);

        // The software low cut and ClipGuard run in the mixer, so they act on
        // any microphone on XLR 1; the gain lock guards the interface's gain.
        rows.Add(new HeadingRow("Software"));
        rows.Add(new ToggleRow("Gain lock", state.Flag("gainLocked"),
            value => Set(link, "gainLock", value)) { Enabled = state.Can("gain"), DisabledText = off });
        string[] lowCuts = ["off", "80 Hz", "120 Hz"];
        int lowCutAt = state.Mixer.LowCutHz switch { 80 => 1, 120 => 2, _ => 0 };
        rows.Add(new ChoiceRow("Software low cut", lowCuts, lowCutAt, index =>
            link.Send("setLowCutHz", body => body["value"] = index switch { 1 => 80, 2 => 120, _ => 0 })));
        rows.Add(new ToggleRow("Software ClipGuard", state.Mixer.SoftClipGuard,
            value => link.Send("setSoftClipGuard", body => body["value"] = value))
        { Enabled = state.Mixer.SoftClipGuardAvailable, Note = state.Mixer.SoftClipGuardAvailable ? null : "needs swh-plugins" });

        rows.Add(new HeadingRow("Headphones"));
        rows.Add(new NumberRow("Phones 1", Percent(state.Number("hpVolumeDb")), 0, 100, 5, Widgets.Percent0,
            value => Set(link, "hpVolumeDb", Decibels(value))) { Enabled = state.Can("hpVolume"), DisabledText = off });
        if (state.Count("hpOutputs") > 1)
            rows.Add(new NumberRow("Phones 2", Percent(state.Number("hp2VolumeDb")), 0, 100, 5, Widgets.Percent0,
                value => Set(link, "hp2VolumeDb", Decibels(value))) { Enabled = state.Can("hpVolume") });
        rows.Add(new NumberRow("Mic to PC blend", state.Number("crossfade"), 0, 200, 10,
            value => value <= 0 ? "mic only" : value >= 200 ? "PC only" : $"{value:0}",
            value => Set(link, "crossfade", (int)Math.Round(value))) { Enabled = state.Can("crossfade"), DisabledText = off });
        rows.Add(new ToggleRow("Low impedance", state.Flag("lowImpedance"),
            value => Set(link, "lowImpedance", value)) { Enabled = state.Can("lowImpedance"), DisabledText = off });

        if (state.Can("outputRouting"))
        {
            rows.Add(new HeadingRow("Hardware outputs"));
            rows.Add(new ToggleRow("Phones 1 out", state.Flag("outHp1"), value => Set(link, "outHp1", value)));
            rows.Add(new ToggleRow("Phones 2 out", state.Flag("outHp2"), value => Set(link, "outHp2", value))
            { Enabled = state.Count("hpOutputs") > 1 });
            rows.Add(new ToggleRow("USB Aux out", state.Flag("outUsbAux"), value => Set(link, "outUsbAux", value)));
            rows.Add(new ToggleRow("Line out", state.Flag("outLineOut"), value => Set(link, "outLineOut", value)));
        }

        if (state.Ducking is { } ducking) Ducking(rows, app, state, ducking);

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

        // The Wave XLR Pro's analog Line In, an input of its own beside USB Aux.
        if (state.Can("lineInput"))
        {
            // The highest gain the daemon writes; one from before the cap held it to the unit's gain range.
            int lineMax = state.Count("lineInGainMaxDb") is int max and > 0 ? max
                : state.Count("gainMaxDb") is int gainMax and > 0 ? gainMax : 80;
            rows.Add(new HeadingRow("Line In"));
            rows.Add(new NumberRow("Gain", state.Number("lineInGainDb"), 0, lineMax, 1,
                value => $"{value:0} dB", value => Set(link, "lineInGain", (int)Math.Round(value))) { FineStep = 1 });
            // Holds the gain against the desktop mixer; this row's own writes still apply.
            rows.Add(new ToggleRow("Level lock", state.Flag("lineInLevelLock"),
                value => Set(link, "lineInLevelLock", value)));
        }

        return rows;
    }

    private static void PrimaryInput(List<Row> rows, DaemonLink link, Snapshot state, int input, string? device = null)
    {
        string suffix = input == 1 ? string.Empty : "2";
        int gainMax = state.Count("gainMaxDb") is int max and > 0 ? max : 80;
        const string off = "not on this device";
        void Write(string control, JsonNode value) => link.Send(device is null ? "set" : "setWaveControl", body =>
        {
            if (device is not null) body["device"] = device;
            body["control"] = control;
            body["value"] = value;
        });
        rows.Add(new NumberRow($"Gain", state.Number($"gain{suffix}Db"), 0, gainMax, 1,
            value => $"{value:0} dB", value => Write($"gain{suffix}", (int)Math.Round(value)))
        {
            Enabled = state.Can("gain"), FineStep = 1, DisabledText = off,
            Note = state.Flag("gainLocked") ? "locked" : null,
        });
        rows.Add(new ToggleRow("Mute", state.Flag($"mute{suffix}"),
            value => Write($"mute{suffix}", value)) { Enabled = state.Can("mute"), DisabledText = off });
        rows.Add(new ToggleRow("Low cut", state.Flag($"lowCut{suffix}"),
            value => Write($"lowCut{suffix}", value)) { Enabled = state.Can("lowCut"), DisabledText = off });
        rows.Add(new ToggleRow("Expander", state.Flag($"expander{suffix}"),
            value => Write($"expander{suffix}", value)) { Enabled = state.Can("expander"), DisabledText = off });
        rows.Add(new ToggleRow("Voice tune", state.Flag($"voiceTune{suffix}"),
            value => Write($"voiceTune{suffix}", value)) { Enabled = state.Can("voiceTune"), DisabledText = off });
        rows.Add(new NumberRow("VT strength", state.Number($"voiceTuneStrength{suffix}"), 0, 100, 5,
            Widgets.Percent0, value => Write($"voiceTuneStrength{suffix}", (int)Math.Round(value)))
        { Enabled = state.Can("voiceTune"), DisabledText = off });
        rows.Add(new ToggleRow("Phantom power", state.Flag($"phantom{suffix}"),
            value => Write($"phantom{suffix}", value))
        {
            Enabled = state.Can("phantom"), DisabledText = off,
            Note = state.Flag($"phantomSettling{suffix}") ? "settling" : null,
        });
        rows.Add(new ToggleRow("ClipGuard", state.Flag($"clipGuard{suffix}"),
            value => Write($"clipGuard{suffix}", value)) { Enabled = state.Can("clipGuard"), DisabledText = off });
        rows.Add(new ToggleRow("Compressor", state.Flag($"compressor{suffix}"),
            value => Write($"compressor{suffix}", value)) { Enabled = state.Can("compressor"), DisabledText = off });
    }

    /// <summary>
    /// What feeds XLR 1 and XLR 2: the interface's own input or one port of
    /// another capture source, each with its port, Apply and the strip's
    /// connection. The sources are the ones the window offers, which leaves
    /// out an attached supported interface's nodes for now.
    /// </summary>
    private void Bindings(List<Row> rows, App app, Snapshot state)
    {
        List<ChannelEntry> strips = [.. state.Mixer.Channels.Where(channel => channel.Bindable)];
        if (strips.Count == 0) return;
        rows.Add(new HeadingRow("Bindings"));
        List<DeviceEntry> sources = state.BindingChoices;
        foreach (ChannelEntry strip in strips)
        {
            string? bound = state.BoundSource(strip);
            int boundPort = (strip.Binding?.MonoChannel ?? 0) + 1;
            // The interface's own input: an XLR jack, or a USB microphone's capsule.
            List<(string? Name, string Label)> choices = [(null, state.UnitInputLabel(strip))];
            foreach (DeviceEntry source in sources)
            {
                string label = source.Description is { Length: > 0 } description ? description : source.Name;
                // Two microphones of one model share a description; the node name tells them apart.
                if (sources.Count(other => other.Description == source.Description) > 1) label = $"{label} ({source.Name})";
                choices.Add((source.Name, label));
            }
            // A saved source that is unplugged, or that the list leaves out, stays chosen, as the daemon keeps it.
            if (bound is not null && !choices.Any(choice => choice.Name == bound))
                choices.Add((bound, $"{bound} (missing)"));

            (string? chosen, int port, bool drive) = _drafts.TryGetValue(strip.Id, out var draft) ? draft : (bound, boundPort, true);
            int at = choices.FindIndex(choice => choice.Name == chosen);
            if (at < 0) (at, chosen, port) = (0, null, 1);   // a drafted source that has since gone
            DeviceEntry? device = sources.FirstOrDefault(source => source.Name == chosen);
            int ports = device?.Channels is int count and > 0 ? Math.Min(count, 64) : 64;
            port = Math.Clamp(port, 1, ports);
            string id = strip.Id;
            string name = strip.Name;
            rows.Add(new ChoiceRow(name, [.. choices.Select(choice => choice.Label)], at,
                index => _drafts[id] = (choices[index].Name, 1, drive)));
            rows.Add(new NumberRow($"{name} port", port, 1, ports, 1, value => PortLabel(device, (int)value),
                value => _drafts[id] = (chosen, (int)value, drive))
            { Enabled = chosen is not null, Integer = true, FineStep = 1, DisabledText = "the interface takes no port" });
            if (chosen?.StartsWith("bluez_input.", StringComparison.Ordinal) == true)
                rows.Add(new TextRow("Bluetooth", "stays in headset mode while bound"));
            // Another supported unit that is not driven yet: drive it as an
            // additional interface too, on by default, since otherwise its
            // gain is out of reach.
            WaveInterfaceEntry? undriven = state.UndrivenUnit(chosen);
            if (undriven is not null)
                rows.Add(new ToggleRow("Also drive unit", drive, value => _drafts[id] = (chosen, port, value))
                { Note = undriven.Name });
            bool changed = chosen != bound || chosen is not null && port != boundPort;
            string done = chosen is null ? $"{name} takes {choices[0].Label}" : $"{name} takes {choices[at].Label}, port {port}";
            string? driveUnit = undriven is not null && drive ? undriven.Id : null;
            rows.Add(new ActionRow($"Apply to {name}", _applying is null ? "apply" : "sending",
                () => Apply(app, id, chosen, port, driveUnit, done))
            { Note = changed ? "not applied" : null });
            rows.Add(new TextRow($"{name} feed", strip.BindingWarning is { Length: > 0 } warning ? warning
                : strip.BindingConnected ? $"linked, {state.BindingLabel(strip)}" : "not linked"));
        }
    }

    /// <summary>
    /// Each microphone's EQ: what runs it, and where a unit that runs the EQ
    /// itself carries the microphone, the choice between the unit and this
    /// computer, which setEqProcessing sends. Each complete EQ opens its band editor.
    /// </summary>
    private void Equalisers(List<Row> rows, App app, Snapshot state)
    {
        DaemonLink link = app.Link;
        List<ChannelEntry> microphones = [.. state.Mixer.Shown.Where(channel => channel.Eq is not null)];
        if (microphones.Count == 0) return;
        rows.Add(new HeadingRow("Microphone EQ"));
        string[] places = ["on the unit", "in software"];
        foreach (ChannelEntry channel in microphones)
        {
            EqEntry eq = channel.Eq!;
            string id = channel.Id;
            rows.Add(new TextRow($"{channel.Name} EQ", eq.Status));
            if (EqEditor.CanEdit(channel))
                rows.Add(new ActionRow($"{channel.Name} bands", "edit EQ",
                    () => _eqEditor = new EqEditor(id, link.SessionRevision)));
            if (!eq.Offered) continue;
            rows.Add(new ChoiceRow($"{channel.Name} EQ runs", places, eq.Processing == "software" ? 1 : 0,
                index => link.Send("setEqProcessing", body =>
                {
                    body["channel"] = id;
                    body["value"] = index == 1 ? "software" : "device";
                })));
            if (eq.Why is { Length: > 0 } why) rows.Add(new TextRow($"{channel.Name} unit", why));
        }
    }

    /// <summary>
    /// Each microphone's noise removal: its switch, the engine among those the
    /// daemon's plugin host carries, and the strength, each sent on its own
    /// with setNoiseRemoval, with what runs and why not beside them.
    /// </summary>
    private static void NoiseRemovals(List<Row> rows, DaemonLink link, Snapshot state)
    {
        List<ChannelEntry> microphones = [.. state.Mixer.Shown.Where(channel => channel.Noise is not null)];
        if (microphones.Count == 0) return;
        rows.Add(new HeadingRow("Noise removal"));
        foreach (ChannelEntry channel in microphones)
        {
            NoiseEntry noise = channel.Noise!;
            string id = channel.Id;
            void Send(string field, object value) => link.Send("setNoiseRemoval", body =>
            {
                body["channel"] = id;
                body[field] = System.Text.Json.JsonSerializer.SerializeToNode(value);
            });
            rows.Add(new TextRow($"{channel.Name} noise removal", noise.Status));
            if (!noise.Available) continue;
            rows.Add(new ToggleRow($"{channel.Name} noise removal on", noise.Enabled, value => Send("enabled", value)));
            string[] engines = [.. MicNoise.Engines.Where(noise.Engines.Contains)];
            if (engines.Length > 1)
                rows.Add(new ChoiceRow($"{channel.Name} engine", [.. engines.Select(NoiseEntry.EngineName)],
                    Math.Max(0, Array.IndexOf(engines, noise.Engine)), index => Send("engine", engines[index])));
            else rows.Add(new TextRow($"{channel.Name} engine", NoiseEntry.EngineName(noise.Engine)));
            rows.Add(new NumberRow($"{channel.Name} strength", noise.Strength, MicNoise.MinStrength, MicNoise.MaxStrength, 6,
                value => value >= MicNoise.MaxStrength ? "no limit" : $"{value:0} dB",
                value => Send("strength", MicNoise.SnapStrength(value))) { FineStep = MicNoise.StrengthStep, Integer = true });
        }
    }

    /// <summary>
    /// Each microphone's expander and compressor on this computer, in the
    /// order the signal meets them: the switch and the threshold, and while
    /// one is on its other settings, each sent on its own with setExpander or
    /// setCompressor. They are not the unit's own Expander and Compressor
    /// switches, which the unit's rows carry.
    /// </summary>
    private static void Dynamics(List<Row> rows, DaemonLink link, Snapshot state)
    {
        List<ChannelEntry> microphones = [.. state.Mixer.Shown.Where(channel => channel.Compressor is not null && channel.Expander is not null)];
        if (microphones.Count == 0) return;
        rows.Add(new HeadingRow("Dynamics on this computer"));
        static string Db(double value) => $"{value:0.#} dB";
        static string Ms(double value) => $"{value:0.#} ms";
        static string Ratio(double value) => $"{value:0.#}:1";
        foreach (ChannelEntry channel in microphones)
        {
            ExpanderEntry expander = channel.Expander!;
            CompressorEntry compressor = channel.Compressor!;
            string id = channel.Id;
            Action<double> Sender(string command, string field, DynamicsRange range) => value =>
                link.Send(command, body =>
                {
                    body["channel"] = id;
                    body[field] = Math.Clamp(range.Snap(value), range.Minimum, range.Maximum);
                });
            // Times move on ratios, so a few milliseconds and two seconds are both in reach.
            NumberRow Number(string label, double value, DynamicsRange range, double step, Func<double, string> text, string command) =>
                new($"{channel.Name} {label}", value, range.Minimum, range.Maximum, step, text, Sender(command, range.Name, range))
                {
                    FineStep = range.Step,
                    Logarithmic = range.Unit == "ms" && range.Minimum > 0,
                };

            rows.Add(new TextRow($"{channel.Name} expander", expander.Status));
            if (expander.Available)
            {
                rows.Add(new ToggleRow($"{channel.Name} exp on", expander.Enabled, value => link.Send("setExpander", body =>
                {
                    body["channel"] = id;
                    body["enabled"] = value;
                })));
                rows.Add(Number("exp threshold", expander.Threshold, MicExpander.ThresholdRange, 2, Db, "setExpander"));
                if (expander.Enabled)
                {
                    rows.Add(Number("exp ratio", expander.Ratio, MicExpander.RatioRange, 0.5, Ratio, "setExpander"));
                    rows.Add(Number("exp range", expander.Range, MicExpander.RangeRange, 2, Db, "setExpander"));
                    rows.Add(Number("exp attack", expander.Attack, MicExpander.AttackRange, 1, Ms, "setExpander"));
                    rows.Add(Number("exp hold", expander.Hold, MicExpander.HoldRange, 10, Ms, "setExpander"));
                    rows.Add(Number("exp release", expander.Release, MicExpander.ReleaseRange, 10, Ms, "setExpander"));
                }
            }
            rows.Add(new TextRow($"{channel.Name} compressor", compressor.Status));
            if (!compressor.Available) continue;
            rows.Add(new ToggleRow($"{channel.Name} comp on", compressor.Enabled, value => link.Send("setCompressor", body =>
            {
                body["channel"] = id;
                body["enabled"] = value;
            })));
            rows.Add(Number("comp threshold", compressor.Threshold, MicCompressor.ThresholdRange, 2, Db, "setCompressor"));
            if (!compressor.Enabled) continue;
            rows.Add(Number("comp ratio", compressor.Ratio, MicCompressor.RatioRange, 0.5, Ratio, "setCompressor"));
            rows.Add(Number("comp attack", compressor.Attack, MicCompressor.AttackRange, 1, Ms, "setCompressor"));
            rows.Add(Number("comp release", compressor.Release, MicCompressor.ReleaseRange, 10, Ms, "setCompressor"));
            rows.Add(Number("comp knee", compressor.Knee, MicCompressor.KneeRange, 1, Db, "setCompressor"));
            rows.Add(Number("comp make-up", compressor.Makeup, MicCompressor.MakeupRange, 1, Db, "setCompressor"));
        }
    }

    /// <summary>
    /// Each microphone's voice changer: one choice of Off or a voice, sent as
    /// one setVoiceChanger with the switch and the voice together, and the
    /// custom voice's pitch and formant in semitones, each sent on its own,
    /// with what runs and why not beside them.
    /// </summary>
    private static void VoiceChangers(List<Row> rows, DaemonLink link, Snapshot state)
    {
        List<ChannelEntry> microphones = [.. state.Mixer.Shown.Where(channel => channel.Voice is not null)];
        if (microphones.Count == 0) return;
        rows.Add(new HeadingRow("Voice changer"));
        string[] choices = ["Off", .. MicVoice.Voices.Select(VoiceEntry.VoiceName)];
        foreach (ChannelEntry channel in microphones)
        {
            VoiceEntry voice = channel.Voice!;
            string id = channel.Id;
            void Send(Action<System.Text.Json.Nodes.JsonObject> fields) => link.Send("setVoiceChanger", body =>
            {
                body["channel"] = id;
                fields(body);
            });
            rows.Add(new TextRow($"{channel.Name} voice changer", voice.Status));
            if (!voice.Available) continue;
            int selected = voice.Enabled ? Math.Max(0, MicVoice.VoiceIndex(voice.Voice)) + 1 : 0;
            rows.Add(new ChoiceRow($"{channel.Name} voice", choices, selected, index => Send(body =>
            {
                if (index == 0) body["enabled"] = false;
                else
                {
                    body["enabled"] = true;
                    body["voice"] = MicVoice.Voices[index - 1];
                }
            })));
            if (voice.Voice != MicVoice.Custom) continue;
            static string Semitones(double value) => $"{value:+0.#;-0.#;0} st";
            rows.Add(new NumberRow($"{channel.Name} pitch", voice.Pitch, MicVoice.MinShift, MicVoice.MaxShift, 1, Semitones,
                value => Send(body => body["pitch"] = MicVoice.SnapShift(value))) { FineStep = MicVoice.ShiftStep });
            rows.Add(new NumberRow($"{channel.Name} formant", voice.Formant, MicVoice.MinShift, MicVoice.MaxShift, 1, Semitones,
                value => Send(body => body["formant"] = MicVoice.SnapShift(value))) { FineStep = MicVoice.ShiftStep });
        }
    }

    /// <summary>A port numbered from 1, with the channel PipeWire names for it when it reports one.</summary>
    private static string PortLabel(DeviceEntry? device, int port) =>
        device?.Positions is { } positions && port >= 1 && port <= positions.Count && device.Channels == positions.Count
            ? $"{port} ({positions[port - 1]})" : $"{port}";

    private void Apply(App app, string strip, string? source, int port, string? driveUnit, string label)
    {
        if (_applying is not null) return;
        _applyingStrip = strip;
        _applyingLabel = label;
        _driveAfter = driveUnit;
        _driving = false;
        _applying = app.Link.Request("setInputBinding", body =>
        {
            body["channel"] = strip;
            body["source"] = source;
            if (source is not null) body["captureMonoChannel"] = port - 1;
        });
    }

    /// <summary>
    /// Capture microphones with their source and connection, and the controls
    /// of each carried unit, written with setWaveControl.
    /// </summary>
    private static void AdditionalInputs(List<Row> rows, DaemonLink link, Snapshot state)
    {
        foreach (ChannelEntry channel in state.Mixer.Channels)
        {
            bool capture = channel.CaptureSource is not null && channel.Mono;
            if (capture)
            {
                rows.Add(new HeadingRow(channel.Name));
                string source = state.BoundSource(channel)!;
                rows.Add(new TextRow("Source", $"{state.SourceName(source)}, port {(channel.Binding?.MonoChannel ?? 0) + 1}"));
                rows.Add(new TextRow("Connection", channel.BindingWarning is { Length: > 0 } warning ? warning
                    : channel.CaptureConnected ? "linked" : "not linked"));
                CaptureGainRow(rows, link, channel);
                if (channel.UnitInput is { Primary: true } primary)
                {
                    rows.Add(new HeadingRow($"{state.Device?.Model ?? "Interface"} input {primary.Input} on {channel.Name}") { Dial = state.Can("gain") });
                    PrimaryInput(rows, link, state, primary.Input, primary.Unit);
                }
            }
            else if (channel.CaptureSource is not null && channel.CaptureGain is not null)
            {
                // A stereo capture input has no microphone group of its own; its gain does.
                rows.Add(new HeadingRow(channel.Name));
                CaptureGainRow(rows, link, channel);
            }
            if (channel.UnitInput is not { Primary: false } carried || state.Additional(carried.Unit) is not { Driven: true } unit)
                continue;
            string suffix = carried.Input == 2 ? "2" : string.Empty;
            UnitControl Control(string name) => new(unit.Id, name + suffix);
            int gainMax = unit.Count("gainMaxDb") is int max and > 0 ? max : 80;
            rows.Add(new HeadingRow($"{unit.Name} input {carried.Input} on {channel.Name}") { Dial = unit.Can("gain") });
            if (unit.Can("gain"))
                rows.Add(new NumberRow("Gain", unit.Number($"gain{suffix}Db"), 0, gainMax, 1, value => $"{value:0} dB",
                    value => link.Send("setWaveControl", body =>
                    {
                        body["device"] = unit.Id;
                        body["control"] = $"gain{suffix}";
                        body["value"] = (int)Math.Round(value);
                    }))
                { FineStep = 1, Note = unit.Flag("gainLocked") ? "locked" : null });
            foreach ((string label, string name) in new[] { ("Mute", "mute"), ("Low cut", "lowCut"), ("ClipGuard", "clipGuard"), ("Phantom power", "phantom") })
                if (unit.Can(name))
                    rows.Add(new ToggleRow(label, unit.Flag(name + suffix), value => Snapshot.Send(link, Control(name), value)));
        }
    }

    /// <summary>
    /// A capture input's own gain, its source's volume, which setCaptureGain
    /// sends: the device's capture control where it has one, its software
    /// level otherwise. A channel that takes a Wave unit's input has none.
    /// </summary>
    private static void CaptureGainRow(List<Row> rows, DaemonLink link, ChannelEntry channel)
    {
        if (channel.CaptureGain is not { } gain) return;
        string id = channel.Id;
        rows.Add(new NumberRow(gain.Hardware ? "Device gain" : "Gain", gain.GainDb, Math.Min(gain.MinDb, gain.MaxDb), Math.Max(gain.MinDb, gain.MaxDb), 1,
            value => $"{value:0.#} dB", value => link.Send("setCaptureGain", body =>
            {
                body["channel"] = id;
                body["value"] = Math.Round(value, 1);
            }))
        { FineStep = 0.5 });
    }

    /// <summary>The headphone scale the window shows: -60 dB is 0% and 0 dB is 100%.</summary>
    private static double Percent(double decibels) => Math.Clamp((60.0 + decibels) / 60.0 * 100.0, 0, 100);

    private static double Decibels(double percent) => -60.0 + Math.Clamp(percent, 0, 100) * 0.6;

    /// <summary>
    /// Ducking, on any device: the switch, the key microphone, a strip of
    /// channels to lower and one of mixes to lower them in, the three
    /// presets, and while the unit can run it, the choice between the unit
    /// and this computer with the reason it does not. While software runs it,
    /// a short bar shows how far it lowers now. The daemon owns every value;
    /// a key sends the change and the row follows the next state.
    /// </summary>
    private void Ducking(List<Row> rows, App app, Snapshot state, DuckingEntry ducking)
    {
        DaemonLink link = app.Link;
        rows.Add(new HeadingRow("Ducking"));
        // Without a built mixer the daemon refuses both commands.
        if (state.Mixer.Channels.Count == 0)
        {
            rows.Add(new TextRow("Ducking", "needs the software mixer"));
            return;
        }
        rows.Add(new ToggleRow("Ducking", ducking.Enabled, value => Duck(link, "enabled", value))
        { Note = ducking.Note });

        // The key: a microphone strip shown on this device, and the saved key
        // even when it is not, so cycling starts from where the daemon is.
        List<ChannelEntry> microphones = [.. state.Mixer.Channels.Where(channel =>
            channel.Mono && (channel.Present || channel.Id == ducking.Key))];
        int keyAt = microphones.FindIndex(channel => channel.Id == ducking.Key);
        List<string> keyNames = [.. microphones.Select(channel => channel.Name)];
        if (keyAt < 0)
        {
            keyNames.Add($"{ducking.Key} (missing)");
            keyAt = keyNames.Count - 1;
        }
        rows.Add(new ChoiceRow("Key", keyNames, keyAt, index =>
        {
            if (index >= microphones.Count) return;
            string key = microphones[index].Id;
            // The key is never ducked, so a channel that becomes the key leaves the list in the same command.
            link.Send("setDucking", body =>
            {
                JsonObject value = new() { ["key"] = key };
                if (ducking.Channels is { } channels && channels.Contains(key))
                    value["channels"] = Ids(channels.Where(id => id != key));
                body["value"] = value;
            });
        }));

        // A daemon from before channels and mixes keeps them to itself.
        if (ducking.Channels is { } ducked)
        {
            List<ChannelEntry> candidates = [.. state.Mixer.Shown.Where(channel => channel.Id != ducking.Key)];
            int at = Math.Max(0, candidates.FindIndex(channel => channel.Id == _duckChannel));
            rows.Add(new ToggleStripRow("Channels", [.. candidates.Select(channel => (channel.Name, ducked.Contains(channel.Id)))], at,
                index => _duckChannel = candidates[index].Id,
                index =>
                {
                    _duckChannel = candidates[index].Id;
                    Duck(link, "channels", Flip(ducked, candidates[index].Id));
                }));
        }
        if (ducking.Mixes is { } mixes)
        {
            List<MixEntry> targets = state.Mixer.Mixes;
            int at = Math.Max(0, targets.FindIndex(mix => mix.Id == _duckMix));
            rows.Add(new ToggleStripRow("Mixes", [.. targets.Select(mix => (mix.Name, mixes.Contains(mix.Id)))], at,
                index => _duckMix = targets[index].Id,
                index =>
                {
                    _duckMix = targets[index].Id;
                    Duck(link, "mixes", Flip(mixes, targets[index].Id));
                }));
        }

        string[] levels = ["low", "medium", "high"], speeds = ["slow", "medium", "fast"];
        rows.Add(PresetRow(link, "Sensitivity", "sensitivity", levels, ["low, -3 dBFS", "medium, -12 dBFS", "high, -24 dBFS"],
            DuckingEntry.Level(ducking.Sensitivity, levels, [-3, -12, -24], "dBFS")));
        rows.Add(PresetRow(link, "Amount", "amount", levels, ["low, 6 dB", "medium, 12 dB", "high, 24 dB"],
            DuckingEntry.Level(ducking.Amount, levels, [6, 12, 24], "dB")));
        rows.Add(PresetRow(link, "Speed", "speed", speeds, speeds,
            DuckingEntry.Times(ducking.Speed, speeds, [(100, 2000), (80, 1200), (30, 200)])));

        // The choice of where it runs is offered only on a unit that can run it.
        if (ducking.Device is not null && ducking.Processing is "device" or "software")
        {
            rows.Add(new ChoiceRow("Runs on", ["this unit", "this computer"], ducking.Processing == "software" ? 1 : 0,
                index => link.Send("setDuckingProcessing", body => body["value"] = index == 1 ? "software" : "device")));
            rows.Add(new TextRow("Key heard", ducking.KeyHeard));
            if (ducking.Why is { Length: > 0 } why) rows.Add(new TextRow("Not on unit", why));
        }
        // The daemon's sentence names the fix, so it is wrapped rather than cut at the card's edge.
        if (ducking.Error is { Length: > 0 } error)
            foreach ((string line, int at) in Wrap(error, 40).Select((line, at) => (line, at)))
                rows.Add(new TextRow(at == 0 ? "Software" : string.Empty, line));
        if (ducking.Backend == "software")
        {
            double reduction = link.DuckReduction();
            rows.Add(new BarRow("Lowered by", reduction / ducking.Depth, $"{reduction:0} dB"));
        }
    }

    /// <summary>Text broken at spaces into lines of at most this many characters, a longer word on its own.</summary>
    private static List<string> Wrap(string text, int width)
    {
        List<string> lines = [];
        string line = string.Empty;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line);
                line = string.Empty;
            }
            line = line.Length == 0 ? word : $"{line} {word}";
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    /// <summary>The list with the id added at its end, or taken out.</summary>
    private static JsonArray Flip(List<string> ids, string id) =>
        Ids(ids.Contains(id) ? ids.Where(other => other != id) : ids.Append(id));

    private static JsonArray Ids(IEnumerable<string> ids) => new(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());

    /// <summary>One field of the ducking setup; the daemon keeps the others.</summary>
    // The three presets, and a custom value the daemon holds (one the UI's
    // sliders set) as a fourth choice that sends nothing: moving off it
    // picks a preset.
    private static ChoiceRow PresetRow(DaemonLink link, string label, string field, string[] words, string[] names,
        (int Preset, string? Custom) value)
    {
        string[] options = value.Custom is null ? names : [.. names, $"custom, {value.Custom}"];
        int selected = value.Custom is null ? Math.Max(0, value.Preset) : names.Length;
        return new ChoiceRow(label, options, selected, index =>
        {
            if (index < words.Length) Duck(link, field, words[index]);
        });
    }

    private static void Duck(DaemonLink link, string field, JsonNode? value) =>
        link.Send("setDucking", body => body["value"] = new JsonObject { [field] = value });

    private static void Set(DaemonLink link, string control, bool value) =>
        link.Send("set", body => { body["control"] = control; body["value"] = value; });

    private static void Set(DaemonLink link, string control, int value) =>
        link.Send("set", body => { body["control"] = control; body["value"] = value; });

    private static void Set(DaemonLink link, string control, double value) =>
        link.Send("set", body => { body["control"] = control; body["value"] = Math.Round(value, 2); });
}
