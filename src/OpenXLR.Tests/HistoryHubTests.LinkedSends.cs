using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// Linked sends through the hub: linking brings a channel's sends to the
/// first mix's level, a linked fader moves every send as one undo step,
/// the mutes stay per mix, and the link is a step of its own.
/// </summary>
public sealed partial class HistoryHubTests
{
    [Fact]
    public async Task LinkingTakesTheFirstMixsLevelAndUnlinkingMovesNothing()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":0.4}""");
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"stream","value":0.9}""");
        Assert.False(Linked(f, "xlr1"));

        await f.Ok("""{"cmd":"setLinkedSends","channel":"xlr1","value":true}""");
        Assert.True(Linked(f, "xlr1"));
        Assert.False(Linked(f, "xlr2"));
        Assert.Equal((.4, .4), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));
        // The other channel is left alone.
        Assert.Equal((1.0, 1.0), (f.Level("xlr2|monitor"), f.Level("xlr2|stream")));

        await f.Ok("""{"cmd":"setLinkedSends","channel":"xlr1","value":false}""");
        Assert.False(Linked(f, "xlr1"));
        Assert.Equal((.4, .4), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"stream","value":0.7}""");
        Assert.Equal((.4, .7), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));
    }

    [Fact]
    public async Task ALinkedFaderMovesEverySendAsOneStepAndLeavesTheMutes()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setLinkedSends","channel":"xlr1","value":true}""");
        f.Now += 2000;
        await f.Ok("""{"cmd":"setChannelMuted","channel":"xlr1","mix":"monitor","value":false}""");
        f.Now += 2000;

        // Any mix's fader moves both sends.
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"stream","value":0.3}""");
        Assert.Equal((.3, .3), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));
        Assert.Equal((1.0, 1.0), (f.Level("xlr2|monitor"), f.Level("xlr2|stream")));
        // The mutes stay per mix.
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "stream"));
        f.Now += 2000;
        await f.Ok("""{"cmd":"setChannelMuted","channel":"xlr1","mix":"stream","value":false}""");
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "stream"));
        f.Now += 2000;
        await f.Ok("""{"cmd":"setChannelMuted","channel":"xlr1","mix":"monitor","value":true}""");
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "stream"));
        Assert.Equal((.3, .3), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));
        f.Now += 2000;

        // A drag on the other mix joins one step, which holds both sends.
        foreach (double value in new[] { .5, .6 })
        {
            await f.Ok($$"""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":{{value}}}""");
            f.Now += 80;
        }
        Assert.Equal((.6, .6), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("sendLevel", "xlr1", "monitor"), (step.Kind, step.Channel, step.Mix));
        // Two values moved, so the step carries no single from and to.
        Assert.Null(step.From);

        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal((.3, .3), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));
        await f.Ok("""{"cmd":"redo"}""");
        Assert.Equal((.6, .6), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));
        await f.Ok("""{"cmd":"undo"}""");
        await f.Ok("""{"cmd":"undo"}""");   // the mutes, one step each
        await f.Ok("""{"cmd":"undo"}""");
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "stream"));
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal((1.0, 1.0), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));
    }

    [Fact]
    public async Task TheLinkIsAStepThatPutsBackTheLevelsItMoved()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"monitor","value":0.4}""");
        f.Now += 2000;
        await f.Ok("""{"cmd":"setLevel","channel":"xlr1","mix":"stream","value":0.9}""");
        f.Now += 2000;
        await f.Ok("""{"cmd":"setLinkedSends","channel":"xlr1","value":true}""");
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("linkedSends", "xlr1", "Mic 1 linked sends"), (step.Kind, step.Channel, step.Label));

        await f.Ok("""{"cmd":"undo"}""");
        Assert.False(Linked(f, "xlr1"));
        Assert.Equal((.4, .9), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));
        await f.Ok("""{"cmd":"redo"}""");
        Assert.True(Linked(f, "xlr1"));
        Assert.Equal((.4, .4), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));
        f.Now += 2000;

        // Unlinking moves no level, so its step holds the switch alone.
        await f.Ok("""{"cmd":"setLinkedSends","channel":"xlr1","value":false}""");
        step = f.State().History!.Undo!;
        Assert.Equal((false, true), ((bool)step.From!, (bool)step.To!));   // now, then what undo writes
        await f.Ok("""{"cmd":"undo"}""");
        Assert.True(Linked(f, "xlr1"));
    }

    [Fact]
    public async Task TheCommandTakesAKnownChannelAndABoolean()
    {
        using var f = new Fixture();
        Assert.Equal("setLinkedSends: value must be a boolean",
            await f.Refused("""{"cmd":"setLinkedSends","channel":"xlr1","value":1}"""));
        Assert.Equal("setLinkedSends: value must be a boolean",
            await f.Refused("""{"cmd":"setLinkedSends","channel":"xlr1","value":"true"}"""));
        Assert.Equal("setLinkedSends: value must be a boolean", await f.Refused("""{"cmd":"setLinkedSends","channel":"xlr1"}"""));
        Assert.Equal("setLinkedSends: unknown channel 'nope'",
            await f.Refused("""{"cmd":"setLinkedSends","channel":"nope","value":true}"""));
        Assert.Equal("setLinkedSends: need 'channel'", await f.Refused("""{"cmd":"setLinkedSends","value":true}"""));
        Assert.False(Linked(f, "xlr1"));
        Assert.Null(f.State().History!.Undo);
    }

    [Fact]
    public async Task AProfileSetsTheLinksItNamesAndBringsLinkedSendsTogether()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setLinkedSends","channel":"xlr1","value":true}""");
        MixerScene scene = f.Mixer.ExportScene();
        Assert.Equal(new Dictionary<string, bool> { ["xlr1"] = true, ["xlr2"] = false }, scene.LinkedSends);

        // A profile saved before linked sends holds the sends apart and names
        // no link: the link stays and the sends follow the first mix.
        f.Mixer.ApplyScene(new MixerScene { Levels = new() { ["xlr1|monitor"] = .2, ["xlr1|stream"] = .8 } });
        Assert.True(Linked(f, "xlr1"));
        Assert.Equal((.2, .2), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));

        // One that names the links sets them.
        f.Mixer.ApplyScene(new MixerScene
        {
            Levels = new() { ["xlr1|monitor"] = .3, ["xlr1|stream"] = .7, ["xlr2|monitor"] = .5, ["xlr2|stream"] = .6 },
            LinkedSends = new() { ["xlr1"] = false, ["xlr2"] = true, ["gone"] = true },
        });
        Assert.False(Linked(f, "xlr1"));
        Assert.True(Linked(f, "xlr2"));
        Assert.Equal((.3, .7), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));
        Assert.Equal((.5, .5), (f.Level("xlr2|monitor"), f.Level("xlr2|stream")));
        Assert.Equal(["xlr2"], f.Mixer.ExportSettings().LinkedSends);
    }

    [Fact]
    public void ASettingsFileWithALinkedChannelsSendsApartBringsThemTogether()
    {
        using var f = new Fixture();
        f.Mixer.ApplySettings(new MixerSettings
        {
            LinkedSends = ["xlr2", "gone"],
            Levels = new() { ["xlr1|monitor"] = .3, ["xlr1|stream"] = .7, ["xlr2|monitor"] = .4, ["xlr2|stream"] = .9 },
        });
        Assert.False(Linked(f, "xlr1"));
        Assert.True(Linked(f, "xlr2"));
        Assert.Equal((.3, .7), (f.Level("xlr1|monitor"), f.Level("xlr1|stream")));
        Assert.Equal((.4, .4), (f.Level("xlr2|monitor"), f.Level("xlr2|stream")));
        Assert.Equal(["xlr2"], f.Mixer.ExportSettings().LinkedSends);
    }

    private static bool Linked(Fixture f, string channel)
    {
        bool linked = f.State().Mixer!.Channels.Single(c => c.Id == channel).LinkedSends;
        Assert.Equal(linked, f.Mixer.SendsLinked(channel));
        return linked;
    }
}
