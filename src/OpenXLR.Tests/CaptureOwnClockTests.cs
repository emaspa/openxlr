using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// A capture input on its own clock (issue #474): the command and its
/// checks, the setting in mixer.json, the state, the clock readout that lists
/// the device as bridged rather than as a follower, and what the UI, the
/// diagnostics archive and the helper's arguments make of it. The bridge
/// itself is checked in native/tests/bridge.c, and on a private PipeWire
/// server in <see cref="MonitorVolumeIntegrationTests"/>.
/// </summary>
public sealed class CaptureOwnClockTests
{
    private const string Card = "alsa_input.usb-Elgato_Elgato_4K_X_ABCDEF-02.analog-stereo";
    private const string Mic = "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_0123456789-00.mono-fallback";

    [Theory]
    [InlineData("console", "true", null)]
    [InlineData("console", "false", null)]
    [InlineData("console", "1", "setCaptureOwnClock: value must be a boolean")]
    [InlineData("console", null, "setCaptureOwnClock: value must be a boolean")]
    [InlineData("music", "true", "setCaptureOwnClock: need a capture input channel")]
    [InlineData("xlr1", "true", "setCaptureOwnClock: need a capture input channel")]
    [InlineData(null, "true", "setCaptureOwnClock: need a capture input channel")]
    public void TheCommandNeedsACaptureInputAndABoolean(string? channel, string? value, string? error)
    {
        string fields = (channel is null ? "" : $",\"channel\":\"{channel}\"") + (value is null ? "" : $",\"value\":{value}");
        var command = JsonSerializer.Deserialize<Command>($"{{\"cmd\":\"setCaptureOwnClock\"{fields}}}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(error, CommandValidation.Check(command, new Layout(), _ => null));
    }

    [Fact]
    public void TheSettingIsSavedOnlyWhenOnAndReadBack()
    {
        var settings = new MixerSettings
        {
            UserChannels = [new("console", "Console", Card) { CaptureOwnClock = true }, new("mic", "Mic", Mic, 0, 0), new("music", "Music")],
        };
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Single(JsonNode.Parse(json)!["userChannels"]!.AsArray(), c => c!["captureOwnClock"] is not null);
        MixerSettings read = JsonSerializer.Deserialize<MixerSettings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        MixerConfig config = MixerConfig.FromSettings(read);
        Assert.True(config.Channels.Single(c => c.Id == "console").CaptureOwnClock);
        Assert.False(config.Channels.Single(c => c.Id == "mic").CaptureOwnClock);
        // Older settings have no field and leave every input on the mixer's clock.
        MixerSettings old = JsonSerializer.Deserialize<MixerSettings>("""{"userChannels":[{"id":"console","name":"Console","captureSource":"card"}]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(MixerConfig.FromSettings(old).Channels.Single(c => c.Id == "console").CaptureOwnClock);
    }

    [Fact]
    public void SwitchingSavesFirstAndTheStateReportsIt()
    {
        using Mixer mixer = Unbuilt();
        MixerSettings? saved = null;
        mixer.SetCaptureOwnClock("console", true, s => { saved = s; return null; });
        Assert.True(Assert.Single(saved!.UserChannels!, c => c.Id == "console").CaptureOwnClock);
        Assert.True(mixer.ExportSettings().UserChannels!.Single(c => c.Id == "console").CaptureOwnClock);
        ChannelStatus console = mixer.Snapshot().Channels.Single(c => c.Id == "console");
        Assert.Equal(new CaptureClockStatus(true, false, null, null, 0, 0, null, null), console.OwnClock);
        // Every capture input reports its own clock, off or on; no other channel does.
        Assert.Equal(new CaptureClockStatus(false, false, null, null, 0, 0, null, null),
            mixer.Snapshot().Channels.Single(c => c.Id == "mic").OwnClock);
        Assert.Null(mixer.Snapshot().Channels.Single(c => c.Id == "music").OwnClock);

        // A refused save leaves the input as it was.
        Assert.ThrowsAny<Exception>(() => mixer.SetCaptureOwnClock("console", false, _ => "disk full"));
        Assert.True(mixer.Snapshot().Channels.Single(c => c.Id == "console").OwnClock!.On);
        mixer.SetCaptureOwnClock("console", false, _ => null);
        Assert.False(mixer.Snapshot().Channels.Single(c => c.Id == "console").OwnClock!.On);

        Assert.Throws<ArgumentException>(() => mixer.SetCaptureOwnClock("music", true, _ => null));
        Assert.Throws<ArgumentException>(() => mixer.SetCaptureOwnClock("xlr1", true, _ => null));
    }

    [Fact]
    public void TheHelperRunsTheBridgeMode()
    {
        Assert.Equal(["bridge"], NativePluginHost.Arguments(new() { Id = "console_1", Kind = "bridge", Plugin = "bridge" }, null));
        Assert.Contains("underruns", PipeWireAdapter.ClockBridgeMeters);
        Assert.Contains("overruns", PipeWireAdapter.ClockBridgeMeters);
        Assert.Contains("delay", PipeWireAdapter.ClockBridgeMeters);
    }

    [Fact]
    public void WithoutTheHelperTheInputStaysSilentAndSaysWhy()
    {
        var pw = new PipeWireAdapter { BridgeHost = "/missing-openxlr-bridge-host" };
        var error = Assert.Throws<InvalidOperationException>(() => pw.CreateClockBridge("console_1", 2));
        Assert.Equal(Mixer.OwnClockNeedsHost, error.Message);
    }

    // --- the clock readout ------------------------------------------------

    private static string Node(uint id, string name, string description, uint? driver, int? priority = null, int? device = null,
        bool hardware = true)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id, ["type"] = "PipeWire:Interface:Node",
            ["info"] = new Dictionary<string, object?>
            {
                ["props"] = new Dictionary<string, object?>
                {
                    ["node.name"] = name, ["node.description"] = description, ["node.driver-id"] = driver,
                    ["priority.driver"] = priority, ["device.id"] = device, ["device.api"] = hardware ? "alsa" : null,
                }.Where(p => p.Value is not null).ToDictionary(),
            },
        });

    private static JsonElement[] Dump(params string[] nodes)
    {
        using JsonDocument doc = JsonDocument.Parse("[" + string.Join(",", nodes) + "]");
        return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    private static readonly ClockCandidate[] Bridged =
    [
        new(Mic, "unitInput", ["xlr1"], true),
        new(Card, ClockBridged.Role, ["console"], false),
    ];

    [Fact]
    public void ABridgedDeviceIsListedOnItsOwnClockNotAsAFollower()
    {
        // The card drives a graph of its own with the bridge's input; the
        // bridge's output follows the microphone with the mixer.
        MixerClock clock = MixerClock.Read(Dump(
            Node(43, Card, "Elgato 4K X Analog Stereo", null, 2109, 53),
            Node(50, "OpenXLR_bridge_console_1_in", "OpenXLR clock bridge input", 43, hardware: false),
            Node(51, "OpenXLR_bridge_console_1", "OpenXLR clock bridge", 477, hardware: false),
            Node(93, "OpenXLR_mix_monitor", "OpenXLR Monitor A", 477, hardware: false),
            Node(477, Mic, "Elgato Wave XLR Mono", 477, 2200, 469)), Bridged)!;
        Assert.Equal(Mic, clock.Driver!.Name);
        Assert.Empty(clock.Followers);
        Assert.False(clock.MicResampled);
        ClockBridged card = Assert.Single(clock.Bridged);
        Assert.Equal(new ClockBridged(Card, "Elgato 4K X Analog Stereo", ["console"], null, true), card);

        // Something else linked the card into the mixer's graph: it follows
        // the microphone again, and the readout says the bridge is not apart.
        MixerClock joined = MixerClock.Read(Dump(
            Node(43, Card, "Elgato 4K X Analog Stereo", 477, 2109, 53),
            Node(93, "OpenXLR_mix_monitor", "OpenXLR Monitor A", 477, hardware: false),
            Node(477, Mic, "Elgato Wave XLR Mono", 477, 2200, 469)), Bridged)!;
        Assert.Empty(joined.Followers);
        Assert.Equal(new ClockBridged(Card, "Elgato 4K X Analog Stereo", ["console"], Mic, false), Assert.Single(joined.Bridged));
        Assert.NotEqual(clock, joined);
    }

    [Fact]
    public void ABridgedDeviceNeverStandsInForTheMixersDriver()
    {
        // Before the mix sinks run, the driver is the one the mixer's devices
        // follow; the graph the card runs in is not the mixer's, even with
        // the card listed first.
        const string Output = "alsa_output.usb-Elgato_Systems_Elgato_Wave_XLR_0123456789-00.analog-stereo";
        MixerClock clock = MixerClock.Read(Dump(
            Node(30, "Dummy-Driver", "Dummy-Driver", null, 20000, hardware: false),
            Node(43, Card, "Elgato 4K X Analog Stereo", 30, 2109, 53),
            Node(476, Output, "Elgato Wave XLR Analog Stereo", 477, 1109, 469),
            Node(477, Mic, "Elgato Wave XLR Mono", 477, 2200, 469)),
            [new(Card, ClockBridged.Role, ["console"], false), new(Output, "unitOutput", [], false)])!;
        Assert.Equal(Mic, clock.Driver!.Name);
        Assert.Equal(new ClockBridged(Card, "Elgato 4K X Analog Stereo", ["console"], "Dummy-Driver", true), Assert.Single(clock.Bridged));
    }

    // --- what the UI and the archive show ------------------------------------

    private const string State = """
        {"mixer":{"clock":{"driver":{"name":"mic","description":"Wave XLR","priorityDriver":2200},"followers":[],
          "bridged":[{"name":"card","description":"4K X","channels":["console"],"driver":null,"separate":true}]},
          "channels":[{"id":"console","ownClock":{"on":true,"running":true,"delayMs":26.7,"targetMs":26.7,
            "underruns":0,"overruns":1,"driftPpm":-12.5,"error":null}}]}}
        """;

    [Fact]
    public void TheUiReadsTheBridgedDevice()
    {
        AudioClock clock = AudioClock.From(JsonNode.Parse(State)!["mixer"])!;
        AudioClockBridged card = Assert.Single(clock.Bridged);
        Assert.Equal(("card", "4K X", true), (card.Name, card.Description, card.Separate));
        Assert.Same(card, clock.BridgedFor("console"));
        Assert.Null(clock.BridgedFor("xlr1"));
        Assert.Null(clock.ResampledFor("console"));
        // An older daemon sends no list.
        Assert.Empty(AudioClock.From(JsonNode.Parse("""{"clock":{"driver":{"name":"mic"},"followers":[]}}"""))!.Bridged);
    }

    [Fact]
    public void TheStatusLineSaysTheDelayOrThatItWaits()
    {
        JsonNode own = JsonNode.Parse(State)!["mixer"]!["channels"]![0]!["ownClock"]!;
        Assert.Equal(Localizer.Format("CaptureOwnClockDropouts", 26.7.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture), 1L),
            ChannelViewModel.OwnClockText(own));
        own["overruns"] = 0;
        Assert.Equal(Localizer.Format("CaptureOwnClockRunning", 26.7.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)),
            ChannelViewModel.OwnClockText(own));
        own["running"] = false;
        Assert.Equal(Localizer.Text("CaptureOwnClockWaiting"), ChannelViewModel.OwnClockText(own));
        // Off, failed, or from an older daemon: no line; the failure shows as the input's warning.
        Assert.Null(ChannelViewModel.OwnClockText(JsonNode.Parse("""{"on":false,"running":false}""")));
        Assert.Null(ChannelViewModel.OwnClockText(JsonNode.Parse("""{"on":true,"running":false,"error":"no helper"}""")));
        Assert.Null(ChannelViewModel.OwnClockText(null));
    }

    [Fact]
    public void TheArchiveSaysTheDeviceIsBridgedAndWhetherTheBridgeHolds()
    {
        IReadOnlyList<string> lines = Diagnostics.ClockLines(State);
        Assert.Equal(
        [
            "audio clock (last read by the daemon): Wave XLR drives the mixer's graph (mic, priority.driver 2200)",
            "capture input 4K X runs on its own clock, bridged into the mixer's graph, node card",
            "  console: running, delay 26.7 ms, target 26.7 ms, 0 underruns, 1 overruns, drift -12.5 ppm",
        ], lines);
    }

    // --- set-up ---------------------------------------------------------------

    /// <summary>A mixer that is not built, with a stereo and a mono capture input and an application channel.</summary>
    private static Mixer Unbuilt()
    {
        var mixer = new Mixer(new PipeWireAdapter { BridgeHost = "/missing-openxlr-bridge-host" });
        typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, new MixerConfig
        {
            Channels = [new("xlr1", "Mic 1") { InputPair = 0 },
                new("console", "Console") { CaptureSource = Card },
                new("mic", "Mic") { CaptureSource = Mic, CaptureMonoChannel = 0 },
                new("music", "Music")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
        });
        return mixer;
    }

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "console" or "music" or "xlr1";
        public bool HasMix(string id) => id == "monitor";
        public bool HasCaptureChannel(string id) => id == "console";
        public bool IsMonitorFeed(string feed) => false;
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => false;
        public int OverrideCount => 0;
    }
}
