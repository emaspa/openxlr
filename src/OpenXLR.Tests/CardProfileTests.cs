using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class CardProfileTests : IDisposable
{
    private const string Fragment = "Elgato_Wave_XLR_Pro";
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-card-profile-").FullName;
    private readonly string? _path = Environment.GetEnvironmentVariable("PATH");

    public CardProfileTests()
    {
        ExecutableScript.Write(Path.Combine(_directory, "pw-dump"), """
            cat "$(dirname "$0")/dump.json"
            """);
        ExecutableScript.Write(Path.Combine(_directory, "wpctl"), """
            printf '%s\n' "$*" >> "$(dirname "$0")/writes"
            """);
        Environment.SetEnvironmentVariable("PATH", _directory + Path.PathSeparator + _path);
    }

    [Fact]
    public void ParkingAndRestoringUseTheLatestCardAndProfileIndices()
    {
        Dump($"[{Card(7, "HiFi", 4)}]\n[{Card(7, "HiFi", 9)}]");
        Assert.Equal(("HiFi", "HiFi"), CardProfile.EnsureProAudio(Fragment));
        Assert.Equal(["set-profile 7 9"], Writes());

        Dump($$"""
            [{{Card(7, "pro-audio", 9)}}]
            [{"id":7,"info":null},{{Card(99, "pro-audio", 12, 6)}}]
            """);
        CardProfile.SetProfile(Fragment, "HiFi");
        Assert.Equal(["set-profile 7 9", "set-profile 99 6"], Writes());
    }

    [Theory]
    [InlineData("\"info\":null")]
    [InlineData("\"props\":null")]
    public void ARemovedCardIsNeverSelected(string tombstone)
    {
        Dump($$"""[{{Card(7, "HiFi")}}] [{"id":7,{{tombstone}}}]""");
        Assert.Equal((null, null), CardProfile.EnsureProAudio(Fragment));
        CardProfile.SetProfile(Fragment, "HiFi");
        Assert.Empty(Writes());
    }

    [Fact]
    public void ADeviceWithoutInfoDoesNotHideThePresentCard()
    {
        Dump($$"""[{"id":8,"type":"PipeWire:Interface:Device","info":null},{{Card(9, "Direct")}}]""");
        Assert.Equal(("Direct", "Direct"), CardProfile.EnsureProAudio(Fragment));
        Assert.Equal(["set-profile 9 3"], Writes());
    }

    [Theory]
    [InlineData("pro-audio")]
    [InlineData("off")]
    public void NonUcmProfilesStayUntouched(string active)
    {
        Dump($"[{Card(7, active)}]");
        Assert.Equal((active, null), CardProfile.EnsureProAudio(Fragment));
        Assert.Empty(Writes());
    }

    [Fact]
    public void IncompleteUpdatesCannotWriteAStaleProfile()
    {
        Dump($"[{Card(7, "HiFi")}] [");
        Assert.ThrowsAny<JsonException>(() => CardProfile.EnsureProAudio(Fragment));
        Assert.Empty(Writes());
    }

    [Fact]
    public void TwoCardsUnderOneFragmentAreNeitherParkedNorRestored()
    {
        Dump($"[{Card(7, "HiFi")},{Card(9, "HiFi").Replace(Fragment, Fragment.ToLowerInvariant())}]");
        Assert.Equal((null, null), CardProfile.EnsureProAudio(Fragment));
        CardProfile.SetProfile(Fragment, "HiFi");
        Assert.Empty(Writes());
        Assert.Equal((null, null), CardProfile.EnsureProAudio(""));
    }

    [Fact]
    public void TheSerialSeparatorSelectsOnlyTheExactCard()
    {
        Dump($"[{Card(7, "HiFi").Replace(Fragment, Fragment + "_unitA")},{Card(9, "HiFi").Replace(Fragment, Fragment + "_unitAB")}]");
        Assert.Equal(("HiFi", "HiFi"), CardProfile.EnsureProAudio([Fragment + "_unitA-", Fragment], out string? matched));
        Assert.Equal(Fragment + "_unitA-", matched);
        Assert.Equal(["set-profile 7 3"], Writes());
    }

    [Fact]
    public void ASerialSpelledDifferentlyByUdevFindsTheLoneCardByItsModel()
    {
        Dump($"[{Card(7, "HiFi").Replace(Fragment, Fragment + "_unit_A")}]");
        Assert.Equal(("HiFi", "HiFi"), CardProfile.EnsureProAudio([Fragment + "_unitA-", Fragment], out string? matched));
        Assert.Equal(Fragment, matched);
        Assert.Equal(["set-profile 7 3"], Writes());
    }

    [Fact]
    public void ASerialSpelledDifferentlyWithTwoCardsOfTheModelTouchesNeither()
    {
        Dump($"[{Card(7, "HiFi").Replace(Fragment, Fragment + "_unit_A")},{Card(9, "HiFi").Replace(Fragment, Fragment + "_unit_B")}]");
        Assert.Equal((null, null), CardProfile.EnsureProAudio([Fragment + "_unitA-", Fragment], out string? matched));
        Assert.Null(matched);
        Assert.Empty(Writes());
    }

    [Fact]
    public void HardwareOutputsAreOnlyAliasedOnTheOneMatchingCard()
    {
        Dump($$$"""
            [{"id":7,"type":"PipeWire:Interface:Node","info":{"props":{
              "node.name":"alsa_output.usb-{{{Fragment}}}_unitA-00.pro-output-0","media.class":"Audio/Sink","device.api":"alsa"} } },
             {"id":9,"type":"PipeWire:Interface:Node","info":{"props":{
              "node.name":"alsa_output.usb-{{{Fragment}}}_unitAB-00.pro-output-0","media.class":"Audio/Sink","device.api":"alsa"} } }]
            """);
        var adapter = new PipeWireAdapter();
        var ambiguous = adapter.ListDevices(exposeHardwareMonitorOutputs: true, hardwareSinkHint: Fragment);
        Assert.Equal(2, ambiguous.Count);
        Assert.DoesNotContain(ambiguous, node => node.Name.Contains('#'));
        var exact = adapter.ListDevices(true, [Fragment + "_unitA-", Fragment]);
        Assert.Equal(4, exact.Count);
        Assert.Contains(exact, node => node.Name.EndsWith("_unitA-00.pro-output-0#hp1", StringComparison.Ordinal));
        Assert.Contains(exact, node => node.Name.EndsWith("_unitAB-00.pro-output-0", StringComparison.Ordinal));
        Assert.DoesNotContain(exact, node => node.Name.Contains("unitAB-00.pro-output-0#", StringComparison.Ordinal));
        Assert.Equal(2, adapter.ListDevices(true, [Fragment + "_absent-"]).Count);
    }

    private static string Card(int id, string active, int pro = 3, int hifi = 2) => $$"""
        {"id":{{id}},"type":"PipeWire:Interface:Device","info":{
          "props":{"device.name":"alsa_card.usb-Elgato_Wave_XLR_Pro-00"},
          "params":{"EnumProfile":[{"name":"pro-audio","index":{{pro}}},{"name":"HiFi","index":{{hifi}}}],
                    "Profile":[{"name":{{JsonSerializer.Serialize(active)}}}] } } }
        """;

    private void Dump(string value) => File.WriteAllText(Path.Combine(_directory, "dump.json"), value);
    private string[] Writes() => File.Exists(Path.Combine(_directory, "writes"))
        ? File.ReadAllLines(Path.Combine(_directory, "writes")) : [];

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _path);
        Directory.Delete(_directory, true);
    }
}
