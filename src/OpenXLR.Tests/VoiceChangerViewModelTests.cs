using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// The Voice changer window's model, with no window: it offers every
/// microphone the daemon reports a voice changer for, starts on XLR 1, sends
/// one <c>setVoiceChanger</c> per press with the switch and the voice
/// together, keeps a fresh press against an older report, lights the tile
/// that plays and the header button while any microphone has a voice on, and
/// says why a voice does not run.
/// </summary>
[Collection("xdg-config")]
public sealed class VoiceChangerViewModelTests
{
    private sealed class Recorder : IVoiceCommands
    {
        public readonly ConcurrentQueue<(string Channel, bool? Enabled, string? Voice, double? Pitch, double? Formant)> Sent = new();

        public Task SetVoiceChangerAsync(string channel, bool? enabled = null, string? voice = null, double? pitch = null, double? formant = null)
        {
            Sent.Enqueue((channel, enabled, voice, pitch, formant));
            return Task.CompletedTask;
        }
    }

    private static string Voice(bool enabled = false, string voice = "deep", double pitch = 0, double formant = 0,
        string? active = null, bool available = true, string? error = null, double? latencyMs = null)
        => $$"""
            {"enabled":{{(enabled ? "true" : "false")}},"voice":"{{voice}}","pitch":{{pitch}},"formant":{{formant}},
             "active":{{(active is null ? "null" : $"\"{active}\"")}},"available":{{(available ? "true" : "false")}},
             "latencyMs":{{(latencyMs is double ms ? ms.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}},
             "error":{{(error is null ? "null" : $"\"{error}\"")}}}
            """;

    /// <summary>A mixer's channels: the guest microphone first, then XLR 1, Aux In without a voice changer, and an absent XLR 2.</summary>
    private static JsonNode Channels(string xlr1, string? guest = null) => JsonNode.Parse($$"""
        [{"id":"guest","name":"Guest","voice":{{guest ?? Voice()}}},
         {"id":"xlr1","name":"Main mic","voice":{{xlr1}}},
         {"id":"aux","name":"Aux In"},
         {"id":"xlr2","name":"XLR 2","present":false,"voice":{{Voice(enabled: true)}}}]
        """)!;

    private static VoiceTileViewModel Tile(VoiceChangerViewModel changer, string? voice) => changer.Tiles.Single(t => t.Voice == voice);

    [Fact]
    public void ItOffersEveryReportedMicrophoneAndStartsOnXlr1() => TestUi.Run(() =>
    {
        var changer = new VoiceChangerViewModel(new Recorder(), new ManualTime());
        Assert.False(changer.IsReported);
        // Off, then every voice in the daemon's order.
        Assert.Equal([null, "custom", "deep", "high", "robot", "radio", "alien", "dracula", "frankenstein", "mummy", "werewolf", "witch"],
            changer.Tiles.Select(t => t.Voice));
        Assert.Equal(Localizer.Text("VoiceOff"), changer.Tiles[0].Name);
        Assert.Equal(Localizer.Text("VoiceFrankensteinHint"), Tile(changer, "frankenstein").Hint);

        changer.Apply(Channels(Voice(enabled: true, voice: "robot", active: "robot")));
        Assert.True(changer.IsReported);
        Assert.True(changer.CanPickMicrophone);
        Assert.Equal(["guest", "xlr1"], changer.Microphones.Select(m => m.Id));
        Assert.Equal("xlr1", changer.SelectedMicrophone!.Id);
        Assert.Equal("Main mic", changer.SelectedMicrophone.ToString());
        Assert.True(changer.Enabled);
        Assert.True(changer.AnyOn);
        Assert.True(Tile(changer, "robot").IsSelected);
        Assert.Equal(Localizer.Format("VoiceTileSelected", Localizer.Text("VoiceRobot")), Tile(changer, "robot").AutomationName);
        Assert.False(changer.Tiles[0].IsSelected);
        Assert.False(changer.ShowsCustom);
        Assert.False(changer.HasStatus);

        // Another microphone shows its own setting.
        changer.SelectedMicrophone = changer.Microphones[0];
        Assert.False(changer.Enabled);
        Assert.True(changer.Tiles[0].IsSelected);
        Assert.True(changer.AnyOn, "XLR 1 still has a voice on.");

        // Without the voice changer anywhere, nothing is offered and nothing lights.
        changer.Reset();
        Assert.False(changer.IsReported);
        Assert.Null(changer.SelectedMicrophone);
        Assert.False(changer.AnyOn);
    });

