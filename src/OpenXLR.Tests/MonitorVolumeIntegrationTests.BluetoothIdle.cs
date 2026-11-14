using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// A link from a running mix keeps a Bluetooth output's stream open even
/// when it carries only silence. A Bluetooth output set to release its link
/// is unlinked while its feed has been silent for a while and linked again
/// on sound; every other output, and a Bluetooth output left at the
/// default, stays linked. The meters' clock is replaced, so the test decides
/// when there was sound.
/// </summary>
public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void ABluetoothOutputSetToReleaseIsUnlinkedWhileItsFeedIsSilentAndLinkedAgainOnSound()
    {
        const string headphones = "bluez_output.IDLE_TEST.1", speakers = "idle_test_speakers";
        long now = 0, sound = 0;
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw) { Clock = () => Volatile.Read(ref now), LastSound = _ => Volatile.Read(ref sound) };
        pw.CreateNullSink(headphones, "Idle test headphones");
        pw.CreateNullSink(speakers, "Idle test speakers");
        WaitForInputPorts(pw, headphones, 2);
        WaitForInputPorts(pw, speakers, 2);
        mixer.Build(MonitorConfig());

        // Sound just now: both outputs are linked, and nothing waits to wake.
        mixer.SetMonitorOutputs([headphones, speakers]);
        WaitForRecovery(() => GraphLinksInto(headphones).Length == 2 && GraphLinksInto(speakers).Length == 2);
        Assert.False(mixer.WakeIdleOutputs());

        // Off by default: the silence leaves the headphones linked.
        Volatile.Write(ref now, (long)Mixer.IdleRelease.TotalMilliseconds);
        Assert.False(mixer.EnsureMonitorRoutes());
        Assert.Equal(2, GraphLinksInto(headphones).Length);
        Assert.Empty(mixer.Snapshot().MonitorIdleRelease);
        Assert.Equal(0, mixer.WaitingToWake);

        // Only a selected Bluetooth output takes it.
        Assert.Contains("not a Bluetooth output", mixer.SetMonitorIdleRelease(speakers, true));
        Assert.Contains("not a selected monitor output", mixer.SetMonitorIdleRelease("bluez_output.OTHER.1", true));

        // Turned on after the meters read 10 s of silence: unlinked at once.
        Assert.Null(mixer.SetMonitorIdleRelease(headphones, true));
        Assert.Equal([headphones], mixer.Snapshot().MonitorIdleRelease);
        WaitForRecovery(() => GraphLinksInto(headphones).Length == 0);
        Assert.Equal(2, GraphLinksInto(speakers).Length);
        // While the silence lasts, neither the sweep nor the meter timer links them.
        Assert.False(mixer.EnsureMonitorRoutes());
        Assert.False(mixer.WakeIdleOutputs());
        Assert.Empty(GraphLinksInto(headphones));

        // Sound again: the meter timer links them without waiting for a sweep.
        Volatile.Write(ref sound, Volatile.Read(ref now));
        Assert.True(mixer.WakeIdleOutputs());
        WaitForRecovery(() => GraphLinksInto(headphones).Length == 2);
        Assert.False(mixer.WakeIdleOutputs());

        // Silent again, then turned off while unlinked: linked again at once.
        Volatile.Write(ref now, Volatile.Read(ref sound) + (long)Mixer.IdleRelease.TotalMilliseconds);
        Assert.True(mixer.EnsureMonitorRoutes());
        WaitForRecovery(() => GraphLinksInto(headphones).Length == 0);
        Assert.Null(mixer.SetMonitorIdleRelease(headphones, false));
        WaitForRecovery(() => GraphLinksInto(headphones).Length == 2);
        Assert.Equal(0, mixer.WaitingToWake);

        // Turned on one second after sound: the 10 s run from that sound.
        Volatile.Write(ref sound, Volatile.Read(ref now));
        Volatile.Write(ref now, Volatile.Read(ref sound) + 1000);
        Assert.Null(mixer.SetMonitorIdleRelease(headphones, true));
        Assert.Equal(2, GraphLinksInto(headphones).Length);
        Volatile.Write(ref now, Volatile.Read(ref sound) + (long)Mixer.IdleRelease.TotalMilliseconds);
        Assert.True(mixer.EnsureMonitorRoutes());
        WaitForRecovery(() => GraphLinksInto(headphones).Length == 0);
    }

    [MonitorPipeWireFact]
    public void TheIdleReleaseIsSavedWithItsOutputAndDroppedWhenTheOutputIs()
    {
        const string headphones = "bluez_output.IDLE_SAVE.1", speakers = "idle_save_speakers";
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        var outputs = new PipeWireAdapter();
        using var mixer = new Mixer(pw);
        uint headphonesModule = outputs.CreateNullSink(headphones, "Idle save headphones");
        outputs.CreateNullSink(speakers, "Idle save speakers");
        try
        {
            WaitForInputPorts(pw, headphones, 2);
            WaitForInputPorts(pw, speakers, 2);
            mixer.Build(MonitorConfig());
            mixer.SetMonitorOutputs([headphones, speakers]);
            Assert.Null(mixer.SetMonitorIdleRelease(headphones, true));
            MixerSettings saved = mixer.ExportSettings();
            MixerScene scene = mixer.ExportScene();
            Assert.Equal([headphones], saved.MonitorIdleRelease);
            Assert.Equal([headphones], scene.MonitorIdleRelease);

            // A disconnect keeps the output selected, and with it the flag.
            outputs.UnloadModule(headphonesModule);
            WaitForRecovery(() => !pw.ListDevices().Any(d => d.Name == headphones));
            mixer.EnsureMonitorRoutes();
            Assert.Equal([headphones], mixer.Snapshot().MonitorIdleRelease);
            outputs.CreateNullSink(headphones, "Idle save headphones");
            WaitForInputPorts(pw, headphones, 2);
            mixer.EnsureMonitorRoutes();
            Assert.Equal([headphones], mixer.Snapshot().MonitorIdleRelease);

            // Deselecting drops it; ticking the output again starts with it off.
            mixer.SetMonitorOutputs([speakers]);
            Assert.Empty(mixer.Snapshot().MonitorIdleRelease);
            mixer.SetMonitorOutputs([headphones, speakers]);
            Assert.Empty(mixer.Snapshot().MonitorIdleRelease);

            // A reload restores it for a selected Bluetooth output only.
            mixer.ApplySettings(saved with { MonitorIdleRelease = [headphones, speakers, "bluez_output.OTHER.1"] });
            Assert.Equal([headphones], mixer.Snapshot().MonitorIdleRelease);
            mixer.ApplySettings(saved with { MonitorIdleRelease = [] });
            Assert.Empty(mixer.Snapshot().MonitorIdleRelease);

            // A profile carries it; one saved before the option keeps the current choice.
            mixer.ApplyScene(scene);
            Assert.Equal([headphones], mixer.Snapshot().MonitorIdleRelease);
            mixer.ApplyScene(scene with { MonitorIdleRelease = null });
            Assert.Equal([headphones], mixer.Snapshot().MonitorIdleRelease);
            mixer.ApplyScene(scene with { MonitorIdleRelease = [] });
            Assert.Empty(mixer.Snapshot().MonitorIdleRelease);
        }
        finally { outputs.TearDown(); }
    }

    [MonitorPipeWireFact]
    public void AnIdleOutputThatLeavesOrIsPausedLeavesNothingForTheMeterTimerToRetry()
    {
        const string headphones = "bluez_output.IDLE_GONE.1";
        long now = 0, sound = 0;
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        var outputs = new PipeWireAdapter();
        using var mixer = new Mixer(pw) { Clock = () => Volatile.Read(ref now), LastSound = _ => Volatile.Read(ref sound) };
        uint module = outputs.CreateNullSink(headphones, "Idle gone headphones");
        try
        {
            WaitForInputPorts(pw, headphones, 2);
            mixer.Build(MonitorConfig());
            mixer.SetMonitorOutputs([headphones]);
            Assert.Null(mixer.SetMonitorIdleRelease(headphones, true));
            WaitForRecovery(() => GraphLinksInto(headphones).Length == 2);
            Volatile.Write(ref now, (long)Mixer.IdleRelease.TotalMilliseconds);
            Assert.True(mixer.EnsureMonitorRoutes());
            Assert.Equal(1, mixer.WaitingToWake);

            // Paused and resumed in silence: still idle, and watched again.
            mixer.SetMonitorMuted(true);
            Assert.Equal(0, mixer.WaitingToWake);
            mixer.SetMonitorMuted(false);
            Assert.Empty(GraphLinksInto(headphones));
            Assert.Equal(1, mixer.WaitingToWake);

            // The headphones disconnect while idle, then a mix sounds: one
            // sweep finds nothing to link, and the timer stops asking.
            outputs.UnloadModule(module);
            WaitForRecovery(() => !pw.ListDevices().Any(d => d.Name == headphones));
            Volatile.Write(ref sound, Volatile.Read(ref now));
            Assert.False(mixer.WakeIdleOutputs());
            Assert.Equal(0, mixer.WaitingToWake);
            Assert.False(mixer.WakeIdleOutputs());

            // A torn-down mixer leaves the timer nothing to check.
            Volatile.Write(ref now, Volatile.Read(ref sound) + (long)Mixer.IdleRelease.TotalMilliseconds);
            outputs.CreateNullSink(headphones, "Idle gone headphones");
            WaitForInputPorts(pw, headphones, 2);
            mixer.EnsureMonitorRoutes();
            Assert.Equal(1, mixer.WaitingToWake);
            mixer.TearDown();
            Assert.Equal(0, mixer.WaitingToWake);
        }
        finally { outputs.TearDown(); }
    }
}
