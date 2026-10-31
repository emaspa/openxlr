using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The voice changer on a private PipeWire server, on the fake unit's 1 kHz
/// tone: switched on with the EQ and noise removal off, the microphone stage
/// splices in at the head of XLR 1 without restarting the chain behind it;
/// changing the voice is live, with no new node or helper process; the stage
/// reports the voice heard and its delay, apart from noise removal's; off,
/// the stage stays and the tone comes back at
/// its level; a profile recall switches it live. A helper built before the
/// voice changer runs the stage without it: the state says so and the
/// microphone keeps playing, and the rest of the test is left out.
/// </summary>
public sealed partial class DspAudioIntegrationTests
{
    [DspPipeWireFact]
    public Task TheVoiceChangerSplicesInAndEveryChangeIsLive() => OnTheUnitWithTone(compensate: false, (mixer, pw, runs) =>
    {
        mixer.SetInserts("xlr1", [Fixture("one", 100)]);
        ChainGraph xlr1 = SettledChain("xlr1");
        double flat = Level("xlr1");

        // A voice chosen while it is off is only stored: no stage runs.
        mixer.SetVoiceChanger("xlr1", new(Voice: "robot"));
        Assert.Empty(Chain("xlr1", "OpenXLR_eq_").Nodes);
        Assert.Null(VoiceState(mixer).Active);

        // On, with the EQ and noise removal off: the stage comes in at the
        // head, and the chain behind it keeps its nodes and every link but the
        // one it is fed by.
        mixer.SetVoiceChanger("xlr1", new(Enabled: true));
        ChainGraph stage = SettledChain("xlr1", "OpenXLR_eq_");
        Assert.Single(stage.Nodes);
        AssertSpliced(xlr1, SettledChain("xlr1"), stage);
        xlr1 = SettledChain("xlr1");
        if (WaitForVoice(mixer, "robot") is Mixer.VoiceNotBuilt)
        {
            // A helper without the voice changer: the microphone plays dry.
            AssertLevel("xlr1", flat);
            return;
        }
        Assert.False(mixer.EqFor("xlr1").Enabled, "The EQ itself stays off.");
        Assert.False(mixer.NoiseFor("xlr1").Enabled, "Noise removal stays off.");

        // Another voice, and the custom shifts, are live.
        int before = Volatile.Read(ref runs.Count);
        mixer.SetVoiceChanger("xlr1", new(Voice: "witch"));
        WaitForVoice(mixer, "witch");
        mixer.SetVoiceChanger("xlr1", new(Voice: "custom", Pitch: -5, Formant: 2));
        WaitForVoice(mixer, "custom");

        // Each part of the stage reports its own delay: the shifter's block,
        // and noise removal's beside it, never the two added together.
        Assert.True(SpinWait.SpinUntil(() => { mixer.EnsureFilterRoutes(); return VoiceState(mixer).LatencyMs == 33.3; },
            TimeSpan.FromSeconds(10)), $"the voice changer reports {VoiceState(mixer).LatencyMs} ms");
        mixer.SetNoiseRemoval("xlr1", new(Enabled: true, Strength: 0));
        WaitForNoise(mixer, "rnnoise", 30);
        Assert.Equal(33.3, VoiceState(mixer).LatencyMs);
        mixer.SetNoiseRemoval("xlr1", new(Enabled: false));
        WaitForNoise(mixer, null, null);
        Assert.Equal(before, Volatile.Read(ref runs.Count));
        Assert.Equal(stage, SettledChain("xlr1", "OpenXLR_eq_"));
        Assert.Equal(xlr1, SettledChain("xlr1"));

        // Off: dry inside the stage, which stays until the route is next built.
        mixer.SetVoiceChanger("xlr1", new(Enabled: false));
        WaitForVoice(mixer, null);
        Assert.Null(VoiceState(mixer).LatencyMs);
        AssertLevel("xlr1", flat);
        Assert.Equal(stage, SettledChain("xlr1", "OpenXLR_eq_"));

        // A recall that names the voice changer switches it live; one without
        // leaves it alone; the saved settings carry it.
        MixerScene scene = mixer.ExportScene();
        mixer.ApplyScene(scene with { VoiceChanger = new() { ["xlr1"] = new MicVoice { Enabled = true, Voice = "radio" } } });
        WaitForVoice(mixer, "radio");
        mixer.ApplyScene(scene with { VoiceChanger = null });
        Assert.True(mixer.VoiceFor("xlr1").Enabled);
        Assert.Equal("radio", mixer.ExportSettings().VoiceChanger["xlr1"].Voice);
        Assert.Equal(stage, SettledChain("xlr1", "OpenXLR_eq_"));
        Assert.Equal(xlr1, SettledChain("xlr1"));
        mixer.SetVoiceChanger("xlr1", new(Enabled: false));
        WaitForVoice(mixer, null);
        AssertLevel("xlr1", flat);
    });

    private static MicVoiceState VoiceState(Mixer mixer, string channel = "xlr1")
        => mixer.Snapshot().Channels.Single(c => c.Id == channel).Voice!;

    /// <summary>
    /// Sweep until the stage reports <paramref name="active"/> with no error,
    /// or says its helper has no voice changer, which is returned.
    /// </summary>
    private static string? WaitForVoice(Mixer mixer, string? active)
    {
        MicVoiceState? last = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureFilterRoutes();
            last = VoiceState(mixer);
            return last.Error == Mixer.VoiceNotBuilt || last.Active == active && last.Error is null;
        }, TimeSpan.FromSeconds(10)), $"the voice changer reports {last?.Active ?? "none"} ({last?.Error}), expected {active ?? "none"}");
        return last!.Error;
    }
}
