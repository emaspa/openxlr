using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

// Device managers keep last settings and the additional choices under the
// configuration directory, which these tests redirect.
[Collection("xdg-config")]
public sealed class WaveInterfaceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-wave-units-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    public WaveInterfaceTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
    private static UsbLocation Location(string serial, byte address = 2) => new(1, address, "1-2", serial);
    private static IConfiguration Configuration => new ConfigurationBuilder().Build();
    private static DeviceManager Manager(Func<IReadOnlyList<IAudioDevice>> detect) => new(NullLogger<DeviceManager>.Instance, Configuration, detect);
    private static string PreferencesPath => OpenXLR.Core.OpenXlrPaths.ConfigFile("wave-interfaces.json");

    [Fact]
    public void ASerialIdentifiesAUnitAcrossAddressAndPortChangesAndSeparatesUnitsOfOneModel()
    {
        var a = new DeviceInfo("Elgato", "Wave XLR", 0x0fd9, 0x007d) { Location = Location("unitA") };
        var replugged = a with { Location = new(2, 9, "2-4", "unitA") };
        var b = a with { Location = Location("unitB") };
        Assert.Equal(a.InstanceId, replugged.InstanceId);
        Assert.NotEqual(a.InstanceId, b.InstanceId);
        Assert.True(UsbLocation.IsInstanceId(a.InstanceId));
        Assert.False(UsbLocation.IsInstanceId("0fd9:007d"));
        // Without a serial the port is the identity.
        Assert.NotEqual(new UsbLocation(1, 2, "1-2", null).Key, new UsbLocation(1, 2, "1-3", null).Key);

        var policy = new HungTransferPolicy();
        for (int i = 0; i < HungTransferPolicy.Limit; i++) policy.NoteHung(a.InstanceId);
        Assert.True(policy.IsSetAside(a.InstanceId));
        Assert.False(policy.IsSetAside(b.InstanceId));
    }

    [Fact]
    public void TheSerialFragmentEndsAtTheAlsaSeparatorAndFallsBackToTheModel()
    {
        var a = new DeviceInfo("Elgato", "Wave XLR", 0x0fd9, 0x007d) { Location = Location("unitA") };
        Assert.Equal("Wave_XLR_unitA-", a.NodeNameFragment);
        Assert.Equal(["Wave_XLR_unitA-", "Wave_XLR"], a.NodeNameFragments);
        Assert.Contains(a.NodeNameFragment, "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_unitA-00.analog-stereo");
        Assert.DoesNotContain(a.NodeNameFragment, "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_unitAB-00.analog-stereo");
        // A serial udev would spell differently, or none at all, leaves the model fragment.
        var odd = a with { Location = Location("unit A") };
        Assert.Equal(["Wave_XLR"], odd.NodeNameFragments);
        Assert.Equal(["Wave_XLR"], new DeviceInfo("Elgato", "Wave XLR", 0x0fd9, 0x007d).NodeNameFragments);

        // The model fragment only while no other attached unit answers to it.
        var b = a with { Location = Location("unitB") };
        var pro = new DeviceInfo("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4) { Location = Location("proA") };
        var dock = new DeviceInfo("Elgato", "XLR Dock", 0x0fd9, 0x00a6) { Location = Location("dockA") };
        Assert.Equal(["Wave_XLR_unitA-", "Wave_XLR"], a.FragmentsAmong([a]));
        Assert.Equal(["Wave_XLR_unitA-", "Wave_XLR"], a.FragmentsAmong([a, dock]));
        Assert.Equal(["Wave_XLR_unitA-"], a.FragmentsAmong([a, b]));
        Assert.Equal(["Wave_XLR_unitA-"], a.FragmentsAmong([a, pro]));
        Assert.Equal(["Wave_XLR_Pro_proA-", "Wave_XLR_Pro"], pro.FragmentsAmong([a, pro]));
    }

    [Fact]
    public void NodesAreFoundByTheFirstFragmentThatNamesOneCard()
    {
        static AudioNode Source(string name) => new(name, name, AudioNodeKind.Source, false, true);
        AudioNode pro = Source("alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_Pro_unit_A-00.pro-input-0");
        AudioNode split = Source("alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_Pro_unit_A-00.HiFi__Mic2__source");
        AudioNode other = Source("alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_unit_B-00.analog-stereo");
        Assert.Equal("usb-Elgato_Systems_Elgato_Wave_XLR_Pro_unit_A-00", InterfaceNodes.CardKey(pro.Name));
        Assert.Equal("test_Wave_XLR_unitA", InterfaceNodes.CardKey("test_Wave_XLR_unitA"));

        // The serial as sysfs gives it matches nothing; the model finds the one card.
        var found = InterfaceNodes.Pick([pro, split, other], ["Wave_XLR_Pro_unitA-", "Wave_XLR_Pro"], out string? matched, out bool ambiguous);
        Assert.Equal([pro, split], found); Assert.Equal("Wave_XLR_Pro", matched); Assert.False(ambiguous);
        // Two cards under the first fragment that matches: nothing, and no broader try.
        found = InterfaceNodes.Pick([pro, other], ["Wave_XLR", "Elgato"], out matched, out ambiguous);
        Assert.Empty(found); Assert.Equal("Wave_XLR", matched); Assert.True(ambiguous);
        found = InterfaceNodes.Pick([pro, other], ["Wave_XLR_absent-"], out matched, out ambiguous);
        Assert.Empty(found); Assert.Null(matched); Assert.False(ambiguous);
    }

    [Fact]
    public void AChangedUsbAddressRecoversASetAsideUnitEvenWhenTheUnplugTickWasMissed()
    {
        var previous = DeviceManager.HungReconnectDelay;
        DeviceManager.HungReconnectDelay = TimeSpan.Zero;
        try
        {
            using var device = new FakeDevice("unitA") { Hanging = true };
            using var manager = Manager(() => [device]);
            for (int i = 0; i < HungTransferPolicy.Limit; i++) manager.SweepOnce();
            Assert.NotNull(manager.Warning); Assert.Null(manager.ActiveInfo);
            device.Hanging = false; device.MoveToAddress(7); manager.SweepOnce();
            Assert.Null(manager.Warning); Assert.Equal(device.Info.InstanceId, manager.ActiveInfo?.InstanceId);
            manager.SuspendSession();
        }
        finally { DeviceManager.HungReconnectDelay = previous; }
    }

    [Fact]
    public void ScopedDeviceConstructionNeverLeaksTheLocationToTheNextBackend()
    {
        using var selected = UsbTransport.At(Location("unitA"), () => new WaveXlrMk1Device());
        using var other = new WaveXlrMk1Device();
        Assert.NotNull(selected.Info.Location); Assert.Null(other.Info.Location);
        Assert.Throws<IOException>(() => UsbTransport.At<int>(Location("unitB"), () => throw new IOException("failed")));
        using var afterFailure = new WaveXlrMk1Device(); Assert.Null(afterFailure.Info.Location);
    }

    [Fact]
    public void AnExactOpenNeverFallsBackToTheFirstUnitOfTheModel()
    {
        using var backend = new FakeUsb();
        using var requests = new MemoryStream(); using var replies = new MemoryStream();
        UsbHelperProtocol.WriteFrame(requests, UsbHelperProtocol.Open(0x0fd9, 0x007d, Location("unitA", 8)));
        requests.Position = 0; UsbHelperProtocol.Serve(requests, replies, backend);
        Assert.Equal((byte)1, backend.Bus); Assert.Equal((byte)8, backend.Address); Assert.Equal(0, backend.ModelOpens);
        replies.Position = 0; Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(UsbHelperProtocol.ReadFrame(replies)!));
        // A transport that cannot open by address refuses.
        using var modelOnly = new ModelOnlyUsb(); using var refused = new MemoryStream();
        requests.Position = 0; UsbHelperProtocol.Serve(requests, refused, modelOnly);
        Assert.Equal(0, modelOnly.ModelOpens);
        refused.Position = 0; Assert.Equal(UsbHelperProtocol.NotOpened, BinaryPrimitives.ReadInt32LittleEndian(UsbHelperProtocol.ReadFrame(refused)!));
    }

    [Fact]
    public void ThePrimarySwitchesBetweenUnitsOfOneModelByInstanceId()
    {
        using var a = new FakeDevice("unitA"); using var b = new FakeDevice("unitB");
        using var manager = Manager(() => [a, b]);
        manager.SweepOnce(); Assert.True(a.Connected); Assert.False(b.Connected);
        Assert.Equal(2, manager.Detected().Count);
        Assert.Equal(2, manager.AttachedCount);
        Assert.NotNull(manager.SetActiveDevice("aaaa:007d"));
        Assert.Null(manager.SetActiveDevice(b.Info.InstanceId)); manager.SweepOnce();
        Assert.False(a.Connected); Assert.True(b.Connected); Assert.Equal(b.Info.InstanceId, manager.ActiveInfo?.InstanceId);
        // Two units of the model: the inputs follow B's serial and nothing else.
        Assert.Equal(["Wave_XLR_unitB-"], manager.ActiveNodeNameFragments);
        manager.SuspendSession(); manager.SweepOnce(); Assert.False(b.Connected);
        Assert.Empty(manager.ActiveNodeNameFragments);
    }

    [Fact]
    public void WhileTheChosenUnitIsAwayNoOtherUnitIsTakenOverButALoneUnitIsDriven()
    {
        using var a = new FakeDevice("unitA"); using var b = new FakeDevice("unitB");
        IReadOnlyList<IAudioDevice> attached = [a, b];
        using var manager = Manager(() => attached);
        manager.SweepOnce();
        Assert.Null(manager.SetActiveDevice(b.Info.InstanceId)); manager.SweepOnce();
        Assert.True(b.Connected);
        // B leaves; A may be an additional interface, so the primary waits for B.
        using var c = new FakeDevice("unitC");
        b.Unplug(); attached = [a, c];
        manager.SweepOnce(); manager.SweepOnce();
        Assert.Null(manager.ActiveInfo); Assert.False(a.Connected); Assert.False(c.Connected);
        // With one unit attached it is driven whatever its id, as before.
        attached = [a]; manager.SweepOnce();
        Assert.True(a.Connected);
        Assert.Equal(["Wave_XLR_unitA-", "Wave_XLR"], manager.ActiveNodeNameFragments);
        manager.SuspendSession();
    }

    [Fact]
    public async Task AdditionalInterfacesStayIsolatedThroughControlsProfilesAndAPrimaryHandoff()
    {
        using var a = new FakeDevice("unitA"); using var b = new FakeDevice("unitB");
        using var primary = Manager(() => [a, b]); primary.SweepOnce();
        using var interfaces = new WaveInterfaces(primary, NullLogger<DeviceManager>.Instance, Configuration, () => [a.Fork(), b.Fork()]);
        await interfaces.StartAsync(CancellationToken.None);
        try
        {
            Wait(() => interfaces.Snapshot().Count == 2);
            Assert.Null(interfaces.SetEnabled(b.Info.InstanceId, true));
            Wait(() => interfaces.Snapshot().Any(s => s.Id == b.Info.InstanceId && s.Connected));
            Assert.Equal([b.Info.InstanceId], JsonSerializer.Deserialize<string[]>(File.ReadAllText(PreferencesPath))!);
            Assert.Null(interfaces.Apply(b.Info.InstanceId, "gain", JsonSerializer.SerializeToElement(52)));
            Assert.Equal(52, b.Gain); Assert.Equal(30, a.Gain);

            var profile = interfaces.CaptureProfile(); Assert.Equal(52, profile[b.Info.InstanceId].GainDb);
            b.Gain = 20;
            Wait(() => interfaces.Snapshot().Single(s => s.Id == b.Info.InstanceId).State?.GainDb == 20);
            Assert.Null(interfaces.ApplyProfile(profile)); Assert.Equal(52, b.Gain); Assert.Equal(30, a.Gain);
            ProfileStore.Save("0fd9:007d", "Rig", new() { AdditionalDevices = profile });
            Assert.Equal(52, ProfileStore.Load("0fd9:007d", "Rig")!.AdditionalDevices![b.Info.InstanceId].GainDb);

            // The primary claims B: its additional manager lets go first, and
            // commands for B reach the primary, never another unit.
            Assert.Null(primary.SetActiveDevice(b.Info.InstanceId)); primary.SweepOnce();
            Wait(() => interfaces.Snapshot().Any(s => s.Id == b.Info.InstanceId && s.Active));
            Assert.Null(interfaces.Apply(b.Info.InstanceId, "gain", JsonSerializer.SerializeToElement(43)));
            Assert.Equal(43, b.Gain); Assert.False(a.Connected);
            Assert.Null(interfaces.SetEnabled(b.Info.InstanceId, false));
            Assert.True(b.Connected); // disabling the additional role cannot close the primary
        }
        finally { await interfaces.StopAsync(CancellationToken.None); primary.SuspendSession(); }
    }

    [Fact]
    public async Task TwoProsWithoutTellingSerialsCannotBeMadeAdditional()
    {
        using var primary = Manager(() => []);
        IAudioDevice[] pros = [new FakeDevice(null, port: "1-2", outputRouting: true), new FakeDevice(null, port: "1-3", outputRouting: true)];
        using var interfaces = new WaveInterfaces(primary, NullLogger<DeviceManager>.Instance, Configuration, () => pros);
        await interfaces.StartAsync(CancellationToken.None);
        try
        {
            Wait(() => interfaces.Snapshot().Count == 2);
            Assert.NotEqual(pros[0].Info.InstanceId, pros[1].Info.InstanceId);
            // Their cards are switched to pro-audio by name, and the name is the same.
            Assert.NotNull(interfaces.SetEnabled(pros[1].Info.InstanceId, true));
            Assert.False(File.Exists(PreferencesPath));
        }
        finally { await interfaces.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task OnlyNamesThatFindNoOtherUnitPickTheChannelsADisabledUnitTakesAlong()
    {
        var pro = new FakeDevice("proP", outputRouting: true);
        var named = new FakeDevice("xlrA", port: "1-3");
        var unnamed = new FakeDevice(null, port: "1-4");
        var dock = new FakeDevice("dockC", port: "1-5", model: "XLR Dock");
        using var primary = Manager(() => [pro]); primary.SweepOnce();
        using var interfaces = new WaveInterfaces(primary, NullLogger<DeviceManager>.Instance, Configuration, () => [pro, named, unnamed, dock]);
        await interfaces.StartAsync(CancellationToken.None);
        try
        {
            Wait(() => interfaces.Snapshot().Count == 4);
            Assert.Empty(interfaces.OwnSourceFragments(pro.Info.InstanceId));                       // the primary
            Assert.Equal(["Wave_XLR_xlrA-"], interfaces.OwnSourceFragments(named.Info.InstanceId)); // Wave_XLR is inside Wave_XLR_Pro
            Assert.Empty(interfaces.OwnSourceFragments(unnamed.Info.InstanceId));                   // only the shared model name
            Assert.Equal(["XLR_Dock_dockC-", "XLR_Dock"], interfaces.OwnSourceFragments(dock.Info.InstanceId));
            Assert.Empty(interfaces.OwnSourceFragments("0fd9:007d@0123456789abcdef"));             // not attached

            static ChannelStatus Channel(string id, string? source) => new(id, id, new Dictionary<string, double>(), [], CaptureSource: source);
            ChannelStatus[] channels =
            [
                Channel("dock", "alsa_input.usb-Elgato_Systems_Elgato_XLR_Dock_dockC-00.analog-stereo"),
                Channel("headset", "alsa_input.usb-headset-00.mono-fallback"),
                Channel("pro", "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_Pro_proP-00.pro-input-0"),
                Channel("music", null),
            ];
            Assert.Equal(["dock"], WaveInterfaces.CaptureChannelsFrom(channels, interfaces.OwnSourceFragments(dock.Info.InstanceId)));
            Assert.Empty(WaveInterfaces.CaptureChannelsFrom(channels, interfaces.OwnSourceFragments(named.Info.InstanceId)));
        }
        finally { await interfaces.StopAsync(CancellationToken.None); primary.SuspendSession(); }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("0fd9:007d")]
    [InlineData("0fd9:007d@xxxxxxxxxxxxxxxx")]
    public void InvalidInstanceIdsAreRefusedEverywhere(string id)
    {
        using var primary = Manager(() => []);
        using var interfaces = new WaveInterfaces(primary, NullLogger<DeviceManager>.Instance, Configuration, () => []);
        Assert.NotNull(interfaces.SetEnabled(id, false));
        Assert.NotNull(interfaces.Apply(id, "gain", JsonSerializer.SerializeToElement(30)));
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new Command { Cmd = "setWaveInterfaceEnabled", Device = id, Value = JsonSerializer.SerializeToElement(true) }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new Command { Cmd = "setWaveControl", Device = id, Control = "gain", Value = JsonSerializer.SerializeToElement(30) }, mixer, _ => null));
        ProfileStore.Save("0fd9:007d", "Invalid", new() { AdditionalDevices = new() { [id] = new() } });
        Assert.Throws<JsonException>(() => ProfileStore.Load("0fd9:007d", "Invalid"));
    }

    [Fact]
    public void UnreadableChoicesAreReportedAndTheNextChangeWritesThemAgain()
    {
        const string id = "0fd9:007d@0123456789abcdef";
        using var primary = Manager(() => []);
        OpenXLR.Core.OpenXlrPaths.WriteAtomic(PreferencesPath, "[\"bad\"]");
        using var interfaces = new WaveInterfaces(primary, NullLogger<DeviceManager>.Instance, Configuration, () => []);
        Assert.NotNull(interfaces.Warning);
        Assert.Empty(interfaces.Snapshot());
        Assert.Null(interfaces.SetEnabled(id, false));
        Assert.Null(interfaces.Warning);
        Assert.Equal("[]", File.ReadAllText(PreferencesPath));
    }

    [Fact]
    public void ARememberedUnpluggedUnitStaysListedAndCanBeForgotten()
    {
        const string id = "0fd9:007d@0123456789abcdef";
        using var primary = Manager(() => []);
        OpenXLR.Core.OpenXlrPaths.WriteAtomic(PreferencesPath, JsonSerializer.Serialize(new[] { id }));
        using var interfaces = new WaveInterfaces(primary, NullLogger<DeviceManager>.Instance, Configuration, () => []);
        var offline = Assert.Single(interfaces.Snapshot());
        Assert.Equal(id, offline.Id); Assert.True(offline.Enabled); Assert.False(offline.Connected);
        Assert.NotNull(interfaces.SetEnabled(id, true)); // not attached
        Assert.Null(interfaces.SetEnabled(id, false)); Assert.Empty(interfaces.Snapshot());
    }

    [Fact]
    public void TwoDocksEachFindTheirOwnAlsaCard()
    {
        string root = Path.Combine(_directory, "asound");
        void Card(int n, string usbid, string? bus)
        {
            string dir = Directory.CreateDirectory(Path.Combine(root, $"card{n}")).FullName;
            File.WriteAllText(Path.Combine(dir, "usbid"), usbid + "\n");
            if (bus is not null) File.WriteAllText(Path.Combine(dir, "usbbus"), bus + "\n");
        }
        Card(0, "046d:086b", "001/004");
        Card(2, "0fd9:00a6", "001/021");
        Card(5, "0fd9:00a6", "003/007");
        Directory.CreateSymbolicLink(Path.Combine(root, "Dock"), Path.Combine(root, "card2"));
        Assert.Equal(2, XlrDockDevice.FindCard(root, new UsbLocation(1, 21, "1-4", "dockA")));
        Assert.Equal(5, XlrDockDevice.FindCard(root, new UsbLocation(3, 7, "3-1", "dockB")));
        // Two docks and no way to tell which: neither card is guessed.
        Assert.Throws<InvalidOperationException>(() => XlrDockDevice.FindCard(root, new UsbLocation(2, 9, "2-1", null)));
        Assert.Throws<InvalidOperationException>(() => XlrDockDevice.FindCard(root, null));
        // One dock is its card, as before more than one unit was driven.
        Directory.Delete(Path.Combine(root, "card5"), true);
        Assert.Equal(2, XlrDockDevice.FindCard(root, new UsbLocation(2, 9, "2-1", null)));
        Assert.Equal(2, XlrDockDevice.FindCard(root, null));
    }

    [Fact]
    public void ADockBuiltAtALocationLooksUpTheCardAtThatLocation()
    {
        using var dock = UsbTransport.At(new UsbLocation(3, 7, "3-1", "dockB"), () => new XlrDockDevice());
        Assert.Equal(new UsbLocation(3, 7, "3-1", "dockB"), dock.Info.Location);
        Assert.EndsWith("@" + new UsbLocation(3, 7, "3-1", "dockB").Key, dock.Info.InstanceId);
    }

    private static void Wait(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)));

    private class ModelOnlyUsb : IUsbTransport
    {
        public int ModelOpens;
        public bool IsOpen => false;
        public bool Open(ushort vid, ushort pid) { ModelOpens++; return true; }
        public void Close() { }
        public int ControlTransfer(byte t, byte r, ushort v, ushort i, byte[] data, ushort len, uint timeout) => 0;
        public void Dispose() { }
    }

    private sealed class FakeUsb : ModelOnlyUsb, IUsbTransport
    {
        public byte Bus, Address;
        public bool Open(ushort vid, ushort pid, byte bus, byte address) { Bus = bus; Address = address; return true; }
    }

    private sealed class FakeDevice(string? serial, FakeDevice.Hardware? shared = null, bool retainsSettings = true,
        string port = "1-2", bool outputRouting = false, string? model = null) : IAudioDevice
    {
        public DeviceInfo Info { get; private set; } = new("Elgato", model ?? (outputRouting ? "Wave XLR Pro" : "Wave XLR"), 0x0fd9,
            model is not null ? (ushort)0x00a6 : outputRouting ? (ushort)0x00b4 : (ushort)0x007d)
            { Location = new(1, 2, port, serial) };
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true, RetainsSettings = retainsSettings, OutputRouting = outputRouting };
        public bool Connected { get; private set; }
        internal sealed class Hardware { public int Gain = 30; }
        private readonly Hardware _hardware = shared ?? new();
        public FakeDevice Fork() => new(serial, _hardware, Capabilities.RetainsSettings, port, outputRouting, model);
        public int Gain { get => _hardware.Gain; set => _hardware.Gain = value; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() => Connected = false;
        public void Unplug() => Connected = false;
        public bool Hanging;
        public void MoveToAddress(byte address) => Info = Info with { Location = Info.Location! with { Address = address } };
        public DeviceState ReadState() => Hanging ? throw new UsbHungException("hung") : new() { GainDb = Gain };
        public void SetGainDb(int db) => Gain = db;
        public void SetMute(bool on) { } public void SetLowCut(bool on) { } public void SetExpander(bool on) { }
        public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int value) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int value) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, true);
    }
}
