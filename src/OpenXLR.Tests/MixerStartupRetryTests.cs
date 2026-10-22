using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// A graph build that fails at startup, because pipewire-pulse does not
/// answer yet, is retried until the server answers, and the stop path joins
/// the retry. The audio server here is a fake pactl that refuses to load
/// modules until the test lets it.
/// </summary>
[Collection("xdg-config")]
public sealed class MixerStartupRetryTests
{
    [Fact]
    public void TheBackoffDoublesFromTwoSecondsAndStopsAtThirty()
    {
        Assert.Equal([2, 4, 8, 16, 30, 30, 30],
            Enumerable.Range(1, 7).Select(n => MixerService.BuildRetryDelay(n).TotalSeconds));
    }

    [Fact]
    public async Task AFailedStartupBuildIsRetriedUntilTheAudioServerAnswers()
    {
        using var fixture = new Fixture();
        int changes = 0;
        fixture.Service.Changed += () => Interlocked.Increment(ref changes);

        await fixture.Service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(fixture.Service.Initialized.IsCompleted);
            Assert.False(fixture.Service.Built);
            Assert.True(fixture.Service.WaitingForAudio);
            Assert.Contains("waiting for the audio server", fixture.Service.GraphWarning);
            Assert.Null(fixture.Service.Snapshot());
            Assert.Contains("waiting for the audio server", fixture.Service.Apply(new Command { Cmd = "setMixMuted", Mix = "monitor" }));
            // At least one retry is refused as well, so the loop keeps going.
            Assert.True(SpinWait.SpinUntil(() => fixture.Refusals() >= 3, TimeSpan.FromSeconds(10)));
            Assert.False(fixture.Service.Built);

            // A profile recalled meanwhile waits for the graph instead of failing.
            Assert.Null(fixture.Service.ApplyScene(new MixerScene { MixVolumes = new() { ["monitor"] = 0.25 } }));

            fixture.Answer();
            await fixture.Service.BuildRetry.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(fixture.Service.Built);
            Assert.False(fixture.Service.WaitingForAudio);
            Assert.Null(fixture.Service.GraphWarning);
            Assert.NotNull(fixture.Service.Snapshot());
            Assert.Equal(0.25, fixture.Service.ExportScene()!.MixVolumes["monitor"]);
            Assert.True(Volatile.Read(ref changes) >= 2, "clients were not told the graph is up");
        }
        finally { await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)); }

        Assert.False(fixture.Service.Built);
        Assert.Contains(fixture.Calls(), call => call.StartsWith("unload-module ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopEndsTheRetriesAndNothingIsBuiltAfterIt()
    {
        using var fixture = new Fixture();
        await fixture.Service.StartAsync(CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => fixture.Refusals() >= 2, TimeSpan.FromSeconds(10)));

        await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(fixture.Service.BuildRetry.IsCompleted);
        // The retry is joined by the stop: the server answering afterwards
        // finds nobody asking.
        string[] calls = fixture.Calls();
        fixture.Answer();
        fixture.Service.SweepOnce();
        Assert.Equal(calls, fixture.Calls());
        Assert.False(fixture.Service.Built);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenamingToTheCurrentNameSavesPendingSettings(bool mix)
    {
        using var fixture = new Fixture();
        fixture.Answer();
        await fixture.Service.StartAsync(CancellationToken.None);
        try
        {
            await fixture.PauseSweeps();
            fixture.Saver.Schedule();
            fixture.Saver.Flush();
            Assert.False(MixerSettings.Load()!.MonitorMuted);

            Assert.Null(fixture.Service.Apply(new Command
            {
                Cmd = "setMonitorMuted", Value = JsonSerializer.SerializeToElement(true),
            }));
            Assert.True(fixture.Saver.Pending);
            Assert.Null(fixture.Service.Apply(fixture.UnchangedRename(mix)));

            Assert.True(MixerSettings.Load()!.MonitorMuted);
            Assert.False(fixture.Saver.Pending);
            Assert.Null(fixture.Service.PersistenceWarning);
        }
        finally { await fixture.Service.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnchangedRenameCannotClearAFailedPendingSave(bool mix)
    {
        using var fixture = new Fixture();
        fixture.Answer();
        await fixture.Service.StartAsync(CancellationToken.None);
        try
        {
            await fixture.PauseSweeps();
            // A directory at the file path refuses writes even when tests
            // run as root; no permission assumption or timing is needed.
            Directory.CreateDirectory(MixerSettings.DefaultPath);
            Assert.Null(fixture.Service.Apply(new Command
            {
                Cmd = "setMonitorMuted", Value = JsonSerializer.SerializeToElement(true),
            }));
            fixture.Saver.Flush();
            Assert.True(fixture.Saver.Pending);
            string warning = Assert.IsType<string>(fixture.Service.PersistenceWarning);

            Assert.NotNull(fixture.Service.Apply(fixture.UnchangedRename(mix)));
            Assert.True(fixture.Saver.Pending);
            Assert.Equal(warning, fixture.Service.PersistenceWarning);

            Directory.Delete(MixerSettings.DefaultPath);
            Assert.Null(fixture.Service.Apply(fixture.UnchangedRename(mix)));
            Assert.True(MixerSettings.Load()!.MonitorMuted);
            Assert.False(fixture.Saver.Pending);
            Assert.Null(fixture.Service.PersistenceWarning);
        }
        finally { await fixture.Service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task TheWindowSaysTheMixerIsWaitingRatherThanOff()
    {
        await using var client = new OpenXLR.UI.DaemonClient();
        var model = new OpenXLR.UI.MainViewModel(client);
        void Apply(string json) => typeof(OpenXLR.UI.MainViewModel)
            .GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(JsonNode)])!
            .Invoke(model, [JsonNode.Parse(json)]);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Apply("""{"connected":false,"mixer":null,"mixerWaiting":true}""");
        Assert.Equal(OpenXLR.UI.Localization.Localizer.Text("MixerWaitingForAudioServer"), model.MixerPlaceholder);
        Assert.Contains(nameof(model.MixerPlaceholder), raised);

        Apply("""{"connected":false,"mixer":null}""");
        Assert.Equal(OpenXLR.UI.Localization.Localizer.Text("SubmixerIsOffTurnItOnInOptions"), model.MixerPlaceholder);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-retry-").FullName;
        private readonly string? _oldPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly DeviceManager _devices;
        internal MixerService Service { get; }
        internal SettingsSaver Saver => (SettingsSaver)typeof(MixerService)
            .GetField("_saves", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service)!;

        internal Command UnchangedRename(bool mix)
        {
            MixerState state = Service.Snapshot()!;
            return mix
                ? new Command { Cmd = "renameMix", Mix = "monitor", Name = state.Mixes.Single(m => m.Id == "monitor").Name }
                : new Command { Cmd = "renameChannel", Channel = "game", Name = state.Channels.Single(c => c.Id == "game").Name };
        }

        internal async Task PauseSweeps()
        {
            // These tests drive persistence explicitly. The fake graph has
            // no published sinks, so its periodic lost-graph probe is paused.
            FieldInfo field = typeof(MixerService).GetField("_streamSweep", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var timer = (Timer?)field.GetValue(Service);
            field.SetValue(Service, null);
            if (timer is not null) await timer.DisposeAsync();
        }

        internal Fixture()
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_directory, "config"));
            new DaemonSettings { Submixer = true }.Save();
            // The registry subscription never becomes ready, so the adapter
            // reads one-shot dumps of an empty graph and waits for no echo.
            ExecutableScript.Write(Path.Combine(_directory, "pw-dump"), """
                if [ "$1" = "--monitor" ]; then echo '[]'; exec /bin/sleep 60; fi
                echo '[]'
                """);
            ExecutableScript.Write(Path.Combine(_directory, "pw-link"), "exit 0");
            ExecutableScript.Write(Path.Combine(_directory, "parec"), "exit 0");
            ExecutableScript.Write(Path.Combine(_directory, "pactl"), """
                dir=${0%/*}
                echo "$*" >> "$dir/calls"
                case "$1" in
                  load-module)
                    if [ ! -e "$dir/answer" ]; then
                      echo refused >> "$dir/refusals"
                      echo 'Connection failure: Timeout' >&2
                      exit 1
                    fi
                    n=$(/bin/cat "$dir/next" 2>/dev/null || echo 100)
                    n=$((n + 1))
                    echo "$n" > "$dir/next"
                    echo "$n" ;;
                  get-default-sink) echo desk ;;
                  get-default-source) echo mic ;;
                esac
                exit 0
                """);
            Environment.SetEnvironmentVariable("PATH", _directory);
            var config = new ConfigurationBuilder().Build();
            _devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            Service = new MixerService(NullLogger<MixerService>.Instance, config, _devices,
                startupDefaults: new StartupDefaults("desk", "mic"))
            {
                RetryDelay = _ => TimeSpan.FromMilliseconds(20),
            };
        }

        internal void Answer() => File.WriteAllText(Path.Combine(_directory, "answer"), "");
        internal int Refusals() => Lines("refusals").Length;
        internal string[] Calls() => Lines("calls");
        private string[] Lines(string name)
            => File.Exists(Path.Combine(_directory, name)) ? File.ReadAllLines(Path.Combine(_directory, name)) : [];

        public void Dispose()
        {
            Service.Dispose();
            _devices.Dispose();
            Environment.SetEnvironmentVariable("PATH", _oldPath);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
