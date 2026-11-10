using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using static OpenXLR.Tests.BrowserTabRoutingTests;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class AppRoutingPolicyTests
{
    [Theory]
    [InlineData("chromium", "browser")]
    [InlineData("Spotify", "music")]
    [InlineData("Discord", "voicechat")]
    [InlineData("wine64-preloader", "game")]
    [InlineData("game.exe", "game")]
    [InlineData("unknown", "system")]
    public void OnlyAutomaticRulesAreDisabled(string binary, string expected)
    {
        var matcher = new StreamMatcher();
        var stream = new AudioStream(10, binary, binary, null);
        Assert.Equal(expected, matcher.Match(stream));
        matcher.RouteByDefault = false;
        Assert.Equal(StreamMatcher.Ignore, matcher.Match(stream));
        matcher.SetOverride(stream.Identity, "music");
        Assert.Equal("music", matcher.Match(stream));
        matcher.RemoveOverride(stream.Identity);
        Assert.Equal(StreamMatcher.Ignore, matcher.Match(stream));
        matcher.RouteByDefault = true;
        Assert.Equal(expected, matcher.Match(stream));
    }

    [Fact]
    public void ChangingTheDefaultReleasesOnlyUnassignedPlaybackAndDoesNotUnmuteIt()
    {
        using var graph = new BrowserGraph();
        graph.Build();
        graph.Mixer.SyncStreams();
        graph.ClearCalls();
        graph.Mixer.SetRouteAppsByDefault(false);
        Assert.True(graph.Mixer.SyncStreams());
        Assert.Equal(StreamMatcher.Ignore, Assert.Single(graph.Mixer.Snapshot().Streams).ChannelId);
        Assert.Contains("move-sink-input 100 desktop", graph.Calls);
        Assert.DoesNotContain(graph.Calls, call => call.StartsWith("set-sink-input-mute"));
        graph.ClearCalls();
        graph.Mixer.SetRouteAppsByDefault(false);
        graph.Mixer.SyncStreams();
        Assert.DoesNotContain(graph.Calls, call => call.StartsWith("move-sink-input"));

        graph.Mixer.AssignApp("chromium", "music");
        graph.Mixer.SyncStreams();
        Assert.Equal("music", Assert.Single(graph.Mixer.Snapshot().Streams).ChannelId);
        graph.ClearCalls();
        graph.Mixer.SetRouteAppsByDefault(true);
        graph.Mixer.SyncStreams();
        graph.Mixer.SetRouteAppsByDefault(false);
        graph.Mixer.SyncStreams();
        Assert.DoesNotContain(graph.Calls, call => call.StartsWith("move-sink-input") || call.StartsWith("set-sink-input-mute"));
        Assert.Equal("music", graph.Mixer.Matcher.Overrides["chromium"]);

        graph.Mixer.ForgetApp("chromium");
        graph.Mixer.SyncStreams();
        Assert.Equal(StreamMatcher.Ignore, Assert.Single(graph.Mixer.Snapshot().Streams).ChannelId);
        graph.Mixer.SetRouteAppsByDefault(true);
        graph.Mixer.SyncStreams();
        Assert.Equal("system", Assert.Single(graph.Mixer.Snapshot().Streams).ChannelId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARefusedReleaseIsRetriedInsteadOfBeingRememberedAsComplete(bool explicitIgnore)
    {
        using var graph = new BrowserGraph();
        graph.Build();
        graph.Mixer.SyncStreams();
        graph.RefuseMoves();
        if (explicitIgnore) graph.Mixer.AssignApp("chromium", StreamMatcher.Ignore);
        else graph.Mixer.SetRouteAppsByDefault(false);
        graph.Mixer.SyncStreams();
        File.Delete(Path.Combine(graph.Folder, "refuse-moves"));
        graph.ClearCalls();
        graph.Mixer.SyncStreams();
        Assert.Contains("move-sink-input 100 desktop", graph.Calls);
        Assert.Equal(StreamMatcher.Ignore, Assert.Single(graph.Mixer.Snapshot().Streams).ChannelId);
    }

    [Theory]
    [InlineData("\"application.name\":\"Music Player\"", "Music Player")]
    [InlineData("\"application.process.binary\":\"Music Player.x86_64\",\"application.name\":\"Music Player\"", "Music Player.x86_64")]
    [InlineData("\"application.process.binary\":\"WebKitWebProcess\",\"application.name\":\"News Flash\"", "News Flash")]
    public void AnApplicationWithASpaceInItsNameKeepsOneEntryAndItsAssignment(string props, string published)
    {
        using var graph = new BrowserGraph(props);
        graph.Build();
        graph.Mixer.SyncStreams();
        StreamAssignment listed = Assert.Single(graph.Mixer.Snapshot().Streams);
        Assert.Equal(StreamMatcher.MigrateIdentity(published), listed.Identity);
        Assert.DoesNotContain(' ', listed.Identity);
        Assert.Equal("system", listed.ChannelId);
        graph.ClearCalls();

        // A client may still send the name as the application published it.
        graph.Mixer.AssignApp(published, "music");
        graph.Mixer.SyncStreams();
        Assert.Contains("move-sink-input 100 OpenXLR_ch_music", graph.Calls);
        StreamAssignment assigned = Assert.Single(graph.Mixer.Snapshot().Streams);
        Assert.Equal((listed.Identity, "music", true), (assigned.Identity, assigned.ChannelId, assigned.Active));
        Assert.Equal("music", Assert.Single(graph.Mixer.ExportSettings().AppOverrides).Value);

        // Settings written while the live name and the stored one disagreed
        // hold both; they come back as the one entry, with the assignment.
        MixerSettings saved = graph.Mixer.ExportSettings();
        saved = saved with { KnownApps = [new SavedApp(published, published, "system"), .. saved.KnownApps] };
        using var restored = new Mixer();
        graph.Build(restored);
        restored.ApplySettings(saved);
        graph.SetStreamSink(1);
        graph.ClearCalls();
        restored.SyncStreams();
        Assert.Contains("move-sink-input 100 OpenXLR_ch_music", graph.Calls);
        StreamAssignment again = Assert.Single(restored.Snapshot().Streams);
        Assert.Equal((listed.Identity, "music", true), (again.Identity, again.ChannelId, again.Active));

        restored.ForgetApp(published);
        Assert.Empty(restored.ExportSettings().AppOverrides);
    }

    [Fact]
    public void PlaybackVolumeAppliesWhileAnApplicationRouteIsWaitingForAMove()
    {
        using var graph = new BrowserGraph();
        graph.Build();
        graph.Mixer.SyncStreams();
        graph.Mixer.SetAppVolume("chromium", .35, _ => null);
        graph.RefuseMoves();
        graph.Mixer.SetRouteAppsByDefault(false);
        graph.ClearCalls();

        graph.Mixer.SyncStreams();
        Assert.Contains("move-sink-input 100 desktop", graph.Calls);
        Assert.Contains("set-sink-input-volume 100 35%", graph.Calls);
        graph.ClearCalls();
        graph.Mixer.SyncStreams();
        Assert.DoesNotContain("set-sink-input-volume 100 35%", graph.Calls);
    }

    [Fact]
    public void PlaybackVolumeKeepsSpacedAndCompactAppIdentitiesSeparate()
    {
        const string spaced = "Music Player";
        const string compact = "musicplayer";
        using var graph = new BrowserGraph("\"application.name\":\"Music Player\"");
        graph.Build();
        graph.Mixer.SyncStreams();
        graph.Mixer.SetAppVolume(spaced, .35, _ => null);
        graph.Mixer.SyncStreams();
        Assert.Contains("set-sink-input-volume 100 35%", graph.Calls);

        graph.SetStream("\"application.process.binary\":\"musicplayer\"", serial: 101);
        graph.ClearCalls();
        graph.Mixer.SyncStreams();
        Assert.DoesNotContain("set-sink-input-volume 101 35%", graph.Calls);

        string path = Path.Combine(graph.Folder, "mixer.json");
        graph.Mixer.SetAppVolume(compact, .68, settings => settings.Save(path));
        graph.Mixer.SyncStreams();
        Assert.Contains("set-sink-input-volume 101 68%", graph.Calls);

        var saved = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
        Assert.Null(warning);
        graph.SetStream("\"application.name\":\"Music Player\"");
        using var restored = new Mixer();
        graph.Build(restored);
        restored.ApplySettings(saved);
        var apps = restored.Snapshot().Streams.ToDictionary(app => app.Identity, StringComparer.Ordinal);
        Assert.Equal(.35, apps[spaced].Volume);
        Assert.Equal(.68, apps[compact].Volume);
        Assert.All(apps.Values, app => Assert.False(app.Active));
        graph.ClearCalls();
        restored.SyncStreams();
        Assert.Contains("set-sink-input-volume 100 35%", graph.Calls);

        restored.SetAppVolume(spaced, null, _ => null);
        Assert.Equal(.68, Assert.Single(restored.ExportSettings().AppVolumes).Value);
        restored.SetAppVolume(spaced, .35, _ => null);
        restored.ForgetApp(spaced);
        Assert.Equal(compact, Assert.Single(restored.ExportSettings().AppVolumes).Key);
        Assert.Equal(compact, Assert.Single(restored.Snapshot().Streams).Identity);
    }

    [Fact]
    public void ADesktopOutputIsLeftAloneAndBrowserPreservationStillWins()
    {
        using var graph = new BrowserGraph();
        graph.Build();
        graph.Mixer.SetRouteAppsByDefault(false);
        graph.SetStreamSink(3);
        graph.Mixer.SyncStreams();
        Assert.DoesNotContain(graph.Calls, call => call.StartsWith("move-sink-input") || call.StartsWith("set-sink-input-mute"));
        graph.Mixer.SetPreserveBrowserOutputs(true);
        graph.Mixer.AssignApp("chromium", "music");
        graph.Mixer.SyncStreams();
        StreamAssignment app = Assert.Single(graph.Mixer.Snapshot().Streams);
        Assert.True(app.BrowserHeld);
        Assert.Equal("music", app.ChannelId);
        Assert.DoesNotContain(graph.Calls, call => call.StartsWith("move-sink-input"));
    }

    [Fact]
    public void FailedSaveLeavesBothRoutingAndTheReportedPolicyUnchanged()
    {
        using var graph = new BrowserGraph();
        graph.Build();
        graph.Mixer.SyncStreams();
        StreamAssignment before = Assert.Single(graph.Mixer.Snapshot().Streams);
        graph.ClearCalls();
        Assert.Throws<IOException>(() => graph.Mixer.SetRouteAppsByDefault(false, settings =>
        {
            Assert.False(settings.RouteAppsByDefault);
            return "disk full";
        }));
        Assert.True(graph.Mixer.Snapshot().RouteAppsByDefault);
        Assert.True(graph.Mixer.ExportSettings().RouteAppsByDefault);
        Assert.Equal(before, Assert.Single(graph.Mixer.Snapshot().Streams));
        graph.Mixer.SyncStreams();
        Assert.DoesNotContain(graph.Calls, call => call.StartsWith("move-sink-input"));
    }

    [Fact]
    public void SettingsSurviveRestartAndOldSettingsRetainAutomaticRouting()
    {
        using var graph = new BrowserGraph();
        graph.Build();
        graph.Mixer.AssignApp("pinned", "music");
        string path = Path.Combine(graph.Folder, "mixer.json");
        graph.Mixer.SetRouteAppsByDefault(false, settings => settings.Save(path));
        MixerSettings saved = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
        Assert.Null(warning);
        Assert.False(saved.RouteAppsByDefault);
        using var restored = new Mixer();
        graph.Build(restored);
        restored.ApplySettings(saved with { KnownApps = [new("other", "Other", "music"), new("pinned", "Pinned", "system")] });
        Assert.False(restored.Snapshot().RouteAppsByDefault);
        Assert.Equal(StreamMatcher.Ignore, restored.Snapshot().Streams.Single(a => a.Identity == "other").ChannelId);
        Assert.Equal("music", restored.Snapshot().Streams.Single(a => a.Identity == "pinned").ChannelId);
        restored.ApplyScene(restored.ExportScene());
        Assert.False(restored.Snapshot().RouteAppsByDefault);
        using var unbuilt = new Mixer();
        unbuilt.ApplySettings(saved);
        Assert.False(unbuilt.ExportSettings().RouteAppsByDefault);
        File.WriteAllText(path, "{}");
        Assert.True(MixerSettings.Load(path, out _)!.RouteAppsByDefault);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("null", false)]
    [InlineData("1", false)]
    [InlineData("\"false\"", false)]
    [InlineData("{}", false)]
    public void TheCommandAcceptsOnlyAnExplicitBoolean(string value, bool accepted)
    {
        using JsonDocument doc = JsonDocument.Parse(value);
        var command = new Command { Cmd = "setRouteAppsByDefault", Value = doc.RootElement.Clone() };
        using var mixer = new Mixer();
        Assert.Equal(accepted, CommandValidation.Check(command, mixer, _ => null) is null);
        Assert.NotNull(CommandValidation.Check(new Command { Cmd = "setRouteAppsByDefault" }, mixer, _ => null));
    }
}
