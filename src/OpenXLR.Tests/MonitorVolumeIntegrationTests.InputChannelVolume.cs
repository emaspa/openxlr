using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    /// <summary>
    /// XLR 1's own volume scales each of its sends and nothing else: not the
    /// other strips' or channels' sends, not XLR 1's sink, not a mix master.
    /// It is measured on the leg volumes PipeWire reports and on the audio a
    /// monitor output records, first with XLR 1 on the interface and then
    /// while it is bound to another source, which keeps the volume.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task Xlr1VolumeScalesEachOfItsSendsAndNothingElseOnTheUnitAndWhileBound()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        (uint unitParent, uint unit) = LoadBindUnit(pw);
        pw.CreateNullSink("primary_signal", "Webcam test signal");
        pw.CreateVirtualMic("bind_source", "primary_signal.monitor", "Webcam microphone");
        pw.CreateNullSink("soundcheck_output", "Channel volume tap");
        mixer.SetInputDeviceHint(["Wave_XLR_BINDUNIT-"], hardwareOutputRouting: false, modelFallback: false);
        mixer.Build(MixerConfig.Default());
        mixer.SetMonitorOutputs(["soundcheck_output"]);
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> unitSignal = BindUnitSignal(stop.Token);
        Task<ProcessResult> webcam = PrimarySignal(stop.Token);
        try
        {
            PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr1").BindingConnected; });
            mixer.SetLevel("xlr1", "stream", 0.6);
            mixer.SetLevel("xlr1", "chat", 0.3);
            mixer.SetMixVolume("stream", 0.9);
            mixer.SetChannelMuted("xlr1", "monitor", false);
            Assert.Null(mixer.SetMonitorFeed("soundcheck_output", "monitor"));
            Dictionary<string, (double Volume, bool Muted)> before = SettledLegs();
            double xlr1Sink = pw.GetSinkVolume("OpenXLR_ch_xlr1")!.Value;
            Dictionary<string, double> masters = mixer.Snapshot().Mixes.ToDictionary(m => m.Id, m => m.Volume);
            // The unit plays .2 on every port; the monitor hears XLR 1 at its full send.
            PrimarySound(.2);

            // On the interface: every XLR 1 leg drops by the volume's cube, the rest stay.
            mixer.SetChannelVolume("xlr1", 0.8);
            Assert.Equal(0.8, Strip(mixer, "xlr1").Volume);
            AssertOnlyXlr1Moved(before, 0.8);
            PrimarySound(.2 * Math.Pow(0.8, 3));

            // Bound to the webcam's left port: the binding moves the source,
            // not the level. The strip's monitor send starts muted after a
            // binding change and is opened again here.
            mixer.SetInputBinding("xlr1", new("bind_source", 0, 0));
            PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr1").BindingConnected; });
            Assert.Equal(0.8, Strip(mixer, "xlr1").Volume);
            mixer.SetChannelMuted("xlr1", "monitor", false);
            AssertOnlyXlr1Moved(before, 0.8);
            PrimarySound(.1 * Math.Pow(0.8, 3));
            // Turned while bound, it still reaches each of XLR 1's sends alone.
            mixer.SetChannelVolume("xlr1", 0.7);
            AssertOnlyXlr1Moved(before, 0.7);
            PrimarySound(.1 * Math.Pow(0.7, 3));

            // Back on the interface, the volume is still the one set while bound.
            mixer.SetInputBinding("xlr1", null);
            PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr1").BindingConnected; });
            mixer.SetChannelMuted("xlr1", "monitor", false);
            Assert.Equal(0.7, Strip(mixer, "xlr1").Volume);
            AssertOnlyXlr1Moved(before, 0.7);
            PrimarySound(.2 * Math.Pow(0.7, 3));
            mixer.SetChannelVolume("xlr1", 1);
            AssertOnlyXlr1Moved(before, 1);
            PrimarySound(.2);

            void AssertOnlyXlr1Moved(Dictionary<string, (double Volume, bool Muted)> baseline, double volume)
            {
                MixerState state = mixer.Snapshot();
                Dictionary<string, (double Volume, bool Muted)> legs = [];
                bool settled = SpinWait.SpinUntil(() =>
                {
                    mixer.EnsureCellLevels();
                    legs = Legs();
                    return baseline.Keys.All(cell => legs.ContainsKey(cell)
                        && Math.Abs(legs[cell].Volume - Expected(cell, baseline[cell].Volume)) < 1e-4);
                }, TimeSpan.FromSeconds(5));
                Assert.True(settled, "leg volumes: " + string.Join(", ", baseline.Keys.Select(cell =>
                    $"{cell}={(legs.TryGetValue(cell, out var leg) ? leg.Volume : double.NaN):0.######} want {Expected(cell, baseline[cell].Volume):0.######}")));
                // XLR 1's sink and the mix masters do not move.
                Assert.Equal(xlr1Sink, pw.GetSinkVolume("OpenXLR_ch_xlr1")!.Value, 4);
                Assert.Equal(masters, mixer.Snapshot().Mixes.ToDictionary(m => m.Id, m => m.Volume));
                Assert.All(mixer.Snapshot().Channels.Where(c => c.Id != "xlr1"), c => Assert.Equal(1.0, c.Volume));

                // An XLR 1 leg carries its send times the volume times the
                // master; every other leg is where it was.
                double Expected(string cell, double start)
                    => cell.StartsWith("xlr1|", StringComparison.Ordinal) ? Target(state, cell, volume) : start;
            }

            Dictionary<string, (double Volume, bool Muted)> SettledLegs()
            {
                Dictionary<string, (double Volume, bool Muted)> legs = [];
                MixerState state = mixer.Snapshot();
                string[] cells = [.. state.Channels.SelectMany(c => c.Levels.Keys.Select(m => $"{c.Id}|{m}"))];
                Assert.Contains("xlr1|stream", cells);
                bool settled = SpinWait.SpinUntil(() =>
                {
                    mixer.EnsureCellLevels();
                    legs = Legs();
                    return cells.All(cell => legs.TryGetValue(cell, out var leg) && Math.Abs(leg.Volume - Target(state, cell, 1)) < 1e-4);
                }, TimeSpan.FromSeconds(6));
                Assert.True(settled, "every send's leg came up at its fader");
                return cells.ToDictionary(cell => cell, cell => legs[cell]);
            }

            // A leg's gain: its send level times the channel volume, set in
            // whole percent and applied on the cubic scale. Every mix's master
            // is on its sink.
            static double Target(MixerState state, string cell, double volume)
            {
                int bar = cell.IndexOf('|', StringComparison.Ordinal);
                ChannelStatus channel = state.Channels.Single(c => c.Id == cell[..bar]);
                MixStatus mix = state.Mixes.Single(m => m.Id == cell[(bar + 1)..]);
                double gain = Mixer.SendGain(channel.Levels[mix.Id], volume, 1);
                return Math.Pow(Math.Round(gain * 100) / 100, 3);
            }
        }
        finally
        {
            stop.Cancel();
            await unitSignal;
            await webcam;
            UnloadModule(unit);
            UnloadModule(unitParent);
        }
    }
}
