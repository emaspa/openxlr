using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class LayoutOrderTests
{
    [Fact]
    public void ReorderPreservesDefinitionsIdsAndStructuralNodes()
    {
        var original = MixerConfig.Default();
        var apps = original.Channels.Where(c => c.InputPair is null).Reverse().ToArray();
        var ordered = original.WithOrder(apps.Select(c => c.Id).ToArray(), ["chat", "stream"]);
        Assert.Equal(["xlr1", "xlr2", "aux", .. apps.Select(c => c.Id)], ordered.Channels.Select(c => c.Id));
        Assert.Equal(["monitor", "monitor2", "chat", "stream", "auxout"], ordered.Mixes.Select(m => m.Id));
        foreach (var channel in original.Channels) Assert.Same(channel, ordered.Channels.Single(c => c.Id == channel.Id));
        foreach (var mix in original.Mixes) Assert.Same(mix, ordered.Mixes.Single(m => m.Id == mix.Id));
        Assert.Equal("game", original.Channels[3].Id); // The original snapshot was not mutated.
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("hardware")]
    [InlineData("null")]
    public void InvalidChannelPermutationIsRejected(string kind)
    {
        var config = MixerConfig.Default();
        var ids = config.Channels.Where(c => c.InputPair is null).Select(c => c.Id).ToList();
        if (kind == "missing") ids.RemoveAt(0);
        else ids[0] = kind switch { "duplicate" => ids[1], "hardware" => "xlr1", "null" => null!, _ => "unknown" };
        Assert.Throws<InvalidOperationException>(() => config.WithOrder(ids, ["stream", "chat"]));
    }

    [Theory]
    [InlineData("monitor")]
    [InlineData("monitor2")]
    [InlineData("auxout")]
    [InlineData("chat")]
    public void StructuralAndDuplicateMixIdsAreRejected(string replacement)
    {
        var config = MixerConfig.Default();
        Assert.Throws<InvalidOperationException>(() => config.WithOrder(
            config.Channels.Where(c => c.InputPair is null).Select(c => c.Id).ToArray(), [replacement, "chat"]));
    }

    [Fact]
    public void EmptyVirtualMixLayoutCanBeReordered()
    {
        var config = MixerConfig.FromSettings(new MixerSettings { UserMixes = [], UserChannels = [new("only", "Only")] });
        Assert.Equal(config.Mixes, config.WithOrder(["only"], []).Mixes);
    }

    [Fact]
    public void CommandListsAreDeserializedAndValidated()
    {
        var command = JsonSerializer.Deserialize<Command>("""{"cmd":"setLayoutOrder","channels":["system"],"mixes":[]} """)!;
        using var mixer = new Mixer();
        Assert.Equal("system", Assert.Single(command.Channels!));
        Assert.Empty(command.Mixes!);
        Assert.Null(CommandValidation.Check(command, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Mixes = null }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Channels = Enumerable.Repeat("x", 33).ToList() }, mixer, _ => null));
    }
}
