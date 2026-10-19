using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class PrimaryInputTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData(" ", 0)]
    [InlineData("OpenXLR_chat", 0)]
    [InlineData("speakers.monitor", 0)]
    [InlineData("mic\nother", 0)]
    [InlineData("mic", -1)]
    [InlineData("mic", 64)]
    public void InvalidPrimaryBindingsAreRejectedByBothCommandAndMixer(string source, int channel)
    {
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new Command
            { Cmd = "setPrimaryInput", Source = source, CaptureMonoChannel = channel }, mixer, _ => null));
        Assert.Throws<ArgumentException>(() => mixer.SetPrimaryInput(new(source, channel)));
        Assert.Null(mixer.PrimaryInput);
    }

    [Fact]
    public void SelectionUsesTheExistingCaptureBoundsAndNullRestoresAutomaticMode()
    {
        using var mixer = new Mixer();
        Assert.Null(CommandValidation.Check(new() { Cmd = "setPrimaryInput" }, mixer, _ => null));
        Assert.Null(CommandValidation.Check(new() { Cmd = "setPrimaryInput", Source = "mic", CaptureMonoChannel = 63 }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setPrimaryInput", Source = "mic", CapturePair = 1 }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setPrimaryInput", CaptureMonoChannel = 1 }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setPrimaryInput", Source = new string('a', 257) }, mixer, _ => null));
        mixer.SetPrimaryInput(new("mic", 63));
        Assert.Equal(new("mic", 63), mixer.ExportSettings().PrimaryInput);
        Assert.False(mixer.SetHardwareMicMonitor(true));
        mixer.SetPrimaryInput(null);
        Assert.Null(mixer.ExportSettings().PrimaryInput);
    }

    [Fact]
    public void SavedBindingRoundTripsAndMalformedEntriesProduceAWarning()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-primary-settings-").FullName;
        string path = Path.Combine(dir, "mixer.json");
        try
        {
            Assert.Null(new MixerSettings { PrimaryInput = new("mic", 3) }.Save(path));
            Assert.Equal(new("mic", 3), MixerSettings.Load(path)!.PrimaryInput);
            File.WriteAllText(path, """{"primaryInput":{"source":"OpenXLR_chat","channel":0}}""");
            Assert.Null(MixerSettings.Load(path, out string? warning)!.PrimaryInput);
            Assert.Contains("primaryInput", warning);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task GenericStateHidesHardwareControlsAndKeepsSoftwareProcessing()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        void Apply(string state) => typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [JsonNode.Parse(state)!]);
        Apply("""{"connected":true,"capabilities":{"xlrInputs":2,"hpOutputs":2,"lowCut":true,"phantom":true,"clipGuard":true,"auxInput":true}}""");
        Assert.True(model.CapPhantom);
        Apply("""{"connected":false,"devices":[],"mixer":{"primaryInput":{"source":"test.mic","channel":1},"primaryInputConnected":true}}""");
        Assert.True(model.GenericMicrophone);
        Assert.False(model.CapPhantom);
        Assert.False(model.CapClipGuard);
        Assert.False(model.HasXlr2);
        Assert.False(model.HasHp2);
        Assert.False(model.CapAuxInput);
        Assert.True(model.ShowSoftLowCut);
        Assert.True(model.ShowSoftClipGuard);
        Assert.False(model.ShowGainLock);
        Assert.False(model.ShowResetDefaults);
        Assert.Contains("test.mic", model.StatusLine);
        model.Channels.Add(new(client, "xlr1", "Microphone", []) { IsHardware = true });
        using var rack = new InputRackViewModel(model);
        using var matrix = new MixMatrixViewModel(model);
        Assert.DoesNotContain(SkinStripParts.Gain, rack.Strips.Single().Spec.Parts);
        Assert.DoesNotContain(SkinStripParts.Gain, matrix.Rows.Single().Spec.Parts);
        Assert.DoesNotContain(SkinStripParts.Mute, matrix.Rows.Single().Spec.Parts);
        Assert.Contains(SkinStripParts.Meter, matrix.Rows.Single().Spec.Parts);
        var flow = FlowGraph.From(model);
        Assert.Contains(flow.Nodes, n => n.Key == "capture:xlr1" && n.Label == "test.mic" && n.Active);
        Assert.DoesNotContain(flow.Nodes, n => n.Key == "hw:xlr1");
        Apply("""{"connected":true,"capabilities":{"xlrInputs":2,"hpOutputs":2,"phantom":true,"clipGuard":true},"mixer":{}}""");
        Assert.False(model.GenericMicrophone);
        Assert.True(model.CapPhantom);
        Assert.True(model.HasXlr2);
        Assert.False(model.ShowSoftClipGuard);
        Assert.Contains(SkinStripParts.Gain, rack.Strips.Single().Spec.Parts);
        Assert.Contains(SkinStripParts.Gain, matrix.Rows.Single().Spec.Parts);
        Assert.Contains(SkinStripParts.Mute, matrix.Rows.Single().Spec.Parts);
    }
}
