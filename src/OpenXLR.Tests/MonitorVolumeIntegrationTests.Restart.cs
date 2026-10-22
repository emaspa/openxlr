using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public async Task ServiceShutdownAndRestartKeepEditedMonitorsAndTheReplacedListeningSelection()
    {
        Assert.StartsWith("openxlr-monitor-test-", Path.GetFileName(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!));
        const string first = "service_restart_phones", second = "service_restart_speakers";
        string directory = Directory.CreateTempSubdirectory("openxlr-monitor-service-restart-").FullName;
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? oldOutput = Environment.GetEnvironmentVariable("OPENXLR_MONITOR_OUTPUT");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
        Environment.SetEnvironmentVariable("OPENXLR_MONITOR_OUTPUT", null);
        var outputs = new PipeWireAdapter();
        try
        {
            outputs.CreateNullSink(first, "Restart phones");
            outputs.CreateNullSink(second, "Restart speakers");
            new DaemonSettings { Submixer = true }.Save();
            Assert.Null(new MixerSettings { UserChannels = [new("test", "Test")], UserMixes = [] }.Save());
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            using var lifetime = new StopLifetime();

            await Run(async (service, hub) =>
            {
                await Send(hub, """{"cmd":"createMix","name":"Headset","kind":"monitor"}""");
                await Send(hub, $$"""{"cmd":"setMonitorOutputs","devices":["{{first}}","{{second}}"]}""");
                await Send(hub, $$"""{"cmd":"setMonitorFeed","device":"{{first}}","mix":"monitor+headset"}""");
                await Send(hub, $$"""{"cmd":"setMonitorFeed","device":"{{second}}","mix":"monitor2+headset"}""");
                await Send(hub, """{"cmd":"setMixMuted","mix":"monitor2","value":true}""");
                await Send(hub, """{"cmd":"setMonitorMuted","value":true}""");
                await Send(hub, """{"cmd":"renameMix","mix":"headset","name":"Renamed headphones"}""");
                await Send(hub, """{"cmd":"deleteMix","mix":"monitor"}""");
                await Send(hub, """{"cmd":"createMix","name":"Spare","kind":"monitor"}""");
                // These last edits use the debounce. Stop must flush them before teardown.
                await Send(hub, """{"cmd":"setMixVolume","mix":"headset","value":0.73}""");
                await Send(hub, """{"cmd":"setLevel","channel":"test","mix":"headset","value":0.41}""");
                await Send(hub, """{"cmd":"setChannelVolume","channel":"test","value":0.64}""");
                Assert.True(service.Snapshot()!.MonitorMuted);
            });
            AssertSaved(paused: true);

            await Run(async (service, hub) =>
            {
                AssertRestored(service, paused: true);
                Assert.True(outputs.GetSinkMuted(first));
                Assert.True(outputs.GetSinkMuted(second));
                // Explicitly opening one monitor replaces the paused selection.
                await Send(hub, """{"cmd":"setMixMuted","mix":"headset","value":false}""");
                AssertRestored(service, paused: false);
                Assert.False(outputs.GetSinkMuted(first));
                Assert.False(outputs.GetSinkMuted(second));
                await Send(hub, """{"cmd":"toggleMonitorMuted"}""");
            });
            AssertSaved(paused: true);

            await Run(async (service, hub) =>
            {
                AssertRestored(service, paused: true);
                await Send(hub, """{"cmd":"toggleMonitorMuted"}""");
                AssertRestored(service, paused: false);
                Assert.False(outputs.GetSinkMuted(first));
                Assert.False(outputs.GetSinkMuted(second));
            });
            AssertSaved(paused: false);

            async Task Run(Func<MixerService, WebSocketHub, Task> body)
            {
                using var service = new MixerService(NullLogger<MixerService>.Instance, config, devices);
                var hub = new WebSocketHub(devices, service, NullLogger<WebSocketHub>.Instance, lifetime);
                try
                {
                    await service.StartAsync(CancellationToken.None);
                    Assert.True(service.Built);
                    await body(service, hub);
                }
                finally { await service.StopAsync(CancellationToken.None); }
            }

            static async Task Send(WebSocketHub hub, string command)
            {
                ApiCommandResult result = await hub.ExecuteForApiAsync(command);
                Assert.True(result.Ok, command + ": " + string.Join(", ", result.Messages));
            }

            static void AssertRestored(MixerService service, bool paused)
            {
                MixerState state = Assert.IsType<MixerState>(service.Snapshot());
                Assert.Equal(paused, state.MonitorMuted);
                Assert.Equal([first, second], state.MonitorOutputs);
                Assert.Equal("headset", state.DefaultMonitorMix);
                Assert.Equal("headset", state.MonitorFeeds.GetValueOrDefault(first, state.DefaultMonitorMix!));
                Assert.Equal("monitor2+headset", state.MonitorFeeds[second]);
                Assert.DoesNotContain(state.Mixes, mix => mix.Id == "monitor");
                Assert.Equal("Renamed headphones", state.Mixes.Single(mix => mix.Id == "headset").Name);
                Assert.Equal(.73, state.Mixes.Single(mix => mix.Id == "headset").Volume);
                Assert.False(state.Mixes.Single(mix => mix.Id == "headset").Muted);
                Assert.True(state.Mixes.Single(mix => mix.Id == "monitor2").Muted);
                Assert.True(state.Mixes.Single(mix => mix.Id == "spare").Muted);
                ChannelStatus channel = Assert.Single(state.Channels, channel => channel.Id == "test");
                Assert.Equal(.64, channel.Volume);
                Assert.Equal(.41, channel.Levels["headset"]);
            }

            static void AssertSaved(bool paused)
            {
                MixerSettings saved = Assert.IsType<MixerSettings>(MixerSettings.Load(null, out string? warning));
                Assert.Null(warning);
                Assert.Equal(paused, saved.MonitorMuted);
                Assert.Equal([first, second], saved.MonitorOutputs);
                Assert.Equal(["monitor2", "spare"], saved.MixMuted.Order());
                Assert.Equal(.73, saved.MixVolumes["headset"]);
                Assert.Equal(.41, saved.Levels["test|headset"]);
                Assert.Equal(.64, saved.ChannelVolumes["test"]);
                Assert.Equal("Renamed headphones", saved.Mixes!.Single(mix => mix.Id == "headset").Name);
                Assert.DoesNotContain(saved.Mixes!, mix => mix.Id == "monitor");
            }
        }
        finally
        {
            outputs.TearDown();
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
            Environment.SetEnvironmentVariable("OPENXLR_MONITOR_OUTPUT", oldOutput);
            Directory.Delete(directory, true);
        }
    }
}
