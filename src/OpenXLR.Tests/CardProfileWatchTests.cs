using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// A driven card on a profile without a capture node, as in a reporter's
/// diagnostics archive: the original Wave XLR left on output:analog-stereo,
/// with no alsa_input node at all. The fixtures follow that card's
/// EnumProfile list. pw-dump and wpctl are fakes on PATH, so no real card is
/// read or switched.
/// </summary>
[Collection("xdg-config")]
public sealed class CardProfileWatchTests : IDisposable
{
    private const string Mk1Name = "alsa_card.usb-Elgato_Systems_Elgato_Wave_XLR_ABC123-00";
    private const string ProName = "alsa_card.usb-Elgato_Elgato_Wave_XLR_Pro_DEF456-00";
    private const string OutputOnly = "output:analog-stereo";
    private const string WithInput = "output:analog-stereo+input:mono-fallback";
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-card-watch-").FullName;
    private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
    private readonly string? _config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private long _now;

    public CardProfileWatchTests()
    {
        ExecutableScript.Write(Path.Combine(_directory, "pw-dump"), """
            printf 'x\n' >> "$(dirname "$0")/reads"
            cat "$(dirname "$0")/dump.json"
            """);
        ExecutableScript.Write(Path.Combine(_directory, "wpctl"), """
            printf '%s\n' "$*" >> "$(dirname "$0")/writes"
            """);
        Environment.SetEnvironmentVariable("PATH", _directory + Path.PathSeparator + _path);
        Directory.CreateDirectory(Path.Combine(_directory, "config"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_directory, "config"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _path);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config);
        Directory.Delete(_directory, true);
    }

    [Fact]
    public void AnOutputOnlyWaveXlrIsSwitchedToTheSameOutputWithAnInput()
    {
        Dump(Mk1(OutputOnly));
        CardProfileWatch watch = Mk1Watch();
        CardProfileNote note = Assert.Single(watch.Check());
        Assert.True(note.Warning);
        Assert.Contains($"profile {OutputOnly} has no microphone input; switched it to {WithInput}", note.Text);
        // Index 1 is the +input profile: not pro-audio, not the IEC958 one, not input only.
        Assert.Equal(["set-profile 41 1"], Writes());
        Assert.Equal((OutputOnly, WithInput), watch.Switched);

        Dump(Mk1(WithInput));
        Tick(CardProfileWatch.Spacing);
        Assert.Empty(watch.Check());
        Assert.Equal(new CardProfileStatus(Mk1Name, WithInput, true, OutputOnly), watch.Status);
        Assert.Single(Writes());
    }

    [Theory]
    [InlineData(WithInput)]
    [InlineData("output:iec958-stereo+input:mono-fallback")]
    [InlineData("pro-audio")]
    public void ACardThatHasAnInputIsLeftAloneAndLookedAtSlowly(string active)
    {
        Dump(Mk1(active));
        CardProfileWatch watch = Mk1Watch();
        Assert.Empty(watch.Check());
        Assert.Equal(new CardProfileStatus(Mk1Name, active, true), watch.Status);
        Tick(CardProfileWatch.Spacing);
        watch.Check();
        Assert.Equal(1, Reads());   // nothing switched, so no quick second look
        Tick(CardProfileWatch.SlowSpacing);
        watch.Check();
        Assert.Equal(2, Reads());
        Assert.Empty(Writes());
        Assert.False(watch.Done);
    }

    [Fact]
    public void TheProOnProAudioIsLeftAlone()
    {
        Dump(Pro("pro-audio"));
        var watch = new CardProfileWatch("Elgato Wave XLR Pro", ["Wave_XLR_Pro_DEF456-", "Wave_XLR_Pro"], parksUcm: true, usbId: (0x0fd9, 0x00b4), clock: () => _now);
        Assert.Empty(watch.Check());
        Assert.True(watch.Done);
        Assert.Null(watch.Parked);
        Assert.Equal(new CardProfileStatus(ProName, "pro-audio", true), watch.Status);
        Assert.Empty(Writes());
    }