    [Fact]
    public void APressSendsTheSwitchAndTheVoiceTogether() => TestUi.Run(() =>
    {
        var sent = new Recorder();
        var time = new ManualTime();
        var changer = new VoiceChangerViewModel(sent, time);
        changer.Apply(Channels(Voice()));
        Assert.False(changer.AnyOn);

        changer.Choose(Tile(changer, "dracula"));
        Assert.True(sent.Sent.TryDequeue(out var change));
        Assert.Equal(("xlr1", (bool?)true, "dracula", (double?)null, (double?)null), change);
        Assert.True(Tile(changer, "dracula").IsSelected);
        Assert.True(changer.AnyOn);
        Assert.Equal(Localizer.Text("VoiceStarting"), changer.Status);

        // A report from before the press does not move the tile back.
        changer.Apply(Channels(Voice()));
        Assert.True(Tile(changer, "dracula").IsSelected);
        time.Advance(VoiceChangerViewModel.EchoWait);
        changer.Apply(Channels(Voice(enabled: true, voice: "dracula", active: "dracula")));
        Assert.True(Tile(changer, "dracula").IsSelected);
        Assert.False(changer.HasStatus);
        // Once the stage reports the voice's delay, the line says it.
        changer.Apply(Channels(Voice(enabled: true, voice: "dracula", active: "dracula", latencyMs: 33.3)));
        Assert.Equal(Localizer.Format("VoiceRunning", "33"), changer.Status);
        Assert.False(changer.HasError);

        // The lit tile sends nothing; Off sends the switch alone.
        changer.Choose(Tile(changer, "dracula"));
        Assert.Empty(sent.Sent);
        changer.Choose(changer.Tiles[0]);
        Assert.True(sent.Sent.TryDequeue(out change));
        Assert.Equal(("xlr1", (bool?)false, (string?)null, (double?)null, (double?)null), change);
        Assert.True(changer.Tiles[0].IsSelected);
        Assert.False(changer.AnyOn);
        changer.Choose(changer.Tiles[0]);
        Assert.Empty(sent.Sent);
    });

    [Fact]
    public void TheCustomVoiceShowsItsShiftsAndSendsEachAlone() => TestUi.Run(() =>
    {
        var sent = new Recorder();
        var changer = new VoiceChangerViewModel(sent, new ManualTime());
        changer.Apply(Channels(Voice(enabled: true, voice: "custom", pitch: -3, formant: 1.5, active: "custom")));
        Assert.True(changer.ShowsCustom);
        Assert.Equal(-3, changer.Pitch);
        Assert.Equal(Localizer.Format("VoiceShiftValue", "-3"), changer.PitchText);
        Assert.Equal(Localizer.Format("VoiceShiftValue", "+1.5"), changer.FormantText);
        changer.Pitch = 4.3;
        Assert.Equal(4.5, changer.Pitch);
        changer.Formant = -20;
        Assert.Equal(-12, changer.Formant);
        SliderSync.FlushPending();
        Assert.Equal([("xlr1", (bool?)null, (string?)null, (double?)4.5, (double?)null), ("xlr1", (bool?)null, (string?)null, (double?)null, (double?)-12)],
            sent.Sent.OrderBy(s => s.Pitch is null));
        SliderSync.Forget("voicepitch:xlr1");
        SliderSync.Forget("voiceformant:xlr1");
    });

    [Fact]
    public void WithoutTheHelperOnlyOffTakesAPressAndTheReasonShows() => TestUi.Run(() =>
    {
        var sent = new Recorder();
        var changer = new VoiceChangerViewModel(sent, new ManualTime());
        const string reason = "The voice changer needs the OpenXLR native plugin host.";
        changer.Apply(Channels(Voice(enabled: true, voice: "witch", available: false, error: reason)));
        Assert.False(changer.Available);
        Assert.True(changer.HasError);
        Assert.Equal(reason, changer.Status);
        Assert.False(Tile(changer, "robot").IsEnabled);
        Assert.True(changer.Tiles[0].IsEnabled);
        changer.Choose(Tile(changer, "robot"));
        Assert.Empty(sent.Sent);
        changer.Choose(changer.Tiles[0]);
        Assert.True(sent.Sent.TryDequeue(out var change));
        Assert.Equal((bool?)false, change.Enabled);
    });
}
