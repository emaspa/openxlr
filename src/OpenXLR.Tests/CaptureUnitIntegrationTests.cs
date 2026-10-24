using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void CaptureUnitIsLearnedOnceWithoutChangingTheRunningChain()
    {
        const string unit = "0fd9:0070@0123456789abcdef";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = CaptureChainFixture.Build(pw);
        mixer.SetInserts("capture", [CaptureChainFixture.Gain(1)]);
        CaptureChainFixture.WaitForChain(pw, "capture");
        CaptureChainFixture.Wait(() => ChainFeed("capture_test_source", "capture") is [_]
            && !mixer.EnsureInputFeeds() && !mixer.EnsureFilterRoutes());
        string[] nodes = CaptureChainFixture.Nodes(pw, "capture");
        string head = ChainHead(Assert.Single(ChainFeed("capture_test_source", "capture")));
        uint[] links = IncomingRouteLinks(head);
        Assert.False(mixer.MigrateCaptureInputs(_ => null));
        Assert.True(mixer.MigrateCaptureInputs(source => source == "capture_test_source" ? unit : null));
        Assert.False(mixer.MigrateCaptureInputs(_ => "0fd9:0070@fedcba9876543210"));
        Assert.Equal(nodes, CaptureChainFixture.Nodes(pw, "capture"));
        Assert.Equal(links, IncomingRouteLinks(head));
        Assert.Equal(unit, mixer.ExportSettings().UserChannels!.Single(c => c.Id == "capture").CaptureUnit);
        Assert.Equal(unit, mixer.Snapshot().Channels.Single(c => c.Id == "capture").Binding.Unit);
        Assert.Single(mixer.ExportSettings().Inserts["capture"]);
    }

    [MonitorPipeWireFact]
    public void ACaptureWithABareModelIdRefusesToFollowIndistinguishableUnits()
    {
        const string source = "alsa_input.usb-Elgato_Wave_3-00.mono-fallback";
        const string renamed = "alsa_input.usb-Elgato_Wave_3-00.analog-stereo";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        pw.CreateNullSink("capture_ambiguous_signal", "Capture signal");
        uint node = pw.CreateVirtualMic(source, "capture_ambiguous_signal.monitor", "Wave:3 mono");
        using var mixer = new Mixer(pw);
        mixer.SetAttachedUnits([
            new("0fd9:0070@0123456789abcdef", [], 1, false, false),
            new("0fd9:0070@fedcba9876543210", [], 1, false, false),
        ]);
        mixer.Build(new MixerConfig
        {
            Channels = [new("system", "System"), new("mic", "Mic")
                { CaptureSource = source, CaptureUnit = "0fd9:0070", CaptureMonoChannel = 0 }],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
        });
        ChannelStatus Capture() => mixer.Snapshot().Channels.Single(c => c.Id == "mic");
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); return Capture().BindingConnected; });
        pw.UnloadModule(node);
        pw.CreateVirtualMic(renamed, "capture_ambiguous_signal.monitor", "Wave:3 stereo");
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); return Capture().BindingWarning?.Contains("more than one attached interface", StringComparison.Ordinal) == true; });
        Assert.False(Capture().BindingConnected);
        Assert.Null(Capture().UnitInput);
        Assert.Empty(GraphLinksInto("OpenXLR_ch_mic"));
        Assert.Equal("0fd9:0070", Capture().Binding.Unit);

        // Discovery may expose the same bare id twice when neither unit has a location.
        mixer.SetAttachedUnits([new("0fd9:0070", ["Wave_3"], 1, false, true), new("0fd9:0070", ["Wave_3"], 1, false, true)]);
        mixer.SetDrivenUnit("0fd9:0070");
        mixer.EnsureInputFeeds();
        Assert.False(Capture().BindingConnected);
        Assert.Null(Capture().UnitInput);
    }

    [MonitorPipeWireFact]
    public async Task CaptureInputsRememberTheirUnitFollowItsNodeAndKeepTheirChainsWhenDisabled()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-capture-unit-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        const string card = "usb-Elgato_Systems_Elgato_Wave_3_w3A-00";
        const string original = "alsa_input." + card + ".mono-fallback";
        const string renamed = "alsa_input." + card + ".analog-stereo";
        const string split = "alsa_input." + card + ".second-source";
        try
        {
            new DaemonSettings { Submixer = true }.Save();
            new MixerSettings { UserChannels = [new("system", "System"), new("legacy", "Legacy mic", original, 0, 0)], UserMixes = [] }.Save();
            pw.CreateNullSink("capture_unit_signal", "Capture unit signal");
            uint node = pw.CreateVirtualMic(original, "capture_unit_signal.monitor", "Wave:3 mono");
            WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == original));
            var primary = new Unit("Wave XLR", 0x007d, "unitP");
            var wave3 = new Unit("Wave:3", 0x0070, "w3A", xlrJacks: false);
            IReadOnlyList<IAudioDevice> attached = [primary, wave3];
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => attached);
            devices.SweepOnce();
            using var lifetime = new TestLifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            using var interfaces = new WaveInterfaces(devices, NullLogger<DeviceManager>.Instance, config,
                () => attached.Select(d => (IAudioDevice)new Unit(d.Info.Model, d.Info.ProductId,
                    d == primary ? "unitP" : "w3A", xlrJacks: d == primary)).ToArray());
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime, interfaces);
            await mixer.StartAsync(CancellationToken.None);
            await interfaces.StartAsync(CancellationToken.None);
            try
            {
                WaitForRecovery(() => interfaces.Snapshot().Count == 2 && mixer.Built);
                string id = wave3.Info.InstanceId;
                ChannelStatus Channel(string key) => mixer.Snapshot()!.Channels.Single(c => c.Id == key);
                async Task Ok(string command)
                {
                    ApiCommandResult result = await hub.ExecuteForApiAsync(command);
                    string refusal = string.Join("; ", result.Messages.Select(m => m switch
                    {
                        ErrorMessage e => e.Message,
                        CommandResultMessage { Error: { } error } => error,
                        _ => null,
                    }).OfType<string>());
                    Assert.True(result.Ok, command + "\nrefused: " + refusal);
                }
                void SweepUntil(Func<bool> condition) => CaptureChainFixture.Wait(() => { mixer.SweepOnce(); return condition(); });
                void FedFrom(string key, string source, bool chained)
                {
                    SweepUntil(() =>
                    {
                        // A chain's head is a filter-chain or a native host node, named by its host.
                        string? target = !chained ? "OpenXLR_ch_" + key : ChainFeed(source, key) is [var feed] ? ChainHead(feed) : null;
                        if (target is null) return false;
                        var links = GraphLinksInto(target);
                        return links.Length == (chained ? 1 : 2) && links.All(l => l.From.StartsWith(source + ":", StringComparison.Ordinal))
                            && Channel(key).BindingConnected;
                    });
                    Assert.Null(Channel(key).BindingWarning);
                }

                // An existing capture learns its unit on first sight and saves it.
                SweepUntil(() => Channel("legacy").Binding.Unit == id);
                WaitForRecovery(() => MixerSettings.Load()!.UserChannels!.Single(c => c.Id == "legacy").CaptureUnit == id);
                await Ok($$"""{"cmd":"createCaptureChannel","name":"New mic","source":"{{original}}","captureMonoChannel":0}""");
                Assert.Equal(id, Channel("new-mic").Binding.Unit);
                Assert.Equal(id, MixerSettings.Load()!.UserChannels!.Single(c => c.Id == "new-mic").CaptureUnit);
                await Ok($$"""{"cmd":"setWaveInterfaceEnabled","device":"{{id}}","value":true}""");
                SweepUntil(() => Channel("legacy").UnitInput == new UnitInputStatus(id, 1, false));
                Assert.Null(Channel("new-mic").UnitInput);
                FedFrom("legacy", original, false);

                InsertDefinition insert = CaptureChainFixture.Gain(1);
                await Ok(System.Text.Json.JsonSerializer.Serialize(new { cmd = "setInserts", channel = "legacy", inserts = new[] { insert } },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
                FedFrom("legacy", original, true);
                int? combine = pw.FindNodeId("OpenXLR_ch_legacy");
                // Rebinding through the command clears the old owner, then
                // learns the selected supported unit just as creation does.
                uint plain = pw.CreateVirtualMic("capture_rebind_plain", "capture_unit_signal.monitor", "Plain microphone");
                WaitForRecovery(() => mixer.Devices()!.Any(d => d.Name == "capture_rebind_plain" && d.Kind == AudioNodeKind.Source && !d.IsOwn));
                await Ok("""{"cmd":"setCaptureSource","channel":"legacy","source":"capture_rebind_plain","captureMonoChannel":0}""");
                FedFrom("legacy", "capture_rebind_plain", true);
                Assert.Null(Channel("legacy").Binding.Unit);
                Assert.Null(Channel("legacy").UnitInput);
                Assert.Null(MixerSettings.Load()!.UserChannels!.Single(c => c.Id == "legacy").CaptureUnit);
                await Ok($$"""{"cmd":"setCaptureSource","channel":"legacy","source":"{{original}}","captureMonoChannel":0}""");
                FedFrom("legacy", original, true);
                Assert.Equal(id, Channel("legacy").Binding.Unit);
                Assert.Equal(id, MixerSettings.Load()!.UserChannels!.Single(c => c.Id == "legacy").CaptureUnit);
                Assert.Equal(insert.Plugin, Assert.Single(mixer.Snapshot()!.Inserts["legacy"]).Insert.Plugin);
                Assert.Equal(combine, pw.FindNodeId("OpenXLR_ch_legacy"));
                pw.UnloadModule(plain);
                pw.UnloadModule(node);
                SweepUntil(() => !Channel("legacy").BindingConnected && CaptureChainFixture.Nodes(pw, "legacy").Length == 0);
                node = pw.CreateVirtualMic(renamed, "capture_unit_signal.monitor", "Wave:3 stereo");
                FedFrom("legacy", renamed, true);
                FedFrom("new-mic", renamed, false);
                Assert.Equal(combine, pw.FindNodeId("OpenXLR_ch_legacy"));
                Assert.Equal(new ChannelBinding("source", original, 0, 0, id), Channel("legacy").Binding);
                Assert.Equal(insert.Plugin, Assert.Single(mixer.Snapshot()!.Inserts["legacy"]).Insert.Plugin);
                Assert.Equal(new UnitInputStatus(id, 1, false), Channel("legacy").UnitInput);

                // A second source on the card invalidates even a healthy followed feed.
                uint second = pw.CreateVirtualMic(split, "capture_unit_signal.monitor", "Wave:3 split");
                SweepUntil(() => !Channel("legacy").BindingConnected && !Channel("new-mic").BindingConnected);
                Assert.Contains("2 sources", Channel("legacy").BindingWarning);
                Assert.Contains("2 sources", Channel("new-mic").BindingWarning);
                Assert.Empty(GraphLinksInto("OpenXLR_ch_new-mic"));
                Assert.Equal(insert.Plugin, Assert.Single(mixer.Snapshot()!.Inserts["legacy"]).Insert.Plugin);

                // The exact name wins, even with other sources on the same card.
                uint exact = pw.CreateVirtualMic(original, "capture_unit_signal.monitor", "Wave:3 mono returned");
                FedFrom("legacy", original, true);
                FedFrom("new-mic", original, false);
                pw.UnloadModule(second);
                pw.UnloadModule(exact);
                FedFrom("legacy", renamed, true);
                FedFrom("new-mic", renamed, false);
                exact = pw.CreateVirtualMic(original, "capture_unit_signal.monitor", "Wave:3 mono returned again");
                FedFrom("legacy", original, true);
                FedFrom("new-mic", original, false);

                string[] chain = CaptureChainFixture.Nodes(pw, "legacy");
                await Ok($$"""{"cmd":"setWaveInterfaceEnabled","device":"{{id}}","value":false,"keepChannels":true}""");
                SweepUntil(() => Channel("legacy").UnitInput is null);
                FedFrom("legacy", original, true);
                Assert.Equal(chain, CaptureChainFixture.Nodes(pw, "legacy"));
                Assert.Equal(2, MixerSettings.Load()!.UserChannels!.Count(c => c.CaptureUnit == id));
                // The chain is saved by the debounced writer, not with the command.
                WaitForRecovery(() => MixerSettings.Load()!.Inserts.GetValueOrDefault("legacy") is [_]);

                // Remembered unit ids still find the channels after unplugging.
                await Ok($$"""{"cmd":"setWaveInterfaceEnabled","device":"{{id}}","value":true}""");
                attached = [primary];
                devices.SweepOnce();
                WaitForRecovery(() => interfaces.Snapshot().Any(u => u.Id == id && !u.Connected));
                pw.UnloadModule(node);
                pw.UnloadModule(exact);
                SweepUntil(() => !Channel("legacy").BindingConnected && !Channel("new-mic").BindingConnected);
                Assert.Null(Channel("legacy").UnitInput);
                await Ok($$"""{"cmd":"setWaveInterfaceEnabled","device":"{{id}}","value":false}""");
                Assert.DoesNotContain(mixer.Snapshot()!.Channels, c => c.CaptureSource is not null);
                Assert.False(MixerSettings.Load()!.Inserts.ContainsKey("legacy"));
                WaitForRecovery(() => CaptureChainFixture.Nodes(pw, "legacy").Length == 0 && pw.FindNodeId("OpenXLR_ch_legacy") is null);

                // Clearing the primary's additional preference never deletes its inputs.
                const string primarySource = "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_unitP-00.analog-stereo";
                pw.CreateVirtualMic(primarySource, "capture_unit_signal.monitor", "Primary microphone");
                // Creation checks the daemon's registry, which can lag behind
                // this test adapter's independent subscription.
                WaitForRecovery(() => mixer.Devices()!.Any(d => d.Name == primarySource && d.Kind == AudioNodeKind.Source && !d.IsOwn));
                await Ok($$"""{"cmd":"createCaptureChannel","name":"Primary mic","source":"{{primarySource}}","captureMonoChannel":0}""");
                Assert.Equal(primary.Info.InstanceId, Channel("primary-mic").Binding.Unit);
                await Ok($$"""{"cmd":"setWaveInterfaceEnabled","device":"{{primary.Info.InstanceId}}","value":false}""");
                Assert.Equal(primary.Info.InstanceId, Channel("primary-mic").Binding.Unit);
                Assert.Contains(MixerSettings.Load()!.UserChannels!, c => c.Id == "primary-mic");
            }
            finally
            {
                await interfaces.StopAsync(CancellationToken.None);
                await mixer.StopAsync(CancellationToken.None);
                devices.SuspendSession();
            }
        }
        finally
        {
            pw.TearDown();
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The links from a source into the nodes of a channel's insert chain.</summary>
    private static (string From, string To)[] ChainFeed(string source, string key)
    {
        using var graph = PipeWireSnapshot.Parse(ProcessRunner.Run("pw-dump", []).Stdout);
        return [.. PipeWireAdapter.ParseGraphLinks(graph.RootElement.EnumerateArray())
            .Where(link => link.From.StartsWith(source + ":", StringComparison.Ordinal)
                && link.To.StartsWith($"OpenXLR_lc_{key}_", StringComparison.Ordinal))];
    }

    private static string ChainHead((string From, string To) link) => link.To[..link.To.LastIndexOf(':')];
}
