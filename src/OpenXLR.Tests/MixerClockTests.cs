using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// The audio clock of the mixer's graph, read from pw-dump node properties:
/// which node drives, which of the mixer's devices follow it and which of
/// them are resampled; the registry revision that keeps an idle graph from
/// being read again; and what the UI and the diagnostics archive make of it.
/// The fixtures are trimmed from recorded dumps: an owner's archive where
/// an Elgato 4K X capture card drove a Wave XLR (issue #474), and the
/// maintainer's desk, where the Wave XLR Pro drives.
/// </summary>
public sealed class MixerClockTests
{
    private const string WaveXlrIn = "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_0123456789-00.mono-fallback";
    private const string WaveXlrOut = "alsa_output.usb-Elgato_Systems_Elgato_Wave_XLR_0123456789-00.analog-stereo";
    private const string FourKX = "alsa_input.usb-Elgato_Elgato_4K_X_ABCDEF-02.analog-stereo";
    private const string Buds = "alsa_output.usb-SteelSeries_Arctis_GameBuds-00.analog-stereo";
    private const string ProIn = "alsa_input.usb-Elgato_Elgato_Wave_XLR_Pro_SERIAL-00.pro-input-0";
    private const string ProOut = "alsa_output.usb-Elgato_Elgato_Wave_XLR_Pro_SERIAL-00.pro-output-0";
    private const string Brio = "alsa_input.usb-046d_Logi_4K_Stream_Edition_C971DF30-03.analog-stereo";
    private const string Katana = "alsa_output.usb-Creative_Technology_Ltd_Sound_BlasterX_Katana_DB58-00.analog-stereo";

