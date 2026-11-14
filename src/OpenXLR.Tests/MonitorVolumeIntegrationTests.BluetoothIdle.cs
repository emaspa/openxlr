using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// A link from a running mix keeps a Bluetooth output's stream open even
/// when it carries only silence, so the mixer unlinks a Bluetooth output
/// whose feed has been silent for a while and links it again on sound.
/// The meters' clock is replaced, so the test decides when there was sound.
/// </summary>
public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void ABluetoothOutputIsUnlinkedWhileItsFeedIsSilentAndLinkedAgainOnSound()
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

        // After the silence the headphones lose their links; the wired output keeps its own.
        Volatile.Write(ref now, (long)Mixer.IdleRelease.TotalMilliseconds);
        Assert.True(mixer.EnsureMonitorRoutes());
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
    }
}
