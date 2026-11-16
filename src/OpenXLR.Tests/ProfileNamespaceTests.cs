using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// Which directory profiles live in: the connected unit's, else
/// <c>generic</c>, whatever the strips are bound to. Profiles, last settings
/// and mixer.json live under the configuration directory, which these tests
/// redirect.
/// </summary>
[Collection("xdg-config")]
public sealed class ProfileNamespaceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-profile-namespace-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public ProfileNamespaceTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
        new DaemonSettings { Submixer = true }.Save();
        // XLR 1 on a source changes nothing about the namespace.
        Assert.Null(new MixerSettings { InputBindings = new() { ["xlr1"] = new("webcam", 0, 0) } }.Save());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task AConnectedUnitOwnsTheProfilesWhileAStripIsBound()
    {
        var wave = new Unit();
        var attached = new List<IAudioDevice> { wave };
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [.. attached]);
        using var mixer = new MixerService(NullLogger<MixerService>.Instance, new ConfigurationBuilder().Build(), devices);
        var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, new Lifetime());
        ProfileStore.Save("generic", "Desk scene", new Profile { Mixer = new MixerScene() });
        devices.SweepOnce();
        Assert.True(wave.Connected);

        Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"saveProfile","name":"Podcast"}""")).Ok);
        Profile saved = ProfileStore.Load("0fd9:007d", "Podcast")!;
        Assert.Equal(30, saved.Device!.GainDb);
        Assert.Null(ProfileStore.Load("generic", "Podcast"));
        Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"setRecallOnConnect","name":"Podcast"}""")).Ok);
        StateMessage state = hub.Snapshot();
        Assert.Equal(["Podcast"], state.Profiles);
        Assert.Equal("Podcast", state.RecallOnConnect);
        Assert.Equal("Podcast", state.ActiveProfile);
        Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Podcast"}""")).Ok);
        Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Desk scene"}""")).Ok);

        // Unplugged: the list switches to generic, as it switches between two units.
        attached.Clear();
        wave.Disconnect();
        devices.SweepOnce();
        Assert.False(devices.Snapshot().Connected);
        state = hub.Snapshot();
        Assert.Equal(["Desk scene"], state.Profiles);
        Assert.Null(state.RecallOnConnect);
        Assert.Null(state.ActiveProfile);

        // Plugged back in, the unit's list and recall return.
        attached.Add(wave);
        devices.SweepOnce();
        state = hub.Snapshot();
        Assert.Equal(["Podcast"], state.Profiles);
        Assert.Equal("Podcast", state.RecallOnConnect);
        devices.SuspendSession();
    }

    [Fact]
    public async Task WithoutAUnitProfilesAreGenericAndHoldOnlyTheMixer()
    {
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => []);
        using var mixer = new MixerService(NullLogger<MixerService>.Instance, new ConfigurationBuilder().Build(), devices);
        var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, new Lifetime());
        devices.SweepOnce();
        ProfileStore.Save("generic", "Desk scene", new Profile { Mixer = new MixerScene() });
        ProfileStore.Save("0fd9:007d", "Podcast", new Profile { Device = new() { GainDb = 30 } });

        StateMessage state = hub.Snapshot();
        Assert.False(state.Connected);
        Assert.Equal(["Desk scene"], state.Profiles);
        Assert.Null(state.RecallOnConnect);

        // Recall on connect belongs to a unit.
        ApiCommandResult recall = await hub.ExecuteForApiAsync("""{"cmd":"setRecallOnConnect","name":"Desk scene"}""");
        Assert.False(recall.Ok);
        Assert.Contains("needs a connected interface", Error(recall));
        Assert.Null(ProfileStore.RecallOnConnect("generic"));
        // A generic profile is the mixer scene, so it needs the mixer running.
        ApiCommandResult save = await hub.ExecuteForApiAsync("""{"cmd":"saveProfile","name":"Desk scene"}""");
        Assert.False(save.Ok);
        Assert.Contains("only the mixer", Error(save));
        Assert.NotNull(ProfileStore.Load("generic", "Desk scene")!.Mixer);
        Assert.Contains("mixer not built", Error(await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Desk scene"}""")));
        Assert.Contains("no profile named", Error(await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Podcast"}""")));
        Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"deleteProfile","name":"Desk scene"}""")).Ok);
        Assert.Empty(hub.Snapshot().Profiles!);
    }

    [Fact]
    public async Task AProfileIsNotSavedWhileTheMixerIsBeingBuilt()
    {
        var wave = new Unit();
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [wave]);
        using var mixer = new MixerService(NullLogger<MixerService>.Instance, new ConfigurationBuilder().Build(), devices);
        var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, new Lifetime());
        devices.SweepOnce();
        var scene = new MixerScene { MixVolumes = new() { ["monitor"] = 0.4 } };
        ProfileStore.Save("0fd9:007d", "Podcast", new Profile { Device = new() { GainDb = 30 }, Mixer = scene });
        FieldInfo building = typeof(MixerService).GetField("_building", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // A build under way: the device part could be saved, but the mixer
        // part would be lost, so the existing profile is left as it was.
        building.SetValue(mixer, true);
        ApiCommandResult save = await hub.ExecuteForApiAsync("""{"cmd":"saveProfile","name":"Podcast"}""");
        Assert.False(save.Ok);
        Assert.Contains("still being built", Error(save));
        Assert.Equal(0.4, ProfileStore.Load("0fd9:007d", "Podcast")!.Mixer!.MixVolumes["monitor"]);

        // With no build under way and no graph a profile holds the device alone, as before.
        building.SetValue(mixer, false);
        Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"saveProfile","name":"Podcast"}""")).Ok);
        Assert.Null(ProfileStore.Load("0fd9:007d", "Podcast")!.Mixer);
        devices.SuspendSession();
    }

    private static string Error(ApiCommandResult result)
        => string.Join(" ", result.Messages.Select(m => m switch
        {
            ErrorMessage e => e.Message,
            CommandResultMessage c => c.Error,
            _ => null,
        }).OfType<string>());

    private sealed class Unit : IAudioDevice
    {
        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR", 0x0fd9, 0x007d);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true };
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() => Connected = false;
        public DeviceState ReadState() => new() { GainDb = 30 };
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
