using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// The window's model of the input bindings, without a window: which channel
/// draws the interface's input controls as <c>unitInput</c> moves, what each
/// binding chip says, the heading and the crossfade line while the
/// interface's microphone is elsewhere or nowhere, the header's link, and the
/// parts the input rack and the mix matrix draw for each case.
/// </summary>
public sealed class InputBindingModelTests
{
    internal const string Unit = "0fd9:00b4@3f1c0000aaaa0000";
    internal const string ProNode = "alsa_input.usb-Elgato_Elgato_Wave_XLR_Pro_SERIAL-00.pro-input-0";

    private static void Apply(MainViewModel model, string state)
        => typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [JsonNode.Parse(state)!]);

    /// <summary>A Wave XLR Pro's state with the given channels, devices and mixer flags.</summary>
    /// <remarks>
    /// Without a window the fixture holds no mix: a mix loads its plugin
    /// catalogue, and a client with no daemon posts that failure to the
    /// dispatcher, which would then belong to the test's thread and refuse
    /// every other test's controls. The window test asks for the mix.
    /// </remarks>
    internal static string ProState(string channels, bool connected = true, string mixerExtra = "", string detected = "", bool withMix = false)
    {
        string device = connected
            ? "\"connected\":true,\"device\":{\"vendor\":\"Elgato\",\"model\":\"Wave XLR Pro\"},"
              + "\"capabilities\":{\"xlrInputs\":2,\"hpOutputs\":2,\"lowCut\":true,\"expander\":true,\"voiceTune\":true,\"phantom\":true,"
              + "\"clipGuard\":true,\"compressor\":true,\"crossfade\":true,\"outputRouting\":true,\"physicalControls\":true},"
              + "\"detected\":[{\"usbId\":\"" + Unit + "\",\"name\":\"Wave XLR Pro\",\"active\":true}],"
              + "\"waveInterfaces\":[{\"id\":\"" + Unit + "\",\"name\":\"Wave XLR Pro\",\"active\":true,\"connected\":true,"
              + "\"captureHint\":\"Wave_XLR_Pro_SERIAL-\"}],"
            : "\"connected\":false,\"detected\":[" + detected + "],";
        return "{" + device
            + "\"devices\":[{\"name\":\"cam\",\"description\":\"Webcam\",\"kind\":1,\"channels\":2,\"positions\":[\"FL\",\"FR\"]},"
            + "{\"name\":\"" + ProNode + "\",\"description\":\"Wave XLR Pro\",\"kind\":1,\"channels\":6,\"drivenUnit\":\"" + Unit + "\"}],"
            + "\"mixer\":{\"mixes\":[" + (withMix ? "{\"id\":\"monitor\",\"name\":\"Monitor A\",\"kind\":\"monitor\"}" : "") + "],\"inserts\":{}" + mixerExtra
            + ",\"channels\":[" + channels + "]}}";
    }

    /// <summary>XLR 1 or XLR 2 on the interface's pair, carrying the given unit input or none.</summary>
    internal static string Xlr(string id, int pair, string? unitInput)
        => "{\"id\":\"" + id + "\",\"name\":\"" + (id == "xlr1" ? "XLR 1" : "XLR 2") + "\",\"hardware\":true,\"present\":true,"
            + "\"levels\":{\"monitor\":1},\"binding\":{\"kind\":\"unit\",\"pair\":" + pair + "},\"bindingConnected\":true"
            + (unitInput is null ? "" : ",\"unitInput\":" + unitInput) + "}";

    /// <summary>XLR 1 bound to the webcam's second port.</summary>
    internal static string BoundXlr1(bool connected, string? warning = null)
        => "{\"id\":\"xlr1\",\"name\":\"Mic 1\",\"hardware\":true,\"present\":true,\"levels\":{\"monitor\":1},"
            + "\"binding\":{\"kind\":\"source\",\"source\":\"cam\",\"pair\":0,\"monoChannel\":1},\"bindingConnected\":"
            + (connected ? "true" : "false") + (warning is null ? "" : ",\"bindingWarning\":\"" + warning + "\"") + "}";

    /// <summary>The primary unit's input as <c>unitInput</c> spells it.</summary>
    internal static string Input(int input) => "{\"unit\":\"" + Unit + "\",\"input\":" + input + ",\"primary\":true}";

    /// <summary>A capture channel on the Pro's own node at its first port.</summary>
    internal static string ProMic(string? unitInput)
        => "{\"id\":\"promic\",\"name\":\"Pro mic\",\"levels\":{\"monitor\":1},\"captureSource\":\"" + ProNode + "\","
            + "\"capturePair\":0,\"captureMonoChannel\":0,\"captureConnected\":true,\"volume\":0.8,"
            + "\"binding\":{\"kind\":\"source\",\"source\":\"" + ProNode + "\",\"pair\":0,\"monoChannel\":0},\"bindingConnected\":true"
            + (unitInput is null ? "" : ",\"unitInput\":" + unitInput) + "}";

    [Fact]
    public async Task TheInterfaceControlsFollowTheChannelThatCarriesItsMicrophone()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        using var rack = new InputRackViewModel(model);
        using var matrix = new MixMatrixViewModel(model);
        InputStripViewModel RackStrip(string id) => rack.Strips.Single(s => s.Channel.Id == id);
        MatrixRowViewModel Row(string id) => matrix.Rows.Single(r => r.Channel.Id == id);

        // Both strips on the interface: XLR 1 carries input 1, XLR 2 input 2, as always.
        Apply(model, ProState($"{Xlr("xlr1", 0, Input(1))},{Xlr("xlr2", 1, Input(2))}"));
        ChannelViewModel xlr1 = model.Channels.Single(c => c.Id == "xlr1"), xlr2 = model.Channels.Single(c => c.Id == "xlr2");
        Assert.True(model.DeviceConnected);
        Assert.Equal(1, xlr1.ShownUnitInput);
        Assert.Equal(2, xlr2.ShownUnitInput);
        Assert.Equal(UnitInputPlacement.OnStrip, model.UnitInput1.Placement);
        Assert.Equal(Localizer.Text("XLR1Gain"), model.UnitInput1.GainLabel);
        Assert.Equal(Localizer.Text("Xlr1Jack"), xlr1.BindingLabel);
        Assert.Equal(Localizer.Text("Xlr2Jack"), xlr2.BindingLabel);
        Assert.True(xlr1.IsUnitBound && !xlr1.IsSourceBound);
        Assert.Equal([xlr1, xlr2], model.BindableStrips);
        Assert.Equal("", model.CrossfadeNote);
        Assert.False(model.ShowChooseMicrophone);
        Assert.Equal(model.DeviceName, model.StatusLine);
        Assert.Equal([SkinStripParts.Identity, SkinStripParts.Binding, SkinStripParts.Meter, SkinStripParts.Gain,
            SkinStripParts.HardwareControls, SkinStripParts.Effects], RackStrip("xlr1").Spec.Parts);
        Assert.Same(model.UnitInput1, RackStrip("xlr1").Unit);
        Assert.Contains(SkinStripParts.Gain, Row("xlr1").Spec.Parts);
        Assert.Contains(SkinStripParts.Mute, Row("xlr1").Spec.Parts);
        Assert.True(Row("xlr1").ShowsBinding);

        // XLR 1 bound to a webcam's second port, offline, and the Pro's own
        // microphone kept as a capture channel, which the daemon says carries input 1.
        Apply(model, ProState($"{BoundXlr1(false, "Webcam is not connected.")},{Xlr("xlr2", 1, Input(2))},{ProMic(Input(1))}",
            mixerExtra: ",\"softLowCutApplicable\":true,\"softClipGuardApplicable\":true"));
        ChannelViewModel proMic = model.Channels.Single(c => c.Id == "promic");
        Assert.True(model.DeviceConnected);   // the header LED still means the interface
        Assert.True(xlr1.IsSourceBound);
        Assert.False(xlr1.BindingConnected);
        Assert.Equal("Webcam is not connected.", xlr1.BindingWarning);
        Assert.Equal(Localizer.Format("InputBindingSourcePort", "Webcam", 2), xlr1.BindingLabel);
        Assert.Contains("Webcam is not connected.", xlr1.BindingTip);
        Assert.Equal(0, xlr1.ShownUnitInput);
        Assert.Equal(1, proMic.ShownUnitInput);
        Assert.Equal(UnitInputPlacement.OnChannel, model.UnitInput1.Placement);
        Assert.Same(proMic, model.UnitInput1.Carrier);
        Assert.True(model.UnitInput1.ControlsShown);
        Assert.Equal(3, model.UnitInput1.BlockRow);
        Assert.Equal(Localizer.Format("UnitInputPlaysOn", model.DeviceName, 1, "Pro mic"), model.UnitInput1.Heading);
        Assert.Equal(Localizer.Text("UnitInputGain"), model.UnitInput1.GainLabel);
        Assert.Equal(Localizer.Format("CrossfadeMicPlaysOn", "Pro mic"), model.CrossfadeNote);
        Assert.True(model.ShowSoftLowCut);
        Assert.True(model.ShowSoftClipGuard);
        // XLR 1 keeps its binding, its meter, its software low cut and ClipGuard and its chain; the gain went.
        Assert.Equal([SkinStripParts.Identity, SkinStripParts.Binding, SkinStripParts.Meter,
            SkinStripParts.HardwareControls, SkinStripParts.Effects], RackStrip("xlr1").Spec.Parts);
        Assert.False(RackStrip("xlr1").CarriesUnit);
        Assert.DoesNotContain(SkinStripParts.Gain, Row("xlr1").Spec.Parts);
        Assert.DoesNotContain(SkinStripParts.Mute, Row("xlr1").Spec.Parts);
        Assert.Contains(SkinStripParts.Meter, Row("xlr1").Spec.Parts);
        // The capture channel draws the interface's gain and switches, and keeps its own volume in the rack.
        Assert.Equal([SkinStripParts.Identity, SkinStripParts.Binding, SkinStripParts.Meter, SkinStripParts.Gain, SkinStripParts.HardwareControls],
            RackStrip("promic").Spec.Parts);
        Assert.Same(model.UnitInput1, RackStrip("promic").Unit);
        Assert.True(RackStrip("promic").ShowsVolume);
        Assert.Contains(SkinStripParts.Gain, Row("promic").Spec.Parts);
        Assert.Contains(SkinStripParts.Mute, Row("promic").Spec.Parts);
        Assert.True(Row("promic").ShowsBinding);
        // Its controls are the window's own for input 1, read from the state
        // as it arrives. Nothing here is written: a write would send a command,
        // and a client with no daemon posts its error to the dispatcher, which
        // would then belong to this test's thread. The window test moves them.
        var raised = new List<string?>();
        model.UnitInput1.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        model.UnitInput2.PropertyChanged += (_, e) => raised.Add("2:" + e.PropertyName);
        Apply(model, ProState($"{BoundXlr1(false, "Webcam is not connected.")},{Xlr("xlr2", 1, Input(2))},{ProMic(Input(1))}",
            mixerExtra: ",\"softLowCutApplicable\":true,\"softClipGuardApplicable\":true")
            .Replace("\"connected\":true,", "\"connected\":true,\"state\":{\"gainDb\":41,\"phantom\":true,\"mute2\":true},", StringComparison.Ordinal));
        Assert.Equal(41, RackStrip("promic").Unit!.GainDb);
        Assert.Equal("41 dB", RackStrip("promic").Unit!.GainText);
        Assert.True(RackStrip("promic").Unit!.Phantom);
        Assert.False(model.UnitInput2.Phantom);
        Assert.True(model.UnitInput2.Mute);
        Assert.Contains(nameof(UnitInputViewModel.GainDb), raised);
        Assert.Contains(nameof(UnitInputViewModel.Phantom), raised);
        Assert.Contains("2:" + nameof(UnitInputViewModel.Mute), raised);
        Assert.DoesNotContain("2:" + nameof(UnitInputViewModel.Phantom), raised);
        FlowGraph flow = FlowGraph.From(model);
        Assert.Contains(flow.Nodes, n => n.Key == "capture:xlr1" && n.Label == xlr1.BindingLabel
            && n.Detail == "Webcam is not connected." && !n.Active);
        Assert.DoesNotContain(flow.Nodes, n => n.Key == "hw:xlr1");
        Assert.Contains(flow.Nodes, n => n.Key == "hw:xlr2");

        // The capture channel goes: the interface's microphone is in no channel, its controls fold under a heading.
        Apply(model, ProState($"{BoundXlr1(true)},{Xlr("xlr2", 1, Input(2))}",
            mixerExtra: ",\"softLowCutApplicable\":true,\"softClipGuardApplicable\":true"));
        Assert.Equal(UnitInputPlacement.NotInMixer, model.UnitInput1.Placement);
        Assert.Null(model.UnitInput1.Carrier);
        Assert.True(model.UnitInput1.CanFold);
        Assert.False(model.UnitInput1.Expanded);
        Assert.False(model.UnitInput1.ControlsShown);
        model.UnitInput1.Expanded = true;
        Assert.True(model.UnitInput1.ControlsShown);
        Assert.Equal(Localizer.Format("UnitInputNotInMixer", model.DeviceName, 1), model.UnitInput1.Heading);
        Assert.Equal(Localizer.Text("CrossfadeMicNotInMixer"), model.CrossfadeNote);
        Assert.All(model.Channels, c => Assert.NotEqual(1, c.ShownUnitInput));
        Assert.Equal(2, xlr2.ShownUnitInput);
        Assert.True(xlr1.BindingConnected);
        Assert.Null(xlr1.BindingWarning);

        // Back on the jack: the controls come back to XLR 1, and XLR 2's never moved.
        Apply(model, ProState($"{Xlr("xlr1", 0, Input(1))},{Xlr("xlr2", 1, Input(2))}"));
        Assert.Equal(UnitInputPlacement.OnStrip, model.UnitInput1.Placement);
        Assert.Equal(1, xlr1.ShownUnitInput);
        Assert.Contains(SkinStripParts.Gain, RackStrip("xlr1").Spec.Parts);
        Assert.Equal("", model.CrossfadeNote);
    }

    [Fact]
    public async Task WithoutAnInterfaceTheHeaderOffersTheBindingAndTheStripsStandForTheirInputs()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);

        // Nothing attached and XLR 1 waiting for the interface: the link, and
        // XLR 1 still stands for the input it would carry.
        Apply(model, ProState(Xlr("xlr1", 0, null), connected: false));
        Assert.False(model.DeviceConnected);
        Assert.True(model.ShowChooseMicrophone);
        Assert.False(model.ShowDeviceStatus);
        Assert.Equal(1, model.Channels.Single().ShownUnitInput);
        Assert.Equal(UnitInputPlacement.OnStrip, model.UnitInput1.Placement);

        // A supported unit attached but not driven yet: the header waits for it.
        Apply(model, ProState(Xlr("xlr1", 0, null), connected: false,
            detected: $$"""{"usbId":"{{Unit}}","name":"Wave XLR Pro","active":true}"""));
        Assert.False(model.ShowChooseMicrophone);
        Assert.True(model.ShowDeviceStatus);

        // XLR 1 bound to the webcam: no link, and no channel carries the interface's input.
        Apply(model, ProState(BoundXlr1(true), connected: false));
        Assert.False(model.ShowChooseMicrophone);
        Assert.Equal(Localizer.Text("NoDevice"), model.StatusLine);
        Assert.Equal(UnitInputPlacement.NotInMixer, model.UnitInput1.Placement);
        Assert.Equal(Localizer.Format("UnitInputNotInMixer", Localizer.Text("TheInterface"), 1), model.UnitInput1.Heading);

        // No mixer: the panel draws the interface's controls as it always has.
        Apply(model, """{"connected":true,"capabilities":{"xlrInputs":2}}""");
        Assert.False(model.HasMixer);
        Assert.Empty(model.BindableStrips);
        Assert.Equal(UnitInputPlacement.OnStrip, model.UnitInput1.Placement);
        Assert.Equal(UnitInputPlacement.OnStrip, model.UnitInput2.Placement);
        Assert.True(model.UnitInput1.ControlsShown);
    }

    [Fact]
    public async Task XlrTwoBoundToASourceGivesAOneInputInterfaceASecondStrip()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        using var rack = new InputRackViewModel(model);
        using var matrix = new MixMatrixViewModel(model);
        string xlr2Bound = """
            {"id":"xlr2","name":"XLR 2","hardware":true,"present":true,"levels":{"monitor":1},
             "binding":{"kind":"source","source":"cam","pair":0,"monoChannel":0},"bindingConnected":true}
            """;
        Apply(model, ProState($"{Xlr("xlr1", 0, Input(1))},{xlr2Bound}").Replace("\"xlrInputs\":2", "\"xlrInputs\":1"));
        Assert.False(model.HasXlr2);
        Assert.True(model.ShowXlr2Section);
        InputStripViewModel strip = rack.Strips.Single(s => s.Channel.Id == "xlr2");
        Assert.True(strip.Shown);
        Assert.False(strip.CarriesUnit);
        Assert.DoesNotContain(SkinStripParts.Gain, strip.Spec.Parts);
        Assert.Contains(SkinStripParts.Binding, strip.Spec.Parts);
        Assert.Contains(matrix.Rows, r => r.Channel.Id == "xlr2");
        Assert.Equal(Localizer.Format("InputBindingSourcePort", "Webcam", 1), strip.Channel.BindingLabel);

        // On the jack again, a one-input unit has no XLR 2 to show.
        Apply(model, ProState($"{Xlr("xlr1", 0, Input(1))},{Xlr("xlr2", 1, null)}").Replace("\"xlrInputs\":2", "\"xlrInputs\":1"));
        Assert.False(model.ShowXlr2Section);
        Assert.False(rack.Strips.Single(s => s.Channel.Id == "xlr2").Shown);
        Assert.DoesNotContain(matrix.Rows, r => r.Channel.Id == "xlr2");
    }

    [Fact]
    public async Task AUsbMicrophonesCapsuleIsNoJack()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        // A Wave:3: one input, its built-in capsule, so the daemon names the strip Mic 1.
        Apply(model, ProState(Xlr("xlr1", 0, Input(1)))
            .Replace("\"xlrInputs\":2", "\"xlrInputs\":1,\"xlrJacks\":false", StringComparison.Ordinal)
            .Replace("\"name\":\"XLR 1\"", "\"name\":\"Mic 1\"", StringComparison.Ordinal));
        ChannelViewModel strip = model.Channels.Single();
        Assert.False(model.UnitXlrJacks);
        Assert.Equal("Mic 1", strip.Name);
        Assert.Equal(1, strip.ShownUnitInput);
        Assert.Equal(Localizer.Text("UnitCapsule"), strip.BindingLabel);
        Assert.Equal(Localizer.Text("Mic1Gain"), model.UnitInput1.GainName);
        Assert.Equal(Localizer.Text("Mic1Gain"), model.UnitInput1.GainLabel);
        Assert.Equal(Localizer.Format("StripInputLabel", "Mic 1"), model.Xlr1InputLabel);
        Assert.Contains(FlowGraph.From(model).Nodes, n => n.Key == "hw:xlr1" && n.Label == Localizer.Text("UnitCapsule"));

        // A Pro again: the jack, the XLR name.
        Apply(model, ProState(Xlr("xlr1", 0, Input(1))));
        Assert.True(model.UnitXlrJacks);
        Assert.Equal(Localizer.Text("Xlr1Jack"), strip.BindingLabel);
        Assert.Equal(Localizer.Text("XLR1Gain"), model.UnitInput1.GainLabel);
        Assert.Equal(Localizer.Format("StripInputLabel", "XLR 1"), model.Xlr1InputLabel);
    }

    [Fact]
    public async Task AStripCarryingAnAdditionalUnitsInputDrawsThatUnitsControls()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        using var rack = new InputRackViewModel(model);
        using var matrix = new MixMatrixViewModel(model);
        Apply(model, InputBindingWindowTests.DrivenState(withMix: false));
        ChannelViewModel xlr2 = model.Channels.Single(c => c.Id == "xlr2");
        WaveInterfaceViewModel wave3 = model.WaveInterfaces.Single(w => !w.Active);
        // XLR 2 on the Wave:3: its controls come from the Wave:3's entry, not the primary's.
        Assert.Same(wave3.Input1, xlr2.ExtraUnitInput);
        Assert.Equal(0, xlr2.ShownUnitInput);
        Assert.Equal(25, xlr2.ExtraUnitInput!.GainDb);
        Assert.Equal(40, xlr2.ExtraUnitInput.GainMaxDb);
        Assert.True(xlr2.ExtraUnitInput.HasClipGuard);
        Assert.False(xlr2.ExtraUnitInput.HasPhantom);
        Assert.Equal([wave3.Input1], wave3.Inputs);
        Assert.Same(wave3.Inputs, wave3.Inputs);   // one list while the count stays, so a state push draws nothing again
        InputStripViewModel strip = rack.Strips.Single(s => s.Channel.Id == "xlr2");
        Assert.Same(xlr2.ExtraUnitInput, strip.ExtraUnit);
        Assert.Null(strip.Unit);
        Assert.Contains(SkinStripParts.Gain, strip.Spec.Parts);
        Assert.Contains(SkinStripParts.HardwareControls, strip.Spec.Parts);
        MatrixRowViewModel row = matrix.Rows.Single(r => r.Channel.Id == "xlr2");
        Assert.True(row.CarriesExtraUnit);
        Assert.Contains(SkinStripParts.Gain, row.Spec.Parts);
        Assert.Contains(SkinStripParts.Mute, row.Spec.Parts);
        // The Pro's own second input stays on the capture channel kept for it.
        Assert.Equal(2, model.Channels.Single(c => c.Id == "promic2").ShownUnitInput);

        // The unit no longer driven: the binding stays, the controls go.
        Apply(model, InputBindingWindowTests.DrivenState(enabled: false, withMix: false));
        Assert.Null(xlr2.ExtraUnitInput);
        Assert.True(xlr2.IsSourceBound);
        Assert.Equal("0fd9:0070@00000000000000c3", xlr2.Binding!.Unit);
        Assert.DoesNotContain(SkinStripParts.Gain, strip.Spec.Parts);
        Assert.DoesNotContain(SkinStripParts.HardwareControls, strip.Spec.Parts);
        Assert.False(row.CarriesExtraUnit);
        Assert.DoesNotContain(SkinStripParts.Mute, row.Spec.Parts);
    }

    [Fact]
    public async Task ADaemonWithoutBindingsKeepsTheStripsOnTheInterface()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        Apply(model, """
            {"connected":true,"capabilities":{"xlrInputs":2,"crossfade":true},
             "mixer":{"mixes":[],"channels":[{"id":"xlr1","name":"XLR 1","hardware":true},{"id":"xlr2","name":"XLR 2","hardware":true}]}}
            """);
        Assert.All(model.Channels, c => Assert.Null(c.Binding));
        Assert.Equal([1, 2], model.Channels.Select(c => c.ShownUnitInput));
        Assert.True(model.UnitInput1.OnStrip);
        Assert.Equal("", model.CrossfadeNote);
        Assert.Equal(Localizer.Text("Xlr1Jack"), model.Channels[0].BindingLabel);
    }

    [Fact]
    public async Task AUnitWithADialOffersTheGainLockOnlyToReleaseIt()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        JsonNode state = JsonNode.Parse(ProState($"{Xlr("xlr1", 0, Input(1))},{Xlr("xlr2", 1, Input(2))}"))!;
        void Report(bool locked)
        {
            state["state"] = new JsonObject { ["gainLocked"] = locked };
            Apply(model, state.ToJsonString());
        }

        // Unlocked: the dial would walk past a lock, so nothing offers one, and a write from a control is refused.
        Report(false);
        Assert.True(model.DeviceConnected);
        Assert.False(model.ShowGainLock);
        Assert.False(model.UnitInput1.CanSetGainLock);
        Assert.False(model.UnitInput2.CanSetGainLock);
        model.UnitInput1.GainLocked = true;
        Assert.False(model.GainLocked);

        // Locked from a Deck key, the terminal mixer or the API: both inputs show the lock so it can be released.
        Report(true);
        Assert.False(model.ShowGainLock);
        Assert.True(model.UnitInput1.GainLocked);
        Assert.True(model.UnitInput1.CanSetGainLock);
        Assert.True(model.UnitInput2.CanSetGainLock);

        // Released from either input, it is gone again.
        model.UnitInput2.GainLocked = false;
        Assert.False(model.GainLocked);
        Assert.False(model.UnitInput1.CanSetGainLock);
        Assert.False(model.UnitInput2.CanSetGainLock);

        // A unit without a dial, an XLR Dock, always shows it.
        state["capabilities"]!["physicalControls"] = false;
        Report(false);
        Assert.True(model.ShowGainLock);
        Assert.True(model.UnitInput1.CanSetGainLock);
        model.UnitInput1.GainLocked = true;
        Assert.True(model.GainLocked);
    }
}
