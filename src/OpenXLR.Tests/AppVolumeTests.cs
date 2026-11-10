using OpenXLR.Core.Mixing;
using static OpenXLR.Tests.BrowserTabRoutingTests;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class AppVolumeTests : IDisposable
{
    private const string Vlc = "\"application.process.binary\":\"vlc\",\"application.name\":\"VLC\"";
    private const string Mpv = "\"application.process.binary\":\"mpv\",\"application.name\":\"mpv\"";

    private readonly BrowserGraph _graph = new();
    private readonly Mixer _mixer;
    private long _now = 10_000;

    public AppVolumeTests()
    {
        _mixer = NewMixer();
    }

    public void Dispose()
    {
        _mixer.Dispose();
        _graph.Dispose();
    }

    private Mixer NewMixer()
    {
        var mixer = new Mixer { Clock = () => _now };
        _graph.Build(mixer);
        return mixer;
    }

    private void Streams(params string[] nodes) => _graph.SetStreams(_mixer, nodes);

    private string[] VolumeWrites => [.. _graph.Calls.Where(call => call.StartsWith("set-sink-input-volume", StringComparison.Ordinal))];

    private StreamAssignment App(string identity) => _mixer.Snapshot().Streams.Single(app => app.Identity == identity);

    [Fact]
    public void ACommandWritesTheLevelToEveryPlayingStreamOfTheApplicationAtOnce()
    {
        Streams(BrowserGraph.Node(10, 100, Vlc), BrowserGraph.Node(11, 101, Vlc), BrowserGraph.Node(12, 102, Mpv));
        _mixer.SyncStreams();
        _graph.ClearCalls();

        _mixer.SetAppVolume("VLC", .35);
        // No sweep ran: the fader is heard when the command returns.
        Assert.Equal(["set-sink-input-volume 100 35%", "set-sink-input-volume 101 35%"], VolumeWrites);
        Assert.Equal(.35, App("vlc").Volume);
        Assert.Null(App("mpv").Volume);
        Assert.Equal(.35, Assert.Single(_mixer.ExportSettings().AppVolumes).Value);

        // The registry shows the write a moment later; nothing is repeated.
        _graph.ClearCalls();
        Streams(BrowserGraph.Node(10, 100, Vlc, 35), BrowserGraph.Node(11, 101, Vlc, 35), BrowserGraph.Node(12, 102, Mpv));
        _now += 1000;
        _mixer.SyncStreams();
        _mixer.SyncStreams();
        Assert.Empty(VolumeWrites);
        Assert.False(_mixer.TakeAppVolumesChanged());
        Assert.Equal(.35, App("vlc").LiveVolume);
        Assert.Equal(1, App("mpv").LiveVolume);
    }

    [Fact]
    public void ResetAndForgetPutThePlayingStreamsBackToFullVolume()
    {
        Streams(BrowserGraph.Node(10, 100, Vlc), BrowserGraph.Node(12, 102, Mpv));
        _mixer.SyncStreams();
        _mixer.SetAppVolume("vlc", .35);
        _mixer.SetAppVolume("mpv", .5);
        _graph.ClearCalls();

        _mixer.SetAppVolume("vlc", null);
        Assert.Equal(["set-sink-input-volume 100 100%"], VolumeWrites);
        Assert.Null(App("vlc").Volume);
        Assert.Equal("mpv", Assert.Single(_mixer.ExportSettings().AppVolumes).Key);
        // Clearing what is already clear writes nothing.
        _mixer.SetAppVolume("vlc", null);
        Assert.Single(VolumeWrites);

        _graph.ClearCalls();
        _mixer.ForgetApp("mpv");
        Assert.Equal(["set-sink-input-volume 102 100%"], VolumeWrites);
        Assert.Empty(_mixer.ExportSettings().AppVolumes);
    }

    [Fact]
    public void AResetTheSoundServerRefusedIsRepeatedByTheSweep()
    {
        Streams(BrowserGraph.Node(10, 100, Vlc));
        _mixer.SyncStreams();
        _mixer.SetAppVolume("vlc", .35);
        _graph.RefuseVolumes();
        _mixer.SetAppVolume("vlc", null);
        _graph.RefuseVolumes(false);
        _graph.ClearCalls();

        _mixer.SyncStreams();
        Assert.Equal(["set-sink-input-volume 100 100%"], VolumeWrites);
        _graph.ClearCalls();
        _mixer.SyncStreams();
        Assert.Empty(VolumeWrites);
    }

    [Fact]
    public void ALevelMovedOutsideOpenXlrIsFollowedAndGivenToTheOtherStreams()
    {
        Streams(BrowserGraph.Node(10, 100, Vlc), BrowserGraph.Node(11, 101, Vlc));
        _mixer.SyncStreams();
        _mixer.SetAppVolume("vlc", .35);
        Streams(BrowserGraph.Node(10, 100, Vlc, 35), BrowserGraph.Node(11, 101, Vlc, 35));
        _now += 1000;
        _mixer.SyncStreams();
        _graph.ClearCalls();

        // The desktop's applet, or the program's own slider, moves one stream.
        Streams(BrowserGraph.Node(10, 100, Vlc, 62), BrowserGraph.Node(11, 101, Vlc, 35));
        _now += 1000;
        Assert.True(_mixer.SyncStreams());
        Assert.Equal(["set-sink-input-volume 101 62%"], VolumeWrites);
        Assert.Equal(.62, App("vlc").Volume);
        Assert.Equal(.62, _mixer.ExportSettings().AppVolumes["vlc"]);
        Assert.True(_mixer.TakeAppVolumesChanged());
        Assert.False(_mixer.TakeAppVolumesChanged());

        // A boost past unity is followed as full volume and left as it is.
        _graph.ClearCalls();
        Streams(BrowserGraph.Node(10, 100, Vlc, 120), BrowserGraph.Node(11, 101, Vlc, 62));
        _now += 1000;
        _mixer.SyncStreams();
        Assert.Equal(["set-sink-input-volume 101 100%"], VolumeWrites);
        Assert.Equal(1, App("vlc").Volume);
    }

    [Fact]
    public void AWriteTheStreamDoesNotKeepIsRepeatedAndThenTheStreamIsFollowed()
    {
        Streams(BrowserGraph.Node(10, 100, Vlc, 80));
        _mixer.SyncStreams();
        _mixer.SetAppVolume("vlc", .35);
        _graph.ClearCalls();

        // Within the settle time the old level is the registry's echo.
        _now += 1000;
        _mixer.SyncStreams();
        Assert.Empty(VolumeWrites);
        Assert.Equal(.35, App("vlc").Volume);

        // After it, something set the stream back: twice more, then follow.
        _now += 1000;
        _mixer.SyncStreams();
        _now += 2000;
        _mixer.SyncStreams();
        Assert.Equal(["set-sink-input-volume 100 35%", "set-sink-input-volume 100 35%"], VolumeWrites);
        Assert.False(_mixer.TakeAppVolumesChanged());
        _now += 2000;
        _mixer.SyncStreams();
        Assert.Equal(2, VolumeWrites.Length);
        Assert.Equal(.8, App("vlc").Volume);
        Assert.True(_mixer.TakeAppVolumesChanged());
    }

    [Fact]
    public void ANewStreamGetsTheSavedLevelWithoutASweepAndOneAlreadyThereIsLeftAlone()
    {
        Streams(BrowserGraph.Node(10, 100, Vlc));
        _mixer.SyncStreams();
        _mixer.SetAppVolume("vlc", .35);
        MixerSettings saved = _mixer.ExportSettings();

        // The session manager restored another program's level to the new stream.
        _graph.ClearCalls();
        Streams(BrowserGraph.Node(10, 100, Vlc, 35), BrowserGraph.Node(14, 104, Vlc, 90));
        _now += 1000;
        _mixer.SyncAppVolumes();
        Assert.Equal(["set-sink-input-volume 104 35%"], VolumeWrites);

        // A daemon that starts while the program plays at its level writes nothing.
        using Mixer restarted = NewMixer();
        restarted.ApplySettings(saved);
        _graph.SetStreams(restarted, BrowserGraph.Node(10, 100, Vlc, 35), BrowserGraph.Node(14, 104, Vlc, 35));
        _graph.ClearCalls();
        restarted.SyncStreams();
        restarted.ApplySettings(saved);
        restarted.SyncStreams();
        Assert.Empty(VolumeWrites);
        Assert.Equal(.35, restarted.Snapshot().Streams.Single(app => app.Identity == "vlc").Volume);
    }

    [Fact]
    public void AStreamWhoseRouteIsStillPendingGetsTheLevelAnyway()
    {
        Streams(BrowserGraph.Node(10, 100, BrowserGraph.ChromiumProps));
        _mixer.SyncStreams();
        _mixer.SetAppVolume("chromium", .35);
        _graph.RefuseMoves();
        Streams(BrowserGraph.Node(10, 100, BrowserGraph.ChromiumProps, 35), BrowserGraph.Node(11, 101, BrowserGraph.ChromiumProps, 90));
        _graph.ClearCalls();
        _now += 1000;
        _mixer.SyncStreams();
        Assert.Contains(_graph.Calls, call => call.StartsWith("move-sink-input 101 ", StringComparison.Ordinal));
        Assert.DoesNotContain(_mixer.Streams, stream => stream.Serial == 101);
        Assert.Equal(["set-sink-input-volume 101 35%"], VolumeWrites);
    }

    [Fact]
    public void ARefusedWriteIsTriedAgainAndStreamsWithoutALevelAreNotFollowed()
    {
        Streams(BrowserGraph.Node(10, 100, Vlc, null));
        _mixer.SyncStreams();
        _graph.RefuseVolumes();
        _mixer.SetAppVolume("vlc", .35);
        _mixer.SyncStreams();
        Assert.Single(VolumeWrites);   // not before its retry time
        _graph.RefuseVolumes(false);
        _graph.ClearCalls();
        _now += 300;
        _mixer.SyncStreams();
        Assert.Equal(["set-sink-input-volume 100 35%"], VolumeWrites);

        _graph.ClearCalls();
        _now += 5000;
        _mixer.SyncStreams();
        Assert.Empty(VolumeWrites);
        Assert.Equal(.35, App("vlc").Volume);
        Assert.Null(App("vlc").LiveVolume);
    }

    [Fact]
    public void AnApplicationWithoutASavedLevelShowsWhatItPlaysAtAndIsNeverWritten()
    {
        Streams(BrowserGraph.Node(10, 100, Vlc, 60));
        Assert.True(_mixer.SyncStreams());
        Assert.Null(App("vlc").Volume);
        Assert.Equal(.6, App("vlc").LiveVolume);
        Streams(BrowserGraph.Node(10, 100, Vlc, 45));
        Assert.True(_mixer.SyncStreams());
        Assert.Equal(.45, App("vlc").LiveVolume);
        Assert.False(_mixer.SyncStreams());
        Assert.Empty(VolumeWrites);
        Assert.False(_mixer.TakeAppVolumesChanged());
    }

    [Fact]
    public void TheLevelIsSavedUnderTheKeyTheChannelAssignmentUses()
    {
        Streams(BrowserGraph.Node(10, 100, "\"application.process.binary\":\"Cyberpunk2077.exe\",\"application.name\":\"Cyberpunk 2077\""));
        _mixer.SyncStreams();
        _graph.ClearCalls();
        // A client may send an older spelling of the name, as assignApp accepts.
        _mixer.SetAppVolume("Cyberpunk 2077", .4);
        Assert.Equal(["set-sink-input-volume 100 40%"], VolumeWrites);
        Assert.Equal("cyberpunk2077", Assert.Single(_mixer.ExportSettings().AppVolumes).Key);
        Assert.Equal(.4, App("cyberpunk2077").Volume);

        using Mixer restarted = NewMixer();
        restarted.ApplySettings(_mixer.ExportSettings() with { AppVolumes = new() { ["Cyberpunk2077.exe"] = .4 } });
        Assert.Equal("cyberpunk2077", Assert.Single(restarted.ExportSettings().AppVolumes).Key);

        Assert.Throws<InvalidOperationException>(() => _mixer.SetAppVolume("not-listed", .4));
        Assert.Throws<ArgumentException>(() => _mixer.SetAppVolume("wine64-preloader", .4));
        Assert.Throws<ArgumentOutOfRangeException>(() => _mixer.SetAppVolume("cyberpunk2077", 1.2));
    }
}