    private static string Node(uint id, string name, string description, uint? driver, int? priority = null,
        int? device = null, string? clock = null, bool hardware = true, string state = "running")
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id, ["type"] = "PipeWire:Interface:Node",
            ["info"] = new Dictionary<string, object?>
            {
                ["state"] = state,
                ["props"] = new Dictionary<string, object?>
                {
                    ["node.name"] = name, ["node.description"] = description,
                    ["media.class"] = name.StartsWith("alsa_input", StringComparison.Ordinal) ? "Audio/Source" : "Audio/Sink",
                    ["node.driver-id"] = driver, ["priority.driver"] = priority, ["device.id"] = device,
                    ["clock.name"] = clock, ["device.api"] = hardware ? "alsa" : null,
                }.Where(p => p.Value is not null).ToDictionary(),
            },
        });

    private static JsonElement[] Dump(params string[] nodes)
    {
        using JsonDocument doc = JsonDocument.Parse("[" + string.Join(",", nodes) + "]");
        return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    /// <summary>The broken archive of #474: the 4K X drives, the Wave XLR follows it from another card.</summary>
    private static JsonElement[] CaptureCardDrives() => Dump(
        Node(43, FourKX, "Elgato 4K X Analog Stereo", null, 2109, 53),
        Node(74, Buds, "Arctis GameBuds Analog Stereo", 43, 1109, 75),
        Node(93, "OpenXLR_mix_monitor", "OpenXLR Monitor A", 43, hardware: false),
        Node(99, "OpenXLR_mix_monitor2", "OpenXLR Monitor B", 43, hardware: false),
        Node(476, WaveXlrOut, "Elgato Wave XLR Analog Stereo", null, 1109, 469, state: "suspended"),
        Node(477, WaveXlrIn, "Elgato Wave XLR Mono", 43, 2100, 469));

    /// <summary>The same machine after the clock rule: the Wave XLR drives, the 4K X follows.</summary>
    private static JsonElement[] UnitDrives() => Dump(
        Node(43, FourKX, "Elgato 4K X Analog Stereo", 477, 2109, 53),
        Node(74, Buds, "Arctis GameBuds Analog Stereo", 477, 1109, 75),
        Node(93, "OpenXLR_mix_monitor", "OpenXLR Monitor A", 477, hardware: false),
        Node(477, WaveXlrIn, "Elgato Wave XLR Mono", 477, 2200, 469));

    /// <summary>The desk: the Pro drives; its playback shares its card, the webcam and the Katana do not.</summary>
    private static JsonElement[] ProDrives() => Dump(
        Node(70, "OpenXLR_mix_monitor", "OpenXLR Monitor A", 686, hardware: false),
        Node(76, "OpenXLR_mix_monitor2", "OpenXLR Monitor B", 686, hardware: false),
        Node(623, Brio, "BRIO 4K Stream Edition Analog Stereo", 686, 2109, 621),
        Node(649, Katana, "Sound BlasterX Katana Analog Stereo", 686, 1109, 658),
        Node(684, ProOut, "Wave XLR Pro (raw, driven by OpenXLR)", 686, 1600, 622, "api.alsa.4"),
        Node(686, ProIn, "Wave XLR Pro mic (raw, driven by OpenXLR)", null, 2600, 622, "api.alsa.4"));

    private static readonly ClockCandidate[] WaveXlrMixer =
    [
        new(WaveXlrIn, "unitInput", ["xlr1"], true),
        new(FourKX, "capture", ["console"], false),
        new(Buds, "output", [], false),
        new(WaveXlrOut, "unitOutput", [], false),
    ];

    [Fact]
    public void ACaptureCardThatDrivesLeavesTheMicrophoneResampled()
    {
        MixerClock clock = Assert.IsType<MixerClock>(MixerClock.Read(CaptureCardDrives(), WaveXlrMixer));
        Assert.Equal(new ClockDriver(FourKX, "Elgato 4K X Analog Stereo", 2109) { Role = "capture" }, clock.Driver);
        // The Wave XLR's suspended playback runs in no graph and is left out.
        Assert.Equal([WaveXlrIn, Buds], clock.Followers.Select(f => f.Name));
        ClockFollower mic = clock.Followers[0];
        Assert.True(mic.Microphone && mic.Resampled);
        Assert.Equal(["xlr1"], mic.Channels);
        Assert.Equal("unitInput", mic.Role);
        Assert.Equal(2100, mic.PriorityDriver);
        Assert.True(clock.Followers[1].Resampled);
        Assert.True(clock.MicResampled);
        Assert.Equal("Elgato Wave XLR Mono follows the audio clock of Elgato 4K X Analog Stereo, so PipeWire resamples the microphone; if it sounds garbled or robotic, see the manual.",
            clock.Warning());
    }

    [Fact]
    public void TheUnitAsTheClockResamplesTheCaptureCardInstead()
    {
        MixerClock clock = Assert.IsType<MixerClock>(MixerClock.Read(UnitDrives(), WaveXlrMixer));
        Assert.Equal(WaveXlrIn, clock.Driver!.Name);
        Assert.Equal("unitInput", clock.Driver.Role);
        Assert.Equal(2200, clock.Driver.PriorityDriver);
        Assert.Equal([FourKX, Buds], clock.Followers.Select(f => f.Name));
        Assert.All(clock.Followers, f => Assert.True(f.Resampled));
        Assert.False(clock.MicResampled);
        Assert.Null(clock.Warning());
    }

    [Fact]
    public void NodesOfTheDriversCardShareItsClock()
    {
        ClockCandidate[] desk =
        [
            new(ProIn, "unitInput", ["xlr1", "xlr2", "aux", "line"], true),
            new(Brio, "capture", ["webcam"], false),
            new(ProOut, "unitOutput", [], false),
            new(Katana, "output", [], false),
        ];
        MixerClock clock = Assert.IsType<MixerClock>(MixerClock.Read(ProDrives(), desk));
        Assert.Equal(ProIn, clock.Driver!.Name);
        Assert.Equal(2600, clock.Driver.PriorityDriver);
        Assert.Equal([(Brio, true), (ProOut, false), (Katana, true)], clock.Followers.Select(f => (f.Name, f.Resampled)));
        Assert.False(clock.MicResampled);

        // A webcam used as a microphone is a microphone on another clock.
        ClockCandidate[] webcamMic = [.. desk.Select(d => d.Node == Brio ? d with { Microphone = true } : d)];
        MixerClock withMic = MixerClock.Read(ProDrives(), webcamMic)!;
        Assert.True(withMic.MicResampled);
        Assert.StartsWith("BRIO 4K Stream Edition Analog Stereo follows the audio clock of Wave XLR Pro mic", withMic.Warning());
    }

    [Fact]
    public void WithoutRunningMixesTheDevicesNameTheDriver()
    {
        // Nothing plays into the mixes yet; the microphone already runs.
        JsonElement[] dump = Dump(
            Node(43, FourKX, "Elgato 4K X Analog Stereo", 43, 2109, 53),
            Node(93, "OpenXLR_mix_monitor", "OpenXLR Monitor A", null, hardware: false, state: "suspended"),
            Node(477, WaveXlrIn, "Elgato Wave XLR Mono", 43, 2100, 469));
        MixerClock clock = MixerClock.Read(dump, WaveXlrMixer)!;
        Assert.Equal(FourKX, clock.Driver!.Name);
        Assert.True(clock.MicResampled);
    }

    [Fact]
    public void NoDriverNoClock()
    {
        JsonElement[] idle = Dump(
            Node(93, "OpenXLR_mix_monitor", "OpenXLR Monitor A", null, hardware: false, state: "suspended"),
            Node(477, WaveXlrIn, "Elgato Wave XLR Mono", null, 2100, 469, state: "suspended"));
        Assert.Null(MixerClock.Read(idle, WaveXlrMixer));
        Assert.Null(MixerClock.Read([], WaveXlrMixer));
        // A driver id that names no node in the registry is no driver.
        Assert.Null(MixerClock.Read(Dump(Node(93, "OpenXLR_mix_monitor", "OpenXLR Monitor A", 12, hardware: false)), WaveXlrMixer));
    }

    [Fact]
    public void AnInvalidRegistryIdDoesNotHideTheValidClock()
    {
        JsonElement[] invalid = Dump("""
            {"id":"invalid","type":"PipeWire:Interface:Node","info":{"props":{"node.name":"invalid"}}}
            """);
        JsonElement[] valid = CaptureCardDrives();
        Assert.Equal(MixerClock.Read(valid, WaveXlrMixer), MixerClock.Read([.. invalid, .. valid], WaveXlrMixer));
    }

    [Fact]
    public void WithoutDeviceIdsTheCardInTheNameDecides()
    {
        JsonElement[] dump = Dump(
            Node(1, ProIn, "Pro mic", null, 2600),
            Node(2, ProOut, "Pro out", 1, 1600),
            Node(3, Katana, "Katana", 1, 1109),
            Node(4, "OpenXLR_mix_monitor", "Monitor A", 1, hardware: false));
        MixerClock clock = MixerClock.Read(dump, [new(ProIn, "unitInput", ["xlr1"], true), new(ProOut, "unitOutput", [], false), new(Katana, "output", [], false)])!;
        Assert.Equal([(ProOut, false), (Katana, true)], clock.Followers.Select(f => (f.Name, f.Resampled)));
    }

    [Fact]
    public void TwoReadsOfOneGraphAreEqual()
    {
        MixerClock first = MixerClock.Read(CaptureCardDrives(), WaveXlrMixer)!;
        MixerClock second = MixerClock.Read(CaptureCardDrives(), [.. WaveXlrMixer.Select(d => d with { Channels = [.. d.Channels] })])!;
        Assert.Equal(first, second);
        Assert.NotEqual(first, MixerClock.Read(UnitDrives(), WaveXlrMixer));
    }

    [Fact]
    public void TheStateCarriesTheClockInCamelCase()
    {
        MixerClock clock = MixerClock.Read(CaptureCardDrives(), WaveXlrMixer)!;
        JsonNode json = JsonNode.Parse(JsonSerializer.Serialize(clock, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        Assert.Equal(FourKX, json["driver"]!["name"]!.GetValue<string>());
        Assert.Equal(2109, json["driver"]!["priorityDriver"]!.GetValue<int>());
        Assert.True(json["micResampled"]!.GetValue<bool>());
        Assert.True(json["followers"]![0]!["resampled"]!.GetValue<bool>());
        Assert.Equal("xlr1", json["followers"]![0]!["channels"]![0]!.GetValue<string>());
    }

    /// <summary>
    /// The registry's clock revision moves for what can hand the graph to
    /// another driver (a node's state or clock properties, a link arriving
    /// or leaving) and not for the params and latency updates a running
    /// graph sends all the time, so a busy but unchanged graph schedules no
    /// pw-dump.
    /// </summary>
    [Fact]
    public void OnlyWhatCanMoveTheClockMovesTheClockRevision()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-graph-clock-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, "watch");
        ExecutableScript.Write(script, """
            #!/bin/sh
            exec python3 - "$(dirname "$0")/go" <<'PY'
            import json, os, sys, time
            def node(state, driver, latency):
                return {'id': 477, 'type': 'PipeWire:Interface:Node', 'info': {'state': state,
                        'props': {'node.name': 'mic', 'node.driver-id': driver, 'priority.driver': 2100, 'node.latency': latency}}}
            link = {'id': 600, 'type': 'PipeWire:Interface:Link', 'info': {'state': 'active'}}
            print(json.dumps([{'id': 0, 'type': 'PipeWire:Interface:Core'}, node('idle', 43, '256/48000')]), flush=True)
            steps = [[node('idle', 43, '512/48000')], [node('running', 43, '512/48000')], [node('running', 477, '512/48000')],
                     [link], [dict(link, info={'state': 'paused'})], [{'id': 600, 'info': None}]]
            for step, batch in enumerate(steps):
                while not os.path.exists(sys.argv[1] + str(step)): time.sleep(0.02)
                print(json.dumps(batch), flush=True)
            while True: time.sleep(1)
            PY
            """);
        try
        {
            using var graph = new PipeWireGraph(executable: script);
            Assert.True(graph.WaitFor(_ => true, TimeSpan.FromSeconds(10)));
            int step = 0;
            long? Next(Func<JsonElement[], bool> arrived)
            {
                File.WriteAllText(Path.Combine(dir, "go" + step++), "");
                Assert.True(graph.WaitFor(arrived, TimeSpan.FromSeconds(10)));
                return graph.ClockRevision;
            }
            static JsonElement? Find(JsonElement[] objects, uint id) => objects.Cast<JsonElement?>().FirstOrDefault(e => e!.Value.GetProperty("id").GetUInt32() == id);
            static string? Prop(JsonElement[] objects, string key) => Find(objects, 477)?.GetProperty("info").GetProperty("props").GetProperty(key).ToString();
            long? start = graph.ClockRevision;
            Assert.NotNull(start);
            // A latency update is not a clock change.
            Assert.Equal(start, Next(o => Prop(o, "node.latency") == "512/48000"));
            // A state change, a new driver and a new link are.
            long? running = Next(o => Find(o, 477)?.GetProperty("info").GetProperty("state").GetString() == "running");
            Assert.NotEqual(start, running);
            long? moved = Next(o => Prop(o, "node.driver-id") == "477");
            Assert.NotEqual(running, moved);
            long? linked = Next(o => Find(o, 600) is not null);
            Assert.NotEqual(moved, linked);
            // A link's own state is not; its removal is.
            Assert.Equal(linked, Next(o => Find(o, 600)?.GetProperty("info").GetProperty("state").GetString() == "paused"));
            Assert.NotEqual(linked, Next(o => Find(o, 600) is null));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string StateWith(MixerClock? clock, string warning = "", string cardProfile = "")
    {
        string mixer = clock is null ? "" : ",\"clock\":" + JsonSerializer.Serialize(clock, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string state = InputBindingModelTests.ProState(InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1)), mixerExtra: mixer);
        JsonNode node = JsonNode.Parse(state)!;
        if (warning.Length > 0) node["warning"] = warning;
        if (cardProfile.Length > 0) node["cardProfile"] = JsonNode.Parse(cardProfile);
        return node.ToJsonString();
    }

    private static void Apply(MainViewModel model, string state)
        => typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [JsonNode.Parse(state)!]);

    [Fact]
    public async Task TheUiShowsTheClockAndOffersTheManualForAResampledMicrophone()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        MixerClock broken = MixerClock.Read(CaptureCardDrives(), WaveXlrMixer)!;
        Apply(model, StateWith(broken, broken.Warning()!));
        Assert.NotNull(model.Clock);
        Assert.Equal("Elgato 4K X Analog Stereo", model.Clock!.DriverName);
        Assert.True(model.ClockProblem);
        Assert.False(model.CardProfileProblem);
        Assert.Equal(broken.Warning(), model.DaemonWarning);
        // XLR 1's card in Flow says what it is resampled to.
        FlowNode jack = FlowGraph.From(model).Nodes.Single(n => n.Key == "hw:xlr1");
        Assert.Equal(Localizer.Format("FlowResampledTo", "Elgato 4K X Analog Stereo"), jack.Detail);

        // A card profile without a microphone input keeps the one button.
        Apply(model, StateWith(broken, "no input", """{"card":"c","profile":"output:analog-stereo","capture":false,"problem":"no input"}"""));
        Assert.True(model.CardProfileProblem);
        Assert.False(model.ClockProblem);

        // With the unit as the clock there is nothing to warn about.
        Apply(model, StateWith(MixerClock.Read(UnitDrives(), WaveXlrMixer)));
        Assert.False(model.ClockProblem);
        Assert.Equal("Elgato Wave XLR Mono", model.Clock!.DriverName);
        Assert.Equal(Localizer.Text("FlowHardware"), FlowGraph.From(model).Nodes.Single(n => n.Key == "hw:xlr1").Detail);

        // An older daemon sends no clock.
        Apply(model, StateWith(null));
        Assert.Null(model.Clock);
        Assert.False(model.ClockProblem);
    }

    [Fact]
    public void TheArchiveSaysWhichMicrophoneFollowsWhichDevice()
    {
        MixerClock broken = MixerClock.Read(CaptureCardDrives(), WaveXlrMixer)!;
        Assert.Equal(
        [
            $"audio clock (last read by the daemon): Elgato 4K X Analog Stereo drives the mixer's graph ({FourKX}, priority.driver 2109)",
            $"microphone Elgato Wave XLR Mono follows Elgato 4K X Analog Stereo (resampled), node {WaveXlrIn}",
            $"output Arctis GameBuds Analog Stereo follows Elgato 4K X Analog Stereo (resampled), node {Buds}",
        ], Diagnostics.ClockLines(StateWith(broken)));
        Assert.StartsWith("audio clock: not reported", Assert.Single(Diagnostics.ClockLines(StateWith(null))));
        Assert.StartsWith("audio clock: not reported", Assert.Single(Diagnostics.ClockLines(null)));
    }

    [Fact]
    public void TheManualLinkPointsAtAnAnchorTheManualHas()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "manual.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string manual = File.ReadAllText(Path.Combine(directory.FullName, "docs", "manual.md"));
        foreach (string link in new[] { MainWindow.GarbledMicrophoneManualLink, MainWindow.CardProfileManualLink })
            Assert.Contains($"<a name=\"{link[(link.IndexOf('#') + 1)..]}\"></a>", manual);
    }
}
