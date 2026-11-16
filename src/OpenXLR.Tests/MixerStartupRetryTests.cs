using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
            Assert.False(fixture.Service.RebuildingGraph);
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
            Assert.Equal(0.25, fixture.Service.ExportScene().Scene!.MixVolumes["monitor"]);
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

    [Fact]
    public async Task AFailedBuildAttemptLeavesTheSavedSettingsOnDisk()
    {
        var port = new FailingDuckingPort();
        // One failed attempt after the server answers, then no more until the stop.
        using var fixture = new Fixture(port, _ => port.Thrown > 0 ? TimeSpan.FromHours(1) : TimeSpan.FromMilliseconds(20));
        Assert.Null(new MixerSettings { MixVolumes = new() { ["monitor"] = 0.6 }, MonitorMuted = true }.Save());
        byte[] saved = File.ReadAllBytes(MixerSettings.DefaultPath);
        string? during = "not asked";
        port.DuringBuild = () => during = fixture.Service.Apply(new Command
        {
            Cmd = "setMixVolume", Mix = "monitor", Value = JsonSerializer.SerializeToElement(0.1),
        });

        await fixture.Service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(fixture.Service.WaitingForAudio);
            // The profile recalled while waiting is applied, and a save
            // scheduled, part way through the attempt that then fails.
            Assert.Null(fixture.Service.ApplyScene(new MixerScene { MixVolumes = new() { ["monitor"] = 0.25 } }));
            port.Fail = true;
            fixture.Answer();
            Assert.True(SpinWait.SpinUntil(() => port.Thrown > 0 && fixture.Service.WaitingForAudio, TimeSpan.FromSeconds(10)));
            Assert.False(fixture.Service.Built);

            bool pending = fixture.Saver.Pending;
            fixture.Saver.Flush();
            Assert.Equal(saved, File.ReadAllBytes(MixerSettings.DefaultPath));
            Assert.False(pending);
            // A command sent while the graph existed but the build had not ended was refused.
            Assert.Contains("still being built", during);
        }
        finally { await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)); }
        Assert.Equal(saved, File.ReadAllBytes(MixerSettings.DefaultPath));
    }

    [Fact]
    public async Task ALostGraphIsBuiltAgainInPlaceWithoutStoppingTheDaemon()
    {
        using var lifetime = new Lifetime();
        using var fixture = new Fixture(lifetime: lifetime);
        RestartRequest.Ask(0);
        Assert.Null(new MixerSettings { MixVolumes = new() { ["monitor"] = 0.6 }, MonitorMuted = true }.Save());
        fixture.Answer();
        await fixture.Service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(fixture.Service.Built);
            // pipewire-pulse restarts: every module is gone, and it refuses
            // new ones for a while.
            fixture.LoseGraph();
            Assert.True(SpinWait.SpinUntil(() => !fixture.Service.Built, TimeSpan.FromSeconds(10)), "the lost graph was not noticed");
            int lostAt = fixture.Calls().Length;
            byte[] saved = File.ReadAllBytes(MixerSettings.DefaultPath);
            Assert.True(SpinWait.SpinUntil(() => fixture.Refusals() >= 3, TimeSpan.FromSeconds(10)), "the graph was not built again");

            Assert.True(fixture.Service.WaitingForAudio);
            Assert.True(fixture.Service.RebuildingGraph);
            Assert.Contains("still being built", fixture.Service.GraphWarning);
            Assert.Contains("still being built", fixture.Service.Apply(new Command
            {
                Cmd = "setMixVolume", Mix = "monitor", Value = JsonSerializer.SerializeToElement(0.1),
            }));
            Assert.Null(fixture.Service.Snapshot());
            // Nothing writes the emptied mixer over the user's file meanwhile.
            fixture.Saver.Flush();
            Assert.False(fixture.Saver.Closed);
            Assert.Equal(saved, File.ReadAllBytes(MixerSettings.DefaultPath));

            fixture.Answer();
            Assert.True(SpinWait.SpinUntil(() => fixture.Service.Built && !fixture.Service.WaitingForAudio, TimeSpan.FromSeconds(10)),
                "the graph was not built again once the server answered");
            Assert.Null(fixture.Service.GraphWarning);
            Assert.False(fixture.Service.RebuildingGraph);
            // The wait ends a moment before the build does (ducking, the
            // soundboard and the timers come first), and the scene is refused
            // until then.
            OpenXLR.Core.MixerScene? scene = null;
            Assert.True(SpinWait.SpinUntil(() => (scene = fixture.Service.ExportScene().Scene) is not null, TimeSpan.FromSeconds(10)),
                "the rebuilt mixer's scene could not be read");
            Assert.Equal(0.6, scene!.MixVolumes["monitor"]);
            Assert.True(fixture.Mixer.MonitorMuted);
            // The forgotten modules' ids belong to other clients now: none is unloaded.
            Assert.DoesNotContain(fixture.Calls()[lostAt..], call => call.StartsWith("unload-module ", StringComparison.Ordinal));
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.Equal(0, RestartRequest.ExitCode);
        }
        finally
        {
            await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            RestartRequest.Ask(0);
        }
        Assert.False(fixture.Service.Built);
    }

    [Fact]
    public async Task AStopWhileALostGraphWaitsEndsTheRebuildAndUnloadsNothing()
    {
        using var lifetime = new Lifetime();
        using var fixture = new Fixture(lifetime: lifetime);
        fixture.Answer();
        await fixture.Service.StartAsync(CancellationToken.None);
        fixture.LoseGraph();
        Assert.True(SpinWait.SpinUntil(() => fixture.Service.WaitingForAudio && fixture.Refusals() >= 2, TimeSpan.FromSeconds(10)));
        int lostAt = fixture.Calls().Length;

        await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(fixture.Service.BuildRetry.IsCompleted);
        string[] calls = fixture.Calls();
        Assert.DoesNotContain(calls[lostAt..], call => call.StartsWith("unload-module ", StringComparison.Ordinal));
        // Nothing asks the server once the stop has run.
        fixture.Answer();
        Thread.Sleep(200);
        Assert.Equal(calls, fixture.Calls());
        Assert.False(fixture.Service.Built);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    /// <summary>
    /// One mix sink, post sink or virtual microphone that goes while the
    /// server stays (unloaded from outside, or dropped near pipewire-pulse's
    /// open-file limit) is seen as a lost graph, not only the first mix's
    /// sink, and the graph is built again in place with that node back.
    /// </summary>
    [Theory]
    [InlineData("OpenXLR_mix_chat")]
    [InlineData("OpenXLR_post_stream")]
    [InlineData("OpenXLR_chat")]
    public async Task ANodeLostAloneIsSeenAndTheGraphIsBuiltAgain(string node)
    {
        using var lifetime = new Lifetime();
        using var fixture = new Fixture(lifetime: lifetime);
        fixture.Answer();
        await fixture.Service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(fixture.Service.Built);
            Assert.True(fixture.Publishes(node));
            Assert.Equal(0, fixture.Mixer.MissingGraphNodes()?.Count);
            fixture.LoseNode(node);
            Assert.Equal([node], fixture.Mixer.MissingGraphNodes());
            Assert.True(SpinWait.SpinUntil(() => !fixture.Service.Built, TimeSpan.FromSeconds(10)), $"the lost {node} was not noticed");
            Assert.True(SpinWait.SpinUntil(() => fixture.Service.Built && !fixture.Service.WaitingForAudio, TimeSpan.FromSeconds(10)),
                "the graph was not built again");
            Assert.True(fixture.Publishes(node));
            Assert.Equal(0, fixture.Mixer.MissingGraphNodes()?.Count);
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        }
        finally
        {
            await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// A sweep phase that fails leaves the phases after it to run, and each
    /// failure is logged at warning once, not on every sweep it repeats.
    /// </summary>
    [Fact]
    public async Task ASweepPhaseThatFailsLeavesTheOthersToRun()
    {
        var log = new RecordingLogger<MixerService>();
        var ducking = new FailingDuckingPort();
        using var fixture = new Fixture(ducking, log: log);
        fixture.Answer();
        await fixture.Service.StartAsync(CancellationToken.None);
        try
        {
            await fixture.PauseSweeps();
            fixture.RefuseStreamListings();
            ducking.Fail = true;
            // The sends fail early in each pass, the ducking late in it.
            for (int i = 0; i < 3; i++) Assert.True(fixture.Service.SweepOnce());
            string[] warnings = [.. Sweep(LogLevel.Warning)];
            Assert.Equal(2, warnings.Length);
            Assert.StartsWith("stream sweep: sends: pactl list sink-inputs failed", warnings[0]);
            Assert.StartsWith("stream sweep: ducking: the ducking port failed", warnings[1]);

            fixture.RefuseStreamListings(false);
            ducking.Fail = false;
            Assert.True(fixture.Service.SweepOnce());
            Assert.Equal(["stream sweep: sends recovered", "stream sweep: ducking recovered"], Sweep(LogLevel.Information));
            Assert.Equal(2, Sweep(LogLevel.Warning).Count());
        }
        finally
        {
            await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        }

        IEnumerable<string> Sweep(LogLevel level) => log.Entries
            .Where(e => e.Level == level && e.Message.StartsWith("stream sweep", StringComparison.Ordinal)).Select(e => e.Message);
    }

    private sealed class Lifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() => _stop.Dispose();
    }

    [Fact]
    public async Task NoProfileSceneIsExportedUntilTheBuildHasEnded()
    {
        var port = new FailingDuckingPort();
        using var fixture = new Fixture(port, _ => port.Thrown > 0 ? TimeSpan.FromHours(1) : TimeSpan.FromMilliseconds(20));
        (MixerScene? Scene, string? Refused) during = (null, "not asked");
        port.DuringBuild = () => during = fixture.Service.ExportScene();

        await fixture.Service.StartAsync(CancellationToken.None);
        try
        {
            // Waiting for the audio server: there is no mixer to export.
            (MixerScene? scene, string? refused) = fixture.Service.ExportScene();
            Assert.Null(scene);
            Assert.Contains("waiting for the audio server", refused);

            port.Fail = true;
            fixture.Answer();
            Assert.True(SpinWait.SpinUntil(() => port.Thrown > 0 && fixture.Service.WaitingForAudio, TimeSpan.FromSeconds(10)));
            // Part way through the attempt the graph existed and held the
            // settings, but the build had not ended and was then torn down.
            Assert.Null(during.Scene);
            Assert.Contains("still being built", during.Refused);
            Assert.Contains("waiting for the audio server", fixture.Service.ExportScene().Refused);
        }
        finally { await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    /// <summary>A ducking port that makes the build fail after the settings are applied.</summary>
    private sealed class FailingDuckingPort : IDaemonDuckingPort
    {
        public volatile bool Fail;
        public Action? DuringBuild;
        private int _thrown;
        public int Thrown => Volatile.Read(ref _thrown);

        public event Action? StatusChanged { add { } remove { } }

        public (string Unit, UnitDuckingSupport Support)? Primary()
        {
            if (!Fail) return null;
            if (Interlocked.Increment(ref _thrown) == 1) DuringBuild?.Invoke();
            throw new InvalidOperationException("the ducking port failed");
        }

        public void Post(string unit, ProDuckingTarget target, string owner) { }
        public DeviceDuckingStatus Status(string unit) => DeviceDuckingStatus.None;
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

    [Theory]
    [InlineData("setMonitorMuted")]
    [InlineData("toggleMonitorMuted")]
    [InlineData("setMonitorMuteMicrophones")]
    [InlineData("toggleOutputMute")]
    public async Task AFailedSoundCheckHandBackStillSavesTheRequestedCombinedPause(string command)
    {
        using var fixture = new Fixture();
        fixture.Answer();
        await fixture.Service.StartAsync(CancellationToken.None);
        try
        {
            await fixture.PauseSweeps();
            string first = command == "setMonitorMuteMicrophones" ? "setMonitorMuted" : "setMonitorMuteMicrophones";
            Assert.Null(fixture.Service.Apply(new Command { Cmd = first, Value = JsonSerializer.SerializeToElement(true) }));
            if (command == "toggleOutputMute") fixture.SelectOutsideOutput();
            fixture.Saver.Flush();
            Assert.False(fixture.Saver.Pending);
            fixture.SoundCheckAwaitingRestore();
            int changed = 0;
            fixture.Service.Changed += () => changed++;

            string? error = fixture.Service.Apply(new Command
            {
                Cmd = command, Value = JsonSerializer.SerializeToElement(true),
                Device = command == "toggleOutputMute" ? "outside" : null,
            });
            Assert.Contains("EQ restore failed", Assert.IsType<string>(error));
            Assert.True(fixture.Mixer.MonitorMuted);
            Assert.True(fixture.Mixer.MonitorMuteMicrophones);
            Assert.Null(fixture.Mixer.SoundCheckChannel);
            Assert.True(fixture.Saver.Pending);
            Assert.True(changed > 0);

            fixture.AllowRestore();
            fixture.Saver.Flush();
            MixerSettings saved = MixerSettings.Load()!;
            Assert.True(saved.MonitorMuted);
            Assert.True(saved.MonitorMuteMicrophones);
        }
        finally
        {
            fixture.AllowRestore();
            await fixture.Service.StopAsync(CancellationToken.None);
        }
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

    [Fact]
    public async Task TheUISaysARebuildApartFromTheWaitAtStartup()
    {
        await using var client = new OpenXLR.UI.DaemonClient();
        var model = new OpenXLR.UI.MainViewModel(client);
        void Apply(string json) => typeof(OpenXLR.UI.MainViewModel)
            .GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(JsonNode)])!
            .Invoke(model, [JsonNode.Parse(json)]);
        string waiting = OpenXLR.UI.Localization.Localizer.Text("MixerWaitingForAudioServer");
        string rebuilding = OpenXLR.UI.Localization.Localizer.Text("MixerRebuildingAfterAudioServer");
        Assert.NotEqual(waiting, rebuilding);

        Apply("""{"connected":false,"mixer":null,"mixerWaiting":true,"mixerRebuilding":true}""");
        Assert.Equal(rebuilding, model.MixerPlaceholder);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // The same wait at startup, and from a daemon without the field.
        Apply("""{"connected":false,"mixer":null,"mixerWaiting":true,"mixerRebuilding":false}""");
        Assert.Equal(waiting, model.MixerPlaceholder);
        Assert.Contains(nameof(model.MixerPlaceholder), raised);
        Apply("""{"connected":false,"mixer":null,"mixerWaiting":true,"mixerRebuilding":true}""");
        Apply("""{"connected":false,"mixer":null,"mixerWaiting":true}""");
        Assert.Equal(waiting, model.MixerPlaceholder);

        // A rebuild flag without the wait says nothing of its own.
        Apply("""{"connected":false,"mixer":null,"mixerRebuilding":true}""");
        Assert.Equal(OpenXLR.UI.Localization.Localizer.Text("SubmixerIsOffTurnItOnInOptions"), model.MixerPlaceholder);
        Assert.False(model.MixerRebuilding);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-retry-").FullName;
        private readonly string? _oldPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly DeviceManager _devices;
        private readonly FailedRestorePort _restorePort = new();
        internal MixerService Service { get; }
        internal SettingsSaver Saver => (SettingsSaver)typeof(MixerService)
            .GetField("_saves", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service)!;

        internal Mixer Mixer => (Mixer)typeof(MixerService)
            .GetField("_mixer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service)!;

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

        internal void SoundCheckAwaitingRestore()
        {
            void Set(string name, object value) => typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, value);
            Set("_soundCheck", new FilterHandle("pending", "pending", "pending", new Process()));
            Set("_soundCheckChannel", "xlr1");
            Set("_eqPort", _restorePort);
            Set("_drivenUnit", "test-unit");
            Set("_unitEq", new Dictionary<string, UnitEqSupport> { ["test-unit"] = new("Test unit", null, true) });
        }

        internal void AllowRestore() => _restorePort.Refuse = false;

        internal void SelectOutsideOutput()
        {
            File.WriteAllText(Path.Combine(_directory, "outside-output"), "");
            var outputs = (List<string>)typeof(Mixer).GetField("_monitorOutputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Mixer)!;
            outputs.Add("outside");
            var pipeWire = (PipeWireAdapter)typeof(Mixer).GetField("_pw", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Mixer)!;
            pipeWire.SetSinkVolume("outside", 1); // Drop the fake graph's earlier empty snapshot.
        }

        private sealed class FailedRestorePort : IDeviceEqPort
        {
            public bool Refuse = true;
            public void Post(string unit, int input, OpenXLR.Core.Devices.DeviceEq eq, string owner)
            {
                if (Refuse) throw new InvalidOperationException("EQ restore failed");
            }
            public DeviceEqStatus Status(string unit, int input)
                => Refuse ? throw new InvalidOperationException("EQ restore failed") : DeviceEqStatus.None;
        }

        internal Fixture(IDaemonDuckingPort? ducking = null, Func<int, TimeSpan>? retryDelay = null,
            Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null, ILogger<MixerService>? log = null)
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_directory, "config"));
            new DaemonSettings { Submixer = true }.Save();
            // The registry subscription never becomes ready, so the adapter
            // reads one-shot dumps of an empty graph and waits for no echo.
            ExecutableScript.Write(Path.Combine(_directory, "pw-dump"), """
                if [ "$1" = "--monitor" ]; then echo '[]'; exec /bin/sleep 60; fi
                if [ -f "${0%/*}/outside-output" ]; then
                  echo '[{"id":42,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"outside","media.class":"Audio/Sink","device.api":"alsa"}}}]'
                  exit
                fi
                echo '[]'
                """);
            ExecutableScript.Write(Path.Combine(_directory, "pw-link"), "exit 0");
            ExecutableScript.Write(Path.Combine(_directory, "parec"), "exit 0");
            ExecutableScript.Write(Path.Combine(_directory, "pactl"), """
                dir=${0%/*}
                echo "$*" >> "$dir/calls"
                # A listing the server refuses, as a sweep phase's helper fails.
                if [ "$1 $2" = "list sink-inputs" ] && [ -f "$dir/refuse-streams" ]; then
                  echo 'Connection failure: Timeout' >&2
                  exit 1
                fi
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
                    case "$3" in
                      sink_name=*) echo "${3#sink_name=}" >> "$dir/sinks" ;;
                      source_name=*) echo "${3#source_name=}" >> "$dir/sources" ;;
                    esac
                    echo "$n" ;;
                  list)
                    # The sinks and sources the loaded modules published, until
                    # the server restarts or one is taken away.
                    if [ "$3" = short ] && { [ "$2" = sinks ] || [ "$2" = sources ]; } && [ -f "$dir/$2" ]; then
                      i=0
                      while read -r name; do i=$((i + 1)); printf '%s\t%s\tmodule-null-sink.c\n' "$i" "$name"; done < "$dir/$2"
                    fi ;;
                  get-default-sink) echo desk ;;
                  get-default-source) echo mic ;;
                esac
                exit 0
                """);
            Environment.SetEnvironmentVariable("PATH", _directory);
            var config = new ConfigurationBuilder().Build();
            _devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            Service = new MixerService(log ?? NullLogger<MixerService>.Instance, config, _devices, lifetime,
                startupDefaults: new StartupDefaults("desk", "mic"))
            {
                RetryDelay = retryDelay ?? (_ => TimeSpan.FromMilliseconds(20)),
                DuckingPort = ducking ?? new DeviceDuckingPort(_devices),
            };
        }

        internal void Answer() => File.WriteAllText(Path.Combine(_directory, "answer"), "");

        /// <summary>pipewire-pulse restarted: its sinks are gone and it refuses modules until <see cref="Answer"/>.</summary>
        internal void LoseGraph()
        {
            File.Delete(Path.Combine(_directory, "answer"));
            File.Delete(Path.Combine(_directory, "sinks"));
            File.Delete(Path.Combine(_directory, "sources"));
        }

        /// <summary>One published sink or source goes, its module unloaded from outside, while the server stays.</summary>
        internal void LoseNode(string name)
        {
            foreach (string kind in new[] { "sinks", "sources" })
            {
                string path = Path.Combine(_directory, kind);
                if (File.Exists(path)) File.WriteAllLines(path, File.ReadAllLines(path).Where(line => line != name));
            }
        }

        internal void RefuseStreamListings(bool refuse = true)
        {
            if (refuse) File.WriteAllText(Path.Combine(_directory, "refuse-streams"), "");
            else File.Delete(Path.Combine(_directory, "refuse-streams"));
        }

        internal bool Publishes(string name)
            => Lines("sinks").Contains(name) || Lines("sources").Contains(name);
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