    [Fact]
    public void TheProUcmSplitIsStillParkedOnProAudio()
    {
        Dump(Pro("HiFi"));
        var watch = new CardProfileWatch("Elgato Wave XLR Pro", ["Wave_XLR_Pro_DEF456-", "Wave_XLR_Pro"], parksUcm: true, usbId: (0x0fd9, 0x00b4), clock: () => _now);
        CardProfileNote note = Assert.Single(watch.Check());
        Assert.False(note.Warning);
        Assert.Contains("UCM profile HiFi parked, pro-audio active", note.Text);
        Assert.Equal("HiFi", watch.Parked);
        Assert.True(watch.Done);
        Assert.Equal(["set-profile 47 3"], Writes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACardWithoutAProfileThatAddsAnInputIsReportedAndNotSwitched(bool unavailable)
    {
        // Either the +input profiles are gone, or the card marks them unavailable.
        Dump(unavailable ? Mk1(OutputOnly, available: name => name.Contains("+input") ? "no" : "unknown")
            : Mk1(OutputOnly, without: name => name.Contains("+input")));
        CardProfileWatch watch = Mk1Watch();
        CardProfileNote note = Assert.Single(watch.Check());
        Assert.True(note.Warning);
        Assert.Empty(Writes());
        Assert.Null(watch.Switched);
        string problem = watch.Status!.Problem!;
        Assert.StartsWith($"Elgato Wave XLR has no microphone input: its audio card is on the profile {OutputOnly}", problem);
        Assert.Contains("offers no profile with the same output and an input", problem);
        Assert.False(watch.Status.Capture);

        // Said once; it clears when the user picks a profile with an input.
        Tick(CardProfileWatch.Spacing);
        Assert.Empty(watch.Check());
        Dump(Mk1(WithInput));
        Tick(CardProfileWatch.Spacing);
        Assert.Contains("has a microphone input again", Assert.Single(watch.Check()).Text);
        Assert.Null(watch.Status!.Problem);
        Assert.Empty(Writes());
    }

    [Fact]
    public void AProfilePutBackAfterTheSwitchIsReportedAndNeverSwitchedAgain()
    {
        Dump(Mk1(OutputOnly));
        CardProfileWatch watch = Mk1Watch();
        watch.Check();
        Dump(Mk1(WithInput));
        Tick(CardProfileWatch.Spacing);
        watch.Check();
        Assert.Null(watch.Status!.Problem);

        // The session manager restores its saved profile.
        Dump(Mk1(OutputOnly));
        Tick(CardProfileWatch.Spacing);
        CardProfileNote note = Assert.Single(watch.Check());
        Assert.Contains("not switching it a second time", note.Text);
        Assert.Contains($"switched its audio card to {WithInput}, and something else put it back on {OutputOnly}", watch.Status!.Problem);
        Assert.Equal(OutputOnly, watch.Status.SwitchedFrom);
        for (int i = 0; i < CardProfileWatch.Checks + 2; i++)
        {
            Tick(CardProfileWatch.SlowSpacing);
            Assert.Empty(watch.Check());
        }
        Assert.Equal(["set-profile 41 1"], Writes());
        Assert.NotNull(watch.Status!.Problem);
    }

    [Fact]
    public void AReconnectInTheSameConnectionDoesNotSwitchAgain()
    {
        Dump(Mk1(OutputOnly));
        var watch = new CardProfileWatch("Elgato Wave XLR", ["Wave_XLR"], parksUcm: false, switched: (OutputOnly, WithInput), usbId: (0x0fd9, 0x007d), clock: () => _now);
        watch.Check();
        Assert.Empty(Writes());
        Assert.Contains("put it back", watch.Status!.Problem);
    }

    [Fact]
    public void AnotherModelsCardIsNeverTakenForTheUnitsOwn()
    {
        // The Pro's card holds "Wave_XLR" in its name, and only it is there.
        Dump(Mk1(OutputOnly, name: ProName, product: "0x00b4"));
        CardProfileWatch watch = Mk1Watch();
        watch.Check();
        Assert.Empty(Writes());
        Assert.Null(watch.Status);
        Assert.Null(watch.Matched);
    }

    [Fact]
    public void AUnitWithoutAUsbLocationHasNoCardWatch()
    {
        Dump(Mk1(OutputOnly));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["mixer"] = "true" }).Build();
        var unit = new Unit(located: false);
        using var manager = new DeviceManager(new RecordingLogger<DeviceManager>(), config, () => [unit]);
        manager.SweepOnce();
        Assert.Equal(0, Reads());
        Assert.Null(manager.Snapshot().CardProfile);
    }

