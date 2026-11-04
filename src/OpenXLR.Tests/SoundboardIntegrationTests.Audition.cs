using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class SoundboardIntegrationTests
{
    /// <summary>
    /// An audition is heard on the selected monitor output through the
    /// monitor mix it plays, and nowhere else: not in the Stream or Chat
    /// virtual microphones, not in the other monitor mix, whatever SFX's
    /// sends, mutes and fader say. It follows the output's feed, is refused
    /// when the output plays no monitor mix, and is independent of the clips
    /// pressed into SFX.
    /// </summary>
    [MonitorPipeWireFact]
    public void AnAuditionPlaysInTheMonitorMixesTheOutputsPlayAndNowhereElse()
    {
        const string Output = "soundboard_audition_output";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        // The output belongs to an adapter of its own, so the mixer's teardown leaves it in place.
        var outputs = new PipeWireAdapter();
        var mixer = new Mixer(pw);
        try
        {
            outputs.CreateNullSink(Output, "Audition test output");
            // SFX is open at unity in every mix, so a clip that went through
            // SFX would be heard everywhere.
            static Dictionary<string, double> Sends() => new() { ["monitor"] = 1, ["monitor2"] = 1, ["stream"] = 1, ["chat"] = 1, ["auxout"] = 1 };
            mixer.Build(new MixerConfig
            {
                Mixes =
                [
                    new("monitor", "Monitor A", MixKind.Monitor), new("monitor2", "Monitor B", MixKind.Monitor),
                    new("stream", "Stream", MixKind.VirtualMic), new("chat", "Chat", MixKind.VirtualMic),
                    new("auxout", "Aux", MixKind.AuxPort),
                ],
                Channels = [new("system", "System") { Levels = Sends() }, new("sfx", "SFX") { Levels = Sends() }],
            });
            mixer.SetMonitorOutputs([Output]);
            mixer.UseSoundboard(SoundboardLibrary.Empty, _ => null, Path.Combine(_directory, "clips"));
            mixer.SetSoundboard(true, _ => null);
            string wav = Path.Combine(_directory, "long.wav");
            WriteConstantWav(wav, .1, seconds: 20);
            mixer.ImportClip(wav, "Long");
            WaitLoaded(mixer, "long");
            // The output plays Monitor A, the default feed: the pair goes there alone.
            Assert.Equal(["monitor"], mixer.AuditionMixes);
            string heard = Output + ".monitor";

            mixer.AuditionClip("long");
            AssertLevel(.1, heard, "the audition on the monitor output");
            AssertLevel(0, "OpenXLR_stream", "an audition never reaches Stream");
            AssertLevel(0, "OpenXLR_chat", "an audition never reaches Chat");
            AssertLevel(0, "OpenXLR_mix_auxout.monitor", "an audition never reaches Aux");
            AssertLevel(0, "OpenXLR_mix_monitor2.monitor", "nor the monitor mix the output does not play");
            Assert.True(SpinWait.SpinUntil(() => mixer.Soundboard().Auditioning == "long", TimeSpan.FromSeconds(3)));
            Assert.Empty(mixer.Soundboard().Playing);
            Assert.False(mixer.ReadMeters().ContainsKey("clip:long"));

            // SFX's mute in the monitor mix and its fader do not touch it.
            mixer.SetChannelMuted("sfx", "monitor", true);
            mixer.SetChannelVolume("sfx", 0);
            AssertLevel(.1, heard, "SFX muted in Monitor A and turned down");
            mixer.SetChannelVolume("sfx", 1);

            // A press plays into SFX beside it: Stream hears the press, the
            // output hears the audition alone, SFX being muted there.
            mixer.PlayClip("long", null);
            AssertLevel(.1, "OpenXLR_stream", "the press through SFX");
            AssertLevel(.1, "OpenXLR_mix_auxout.monitor", "the press reaches Aux, so the recording there hears what it is sent");
            AssertLevel(.1, heard, "the audition beside the press");
            mixer.SetChannelMuted("sfx", "monitor", false);
            AssertLevel(.2, heard, "the press and the audition summed on the output");
            // Stopping the audition leaves the press playing, and the other way round.
            mixer.StopAudition();
            AssertLevel(.1, heard, "the press alone after stopAudition");
            AssertLevel(.1, "OpenXLR_stream", "the press after stopAudition");
            Assert.True(SpinWait.SpinUntil(() => mixer.Soundboard().Auditioning is null, TimeSpan.FromSeconds(3)));
            mixer.SetChannelMuted("sfx", "monitor", true);
            mixer.AuditionClip("long");
            mixer.StopClips();
            AssertLevel(0, heard, "stopClips ends the audition");
            AssertLevel(0, "OpenXLR_stream", "stopClips ends the press");

            // The audition follows the mix the output plays.
            Assert.Null(mixer.SetMonitorFeed(Output, "monitor2"));
            Assert.Equal(["monitor2"], mixer.AuditionMixes);
            mixer.AuditionClip("long");
            AssertLevel(.1, heard, "the audition through Monitor B");
            AssertLevel(0, "OpenXLR_mix_monitor.monitor", "Monitor A no longer has it");
            AssertLevel(0, "OpenXLR_stream", "still nothing on Stream");

            // A feed that sums a monitor mix with Stream hears it once, through
            // the monitor mix; Stream itself stays silent.
            Assert.Null(mixer.SetMonitorFeed(Output, "monitor+stream"));
            Assert.Equal(["monitor"], mixer.AuditionMixes);
            mixer.AuditionClip("long");
            AssertLevel(.1, heard, "the audition through Monitor A of Monitor A plus Stream");
            AssertLevel(0, "OpenXLR_stream", "Stream in the output's feed does not carry it");

            // An output that plays Stream alone cannot hear an audition, and
            // the request says so instead of playing it nowhere.
            Assert.Null(mixer.SetMonitorFeed(Output, "stream"));
            Assert.Empty(mixer.AuditionMixes);
            AssertLevel(0, heard, "the audition is unlinked from the mix the output left");
            Assert.Equal(Mixer.SoundboardAuditionUnheard, Assert.Throws<InvalidOperationException>(() => mixer.AuditionClip("long")).Message);
            Assert.Null(mixer.SetMonitorFeed(Output, "monitor"));

            // With no output selected nothing would hear an audition, so it is
            // refused and the pair is linked nowhere; the default monitor mix
            // has no output to play on.
            mixer.SetMonitorOutputs([]);
            Assert.Empty(mixer.AuditionMixes);
            Assert.Equal(Mixer.SoundboardAuditionNoOutput, Assert.Throws<InvalidOperationException>(() => mixer.AuditionClip("long")).Message);
            mixer.EnsureFilterRoutes();
            Assert.Empty(mixer.AuditionMixes);
            mixer.SetMonitorOutputs([Output]);
            Assert.Equal(["monitor"], mixer.AuditionMixes);

            // Nor while listening is paused; it is heard again once listening resumes.
            mixer.SetMonitorMuted(true);
            Assert.Empty(mixer.AuditionMixes);
            Assert.Equal(Mixer.SoundboardAuditionPaused, Assert.Throws<InvalidOperationException>(() => mixer.AuditionClip("long")).Message);
            mixer.EnsureFilterRoutes();
            Assert.Equal(Mixer.SoundboardAuditionPaused, Assert.Throws<InvalidOperationException>(() => mixer.AuditionClip("long")).Message);
            mixer.SetMonitorMuted(false);
            Assert.Equal(["monitor"], mixer.AuditionMixes);

            // The mix's own master applies, as for anything the mix plays:
            // half on the desktop scale is an eighth of the amplitude.
            mixer.AuditionClip("long");
            AssertLevel(.1, heard, "before the master moves");
            mixer.SetMixVolume("monitor", .5);
            AssertLevel(.0125, heard, "Monitor A's master at half");
            mixer.SetMixVolume("monitor", 1);

            // A broken link is made again by the sweep.
            string left = PipeWireAdapter.PlayerNode + ":audition_0", right = PipeWireAdapter.PlayerNode + ":audition_1";
            Assert.True(ProcessRunner.Run("pw-link", ["-d", left, "OpenXLR_mix_monitor:playback_FL"]).Ok);
            Assert.True(ProcessRunner.Run("pw-link", ["-d", right, "OpenXLR_mix_monitor:playback_FR"]).Ok);
            AssertLevel(0, heard, "the pair unlinked by hand");
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                return Linked(left, "OpenXLR_mix_monitor:playback_FL") && Linked(right, "OpenXLR_mix_monitor:playback_FR");
            }, TimeSpan.FromSeconds(10)), ProcessRunner.Run("pw-link", ["-l"]).StdoutText);
            mixer.AuditionClip("long");
            AssertLevel(.1, heard, "after the sweep relinked the pair");

            // A player started again links its pair again.
            SoundboardPlayer player = Player(mixer)!;
            player.Host.Process.Kill();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                return Player(mixer) is { } next && next != player && mixer.Soundboard().Clips.Single().Loaded;
            }, TimeSpan.FromSeconds(15)), mixer.Soundboard().ToString());
            Assert.Equal(["monitor"], mixer.AuditionMixes);
            mixer.AuditionClip("long");
            AssertLevel(.1, heard, "after the helper was restarted");
            AssertLevel(0, "OpenXLR_stream", "and never on Stream");

            // Turning the board off takes the pair's links with the player.
            mixer.SetSoundboard(false, _ => null);
            Assert.Empty(mixer.AuditionMixes);
            AssertLevel(0, heard, "the board turned off");
        }
        finally
        {
            mixer.Dispose();
            pw.TearDown();
            outputs.TearDown();
        }
    }

    /// <summary>
    /// The tools refuse the audition's link: the soundboard still starts
    /// with one player, linked into SFX, the audition is refused until the
    /// sweep links it, and a feed change the soundboard's link cannot follow
    /// still succeeds. A fake <c>pw-link</c> refuses the next port listing
    /// once a given pair is linked, the last pair made before the audition's.
    /// </summary>
    [MonitorPipeWireFact]
    public void AnAuditionLinkTheToolsRefuseIsTriedAgainAndFailsNothingElse()
    {
        const string Output = "soundboard_refused_output";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        var outputs = new PipeWireAdapter();
        var mixer = new Mixer(pw);
        string directory = Path.Combine(_directory, "fake-link");
        Directory.CreateDirectory(directory);
        string? path = Environment.GetEnvironmentVariable("PATH");
        string real = (path ?? "").Split(Path.PathSeparator).Select(p => Path.GetFullPath(Path.Combine(p, "pw-link"))).First(File.Exists);
        string refused = Path.Combine(directory, "refused");
        // Refuse audition connections after the prerequisite pair is linked.
        // A live registry discovers ports without running pw-link listings.
        ExecutableScript.Write(Path.Combine(directory, "pw-link"), $$"""
            #!/bin/sh
            exec python3 - "$@" <<'PY'
            import os, pathlib, sys
            args = sys.argv[1:]
            folder = pathlib.Path({{System.Text.Json.JsonSerializer.Serialize(directory)}})
            armed, refused, after = folder / 'armed', folder / 'refused', folder / 'after'
            if len(args) == 2 and args[0].startswith({{System.Text.Json.JsonSerializer.Serialize(PipeWireAdapter.PlayerNode + ":audition_")}}) and armed.exists():
                refused.touch()
                print('pw-link: refused by the test', file=sys.stderr)
                sys.exit(1)
            if after.exists() and args == after.read_text().split('\n')[:2]:
                armed.touch()
            os.execv({{System.Text.Json.JsonSerializer.Serialize(real)}}, ['pw-link', *args])
            PY
            """);
        void RefuseAfter(string from, string to)
        {
            File.Delete(refused);
            File.Delete(Path.Combine(directory, "armed"));
            File.WriteAllText(Path.Combine(directory, "after"), from + "\n" + to);
        }
        try
        {
            outputs.CreateNullSink(Output, "Refused link test output");
            static Dictionary<string, double> Sends() => new() { ["monitor"] = 1, ["monitor2"] = 1, ["stream"] = 1 };
            mixer.Build(new MixerConfig
            {
                Mixes = [new("monitor", "Monitor A", MixKind.Monitor), new("monitor2", "Monitor B", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic)],
                Channels = [new("system", "System") { Levels = Sends() }, new("sfx", "SFX") { Levels = Sends() }],
            });
            mixer.SetMonitorOutputs([Output]);
            string wav = Path.Combine(_directory, "long.wav");
            WriteConstantWav(wav, .1, seconds: 20);
            mixer.UseSoundboard(new SoundboardLibrary { Enabled = false }, _ => null, Path.Combine(_directory, "clips"));
            mixer.ImportClip(wav, "Long");

            // The player's next connections after SFX feed the audition.
            RefuseAfter(PipeWireAdapter.PlayerNode + ":capture_1", "OpenXLR_ch_sfx:playback_FR");
            try
            {
                Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
                mixer.SetSoundboard(true, _ => null);
            }
            finally { Environment.SetEnvironmentVariable("PATH", path); }
            Assert.True(File.Exists(refused), "the audition's connection was refused");
            SoundboardPlayer player = Player(mixer)!;
            Assert.NotNull(player);
            Assert.Empty(mixer.AuditionMixes);
            Assert.Equal(Mixer.SoundboardAuditionReconnecting, Assert.Throws<InvalidOperationException>(() => mixer.AuditionClip("long")).Message);
            // One player, linked into SFX: a press plays, before any sweep.
            Assert.Single(PlayerNodes());
            Assert.True(SpinWait.SpinUntil(() => mixer.Soundboard().Clips.Single().Loaded, TimeSpan.FromSeconds(10)));
            mixer.PlayClip("long", null);
            AssertLevel(.1, "OpenXLR_stream", "the press after the audition's link was refused");
            mixer.StopClips();
            // The sweep links the audition, with the same player.
            mixer.EnsureFilterRoutes();
            Assert.Equal(["monitor"], mixer.AuditionMixes);
            Assert.Same(player, Player(mixer));
            Assert.Single(PlayerNodes());
            mixer.AuditionClip("long");
            AssertLevel(.1, Output + ".monitor", "the audition once the sweep linked it");
            mixer.StopAudition();

            // A feed change succeeds when the audition cannot follow it, and the sweep catches up.
            RefuseAfter("OpenXLR_mix_monitor2:monitor_FR", Output + ":playback_FR");
            string? feed;
            try
            {
                Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
                feed = mixer.SetMonitorFeed(Output, "monitor2");
            }
            finally { Environment.SetEnvironmentVariable("PATH", path); }
            Assert.Null(feed);
            Assert.True(File.Exists(refused), "the audition's connection was refused");
            Assert.Empty(mixer.AuditionMixes);
            Assert.Equal(Mixer.SoundboardAuditionReconnecting, Assert.Throws<InvalidOperationException>(() => mixer.AuditionClip("long")).Message);
            mixer.EnsureFilterRoutes();
            Assert.Equal(["monitor2"], mixer.AuditionMixes);
            mixer.AuditionClip("long");
            AssertLevel(.1, Output + ".monitor", "the audition through Monitor B after the sweep");
            AssertLevel(0, "OpenXLR_stream", "and never on Stream");
        }
        finally
        {
            mixer.Dispose();
            pw.TearDown();
            outputs.TearDown();
        }

        static string[] PlayerNodes() => [.. ProcessRunner.Run("pw-cli", ["ls", "Node"]).StdoutText.Split('\n')
            .Where(line => line.Contains("node.name = \"" + PipeWireAdapter.PlayerNode + "\"", StringComparison.Ordinal))];
    }

    /// <summary>Whether <c>pw-link -l</c> lists a link from one port to the other.</summary>
    private static bool Linked(string from, string to)
    {
        bool under = false;
        foreach (string line in ProcessRunner.Run("pw-link", ["-l"]).StdoutText.Split('\n'))
        {
            if (!line.StartsWith(' ')) under = line.Trim() == from;
            else if (under && line.Trim() == "|-> " + to) return true;
        }
        return false;
    }
}
