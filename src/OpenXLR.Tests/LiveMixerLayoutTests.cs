using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using System.Text.Json;

namespace OpenXLR.Tests;

public sealed class LiveMixerLayoutTests
{
    [Theory]
    [InlineData("Podcast", "podcast")]
    [InlineData("123", "channel-123")]
    [InlineData("ignore", "ignore-2")]
    [InlineData("🎙", "channel")]
    [InlineData("Studio \"A\"", "studio-a")]
    public void StableIdsAreSafeAndNeverUseTheIgnoreTarget(string name, string id)
        => Assert.Equal(id, Mixer.NewChannelId(name, []));

    [Fact]
    public void StableIdsAvoidExistingHardwareAndApplicationIds()
        => Assert.Equal("xlr1-3", Mixer.NewChannelId("XLR1", ["xlr1", "xlr1-2"]));

    [Fact]
    public void VirtualOutputIdsAvoidStructuralMixIds()
        => Assert.Equal("monitor-2", Mixer.NewLayoutId("Monitor", "output", ["monitor", "monitor2", "auxout"]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    public void InvalidNamesAreRejectedBeforeGraphWork(string? name)
    {
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new Command { Cmd = "createChannel", Name = name }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new Command { Cmd = "createMix", Name = name }, mixer, _ => null));
    }

    [Fact]
    public void LongNamesAreRejectedAndOrdinaryNamesAccepted()
    {
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new Command { Cmd = "createChannel", Name = new string('a', 61) }, mixer, _ => null));
        Assert.Null(CommandValidation.Check(new Command { Cmd = "createChannel", Name = "Studio A" }, mixer, _ => null));
        Assert.Null(CommandValidation.Check(new Command { Cmd = "createMix", Name = "Podcast" }, mixer, _ => null));
    }

    [Fact]
    public void RenameAndDeleteCommandsValidateKnownTargets()
    {
        using var mixer = new Mixer();
        Assert.Null(CommandValidation.Check(new Command
            { Cmd = "renameChannel", Channel = "system", Name = "Desktop" }, mixer, _ => null));
        Assert.Null(CommandValidation.Check(new Command
            { Cmd = "deleteChannel", Channel = "system" }, mixer, _ => null));
        Assert.Null(CommandValidation.Check(new Command
            { Cmd = "renameMix", Mix = "stream", Name = "Broadcast" }, mixer, _ => null));
        Assert.Null(CommandValidation.Check(new Command
            { Cmd = "deleteMix", Mix = "stream" }, mixer, _ => null));

        Assert.Contains("unknown channel", CommandValidation.Check(new Command
            { Cmd = "deleteChannel", Channel = "missing" }, mixer, _ => null));
        Assert.Contains("unknown mix", CommandValidation.Check(new Command
            { Cmd = "deleteMix", Mix = "missing" }, mixer, _ => null));
    }

    [Fact]
    public void CorrelationIdIsBoundedAndRoundTripsThroughProtocol()
    {
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new Command
            { Cmd = "createChannel", Name = "Podcast", RequestId = new string('x', CommandValidation.MaxRequestId + 1) },
            mixer, _ => null));

        Command? parsed = JsonSerializer.Deserialize<Command>("{\"cmd\":\"createMix\",\"name\":\"Podcast\",\"requestId\":\"abc123\"}");
        Assert.NotNull(parsed);
        Assert.Equal("abc123", parsed.RequestId);
    }
}
