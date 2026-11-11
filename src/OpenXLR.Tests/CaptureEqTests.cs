using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The microphone EQ on capture inputs: every capture input, on one mono
/// port or a stereo pair, carries the same EQ as XLR 1 and XLR 2, which
/// always runs on this computer. The EQ commands take its id, setEqProcessing
/// refuses it, the state reports it with processing software and no unit, the
/// settings and a profile carry it, and an additional unit's microphone on a
/// capture input is never written an EQ. A stereo pair has the EQ alone: noise
/// removal, the dynamics and the voice changer stay with the microphones.
/// </summary>
public sealed class CaptureEqTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Extra = "0fd9:00c7@aaaaaaaaaaaaaaaa";

    private static readonly ChannelDefinition Guest = new("guest", "Guest") { CaptureSource = "usb_guest", CaptureMonoChannel = 0 };
    private static readonly ChannelDefinition Camera = new("camera", "Camera") { CaptureSource = "usb_camera", CapturePair = 0 };

    /// <summary>The default layout with a mono and a stereo capture input, never built.</summary>
    private static Mixer WithCaptures(params ChannelDefinition[] captures)
    {
        var mixer = new Mixer();
        typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer,
            MixerConfig.Default() with { Channels = [.. MixerConfig.Default().Channels, .. captures] });
        return mixer;
    }

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1" or "xlr2" or "aux" or "system" or "guest" or "camera";
        public bool HasMix(string id) => id is "monitor";
        public bool IsMonitorFeed(string feed) => feed is "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => key is "xlr1" or "xlr2" or "guest" or "camera";
        public bool IsMicrophoneChannel(string id) => id is "xlr1" or "xlr2" or "guest";
        public bool CarriesEqChannel(string id) => IsMicrophoneChannel(id) || id is "camera";
        public bool EqRunsInSoftwareOnly(string id) => id is "guest" or "camera";
        public int OverrideCount => 0;
    }

    private static string Bands() => JsonSerializer.Serialize(MicEq.Default.Bands, Web);

    [Theory]
    [InlineData("""{"cmd":"setEqEnabled","channel":"guest","value":true}""", null)]
    [InlineData("""{"cmd":"setEqBand","channel":"guest","band":2,"gain":6}""", null)]
    [InlineData("""{"cmd":"setEq","channel":"guest","eq":{"enabled":true,"bands":BANDS}}""", null)]
    [InlineData("""{"cmd":"setEqBand","channel":"guest","band":17,"gain":6}""", "setEqBand: need 'band' from 1 to 16")]
    [InlineData("""{"cmd":"setEqBand","channel":"guest","band":2,"gain":21}""", "setEqBand: gain must be -20 to +20 dB")]
    [InlineData("""{"cmd":"setEqEnabled","channel":"missing","value":true}""", "setEqEnabled: 'missing' is not a microphone or capture input")]
    [InlineData("""{"cmd":"setEqEnabled","channel":"system","value":true}""", "setEqEnabled: 'system' is not a microphone or capture input")]
    [InlineData("""{"cmd":"setEq","channel":"camera","eq":{"enabled":true,"bands":BANDS}}""", null)]
    [InlineData("""{"cmd":"setEqBand","channel":"camera","band":2,"gain":6}""", null)]
    [InlineData("""{"cmd":"setEqEnabled","channel":"camera","value":true}""", null)]
    [InlineData("""{"cmd":"loadEqProfile","channel":"camera","profile":"builtin:clear"}""", null)]
    [InlineData("""{"cmd":"setEqProcessing","channel":"camera","value":"device"}""", "setEqProcessing: 'camera' is a capture input, whose EQ always runs on this computer")]
    [InlineData("""{"cmd":"setNoiseRemoval","channel":"camera","enabled":true}""", "setNoiseRemoval: 'camera' is not a microphone input")]
    [InlineData("""{"cmd":"setVoiceChanger","channel":"camera","enabled":true}""", "setVoiceChanger: 'camera' is not a microphone input")]
    [InlineData("""{"cmd":"setEqProcessing","channel":"guest","value":"software"}""", "setEqProcessing: 'guest' is a capture input, whose EQ always runs on this computer")]
    [InlineData("""{"cmd":"setEqProcessing","channel":"guest","value":"device"}""", "setEqProcessing: 'guest' is a capture input, whose EQ always runs on this computer")]
    [InlineData("""{"cmd":"setEqProcessing","channel":"xlr1","value":"software"}""", null)]
    [InlineData("""{"cmd":"setEqEnabled","channel":"xlr2","value":true}""", null)]
    [InlineData("""{"cmd":"setEqBand","channel":"aux","band":2,"gain":6}""", "setEqBand: 'aux' is not a microphone or capture input")]
    public void TheEqCommandsTakeEveryCaptureInput(string json, string? error)
    {
        var cmd = JsonSerializer.Deserialize<Command>(json.Replace("BANDS", Bands()), Web)!;
        Assert.Equal(error, CommandValidation.Check(cmd, new Layout(), _ => null, nativeHostInstalled: true));
    }

    [Fact]
    public void SwitchingACaptureEqOnNeedsTheNativeHelper()
    {
        var on = JsonSerializer.Deserialize<Command>("""{"cmd":"setEqEnabled","channel":"guest","value":true}""", Web)!;
        Assert.Equal(Mixer.EqNeedsHost, CommandValidation.Check(on, new Layout(), _ => null, nativeHostInstalled: false));
        var band = JsonSerializer.Deserialize<Command>("""{"cmd":"setEqBand","channel":"guest","band":2,"gain":3}""", Web)!;
        Assert.Null(CommandValidation.Check(band, new Layout(), _ => null, nativeHostInstalled: false));
    }

    /// <summary>
    /// The mixer answers the validator for its own layout: both capture inputs
    /// carry the EQ, only the mono one is a microphone, and an application
    /// channel has neither.
    /// </summary>
    [Fact]
    public void TheMixerNamesItsCaptureInputs()
    {
        using Mixer mixer = WithCaptures(Guest, Camera);
        Assert.True(mixer.IsMicrophoneChannel("xlr1"));
        Assert.True(mixer.IsMicrophoneChannel("guest"));
        Assert.False(mixer.IsMicrophoneChannel("camera"));
        Assert.False(mixer.IsMicrophoneChannel("system"));
        Assert.False(mixer.IsMicrophoneChannel("missing"));
        Assert.True(mixer.CarriesEqChannel("xlr1"));
        Assert.True(mixer.CarriesEqChannel("guest"));
        Assert.True(mixer.CarriesEqChannel("camera"));
        Assert.False(mixer.CarriesEqChannel("system"));
        Assert.False(mixer.CarriesEqChannel("missing"));
        Assert.True(mixer.EqRunsInSoftwareOnly("guest"));
        Assert.False(mixer.EqRunsInSoftwareOnly("xlr1"));
        Assert.True(mixer.EqRunsInSoftwareOnly("camera"));
        string? Check(string json) => CommandValidation.Check(JsonSerializer.Deserialize<Command>(json, Web)!, mixer, _ => null, nativeHostInstalled: true);
        Assert.Null(Check("""{"cmd":"setEqBand","channel":"guest","band":2,"gain":6}"""));
        Assert.Null(Check("""{"cmd":"setEqBand","channel":"camera","band":2,"gain":6}"""));
        Assert.Equal("setEqBand: 'system' is not a microphone or capture input", Check("""{"cmd":"setEqBand","channel":"system","band":2,"gain":6}"""));
        Assert.Equal(Mixer.EqSoftwareOnly("guest"), Check("""{"cmd":"setEqProcessing","channel":"guest","value":"software"}"""));
        Assert.Equal(Mixer.EqSoftwareOnly("camera"), Check("""{"cmd":"setEqProcessing","channel":"camera","value":"software"}"""));
        Assert.Null(Check("""{"cmd":"setEqProcessing","channel":"xlr1","value":"software"}"""));
        Assert.Equal("setNoiseRemoval: 'camera' is not a microphone input", Check("""{"cmd":"setNoiseRemoval","channel":"camera","enabled":true}"""));
        Assert.Equal("setCompressor: 'camera' is not a microphone input", Check("""{"cmd":"setCompressor","channel":"camera","enabled":true}"""));
    }

    [Fact]
    public void EveryCaptureInputReportsASoftwareEqAndAStereoPairNothingElse()
    {
        using Mixer mixer = WithCaptures(Guest, Camera);
        MixerState state = mixer.Snapshot();
        foreach (string id in new[] { "guest", "camera" })
        {
            MicEqState eq = state.Channels.Single(c => c.Id == id).Eq!;
            Assert.False(eq.Enabled);
            Assert.Equal(MicEq.Default.Bands, eq.Bands);
            Assert.Equal(MicEqProcessing.Software, eq.Processing);
            Assert.Null(eq.Device);
            Assert.Null(eq.Backend);
        }
        ChannelStatus camera = state.Channels.Single(c => c.Id == "camera");
        Assert.False(camera.Microphone);
        Assert.Null(camera.Noise);
        Assert.Null(camera.Voice);
        Assert.Null(camera.Compressor);
        Assert.Null(camera.Expander);
        Assert.NotNull(state.Channels.Single(c => c.Id == "guest").Noise);
        Assert.Null(state.Channels.Single(c => c.Id == "system").Eq);
        Assert.Equal(MicEqProcessing.Device, state.Channels.Single(c => c.Id == "xlr1").Eq!.Processing);

        using JsonDocument json = JsonSerializer.SerializeToDocument(state, Web);
        JsonElement guest = json.RootElement.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "guest");
        Assert.Equal("software", guest.GetProperty("eq").GetProperty("processing").GetString());
        Assert.Equal(JsonValueKind.Null, guest.GetProperty("eq").GetProperty("device").ValueKind);
    }

    /// <summary>
    /// The mixer keeps a capture input's EQ as it keeps XLR 1's, on a mono
    /// port and on a stereo pair alike: snapped, in the saved settings, in a
    /// profile, which names every channel with the EQ, and refuses a choice of
    /// where it runs. A profile names noise removal on microphones only.
    /// </summary>
    [Fact]
    public void ACaptureEqIsStoredSavedAndNamedInAProfile()
    {
        using Mixer mixer = WithCaptures(Guest, Camera);
        foreach (string id in new[] { "guest", "camera" })
        {
            mixer.SetEqBand(id, 2, new(Gain: 6.1, Frequency: 1000.4));
            mixer.SetEqEnabled(id, true);
            MicEq eq = mixer.EqFor(id);
            Assert.True(eq.Enabled);
            Assert.Equal(6, eq.Bands[1].Gain);
            Assert.Equal(1000, eq.Bands[1].Frequency);
            Assert.True(mixer.EqWantsSoftwareStage(id));
            Assert.True(mixer.WantsMicrophoneStage(id));
            Assert.Equal(MicEqProcessing.Software, mixer.EqProcessingFor(id));
            Assert.Equal(eq, mixer.ExportSettings().Eq[id]);
            Assert.Equal(eq, mixer.ExportScene().Eq![id]);
            Assert.Equal(Mixer.EqSoftwareOnly(id), Assert.Throws<ArgumentException>(() => mixer.SetEqProcessing(id, "software")).Message);
            Assert.False(mixer.ExportSettings().EqProcessing.ContainsKey(id));
        }
        Assert.Equal(["camera", "guest", "xlr1", "xlr2"], mixer.ExportScene().Eq!.Keys.Order());
        Assert.Equal(["guest", "xlr1", "xlr2"], mixer.ExportScene().NoiseRemoval!.Keys.Order());
        Assert.Equal(["guest", "xlr1", "xlr2"], mixer.ExportScene().Compressor!.Keys.Order());

        // A recall takes a stereo pair's EQ like any other.
        Dictionary<string, MicEq> recalled = Mixer.RecallEq(new Dictionary<string, MicEq>(), mixer.ExportScene().Eq);
        Assert.Equal(mixer.EqFor("camera"), recalled["camera"]);

        Assert.Equal(Mixer.NoEq("system"), Assert.Throws<ArgumentException>(() => mixer.SetEqEnabled("system", true)).Message);
        Assert.Throws<ArgumentException>(() => mixer.SetNoiseRemoval("camera", new(Enabled: true)));
        Assert.Throws<ArgumentException>(() => mixer.SetCompressor("camera", new(Enabled: true)));
    }

    /// <summary>
    /// A capture input moved to another mono port or to a stereo pair keeps
    /// its EQ, which runs on both. Noise removal stays saved across the move
    /// and runs only on a mono port again: a stereo pair's stage is wanted for
    /// its EQ alone.
    /// </summary>
    [Fact]
    public void ASourceChangeKeepsTheEq()
    {
        using Mixer mixer = WithCaptures(Guest);
        mixer.SetEqEnabled("guest", true);
        mixer.SetEqBand("guest", 3, new(Gain: -4));
        mixer.SetNoiseRemoval("guest", new(Enabled: true));
        MicEq eq = mixer.EqFor("guest");
        MixerSettings? saved = null;
        mixer.SetCaptureSource("guest", "usb_other", 0, s => { saved = s; return null; }, monoChannel: 1);
        Assert.Equal(eq, mixer.EqFor("guest"));
        Assert.Equal(eq, saved!.Eq["guest"]);
        Assert.NotNull(mixer.Snapshot().Channels.Single(c => c.Id == "guest").Eq);

        mixer.SetCaptureSource("guest", "usb_other", 0, _ => null);
        ChannelStatus stereo = mixer.Snapshot().Channels.Single(c => c.Id == "guest");
        Assert.Equal(eq, new MicEq { Enabled = stereo.Eq!.Enabled, Bands = stereo.Eq.Bands });
        Assert.Null(stereo.Noise);
        Assert.True(mixer.EqWantsSoftwareStage("guest"));
        Assert.Equal(eq, mixer.ExportSettings().Eq["guest"]);
        Assert.True(mixer.ExportSettings().NoiseRemoval["guest"].Enabled);
        mixer.SetEqEnabled("guest", false);
        Assert.False(mixer.WantsMicrophoneStage("guest"));
        mixer.SetEqEnabled("guest", true);

        mixer.SetCaptureSource("guest", "usb_other", 0, _ => null, monoChannel: 0);
        ChannelStatus back = mixer.Snapshot().Channels.Single(c => c.Id == "guest");
        Assert.Equal(eq, new MicEq { Enabled = back.Eq!.Enabled, Bands = back.Eq.Bands });
        Assert.True(back.Noise!.Enabled);
        mixer.SetEqEnabled("guest", false);
        Assert.True(mixer.WantsMicrophoneStage("guest"));
    }

    /// <summary>
    /// An additional unit's microphone on a capture input: the unit's model
    /// runs the EQ itself, but the capture input's EQ stays on this computer,
    /// the state names no unit, and nothing is written to the unit.
    /// </summary>
    [Fact]
    public void AnAdditionalUnitsMicrophoneOnACaptureInputIsNeverWrittenAnEq()
    {
        var port = new Port();
        ChannelDefinition extra = new("extra", "Extra") { CaptureSource = "alsa_input.usb-Elgato_Wave_XLR_Dock_MK2-00.mono", CaptureMonoChannel = 0, CaptureUnit = Extra };
        using Mixer mixer = WithCaptures(extra);
        mixer.SetAttachedUnits([new AttachedUnit(Extra, ["Elgato_Wave_XLR_Dock_MK2"], 1, true, true)]);
        mixer.SetDeviceEqPort(port);
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Extra] = new("Wave XLR Dock MK.2", "1.0.15", true) });
        Assert.Equal(Extra, mixer.Snapshot().Channels.Single(c => c.Id == "extra").UnitInput?.Unit);
        mixer.SetEqEnabled("extra", true);
        mixer.SetEqBand("extra", 2, new(Gain: 6));
        mixer.SyncDeviceEq();
        Assert.False(mixer.EqRunsOnDevice("extra"));
        Assert.True(mixer.EqWantsSoftwareStage("extra"));
        MicEqState eq = mixer.Snapshot().Channels.Single(c => c.Id == "extra").Eq!;
        Assert.Null(eq.Device);
        Assert.Equal(MicEqProcessing.Software, eq.Processing);
        Assert.Empty(port.Posts);
    }

    private sealed class Port : IDeviceEqPort
    {
        public readonly List<(string Unit, int Input, DeviceEq Eq)> Posts = [];
        public void Post(string unit, int input, DeviceEq eq, string owner) => Posts.Add((unit, input, eq));
        public DeviceEqStatus Status(string unit, int input) => DeviceEqStatus.None;
    }
}
