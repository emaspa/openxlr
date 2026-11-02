using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// What the Wave XLR Pro's sections 18 and 54 held before a jack feed first
/// used them: kept once, saved in the mixer settings and never in a profile,
/// read back whether or not the mixer runs, and checked when loaded.
/// </summary>
public sealed class ProJackFeedSettingsTests
{
    private const string Kept = "0C0000000000000000000000C10000AA";
    private const string Other = "000000000000000000000000000000AA";

    [Fact]
    public void AKeptSectionIsSavedInTheSettingsAndNeverInAProfile()
    {
        using var mixer = new Mixer();
        Assert.Empty(mixer.ExportSettings().HardwareRestore);
        Assert.True(mixer.KeepHardwareRestore(18, Kept.ToLowerInvariant()));
        // The first bytes kept stay: a later copy would be the feed's own.
        Assert.False(mixer.KeepHardwareRestore(18, Other));
        Assert.Equal(new Dictionary<string, string> { ["18"] = Kept }, mixer.ExportSettings().HardwareRestore);
        Assert.DoesNotContain("hardwareRestore", JsonSerializer.Serialize(mixer.ExportScene(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Throws<ArgumentException>(() => mixer.KeepHardwareRestore(36, Kept));
        Assert.Throws<ArgumentException>(() => mixer.KeepHardwareRestore(0, Kept));
        Assert.Throws<ArgumentException>(() => mixer.KeepHardwareRestore(54, "00"));

        using (var other = new Mixer())
        {
            other.ApplySettings(mixer.ExportSettings());
            Assert.Equal(Kept, other.HardwareRestore[18]);
        }

        Assert.True(mixer.ForgetHardwareRestore(18));
        Assert.False(mixer.ForgetHardwareRestore(18));
        Assert.Empty(mixer.ExportSettings().HardwareRestore);
    }

    [Fact]
    public void ALoadedFileKeepsOnlySections18And54WithTheirSixteenBytes()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-hardware-restore-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            File.WriteAllText(path, "{\"hardwareRestore\":{\"18\":\"" + Kept + "\",\"36\":\"" + Kept + "\",\"54\":\"00\",\"72\":\"" + Other + "\"}}");
            MixerSettings settings = MixerSettings.Load(path, out string? warning)!;
            Assert.Contains("hardwareRestore.36", warning);
            Assert.Contains("hardwareRestore.54", warning);
            Assert.Contains("hardwareRestore.72", warning);
            Assert.Equal(new Dictionary<string, string> { ["18"] = Kept }, settings.HardwareRestore);
            Assert.Null(settings.Save(path));
            Assert.Equal(settings.HardwareRestore, MixerSettings.Load(path, out _)!.HardwareRestore);
        }
        finally { Directory.Delete(dir, true); }
    }
}
