using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class CaptureInputTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData(" ", 0)]
    [InlineData("mic\nother", 0)]
    [InlineData("OpenXLR_stream", 0)]
    [InlineData("sink.monitor", 0)]
    [InlineData("mic", -1)]
    [InlineData("mic", 32)]
    public void InvalidBindingsCannotBecomeCommandsOrApplicationChannels(string? source, int pair)
    {
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new Command
            { Cmd = "createCaptureChannel", Name = "Mic", Source = source, CapturePair = pair }, mixer, _ => null));
        if (source is null) return; // null is the legacy application-channel format
        var config = MixerConfig.FromSettings(new MixerSettings { UserChannels = [new("bad", "Bad", source, pair)] });
        Assert.DoesNotContain(config.Channels, c => c.Id == "bad");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    public void InvalidMonoPortsCannotBecomeCaptureBindings(int port)
    {
        Assert.False(CaptureBinding.IsValid("mic", 0, port));
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new Command { Cmd = "createCaptureChannel", Name = "Mic", Source = "mic", CaptureMonoChannel = port }, mixer, _ => null));
        Assert.DoesNotContain(MixerConfig.FromSettings(new() { UserChannels = [new("bad", "Bad", "mic", 0, port)] }).Channels, c => c.Id == "bad");
    }

    [Fact]
    public void AMonoPortNeedsACaptureSourceAndTheFirstPair()
    {
        Assert.True(CaptureBinding.IsValid("mic", 0, 63));
        Assert.False(CaptureBinding.IsValid("mic", 1, 2));
        var config = MixerConfig.FromSettings(new() { UserChannels = [new("bad", "Bad", null, 0, 1), new("mono", "Mono", "mic", 0, 2)] });
        Assert.DoesNotContain(config.Channels, c => c.Id == "bad");
        Assert.Equal(2, config.Channels.Single(c => c.Id == "mono").CaptureMonoChannel);
    }

    [Fact]
    public void CaptureLayoutKeepsStableIdentityAndAlwaysLeavesAnApplicationDestination()
    {
        var config = MixerConfig.FromSettings(new MixerSettings { UserChannels = [new("system", "Second mic", "mic.one", 1)] });
        ChannelDefinition capture = config.Channels.Single(c => c.Id == "system");
        Assert.False(capture.IsApplication);
        Assert.Equal("mic.one", capture.CaptureSource);
        Assert.Equal(1, capture.CapturePair);
        Assert.Equal(config.Mixes.Count, capture.MutedIn.Count);
        ChannelDefinition app = Assert.Single(config.Channels, c => c.IsApplication);
        Assert.Equal("system-2", app.Id);
        Assert.Equal(app.Id, config.ResolveApplicationChannel(capture.Id));
        Assert.Throws<InvalidOperationException>(() => config.WithoutChannel(app.Id));
        Assert.DoesNotContain(config.WithoutChannel(capture.Id).Channels, c => c.Id == capture.Id);
        Assert.Equal("mic.one", config.WithChannelName(capture.Id, "Renamed").Channels.Single(c => c.Id == capture.Id).CaptureSource);
        var capped = MixerConfig.FromSettings(new MixerSettings { UserChannels = Enumerable.Range(0, 40)
            .Select(i => new UserChannelDefinition($"mic{i}", "Mic", "mic.one")).ToList() });
        Assert.Equal(32, capped.Channels.Count(c => c.InputPair is null));
        Assert.Single(capped.Channels, c => c.IsApplication);
    }

    [Fact]
    public void InvalidDuplicateDoesNotSupplyTheAcceptedEntriesBinding()
    {
        var config = MixerConfig.FromSettings(new MixerSettings { UserChannels =
            [new("mic", "bad\nname", "wrong.source"), new("mic", "Valid", "right.source"), new("app", "Apps")] });
        Assert.Equal("right.source", config.Channels.Single(c => c.Id == "mic").CaptureSource);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(40)]
    public void TruncatedCaptureCannotReplaceTheSynthesizedApplicationChannel(int index)
    {
        var saved = Enumerable.Range(0, index)
            .Select(i => new UserChannelDefinition($"mic{i}", "Microphone", "capture.source")).ToList();
        saved.Add(new("system", "System", "capture.source"));
        var config = MixerConfig.FromSettings(new MixerSettings { UserChannels = saved });
        ChannelDefinition app = Assert.Single(config.Channels, c => c.IsApplication);
        Assert.Equal("system", app.Id);
        Assert.Null(app.CaptureSource);
        Assert.Equal(32, config.Channels.Count(c => c.InputPair is null));
        Assert.Equal(app.Id, config.ResolveApplicationChannel("missing"));
    }

    [Fact]
    public void BindingNamesAndPairLimitsAreBounded()
    {
        Assert.True(CaptureBinding.IsValid("alsa_input.usb-Wave_XLR_second", 31));
        Assert.False(CaptureBinding.IsValid(new string('a', 257), 0));
        using var mixer = new Mixer();
        Assert.Null(CommandValidation.Check(new Command { Cmd = "createCaptureChannel", Name = "Second Wave",
            Source = "alsa_input.usb-Wave_XLR_second", CapturePair = 0 }, mixer, _ => null));
    }
}
