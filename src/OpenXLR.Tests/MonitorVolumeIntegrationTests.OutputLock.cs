using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void OutputLockHoldsTheSelectedSinkWithoutMovingOtherOutputsOrMutes()
    {
        const string headphones = "test_locked_headphones";
        const string speakers = "test_unlocked_speakers";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        uint headphonesModule = pw.CreateNullSink(headphones, "Lock test headphones");
        uint speakersModule = pw.CreateNullSink(speakers, "Lock test speakers");
        try
        {
            mixer.Build(new MixerConfig { Channels = [], Mixes = [] });
            mixer.SetMonitorOutputs([headphones, speakers]);
            mixer.SetEnforcedDefaults(Mixer.FollowMonitorOutput, null);
            pw.SetSinkVolume(headphones, 1.5);
            pw.SetSinkVolume(speakers, .45);
            mixer.SyncDeviceVolumes();
            Assert.Throws<IOException>(() => mixer.SetOutputVolumeLock(true, _ => "disk full"));
            Assert.Null(mixer.Snapshot().OutputVolumeLock);
            mixer.SetOutputVolumeLock(true, _ => null);
            Assert.Equal(new OutputVolumeLock(headphones, 1.5), mixer.Snapshot().OutputVolumeLock);
            Assert.True(mixer.Snapshot().MonitorVolumeLocked);
            Assert.Throws<InvalidOperationException>(() => mixer.AdjustOutputVolume(headphones, -.1));
            Assert.Throws<InvalidOperationException>(() => mixer.SetOutputDeviceVolume(headphones, .3));

            bool nativeMute = pw.GetSinkMuted(headphones);
            ProcessResult desktop = ProcessRunner.Run("pactl", ["set-sink-volume", headphones, "180%"]);
            Assert.True(desktop.Ok, desktop.Stderr);
            Assert.True(SpinWait.SpinUntil(() => pw.GetUnclampedSinkVolume(headphones) == 1.8, TimeSpan.FromSeconds(3)));
            mixer.SyncDeviceVolumes();
            Assert.Equal(1.5, pw.GetUnclampedSinkVolume(headphones));
            Assert.Equal(.45, pw.GetSinkVolume(speakers));
            Assert.Equal(nativeMute, pw.GetSinkMuted(headphones));

            mixer.SetOutputVolume(.42);
            Assert.Equal(1.5, pw.GetSinkVolume(headphones));
            Assert.Equal(.42, pw.GetSinkVolume(speakers));
            mixer.SetMonitorOutputs([speakers, headphones]);
            mixer.EnforceDefaults();
            Assert.False(mixer.Snapshot().MonitorVolumeLocked);
            mixer.SetOutputVolume(.62);
            Assert.Equal(.62, pw.GetSinkVolume(speakers));
            Assert.Equal(1.5, pw.GetSinkVolume(headphones));

            Assert.Throws<IOException>(() => mixer.SetOutputVolumeLock(false, _ => "disk full"));
            Assert.NotNull(mixer.Snapshot().OutputVolumeLock);
            mixer.SetOutputVolumeLock(false, _ => null);
            mixer.SetOutputDeviceVolume(headphones, .31);
            Assert.Equal(.31, pw.GetSinkVolume(headphones));
        }
        finally
        {
            mixer.SetMonitorOutputs([]);
            pw.UnloadModule(speakersModule);
            pw.UnloadModule(headphonesModule);
        }
    }

    [MonitorPipeWireFact]
    public void OutputLockSurvivesRestartAndReturnsWithTheSameDevice()
    {
        const string sink = "test_reconnected_locked_output";
        string path = Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!, "output-lock-test.json");
        var config = new MixerConfig { Channels = [], Mixes = [] };
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        var devices = new PipeWireAdapter();
        uint module = devices.CreateNullSink(sink, "Reconnect lock test");
        try
        {
            using (var mixer = new Mixer(pw))
            {
                mixer.Build(config);
                mixer.SetMonitorOutputs([sink]);
                mixer.SetEnforcedDefaults(Mixer.FollowMonitorOutput, null);
                pw.SetSinkVolume(sink, .68);
                mixer.SetOutputVolumeLock(true, settings => settings.Save(path));
            }
            Assert.Equal(new OutputVolumeLock(sink, .68), MixerSettings.Load(path)!.OutputVolumeLock);
            devices.UnloadModule(module);
            module = 0;
            using var restored = new Mixer(pw);
            restored.Build(config);
            restored.ApplySettings(MixerSettings.Load(path)!);
            restored.SyncDeviceVolumes();
            Assert.Equal(new OutputVolumeLock(sink, .68), restored.Snapshot().OutputVolumeLock);

            module = devices.CreateNullSink(sink, "Reconnect lock test");
            pw.SetSinkVolume(sink, .3);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                restored.SyncDeviceVolumes();
                return pw.GetSinkVolume(sink) == .68;
            }, TimeSpan.FromSeconds(3)));
            restored.SetMonitorOutputs([]);
        }
        finally
        {
            if (module != 0) devices.UnloadModule(module);
            File.Delete(path);
        }
    }
}
