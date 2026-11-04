using System.Globalization;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The soundboard against a private PipeWire server with the native helper:
/// a clip of constant 0.1 played into SFX is recorded from the Stream and
/// Chat virtual microphones while SFX's mutes, the routing setting, the
/// layout and the helper itself change under it.
/// </summary>
[Collection("xdg-config")]
public sealed partial class SoundboardIntegrationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-soundboard-audio-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [MonitorPipeWireFact]
    public void AClipPlaysThroughSfxAndFollowsItsChannel()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        var mixer = new Mixer(pw);
        bool disposed = false;
        try
        {
            // Where a stream that lost its sink would end up.
            pw.CreateNullSink("soundboard_default", "Soundboard test default output");
            pw.Run("pactl", "set-default-sink", "soundboard_default");
            static Dictionary<string, double> Sends() => new() { ["monitor"] = 1, ["stream"] = 1, ["chat"] = 1 };
            mixer.Build(new MixerConfig
            {
                Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic), new("chat", "Chat", MixKind.VirtualMic)],
                Channels = [new("system", "System") { Levels = Sends() }, new("sfx", "SFX") { Levels = Sends() }],
            });
            var saved = new List<SoundboardLibrary>();
            mixer.UseSoundboard(SoundboardLibrary.Empty, next => { saved.Add(next); return null; }, Path.Combine(_directory, "clips"));
            Func<MixerSettings, string?> persist = _ => null;

            mixer.SetSoundboard(true, persist);
            // The link can already exist while the registry monitor is still
            // receiving the player node.
            Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId(PipeWireAdapter.PlayerNode) is not null, TimeSpan.FromSeconds(3)),
                "The soundboard player did not appear in the PipeWire registry.");
            Assert.True(saved[^1].Enabled);
            string wav = Path.Combine(_directory, "tone.wav");
            WriteConstantWav(wav, .1, seconds: 2);
            SoundboardClip clip = mixer.ImportClip(wav, "Tone");
            Assert.Equal("tone", clip.Id);
            mixer.UpdateClip("tone", new SoundboardClipChange(Loop: true));
            WaitLoaded(mixer, "tone");
            Assert.Equal(2, Assert.Single(mixer.Soundboard().Clips).Seconds);

            // Nothing plays until the key is pressed.
            AssertLevel(0, "OpenXLR_stream", "before any press");
            mixer.PlayClip("tone", null);
            AssertLevel(.1, "OpenXLR_stream", "the clip through SFX's unity send");
            Assert.True(SpinWait.SpinUntil(() => mixer.ReadMeters().TryGetValue("clip:tone", out double[]? at) && at[1] == 1, TimeSpan.FromSeconds(3)));
            Assert.Equal(["tone"], mixer.Soundboard().Playing);
            Assert.True(mixer.SoundboardActivityChanged());

            // A second press in overlap mode adds a voice; restart replaces them.
            mixer.PlayClip("tone", "overlap");
            AssertLevel(.2, "OpenXLR_stream", "two voices");
            mixer.PlayClip("tone", "restart");
            AssertLevel(.1, "OpenXLR_stream", "restart leaves one voice");

            // SFX's own mute in Chat silences the clip there and nowhere else.
            mixer.SetChannelMuted("sfx", "chat", true);
            AssertLevel(0, "OpenXLR_chat", "SFX muted in Chat");
            AssertLevel(.1, "OpenXLR_stream", "SFX still open in Stream");
            mixer.SetChannelMuted("sfx", "chat", false);

            mixer.StopClips();
            AssertLevel(0, "OpenXLR_stream", "stop all");
            Assert.True(SpinWait.SpinUntil(() => mixer.Soundboard().Playing.Count == 0, TimeSpan.FromSeconds(3)));

            // No routing setting applies to a link the daemon made.
            mixer.SetRouteAppsByDefault(false, persist);
            mixer.PlayClip("tone", null);
            AssertLevel(.1, "OpenXLR_stream", "automatic routing off");

            // A live layout edit that reloads nothing keeps the clip playing.
            mixer.CreateMix("Recorder", persist, MixKind.VirtualMic);
            AssertLevel(.1, "OpenXLR_stream", "a mix created during the clip");

            // A rename reloads SFX's sink; the player follows it by id.
            mixer.RenameApplicationChannel("sfx", "Effects", persist);
            Assert.Equal("sfx", mixer.Soundboard().Channel);
            mixer.PlayClip("tone", "restart");
            AssertLevel(.1, "OpenXLR_stream", "SFX renamed");

            // A helper that dies is started again and loads its clips again.
            SoundboardPlayer player = Player(mixer)!;
            Assert.Equal(SoundboardClipStatus.Ready, mixer.Soundboard().Clips.Single().Status);
            player.Host.Process.Kill();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                SoundboardState state = mixer.Soundboard();
                return Player(mixer) is { } next && next != player && state.Clips.Single().Status == SoundboardClipStatus.Ready;
            }, TimeSpan.FromSeconds(15)), mixer.Soundboard().ToString());
            // A player started again plays: the board has nothing to say.
            Assert.Null(mixer.Soundboard().Error);
            mixer.PlayClip("tone", null);
            AssertLevel(.1, "OpenXLR_stream", "after the helper was restarted");

            // Deleting SFX mid-clip is silent, and the board says why it is off.
            mixer.DeleteApplicationChannel("sfx", persist);
            AssertLevel(0, "OpenXLR_stream", "SFX deleted mid-clip");
            SoundboardState off = mixer.Soundboard();
            Assert.Null(off.Channel);
            Assert.False(off.Enabled);
            Assert.Equal(Mixer.SoundboardChannelDeleted, off.Error);
            Assert.Equal(SoundboardClipStatus.Off, off.Clips.Single().Status);
            Assert.False(saved[^1].Enabled);
            Assert.Equal(["tone"], saved[^1].Clips.Select(c => c.Id));
            Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId(PipeWireAdapter.PlayerNode) is null, TimeSpan.FromSeconds(3)));
            Assert.Equal(Mixer.SoundboardChannelDeleted, Assert.Throws<InvalidOperationException>(() => mixer.PlayClip("tone", null)).Message);
            mixer.EnsureFilterRoutes();
            Assert.Null(pw.FindNodeId(PipeWireAdapter.PlayerNode));

            // Turning it on again creates SFX, muted in every mix.
            mixer.SetSoundboard(true, persist);
            ChannelStatus sfx = mixer.Snapshot().Channels.Single(c => c.Id == "sfx");
            Assert.Equal("SFX", sfx.Name);
            Assert.Equal(["monitor", "stream", "chat", "recorder"], sfx.MutedIn);
            Assert.Equal("sfx", mixer.Soundboard().Channel);
            Assert.Null(mixer.Soundboard().Error);
            WaitLoaded(mixer, "tone");
            mixer.PlayClip("tone", null);
            AssertLevel(0, "OpenXLR_stream", "a recreated SFX is muted");
            mixer.SetChannelMuted("sfx", "stream", false);
            AssertLevel(.1, "OpenXLR_stream", "the recreated SFX's Stream send opened");

            // An application channel that already holds the id is used as it is.
            mixer.SetSoundboard(false, persist);
            Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId(PipeWireAdapter.PlayerNode) is null, TimeSpan.FromSeconds(3)));
            int channels = mixer.Snapshot().Channels.Count;
            mixer.SetSoundboard(true, persist);
            Assert.Equal(channels, mixer.Snapshot().Channels.Count);
            WaitLoaded(mixer, "tone");

            // A daemon stopped mid-clip leaves no player and nothing on the
            // default output.
            mixer.PlayClip("tone", null);
            AssertLevel(.1, "OpenXLR_stream", "before the stop");
            mixer.Dispose();
            disposed = true;
            Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId(PipeWireAdapter.PlayerNode) is null, TimeSpan.FromSeconds(3)));
            AssertLevel(0, "soundboard_default.monitor", "the default output after the stop");
        }
        finally
        {
            if (!disposed) mixer.Dispose();
            pw.TearDown();
        }
    }

    /// <summary>A graph with Stream and SFX, the board on, and a looping clip of constant 0.1 loaded as <c>tone</c>.</summary>
    private void StartTone(PipeWireAdapter pw, Mixer mixer)
    {
        static Dictionary<string, double> Sends() => new() { ["monitor"] = 1, ["stream"] = 1 };
        mixer.Build(new MixerConfig
        {
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic)],
            Channels = [new("system", "System") { Levels = Sends() }, new("sfx", "SFX") { Levels = Sends() }],
        });
        mixer.UseSoundboard(SoundboardLibrary.Empty, _ => null, Path.Combine(_directory, "clips"));
        mixer.SetSoundboard(true, _ => null);
        string wav = Path.Combine(_directory, "tone.wav");
        WriteConstantWav(wav, .1, seconds: 2);
        mixer.ImportClip(wav, "Tone");
        mixer.UpdateClip("tone", new SoundboardClipChange(Loop: true));
        WaitLoaded(mixer, "tone");
        _ = pw;
    }

    private static SoundboardPlayer? Player(Mixer mixer)
        => (SoundboardPlayer?)typeof(Mixer).GetField("_player", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mixer);

    /// <summary>
    /// A new graph rate stops the helper by itself within a cycle: it refuses
    /// audio at a rate it was not started at. Four changes in a row, each
    /// followed by sweeps at the daemon's own pace of one a second, must each
    /// bring the player back at the new rate with its clip, and none may count
    /// as a failure, leave a clip out or turn the board off.
    /// </summary>
    [MonitorPipeWireFact]
    public void AChangedGraphRateStartsThePlayerAgainWithItsClips()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        StartTone(pw, mixer);
        int before = Player(mixer)!.Host.SampleRate;
        try
        {
            foreach (int rate in new[] { 44100, 96000, 44100, before })
            {
                SoundboardPlayer old = Player(mixer)!;
                pw.Run("pw-metadata", "-n", "settings", "0", "clock.force-rate", rate == before ? "0" : rate.ToString(CultureInfo.InvariantCulture));
                // The helper stops on its own before any sweep comes.
                Assert.True(SpinWait.SpinUntil(() => !old.Host.IsRunning, TimeSpan.FromSeconds(3)), $"the player at {old.Host.SampleRate} Hz kept running at {rate} Hz");
                bool back = false;
                for (int sweep = 0; sweep < 10 && !back; sweep++)
                {
                    Thread.Sleep(1000);
                    mixer.EnsureFilterRoutes();
                    back = Player(mixer) is { } next && next != old && next.Host.SampleRate == rate
                        && mixer.Soundboard().Clips.Single().Status == SoundboardClipStatus.Ready;
                }
                Assert.True(back, $"{rate} Hz: {Player(mixer)?.Host.SampleRate} {mixer.Soundboard()}");
                SoundboardState state = mixer.Soundboard();
                Assert.True(state.Enabled);
                Assert.Null(state.Error);
                Assert.Null(state.Clips.Single().Error);
                Assert.Equal(2, state.Clips.Single().Seconds);
                Assert.Empty(Failures(mixer));
            }
            mixer.PlayClip("tone", null);
            AssertLevel(.1, "OpenXLR_stream", "after four rate changes");
        }
        finally { pw.Run("pw-metadata", "-n", "settings", "0", "clock.force-rate", "0"); }
        // A steady rate leaves the running player alone.
        SoundboardPlayer steady = Player(mixer)!;
        for (int sweep = 0; sweep < 3; sweep++) mixer.EnsureFilterRoutes();
        Assert.Same(steady, Player(mixer));
    }

    /// <summary>The restart policy's record, by key: what counts towards giving a stage up.</summary>
    private static System.Collections.IDictionary Failures(Mixer mixer)
    {
        object policy = typeof(Mixer).GetField("_restarts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mixer)!;
        return (System.Collections.IDictionary)policy.GetType().GetField("_failures", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(policy)!;
    }

    /// <summary>
    /// Presses sent while the player is not linked never sound late, all at
    /// once: a broken link is made again by the sweep and the presses that
    /// waited for it are dropped, and while the daemon itself is making the
    /// link again a press is refused with a sentence.
    /// </summary>
    [MonitorPipeWireFact]
    public void PressesThatWaitedForALinkNeverSoundLate()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        StartTone(pw, mixer);
        var link = (PortLink)typeof(Mixer).GetField("_playerLink", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mixer)!;
        pw.Unlink(link);
        for (int press = 0; press < 3; press++) mixer.PlayClip("tone", "overlap");
        Thread.Sleep(600);
        mixer.EnsureFilterRoutes();
        AssertLevel(0, "OpenXLR_stream", "presses that waited for the link");
        mixer.PlayClip("tone", null);
        AssertLevel(.1, "OpenXLR_stream", "a press once linked again");
        mixer.StopClips();
        AssertLevel(0, "OpenXLR_stream", "stopped");

        typeof(Mixer).GetField("_playerLinked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(mixer, false);
        Assert.Equal(Mixer.SoundboardReconnecting, Assert.Throws<InvalidOperationException>(() => mixer.PlayClip("tone", null)).Message);
        mixer.EnsureFilterRoutes();
        mixer.PlayClip("tone", null);
        AssertLevel(.1, "OpenXLR_stream", "linked again by the sweep");
    }

    /// <summary>
    /// How long after stopClips the clip is gone from the Stream virtual
    /// microphone, counted in the recording. The recorder's file length when
    /// the command is sent marks the moment in it, to within one of the
    /// recorder's writes; a pause of 100 ms put before the command moved the
    /// figure by the pause, so the mark follows the clock. Here the figure is
    /// the 5 ms fade and a block, far inside the bound.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task StopAllIsSilentWithinFiftyMilliseconds()
    {
        const int rate = 48000;
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        // The desk's block size; the default 1024 frames would add 21 ms a hop.
        mixer.SetBlockSize(256);
        try
        {
            StartTone(pw, mixer);
            var measured = new List<double>();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                mixer.PlayClip("tone", "restart");
                AssertLevel(.1, "OpenXLR_stream", "before the stop");
                string capture = Path.Combine(_directory, $"stop-{attempt}.raw");
                using var cancel = new CancellationTokenSource();
                Task<ProcessResult> recording = ProcessRunner.RunAsync("sh", ["-c",
                    "exec parec --raw --format=float32le --rate=48000 --channels=2 --latency-msec=5 --device=OpenXLR_stream > \"$0\"", capture],
                    TimeSpan.FromSeconds(30), cancel: cancel.Token);
                Assert.True(SpinWait.SpinUntil(() => File.Exists(capture) && new FileInfo(capture).Length >= rate * 8 / 2, TimeSpan.FromSeconds(5)), "the recorder started");
                long mark = new FileInfo(capture).Length / 8;
                mixer.StopClips();
                Thread.Sleep(600);
                cancel.Cancel();
                await recording;
                byte[] bytes = File.ReadAllBytes(capture);
                var frames = new float[bytes.Length / 8];
                for (int i = 0; i < frames.Length; i++)
                    frames[i] = Math.Max(Math.Abs(BitConverter.ToSingle(bytes, i * 8)), Math.Abs(BitConverter.ToSingle(bytes, i * 8 + 4)));
                // The clip played up to the mark, and the recording ends in silence.
                Assert.All(frames[(int)(mark - rate / 10)..(int)mark], v => Assert.InRange(v, .097f, .103f));
                Assert.True(frames.Length - mark > rate * 3 / 10, "the recording ran on after the stop");
                int last = Array.FindLastIndex(frames, v => v >= 1e-4f);
                Assert.True(last < frames.Length - rate / 4, "the recording ends in silence");
                measured.Add((last + 1 - mark) * 1000.0 / rate);
            }
            string figures = string.Join(", ", measured.Select(ms => $"{ms:0.0} ms"));
            Console.WriteLine($"stop all to silence in the recording: {figures}");
            Assert.True(measured.All(ms => ms is >= 0 and <= 50), $"stop all to silence in the recording: {figures}");
        }
        finally { mixer.SetBlockSize(null); }
    }

    private static void WaitLoaded(Mixer mixer, string id)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureFilterRoutes();
            return mixer.Soundboard().Clips.Single(c => c.Id == id).Loaded;
        }, TimeSpan.FromSeconds(10)), mixer.Soundboard().ToString());

    /// <summary>A stereo 16-bit WAV at 48 kHz holding one constant value.</summary>
    private static void WriteConstantWav(string path, double value, int seconds)
    {
        const int rate = 48000;
        int frames = rate * seconds;
        short sample = (short)Math.Round(value * 32768);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 4);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(rate);
        writer.Write(rate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 4);
        for (int i = 0; i < frames * 2; i++) writer.Write(sample);
    }

    /// <summary>
    /// Record a source until its level settles at <paramref name="expected"/>
    /// on both channels, or at silence for 0, within a few seconds.
    /// </summary>
    private static void AssertLevel(double expected, string source, string because)
    {
        ProcessResult result = ProcessRunner.Run("python3", ["-c", """
            import math, os, struct, subprocess, sys, tempfile, time
            expected = float(sys.argv[1])
            deadline = time.monotonic() + 6
            with tempfile.TemporaryFile() as capture:
                p = subprocess.Popen(['parec', '--raw', '--format=float32le', '--rate=48000', '--channels=2', '--latency-msec=20',
                                      '--device=' + sys.argv[2]], stdout=capture, stderr=subprocess.PIPE)
                try:
                    last = 'no captured samples'
                    while time.monotonic() < deadline:
                        size = os.fstat(capture.fileno()).st_size // 8 * 8
                        if size > 9600 * 8:
                            # pread leaves the writer's shared file offset alone.
                            tail = struct.unpack('<19200f', os.pread(capture.fileno(), 9600 * 8, size - 9600 * 8))
                            matching = sum(abs(v - expected) < .003 for v in tail) / len(tail)
                            rms = math.sqrt(sum(v * v for v in tail) / len(tail))
                            last = (min(tail), max(tail), matching, rms)
                            if all(map(math.isfinite, tail)) and (rms < 1e-4 if expected == 0 else matching > .98):
                                break
                        if p.poll() is not None:
                            raise AssertionError(('capture exited', p.returncode, p.communicate()[1].decode()))
                        time.sleep(.05)
                    else:
                        raise AssertionError(('no settled level before the deadline', expected, last))
                finally:
                    if p.poll() is None: p.kill()
                    p.wait()
            """, expected.ToString(CultureInfo.InvariantCulture), source], TimeSpan.FromSeconds(12));
        Assert.True(result.Ok, $"{because}: {result.StdoutText}{result.Stderr}\n" + ProcessRunner.Run("pw-link", ["-l"]).StdoutText);
    }
}
