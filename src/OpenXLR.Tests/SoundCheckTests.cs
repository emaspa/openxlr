using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

public sealed class SoundCheckTests
{
    [Fact]
    public void AMissingHelperIsRefusedAtOnceWithTheReason()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"openxlr-no-host-{Guid.NewGuid():N}", "openxlr-lv2-host");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = Assert.Throws<InvalidOperationException>(() => new PipeWireAdapter().CreateSoundCheck("xlr1", out _, out _, missing));
        Assert.Equal(Mixer.SoundCheckNeedsHost, error.Message);
        Assert.Contains("EnableNativeLv2Host", error.Message);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("loop")]
    [InlineData("live")]
    public void ABuildWithoutTheHelperRefusesEverythingButStop(string action)
    {
        using var mixer = new Mixer();
        Assert.Equal(Mixer.SoundCheckNeedsHost, CommandValidation.Check(
            new Command { Cmd = "soundCheck", Channel = "xlr1", Action = action }, mixer, _ => null, nativeHostInstalled: false));
        Assert.Null(CommandValidation.Check(
            new Command { Cmd = "soundCheck", Channel = "xlr1", Action = "stop" }, mixer, _ => null, nativeHostInstalled: false));
    }

    [Theory]
    [InlineData("xlr1", "record", true)]
    [InlineData("xlr2", "loop", true)]
    [InlineData("xlr1", "live", true)]
    [InlineData("xlr1", "stop", true)]
    [InlineData("system", "record", false)]
    [InlineData("mix:monitor", "record", false)]
    [InlineData(null, "record", false)]
    [InlineData("xlr1", "unknown", false)]
    [InlineData("xlr1", null, false)]
    public void UnknownChannelsAndActionsAreRefused(string? channel, string? action, bool valid)
    {
        using var mixer = new Mixer();
        Assert.Equal(valid, CommandValidation.Check(
            new Command { Cmd = "soundCheck", Channel = channel, Action = action }, mixer, _ => null, nativeHostInstalled: true) is null);
    }

    [Theory]
    [InlineData("xlr1", true)]
    [InlineData("xlr2", true)]
    [InlineData("mono", true)]
    [InlineData("stereo", false)]
    [InlineData("aux", false)]
    [InlineData("system", false)]
    [InlineData("apps", false)]
    [InlineData("mix:monitor", false)]
    [InlineData("mix:chat", false)]
    [InlineData("missing", false)]
    public void SoundCheckValidationUsesTheLayoutsMicrophones(string channel, bool valid)
    {
        using var mixer = new Mixer();
        typeof(Mixer).GetField("_config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(mixer, MixerConfig.Default() with { Channels = [.. MixerConfig.Default().Channels,
                new("mono", "Mono microphone") { CaptureSource = "absent_mic", CaptureMonoChannel = 2 },
                new("stereo", "Stereo input") { CaptureSource = "absent_pair", CapturePair = 1 }, new("apps", "Applications")] });
        Assert.Equal(valid, mixer.IsSoundCheckChannel(channel));
        foreach (string action in new[] { "record", "loop", "live", "stop" })
        {
            var command = new Command { Cmd = "soundCheck", Channel = channel, Action = action };
            string? error = CommandValidation.Check(command, mixer, _ => null, nativeHostInstalled: true);
            if (valid)
            {
                Assert.Null(error);
                Assert.Equal(action == "stop" ? null : Mixer.SoundCheckNeedsHost,
                    CommandValidation.Check(command, mixer, _ => null, nativeHostInstalled: false));
            }
            else Assert.Equal("soundCheck: need a microphone channel and action 'record', 'loop', 'live' or 'stop'", error);
        }
        if (channel == "mono")
        {
            Assert.Contains("Mono microphone", Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck(channel, "record")).Message);
            // A capture microphone has the EQ too, in software, and so does a stereo pair, which has no Sound Check.
            mixer.SetEqEnabled(channel, true);
            Assert.True(mixer.EqFor(channel).Enabled);
            mixer.SetEqEnabled("stereo", true);
            Assert.True(mixer.EqFor("stereo").Enabled);
            Assert.Throws<ArgumentException>(() => mixer.SoundCheck("stereo", "record"));
        }
        else if (!valid) Assert.Throws<ArgumentException>(() => mixer.SoundCheck(channel, "record"));
    }

    [Fact]
    public void NoDeviceDoesNotCreateARecordingAndStopIsIdempotent()
    {
        using var mixer = new Mixer();
        Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck("xlr1", "record"));
        Assert.Throws<ArgumentException>(() => mixer.SoundCheck("system", "record"));
        Assert.Throws<ArgumentException>(() => mixer.SoundCheck("xlr1", "invalid"));
        mixer.SoundCheck("xlr1", "stop");
        mixer.SoundCheck("xlr1", "stop");
        mixer.SoundCheck("xlr2", "stop");
        Assert.Equal("idle", mixer.Snapshot().SoundCheck.Mode);
        Assert.Null(mixer.Snapshot().SoundCheck.Channel);
        Assert.Null(mixer.SoundCheckChannel);
        Assert.DoesNotContain("soundCheck", System.Text.Json.JsonSerializer.Serialize(mixer.ExportSettings()), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OldRepliesCannotChangeANewConnectionsErrorOrUnlockItsPendingCommand(bool closing, bool failed)
    {
        var received = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var release = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            var replies = new List<Task>();
            for (int i = 0; i < 2;)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                int index = i++;
                received[index].TrySetResult();
                replies.Add(Reply());
                async Task Reply()
                {
                    await release[index].Task.WaitAsync(stop);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(), error = index == 1 ? "current failure" : failed ? "old failure" : null }, stop);
                }
            }
            await Task.WhenAll(replies);
            await Task.Delay(Timeout.Infinite, stop);
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var view = new MicSetupViewModel(client, "xlr1");
        Task old = closing ? view.StopOnCloseAsync() : view.RunAsync("record");
        await received[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.Reset();
        Task current = view.RunAsync("record");
        await received[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"live","seconds":1,"error":"current state"}"""));
        release[0].SetResult();
        await old;
        Assert.Equal("current state", view.Error);
        Assert.False(view.CanRecord);
        release[1].SetResult();
        await current;
        Assert.Equal("current failure", view.Error);
        Assert.True(view.CanRecord);
    }

    [Fact]
    public async Task AStoppedSessionsErrorBelongsOnlyToItsMicrophoneAndCanBeDismissed()
    {
        await using var client = new DaemonClient();
        var first = new MicSetupViewModel(client, "xlr1");
        var second = new MicSetupViewModel(client, "xlr2");
        var state = JsonNode.Parse("""{"channel":"xlr1","mode":"idle","seconds":0,"error":"Sound Check ended after ten minutes."}""");
        first.Apply(state); second.Apply(state);
        Assert.NotNull(first.Error);
        Assert.False(first.Active);
        Assert.True(first.CanStop);
        Assert.Null(second.Error);
        Assert.False(second.CanStop);
        first.Apply(JsonNode.Parse("""{"channel":null,"mode":"idle","seconds":0}"""));
        Assert.Null(first.Error);
        Assert.False(first.CanStop);
    }

    [Fact]
    public async Task TheViewUsesServerProgressAndNeverLoopsAnEmptySample()
    {
        await using var client = new DaemonClient();
        var view = new MicSetupViewModel(client, "xlr1");
        Assert.True(view.CanRecord);
        Assert.False(view.CanLoop);
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"recording","seconds":0.05}"""));
        Assert.False(view.CanRecord);
        Assert.False(view.CanLoop);
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"recording","seconds":1.5}"""));
        Assert.True(view.CanLoop);
        Assert.Contains("Recording", view.Status);
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"looping","seconds":1.5}"""));
        Assert.Contains("Looping", view.Status);
        Assert.True(view.CanStop);
        view.Apply(JsonNode.Parse("""{"channel":"xlr2","mode":"looping","seconds":1.5}"""));
        Assert.False(view.Active);
        Assert.False(view.CanLoop);
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"looping","seconds":1.5}"""));
        view.Apply(null);
        Assert.False(view.CanLoop);
        Assert.False(view.CanStop);
        Assert.Equal(Localizer.Text("MicSetupIdle"), view.Status);
        await view.RunAsync("record");
        Assert.NotNull(view.Error);
    }

    [Theory]
    [InlineData("setSoundCheckRaw", "xlr1", "true", true)]
    [InlineData("setSoundCheckHearMyself", "xlr2", "false", true)]
    [InlineData("setSoundCheckRaw", "mono", "true", true)]
    [InlineData("setSoundCheckRaw", "stereo", "true", false)]
    [InlineData("setSoundCheckHearMyself", "system", "true", false)]
    [InlineData("setSoundCheckRaw", "xlr1", "1", false)]
    [InlineData("setSoundCheckHearMyself", "xlr1", null, false)]
    [InlineData("setSoundCheckRaw", null, "true", false)]
    [InlineData("watchInputMeter", "xlr1", null, true)]
    [InlineData("watchInputMeter", "mono", null, true)]
    [InlineData("watchInputMeter", "aux", null, false)]
    [InlineData("watchInputMeter", null, null, false)]
    public void MicSetupCommandsTakeAMicrophoneAndTheHelper(string name, string? channel, string? value, bool valid)
    {
        using var mixer = new Mixer();
        typeof(Mixer).GetField("_config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(mixer, MixerConfig.Default() with { Channels = [.. MixerConfig.Default().Channels,
                new("mono", "Mono microphone") { CaptureSource = "absent_mic", CaptureMonoChannel = 2 },
                new("stereo", "Stereo input") { CaptureSource = "absent_pair", CapturePair = 1 }] });
        var command = new Command { Cmd = name, Channel = channel,
            Value = value is null ? default : System.Text.Json.JsonDocument.Parse(value).RootElement.Clone() };
        string? error = CommandValidation.Check(command, mixer, _ => null, nativeHostInstalled: true);
        Assert.Equal(valid, error is null);
        if (!valid) Assert.StartsWith(name + ": need a microphone channel", error);
        else Assert.Equal(Mixer.SoundCheckNeedsHost, CommandValidation.Check(command, mixer, _ => null, nativeHostInstalled: false));
    }

    [Fact]
    public void MicSetupCommandsNeedARunningMixerAndAMicrophone()
    {
        using var mixer = new Mixer();
        Assert.Throws<InvalidOperationException>(() => mixer.WatchInputMeter("xlr1"));
        Assert.Throws<ArgumentException>(() => mixer.WatchInputMeter("system"));
        Assert.Throws<InvalidOperationException>(() => mixer.SetSoundCheckRaw("xlr1", true));
        Assert.Throws<ArgumentException>(() => mixer.SetSoundCheckHearMyself("system", true));
        Assert.Empty(mixer.InputMeterChannels);
        Assert.Null(mixer.CurrentSoundCheckTake());
        Assert.Null(mixer.NewSoundCheckTake());
        SoundCheckState idle = mixer.Snapshot().SoundCheck;
        Assert.False(idle.Raw || idle.HearMyself || idle.Heard);
        Assert.Null(idle.MonitorLatencyMs);
        Assert.Equal(0, idle.Take);
        Assert.DoesNotContain("in:", string.Join(",", mixer.ReadMeters().Keys));
    }

    [Theory]
    [InlineData("update: id:0 key:'clock.rate' value:'48000' type:''\nupdate: id:0 key:'clock.quantum' value:'1024' type:''\nupdate: id:0 key:'clock.force-quantum' value:'0' type:''", 1024)]
    [InlineData("update: id:0 key:'clock.quantum' value:'1024' type:''\nupdate: id:0 key:'clock.force-quantum' value:'256' type:''", 256)]
    [InlineData("update: id:0 key:'clock.rate' value:'48000' type:''", null)]
    [InlineData("update: id:0 key:'clock.quantum' value:'4' type:''", null)]
    public void TheQuantumIsTheForcedOneOrTheDefault(string metadata, int? expected)
        => Assert.Equal(expected, PipeWireAdapter.ParseGraphQuantum(metadata.Replace("\\n", "\n")));

    [Fact]
    public void ATakeComesFromTheHelpersMetersOnlyOnceItIsCounted()
    {
        var meters = new Dictionary<string, double> { ["frames"] = 72000, ["take"] = 0, ["p0"] = .5 };
        Assert.Null(Mixer.TakeFrom("xlr1", meters, 48000));
        meters["take"] = 2;
        meters["p199"] = 1.7;
        meters["p3"] = .12345;
        SoundCheckTake take = Mixer.TakeFrom("xlr1", meters, 48000)!;
        Assert.Equal(("xlr1", 2, 1.5), (take.Channel, take.Take, take.Seconds));
        Assert.Equal(200, take.Peaks.Count);
        Assert.Equal([.5, 0, 0, .123], take.Peaks.Take(4));
        Assert.Equal(1, take.Peaks[199]);
        // A new recording can already have a different live length before
        // the completed take is first requested.
        meters["takeFrames"] = 48000;
        meters["frames"] = 100;
        Assert.Equal(1, Mixer.TakeFrom("xlr1", meters, 48000)!.Seconds);
        Assert.Null(Mixer.TakeFrom("xlr1", meters, 0));
        var message = System.Text.Json.JsonSerializer.Serialize(SoundCheckTakeMessage.From(null));
        Assert.Equal("""{"channel":null,"take":0,"seconds":0,"peaks":[],"type":"soundCheckTake"}""", message);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-12, .8)]
    [InlineData(-60, 0)]
    [InlineData(-120, 0)]
    [InlineData(6, 1)]
    [InlineData(double.NaN, 0)]
    public void InputLevelsUseTheChannelMetersScale(double dbfs, double scale)
        => Assert.Equal(scale, Mixer.MeterScale(dbfs));

    [Fact]
    public async Task TheCheckRoutesItsOwnTakeLevelAndPlayheadAndHoldsTheClip()
    {
        await using var client = new DaemonClient();
        var view = new MicSetupViewModel(client, "guest");
        Assert.True(view.ShowsPosition);
        Assert.Equal([MicSetupStep.Position, MicSetupStep.Level, MicSetupStep.Listen, MicSetupStep.Shape], view.Steps.Select(s => s.Step));
        view.Steps[2].IsSelected = true;
        Assert.True(view.ShowsListen && view.Steps[2].IsSelected && !view.Steps[0].IsSelected && !view.Steps[1].IsSelected);
        view.Steps[2].IsSelected = false;   // the step shown stays chosen
        Assert.True(view.ShowsListen);

        view.ApplyInputLevel(new System.Text.Json.Nodes.JsonArray(.4, .7, 1));
        Assert.Equal((.4, .7, true), (view.InputLevel, view.InputPeak, view.Clipped));
        Assert.Equal(Localizer.Format("MicSetupPeak", .7 * 60 - 60), view.PeakText);
        view.ApplyInputLevel(new System.Text.Json.Nodes.JsonArray(.1, .2, 0));
        Assert.True(view.Clipped);   // held until cleared
        view.ClearClip();
        Assert.False(view.Clipped);
        view.ApplyInputLevel(new System.Text.Json.Nodes.JsonArray(0, 0, 0));
        Assert.Equal(Localizer.Text("MicSetupPeakSilent"), view.PeakText);

        string Take(string channel) => $$"""{"type":"soundCheckTake","channel":"{{channel}}","take":1,"seconds":1,"peaks":[0.5,2,-1]}""";
        view.ApplyTake(JsonNode.Parse(Take("guest")));
        Assert.False(view.HasTake);   // no session yet
        view.Apply(JsonNode.Parse("""{"channel":"guest","mode":"looping","seconds":1,"take":1,"raw":true,"hearMyself":true,"heard":true,"monitorLatencyMs":21.3}"""));
        view.ApplyTake(JsonNode.Parse(Take("xlr1")));
        Assert.False(view.HasTake);
        view.ApplyTake(JsonNode.Parse(Take("guest")));
        Assert.Equal([.5, 1, 0], view.Peaks!);
        Assert.True(view.Raw && view.ListenRaw && !view.ListenProcessed && view.HearMyself && !view.NotHeard);
        Assert.Equal(Localizer.Format("MicSetupHearMyselfLatency", 21.3), view.LatencyText);
        view.ApplyPosition(new System.Text.Json.Nodes.JsonArray(.25));
        Assert.Equal(.25, view.Position);
        view.Apply(JsonNode.Parse("""{"channel":"guest","mode":"live","seconds":1,"take":1,"heard":false}"""));
        Assert.Equal(-1, view.Position);
        view.ApplyPosition(new System.Text.Json.Nodes.JsonArray(.5));
        Assert.Equal(-1, view.Position);   // the playhead moves only while the take loops
        Assert.True(view.NotHeard && !view.Raw && !view.HearMyself);
        Assert.Equal(Localizer.Text("MicSetupHearMyselfLatencyUnknown"), view.LatencyText);
        Assert.True(view.HasTake);
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"looping","seconds":1,"take":1}"""));
        Assert.False(view.Active || view.HasTake || view.NotHeard);
        view.ApplyInputLevel(new System.Text.Json.Nodes.JsonArray(.4, .7, 1));
        view.Reset();
        Assert.Equal((0.0, 0.0, false), (view.InputLevel, view.InputPeak, view.Clipped));
    }
}