    [Fact]
    public void ACardStillOffIsReportedOnlyOnceTheFirstChecksRunOut()
    {
        Dump(Mk1("off"));
        CardProfileWatch watch = Mk1Watch();
        for (int i = 1; i < CardProfileWatch.Checks; i++)
        {
            Assert.Empty(watch.Check());
            Assert.Null(watch.Status);
            Tick(CardProfileWatch.Spacing);
        }
        Assert.Contains("its audio card is off", Assert.Single(watch.Check()).Text);
        Assert.Contains("its audio card is off", watch.Status!.Problem);
        Assert.Empty(Writes());
    }

    [Fact]
    public void ACardNeverFoundIsGivenUpQuietly()
    {
        Dump("[]");
        CardProfileWatch watch = Mk1Watch();
        for (int i = 0; i < CardProfileWatch.Checks; i++)
        {
            Assert.Empty(watch.Check());
            Tick(CardProfileWatch.Spacing);
        }
        Assert.True(watch.Done);
        Assert.Null(watch.Status);
    }

    [Fact]
    public void TheDrivingManagerSwitchesItsCardAndPublishesWhatItFound()
    {
        Dump(Mk1(OutputOnly));
        using DeviceManager manager = Manager(mixer: true, out _);
        manager.SweepOnce();
        Assert.Equal(["set-profile 41 1"], Writes());
        Assert.Equal(new CardProfileStatus(Mk1Name, OutputOnly, false, OutputOnly), manager.Snapshot().CardProfile);
        Assert.Null(manager.Warning);
    }

