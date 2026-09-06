using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class LiveLayoutEditingTests
{
    private static string[] AppIds(MixerConfig c) => [.. c.Channels.Where(ch => ch.InputPair is null).Select(ch => ch.Id)];

    [Fact]
    public void NewMixSitsBeforeAuxWithMutedSendsOnEveryChannel()
    {
        var config = MixerConfig.Default().WithMix(new MixDefinition("podcast", "Podcast", MixKind.VirtualMic));
        Assert.Equal(["monitor", "monitor2", "stream", "chat", "podcast", "auxout"], config.Mixes.Select(m => m.Id));
        foreach (ChannelDefinition ch in config.Channels)
        {
            Assert.Equal(1.0, ch.Levels["podcast"]);
            Assert.Contains("podcast", ch.MutedIn);
        }
        Assert.Throws<InvalidOperationException>(() => config.WithMix(new MixDefinition("Podcast", "Again", MixKind.VirtualMic)));
        Assert.Throws<InvalidOperationException>(() => config.WithMix(new MixDefinition("m3", "Monitor C", MixKind.Monitor)));
    }

    [Fact]
    public void MixLimitHoldsForCreation()
    {
        var config = MixerConfig.Default();
        for (int i = config.Mixes.Count(m => m.Kind == MixKind.VirtualMic); i < MixerConfig.MaxVirtualMixes; i++)
            config = config.WithMix(new MixDefinition($"m{i}", $"Mix {i}", MixKind.VirtualMic));
        Assert.Throws<InvalidOperationException>(() => config.WithMix(new MixDefinition("one-more", "One more", MixKind.VirtualMic)));
    }

    [Fact]
    public void RemovingAMixDropsItsSendsAndKeepsStructuralMixes()
    {
        var config = MixerConfig.Default().WithoutMix("chat");
        Assert.Equal(["monitor", "monitor2", "stream", "auxout"], config.Mixes.Select(m => m.Id));
        Assert.All(config.Channels, ch => { Assert.False(ch.Levels.ContainsKey("chat")); Assert.DoesNotContain("chat", ch.MutedIn); });
        foreach (string structural in new[] { "monitor", "monitor2", "auxout", "nope" })
            Assert.Throws<InvalidOperationException>(() => MixerConfig.Default().WithoutMix(structural));
    }

    [Fact]
    public void RemovingAChannelKeepsHardwareAndTheLastApplicationChannel()
    {
        var config = MixerConfig.Default().WithoutChannel("game");
        Assert.DoesNotContain("game", config.Channels.Select(c => c.Id));
        Assert.Equal(["xlr1", "xlr2", "aux"], config.Channels.Where(c => c.InputPair is not null).Select(c => c.Id));
        foreach (string id in new[] { "xlr1", "aux", "nope", StreamMatcher.Ignore })
            Assert.Throws<InvalidOperationException>(() => MixerConfig.Default().WithoutChannel(id));

        var single = MixerConfig.FromSettings(new MixerSettings { UserChannels = [new("only", "Only")] });
        Assert.Throws<InvalidOperationException>(() => single.WithoutChannel("only"));
    }

    [Fact]
    public void RenamesChangeOnlyTheDisplayName()
    {
        var original = MixerConfig.Default();
        var renamed = original.WithChannelName("music", "Spotify").WithMixName("stream", "OBS");
        Assert.Equal("Spotify", renamed.Channels.Single(c => c.Id == "music").Name);
        Assert.Equal("OBS", renamed.Mixes.Single(m => m.Id == "stream").Name);
        Assert.Equal(AppIds(original), AppIds(renamed));
        Assert.Equal(original.Channels.Single(c => c.Id == "music").Levels, renamed.Channels.Single(c => c.Id == "music").Levels);
        Assert.Throws<InvalidOperationException>(() => original.WithChannelName("xlr1", "Mic"));
        Assert.Throws<InvalidOperationException>(() => original.WithMixName("monitor", "Ears"));
    }

    [Theory]
    [InlineData("Podcast", "podcast")]
    [InlineData("Late Night Show", "late-night-show")]
    [InlineData("2nd stream", "mix-2nd-stream")]
    [InlineData("!!!", "mix")]
    [InlineData("monitor", "monitor-2")]
    [InlineData("a-very-long-name-that-goes-on-and-on-forever", "a-very-long-name-that-goes-o")]
    public void MixIdsAreSafeAndUnique(string name, string expected)
        => Assert.Equal(expected, MixerConfig.NewId(name, "mix", MixerConfig.Default().Mixes.Select(m => m.Id)));

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1" or "game" or "music";
        public bool HasMix(string id) => id is "monitor" or "stream" or "auxout";
        public bool HasApplicationChannel(string id) => id is "game" or "music";
        public bool HasVirtualMix(string id) => id == "stream";
        public bool IsMonitorFeed(string feed) => feed == "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => false;
        public int OverrideCount => 0;
    }

    private static string? Check(Command cmd) => CommandValidation.Check(cmd, new Layout(), _ => null);

    [Fact]
    public void EditCommandsOnlyTouchEditableNodes()
    {
        Assert.Null(Check(new Command { Cmd = "renameChannel", Channel = "game", Name = "Games" }));
        Assert.NotNull(Check(new Command { Cmd = "renameChannel", Channel = "xlr1", Name = "Mic" }));
        Assert.NotNull(Check(new Command { Cmd = "renameChannel", Channel = "game", Name = " " }));
        Assert.NotNull(Check(new Command { Cmd = "renameChannel", Name = "Games" }));
        Assert.Null(Check(new Command { Cmd = "deleteChannel", Channel = "music" }));
        Assert.NotNull(Check(new Command { Cmd = "deleteChannel", Channel = "nope" }));
        Assert.Null(Check(new Command { Cmd = "renameMix", Mix = "stream", Name = "OBS" }));
        Assert.NotNull(Check(new Command { Cmd = "renameMix", Mix = "monitor", Name = "Ears" }));
        Assert.Null(Check(new Command { Cmd = "deleteMix", Mix = "stream" }));
        Assert.NotNull(Check(new Command { Cmd = "deleteMix", Mix = "auxout" }));
        Assert.NotNull(Check(new Command { Cmd = "deleteMix" }));
        Assert.Null(Check(new Command { Cmd = "createMix", Name = "Podcast" }));
        Assert.NotNull(Check(new Command { Cmd = "createMix", Name = new string('x', 61) }));
        Assert.NotNull(Check(new Command { Cmd = "createMix", Name = "bad\nname" }));
    }

    [Fact]
    public async Task RequestIdRoundTripsAndIsAnsweredAfterTheState()
    {
        var command = JsonSerializer.Deserialize<Command>("""{"cmd":"createMix","name":"Podcast","requestId":"r1"}""")!;
        Assert.Equal("r1", command.RequestId);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // No hosted services: no USB, no audio graph. The hub still dispatches.
        builder.Services.AddSingleton<DeviceManager>();
        builder.Services.AddSingleton<MixerService>();
        builder.Services.AddSingleton<WebSocketHub>();
        await using var app = builder.Build();
        var hub = app.Services.GetRequiredService<WebSocketHub>();

        // The mixer is not built in a test, so the command fails; the failure
        // still arrives as a correlated result, after an authoritative state.
        ApiCommandResult result = await hub.ExecuteForApiAsync("""{"cmd":"createMix","name":"Podcast","requestId":"r1"}""");
        Assert.False(result.Ok);
        Assert.Equal(2, result.Messages.Count);
        Assert.IsType<StateMessage>(result.Messages[0]);
        var done = Assert.IsType<CommandResultMessage>(result.Messages[1]);
        Assert.Equal("r1", done.RequestId);
        Assert.Contains("not built", done.Error);

        // Without a requestId the plain error message is what a client gets.
        ApiCommandResult plain = await hub.ExecuteForApiAsync("""{"cmd":"createMix","name":"Podcast"}""");
        Assert.False(plain.Ok);
        Assert.Contains(plain.Messages, m => m is ErrorMessage);

        // The contract holds for every command, not only the mixer's, and for a read.
        ApiCommandResult device = await hub.ExecuteForApiAsync("""{"cmd":"set","requestId":"r2"}""");
        var deviceResult = Assert.IsType<CommandResultMessage>(device.Messages[^1]);
        Assert.Equal("r2", deviceResult.RequestId);
        Assert.Contains("missing 'control'", deviceResult.Error);
        ApiCommandResult unknown = await hub.ExecuteForApiAsync("""{"cmd":"nothing","requestId":"r3"}""");
        Assert.Contains("unknown cmd", Assert.IsType<CommandResultMessage>(unknown.Messages[^1]).Error);
        ApiCommandResult read = await hub.ExecuteForApiAsync("""{"cmd":"getState","requestId":"r4"}""");
        Assert.True(read.Ok);
        Assert.Null(Assert.IsType<CommandResultMessage>(read.Messages[^1]).Error);
        ApiCommandResult tooLong = await hub.ExecuteForApiAsync("{\"cmd\":\"getState\",\"requestId\":\"" + new string('x', 65) + "\"}");
        Assert.Contains(tooLong.Messages, m => m is ErrorMessage);
    }
}
