using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's direct microphone path plays the capsule unchanged, so
/// the mixer keeps it off while software changes XLR 1's audio and lets the
/// Monitor send carry the processed voice instead.
/// </summary>
public sealed class ProDirectPathProcessingTests
{
    public static TheoryData<string> Processing => ["voice", "noise", "eq", "lowcut"];

    [Theory]
    [MemberData(nameof(Processing))]
    public void SoftwareProcessingOnXlr1TakesTheDirectPathOffUntilItStops(string kind)
    {
        using var mixer = new Mixer();
        Assert.True(mixer.SetHardwareMicMonitor(true));

        Set(mixer, kind, on: true);
        Assert.False(mixer.SetHardwareMicMonitor(true));

        Set(mixer, kind, on: false);
        Assert.True(mixer.SetHardwareMicMonitor(true));
    }

    [Fact]
    public void ProcessingOnXlr2LeavesXlr1OnTheDirectPath()
    {
        using var mixer = new Mixer();
        mixer.SetVoiceChanger("xlr2", new(Enabled: true));
        mixer.SetNoiseRemoval("xlr2", new(Enabled: true));
        Assert.True(mixer.SetHardwareMicMonitor(true));
    }

    [Fact]
    public void InsertsThatDoNotRunKeepTheDirectPath()
    {
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1",
        [
            new() { Id = "bypassed", Kind = "lv2", Plugin = "urn:openxlr:test:gain", Bypass = true },
            new() { Id = "unknown", Kind = "lv2", Plugin = "urn:openxlr:test:not-installed" },
        ]);
        Assert.True(mixer.SetHardwareMicMonitor(true));
    }

    [Fact]
    public void Xlr2TakesItsOwnPathOnlyOnAUnitWithTwoInputs()
    {
        using var mixer = new Mixer();
        Assert.False(mixer.SetHardwareMicMonitor(true, input: 2));
        mixer.SetInputJacks(2, auxInput: true);
        Assert.True(mixer.SetHardwareMicMonitor(true, input: 2));
        // Each path follows its own channel's send and processing.
        mixer.SetVoiceChanger("xlr2", new(Enabled: true));
        Assert.False(mixer.SetHardwareMicMonitor(true, input: 2));
        Assert.True(mixer.SetHardwareMicMonitor(true));
        mixer.SetVoiceChanger("xlr2", new(Enabled: false));
        Assert.True(mixer.SetHardwareMicMonitor(true, input: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.SetHardwareMicMonitor(true, input: 3));
    }

    private static void Set(Mixer mixer, string kind, bool on)
    {
        switch (kind)
        {
            case "voice": mixer.SetVoiceChanger("xlr1", new(Enabled: on)); break;
            case "noise": mixer.SetNoiseRemoval("xlr1", new(Enabled: on)); break;
            case "eq": mixer.SetEqEnabled("xlr1", on); break;
            default: mixer.SetLowCutHz(on ? 80 : 0); break;
        }
    }
}