    [Fact]
    public void TheDrivingManagerWarnsWhenTheCardCannotBeFixed()
    {
        Dump(Mk1(OutputOnly, without: name => name.Contains("+input")));
        using DeviceManager manager = Manager(mixer: true, out RecordingLogger<DeviceManager> log);
        manager.SweepOnce();
        Assert.Empty(Writes());
        string problem = manager.Snapshot().CardProfile!.Problem!;
        Assert.Contains("has no microphone input", problem);
        Assert.Contains(problem, manager.Warning);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("has no microphone input"));
    }

    [Fact]
    public void HardwareControlModeLeavesTheCardAlone()
    {
        Dump(Mk1(OutputOnly));
        using DeviceManager manager = Manager(mixer: false, out _);
        manager.SweepOnce();
        Assert.Equal(0, Reads());
        Assert.Null(manager.Snapshot().CardProfile);
    }

    [Fact]
    public void TheArchiveSaysInOneLineWhetherEachDrivenCardHasAnInput()
    {
        string state = $$$"""
            {"connected":true,"device":{"vendor":"Elgato","model":"Wave XLR"},
             "cardProfile":{"card":"{{{Mk1Name}}}","profile":"{{{OutputOnly}}}","capture":false,"problem":"Elgato Wave XLR has no microphone input."},
             "waveInterfaces":[{"id":"0fd9:007d@1","name":"Elgato Wave XLR","active":true},
               {"id":"0fd9:0070@2","name":"Elgato Wave:3 (1-2)","active":false,
                "cardProfile":{"card":"alsa_card.usb-Elgato_Systems_Elgato_Wave_3_X-00","profile":"{{{WithInput}}}","capture":true,"switchedFrom":"{{{OutputOnly}}}"}}]}
            """;
        Assert.Equal([
            $"audio card of Elgato Wave XLR (last checked by the daemon): {Mk1Name}, profile {OutputOnly}, microphone input no, problem: Elgato Wave XLR has no microphone input.",
            $"audio card of Elgato Wave:3 (1-2) (last checked by the daemon): alsa_card.usb-Elgato_Systems_Elgato_Wave_3_X-00, profile {WithInput}, microphone input yes, switched from {OutputOnly} by OpenXLR",
        ], Diagnostics.CardProfileLines(state));
        Assert.StartsWith("audio cards: not checked", Assert.Single(Diagnostics.CardProfileLines("""{"connected":false}""")));
        Assert.StartsWith("audio cards: not checked", Assert.Single(Diagnostics.CardProfileLines(null)));
    }

    private CardProfileWatch Mk1Watch() => new("Elgato Wave XLR", ["Wave_XLR"], parksUcm: false, usbId: (0x0fd9, 0x007d), clock: () => _now);

    private DeviceManager Manager(bool mixer, out RecordingLogger<DeviceManager> log)
    {
        var unit = new Unit();
        log = new RecordingLogger<DeviceManager>();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["mixer"] = mixer ? "true" : "false" }).Build();
        return new DeviceManager(log, config, () => [unit]);
    }

    /// <summary>An original Wave XLR that answers every read.</summary>
    private sealed class Unit(bool located = true) : IAudioDevice
    {
        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR", 0x0fd9, 0x007d) { Location = located ? new UsbLocation(3, 7, "3-2", "ABC123") : null };
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true, RetainsSettings = true };
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() { }
        public DeviceState ReadState() => new() { GainDb = 40 };
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    private static string Sink(int device) => $$"""[1, ["Audio/Sink", 1, "card.profile.devices", [{{device}}]]]""";
    private static string Source(int device) => $$"""[1, ["Audio/Source", 1, "card.profile.devices", [{{device}}]]]""";
    private static string Duplex(int source, int sink) =>
        $$"""[2, ["Audio/Source", 1, "card.profile.devices", [{{source}}]], ["Audio/Sink", 1, "card.profile.devices", [{{sink}}]]]""";

    // The reporter's card: EnumProfile in its order, the priorities ACP gives them.
    private static readonly (string Name, int Priority, string Classes)[] Mk1Profiles =
    [
        ("off", 0, "[0]"),
        (WithInput, 6501, Duplex(2, 3)),
        (OutputOnly, 6500, Sink(3)),
        ("output:iec958-stereo+input:mono-fallback", 5501, Duplex(2, 4)),
        ("output:iec958-stereo", 5500, Sink(4)),
        ("pro-audio", 1, Duplex(1, 0)),
        ("input:mono-fallback", 1, Source(2)),
    ];

    private static string Mk1(string active, Func<string, bool>? without = null, Func<string, string>? available = null,
        string name = Mk1Name, string product = "0x007d")
    {
        string Entry(int index, (string Name, int Priority, string Classes) p) =>
            $$"""{"index":{{index}},"name":"{{p.Name}}","priority":{{p.Priority}},"available":"{{available?.Invoke(p.Name) ?? "unknown"}}","classes":{{p.Classes}}}""";
        var listed = Mk1Profiles.Select((p, i) => (p, i)).Where(x => without?.Invoke(x.p.Name) != true).ToList();
        var (current, index) = Mk1Profiles.Select((p, i) => (p, i)).Single(x => x.p.Name == active);
        bool capture = current.Classes.Contains("Audio/Source");
        string node = capture
            ? """,{"id":90,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_ABC123-00.mono-fallback","media.class":"Audio/Source","device.id":41}}}"""
            : "";
        return $$$"""
            [{"id":41,"type":"PipeWire:Interface:Device","info":{
              "props":{"device.name":"{{{name}}}","device.api":"alsa","device.vendor.id":"0x0fd9","device.product.id":"{{{product}}}"},
              "params":{"EnumProfile":[{{{string.Join(",", listed.Select(x => Entry(x.i, x.p)))}}}],
                        "Profile":[{{{Entry(index, current)}}}]} } }{{{node}}}]
            """;
    }

    // The Wave XLR Pro as the desk's pw-dump shows it.
    private static string Pro(string active)
    {
        (string Name, int Index, int Priority, string Classes)[] profiles =
        [
            ("off", 0, 0, "[0]"),
            ("HiFi", 1, 9700, """[2, ["Audio/Source", 2, "card.profile.devices", [6, 7]], ["Audio/Sink", 4, "card.profile.devices", [2, 3, 4, 5]]]"""),
            ("Direct", 2, 2000, Duplex(1, 0)),
            ("pro-audio", 3, 1, Duplex(9, 8)),
        ];
        string Entry((string Name, int Index, int Priority, string Classes) p) =>
            $$"""{"index":{{p.Index}},"name":"{{p.Name}}","priority":{{p.Priority}},"available":"yes","classes":{{p.Classes}}}""";
        return $$$"""
            [{"id":47,"type":"PipeWire:Interface:Device","info":{
              "props":{"device.name":"{{{ProName}}}","device.api":"alsa","device.vendor.id":"0x0fd9","device.product.id":"0x00b4"},
              "params":{"EnumProfile":[{{{string.Join(",", profiles.Select(Entry))}}}],
                        "Profile":[{{{Entry(profiles.Single(p => p.Name == active))}}}]} } }]
            """;
    }

    private void Tick(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    private void Dump(string value) => File.WriteAllText(Path.Combine(_directory, "dump.json"), value);
    private string[] Writes() => File.Exists(Path.Combine(_directory, "writes"))
        ? File.ReadAllLines(Path.Combine(_directory, "writes")) : [];
    private int Reads() => File.Exists(Path.Combine(_directory, "reads"))
        ? File.ReadAllLines(Path.Combine(_directory, "reads")).Length : 0;
}
