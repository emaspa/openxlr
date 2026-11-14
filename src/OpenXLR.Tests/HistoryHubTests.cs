using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// Undo and redo through the hub, against a fake pactl: what a step records,
/// what undo and redo write back, what they skip and what clears them.
/// </summary>
[Collection("xdg-config")]
public sealed class HistoryHubTests
{
    [Fact]
    public async Task AFaderIsUndoneAndRedone()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":0.5}""");
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("sendLevel", "xlr1", "monitor", "Mic 1 level in Monitor"), (step.Kind, step.Channel, step.Mix, step.Label));

        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(1.0, f.Level("xlr1|monitor"));
        Assert.Null(f.State().History!.Undo);
        Assert.Equal(step.Step, f.State().History!.Redo!.Step);

        await f.Ok("""{"cmd":"redo"}""");
        Assert.Equal(.5, f.Level("xlr1|monitor"));
        Assert.Equal(step.Step, f.State().History!.Undo!.Step);
        Assert.Null(f.State().History!.Redo);
    }

    [Fact]
    public async Task ADragIsOneStepAndAPauseStartsAnother()
    {
        using var f = new Fixture();
        foreach (double value in new[] { .9, .8, .7 })
        {
            await f.Ok($$"""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":{{value}}}""");
            f.Now += 80;
        }
        f.Now += 2000;
        await f.Ok("""{"cmd":"setChannelVolume","channel":"xlr1","value":0.4}""");
        Assert.Equal(2, f.State().History!.UndoSteps);

        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(1.0, f.Value(SceneField.ChannelVolume, "xlr1"));
        Assert.Equal(.7, f.Level("xlr1|monitor"));
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(1.0, f.Level("xlr1|monitor"));
        Assert.Equal("undo: nothing to undo", await f.Refused("""{"cmd":"undo"}"""));
    }

    [Fact]
    public async Task AMixMasterAndItsMuteAreUndone()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setMixVolume","mix":"stream","value":0.3}""");
        f.Now += 2000;
        await f.Ok("""{"cmd":"setMixMuted","mix":"stream","value":true}""");
        Assert.Equal("Stream mute", f.State().History!.Undo!.Label);
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(false, f.Value(SceneField.MixMuted, "stream"));
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(1.0, f.Value(SceneField.MixVolume, "stream"));
    }

    [Fact]
    public async Task AnExclusiveGroupsSideEffectIsUndoneWithTheMuteThatCausedIt()
    {
        using var f = new Fixture(group: true);
        await f.Ok("""{"cmd":"setChannelMuted","channel":"xlr1","mix":"monitor","value":false}""");
        f.Now += 2000;
        await f.Ok("""{"cmd":"setChannelMuted","channel":"xlr2","mix":"monitor","value":false}""");
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));

        await f.Ok("""{"cmd":"undo"}""");
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr2", "monitor"));

        // Redo opens XLR 2 again and the group closes XLR 1.
        await f.Ok("""{"cmd":"redo"}""");
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.False(f.Mixer.IsChannelMutedIn("xlr2", "monitor"));

        f.Now += 2000;
        await f.Ok("""{"cmd":"cycleExclusiveGroup","group":"mics"}""");
        Assert.Equal("Microphones switch", f.State().History!.Undo!.Label);
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        await f.Ok("""{"cmd":"undo"}""");
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.False(f.Mixer.IsChannelMutedIn("xlr2", "monitor"));
    }

    [Fact]
    public async Task TheStepGuardRefusesAHistoryThatChanged()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":0.5}""");
        long seen = f.State().History!.Undo!.Step;
        f.Now += 2000;
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"stream","value":0.5}""");

        Assert.Equal("undo: the history changed", await f.Refused($$"""{"cmd":"undo","step":{{seen}}}"""));
        Assert.Equal(.5, f.Level("xlr1|stream"));
        await f.Ok($$"""{"cmd":"undo","step":{{seen + 1}}}""");
        Assert.Equal(1.0, f.Level("xlr1|stream"));
        Assert.Equal("redo: the history changed", await f.Refused($$"""{"cmd":"redo","step":{{seen}}}"""));
        Assert.Equal("undo: step must be a whole number of at least 1", await f.Refused("""{"cmd":"undo","step":0}"""));
    }

    [Fact]
    public async Task AValueMovedSinceIsSkippedAndNamed()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":0.5}""");
        // Moved outside the history, as the desktop or a recall would.
        f.Mixer.SetLevel("xlr1", "monitor", .7);
        Assert.Equal("undo: Mic 1 level in Monitor was skipped: it changed since", await f.Refused("""{"cmd":"undo"}"""));
        Assert.Equal(.7, f.Level("xlr1|monitor"));
        Assert.Null(f.State().History!.Undo);
        Assert.Null(f.State().History!.Redo);
    }

    [Fact]
    public async Task APartlyStaleStepUndoesTheRestAndSaysWhatItSkipped()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":0.5}""");
        f.Now += 20;   // a Deck dial sending to every mix: one press, one step
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"stream","value":0.5}""");
        Assert.Equal("Mic 1 level in Monitor and 1 more", f.State().History!.Undo!.Label);
        f.Mixer.SetLevel("xlr1", "stream", .2);

        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(1.0, f.Level("xlr1|monitor"));
        Assert.Equal(.2, f.Level("xlr1|stream"));
        Assert.Equal("Undo skipped part of Mic 1 level in Monitor and 1 more: it changed since.", f.State().History!.Note);

        // The redo holds only what the undo wrote.
        await f.Ok("""{"cmd":"redo"}""");
        Assert.Equal(.5, f.Level("xlr1|monitor"));
        Assert.Equal(.2, f.Level("xlr1|stream"));
        Assert.Null(f.State().History!.Note);
    }

    [Fact]
    public async Task AValueAlreadyBackCountsAsDone()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":0.5}""");
        f.Mixer.SetLevel("xlr1", "monitor", 1);
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Null(f.State().History!.Note);
        Assert.NotNull(f.State().History!.Redo);
    }

    [Fact]
    public async Task ANewStepClearsRedo()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":0.5}""");
        await f.Ok("""{"cmd":"undo"}""");
        await f.Ok("""{"cmd":"setLevel","channel":"xlr2","mix":"monitor","value":0.5}""");
        Assert.Null(f.State().History!.Redo);
        Assert.Equal("redo: nothing to redo", await f.Refused("""{"cmd":"redo"}"""));
    }

    [Fact]
    public async Task LiveSwitchesAndCommandsThatChangeNothingAreNotRecorded()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"toggleMonitorMuted"}""");
        await f.Ok("""{"cmd":"setMonitorMuted","value":false}""");
        await f.Refused("""{"cmd":"set","control":"phantom","value":true}""");
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":1}""");
        HistoryState history = f.State().History!;
        Assert.Equal((0, 0), (history.UndoSteps, history.RedoSteps));
    }

    [Fact]
    public async Task AProfileLoadClearsTheHistory()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"saveProfile","name":"show"}""");
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":0.5}""");
        await f.Ok("""{"cmd":"undo"}""");
        f.Now += 2000;
        await f.Ok("""{"cmd":"setLevel","channel":"xlr2","mix":"monitor","value":0.5}""");
        Assert.NotNull(f.State().History!.Undo);

        await f.Ok("""{"cmd":"loadProfile","name":"show"}""");
        HistoryState history = f.State().History!;
        Assert.Equal((0, 0), (history.UndoSteps, history.RedoSteps));
    }

    [Fact]
    public async Task ARequestIdGetsTheStateAfterTheUndoAndThenTheResult()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":0.5}""");
        ApiCommandResult result = await f.Hub.ExecuteForApiAsync("""{"cmd":"undo","requestId":"z"}""");
        Assert.True(result.Ok);
        Assert.Equal(2, result.Messages.Count);
        StateMessage state = Assert.IsType<StateMessage>(result.Messages[0]);
        Assert.Null(state.History!.Undo);
        Assert.Equal(1.0, state.Mixer!.Channels.Single(c => c.Id == "xlr1").Levels["monitor"]);
        Assert.Equal(new CommandResultMessage("z", null), result.Messages[1]);

        result = await f.Hub.ExecuteForApiAsync("""{"cmd":"undo","requestId":"y"}""");
        Assert.False(result.Ok);
        Assert.Equal(new CommandResultMessage("y", "undo: nothing to undo"), result.Messages[^1]);
    }

    [Fact]
    public async Task AnUndoTheMixerCannotTakeKeepsTheStep()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":0.5}""");
        f.Built = false;
        string error = await f.Refused("""{"cmd":"undo"}""");
        Assert.StartsWith("undo: mixer not built", error, StringComparison.Ordinal);
        f.Built = true;
        Assert.NotNull(f.State().History!.Undo);
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(1.0, f.Level("xlr1|monitor"));
    }

    /// <summary>
    /// A built mixer of two microphones by two mixes, Monitor and Stream,
    /// every send muted at unity, in front of a fake pactl, with the hub
    /// on top and the history on a clock the test moves.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("openxlr-history-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly DeviceManager _devices;
        private readonly MixerService _service;
        private readonly Lifetime _lifetime = new();

        public Mixer Mixer { get; }
        public WebSocketHub Hub { get; }
        public long Now { get; set; } = 100_000;

        public bool Built
        {
            set => typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, value);
        }

        public Fixture(bool group = false)
        {
            ExecutableScript.Write(Path.Combine(_dir, "pactl"), """
                dir=${0%/*}
                case "$*" in
                    'list sinks short') printf '1\tOpenXLR_mix_monitor\n2\tOpenXLR_mix_stream\n' ;;
                    'list sink-inputs') /bin/cat "$dir/legs" ;;
                esac
                exit 0
                """);
            ExecutableScript.Write(Path.Combine(_dir, "pw-dump"), "echo '[]'");
            ExecutableScript.Write(Path.Combine(_dir, "pw-link"), "exit 0");
            File.WriteAllText(Path.Combine(_dir, "legs"), string.Concat(new[] { 1, 2 }.SelectMany(ch => new[] { 0, 1 }.Select(m =>
                $"Sink Input #{ch * 10 + m}\nOwner Module: {ch}\nSink: {m + 1}\n"))));
            Environment.SetEnvironmentVariable("PATH", _dir);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_dir, "config"));
            var config = new ConfigurationBuilder().Build();
            _devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            _service = new MixerService(NullLogger<MixerService>.Instance, config, _devices);
            Mixer = (Mixer)typeof(MixerService).GetField("_mixer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)!;
            Built = true;
            Set("_config", new MixerConfig
            {
                Channels = [new("xlr1", "Mic 1") { InputPair = 0 }, new("xlr2", "Mic 2") { InputPair = 1 }],
                Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic)],
                ExclusiveGroups = group ? [new("mics", "Microphones", ["xlr1", "xlr2"])] : [],
            });
            string[] ids = ["xlr1", "xlr2"];
            string[] mixes = ["monitor", "stream"];
            foreach (string mix in mixes) Field<Dictionary<string, double>>("_mixVolume")[mix] = 1;
            for (int ch = 1; ch <= 2; ch++)
            {
                Field<Dictionary<string, uint>>("_combineModules")[ids[ch - 1]] = (uint)ch;
                for (int m = 0; m < 2; m++)
                {
                    string cell = $"{ids[ch - 1]}|{mixes[m]}";
                    Field<HashSet<string>>("_cells").Add(cell);
                    Field<HashSet<string>>("_muted").Add(cell);
                    Field<Dictionary<string, double>>("_levels")[cell] = 1;
                    Field<Dictionary<string, int>>("_legIndex")[cell] = ch * 10 + m;
                }
            }
            Hub = new WebSocketHub(_devices, _service, NullLogger<WebSocketHub>.Instance, _lifetime);
            Hub.History.Clock = () => Now;
        }

        private T Field<T>(string name) => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Mixer)!;

        private void Set(string name, object value) => typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, value);

        public object? Value(SceneField field, string key)
            => Mixer.ReadSceneValues([field], k => k.Key == key).GetValueOrDefault(new SceneKey(field, key));

        public double Level(string cell) => (double)Value(SceneField.Level, cell)!;

        public StateMessage State() => Hub.Snapshot();

        public async Task Ok(string json)
        {
            ApiCommandResult result = await Hub.ExecuteForApiAsync(json);
            Assert.True(result.Ok, string.Join("; ", result.Messages.OfType<ErrorMessage>().Select(e => e.Message)));
        }

        public async Task<string> Refused(string json)
        {
            ApiCommandResult result = await Hub.ExecuteForApiAsync(json);
            Assert.False(result.Ok);
            return Assert.Single(result.Messages.OfType<ErrorMessage>()).Message;
        }

        public void Dispose()
        {
            try
            {
                _service.Dispose();
                _devices.Dispose();
                _lifetime.Dispose();
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", _path);
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config);
                Directory.Delete(_dir, true);
            }
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => _stop.Token;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
    }
}
