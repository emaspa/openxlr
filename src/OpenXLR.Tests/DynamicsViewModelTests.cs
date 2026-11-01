using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// The compressor's and expander's model, with no window: what it offers, the
/// one field each change sends, a fresh press kept against an older report,
/// the levels a meter push carries into the meters and the history, and the
/// line that says why nothing shows.
/// </summary>
[Collection("xdg-config")]
public sealed class DynamicsViewModelTests
{
    private sealed class Recorder : IDynamicsCommands
    {
        public readonly ConcurrentQueue<(string Processor, string Channel, string Field, object Value)> Sent = new();
        public int Watches;

        public Task SetDynamicsAsync(string processor, string channel, string field, object value)
        {
            Sent.Enqueue((processor, channel, field, value));
            return Task.CompletedTask;
        }

        public Task WatchDynamicsAsync(string channel)
        {
            Interlocked.Increment(ref Watches);
            return Task.CompletedTask;
        }
    }

    private static JsonNode Compressor(bool enabled = false, double threshold = -18, bool available = true, string? error = null)
        => JsonNode.Parse($$"""
            {"enabled":{{(enabled ? "true" : "false")}},"threshold":{{threshold}},"ratio":3,"attack":5,"release":150,"knee":6,"makeup":0,
             "available":{{(available ? "true" : "false")}},"error":{{(error is null ? "null" : $"\"{error}\"")}}}
            """)!;

    private static JsonNode Expander(bool enabled = false, double range = 24)
        => JsonNode.Parse($$"""
            {"enabled":{{(enabled ? "true" : "false")}},"threshold":-50,"ratio":3,"range":{{range}},"attack":1,"release":150,"hold":50,
             "available":true,"error":null}
            """)!;

    [Fact]
    public void EachProcessorOffersItsOwnSettingsInSignalOrder() => TestUi.Run(() =>
    {
        var pair = new ChannelDynamicsViewModel(new Recorder(), "xlr1", "XLR 1", new ManualTime());
        MicDynamicsViewModel compressor = pair.Compressor, expander = pair.Expander;
        Assert.Equal(["ratio", "attack", "release", "knee"], compressor.Advanced.Select(p => p.Field));
        Assert.Equal(["ratio", "range", "attack", "hold", "release"], expander.Advanced.Select(p => p.Field));
        Assert.True(compressor.HasMakeup);
        Assert.False(expander.HasMakeup);
        Assert.Equal(DynamicsGraphStyle.Envelope, compressor.GraphStyle);
        Assert.Equal(DynamicsGraphStyle.Line, expander.GraphStyle);
        Assert.Equal(-60, compressor.FloorDb);
        Assert.Equal(-80, expander.FloorDb);
        Assert.Equal(Localizer.Format("CompressorWindowTitle", "XLR 1"), compressor.Title);
        Assert.Equal(Localizer.Format("DynamicsSubtitle", "XLR 1"), expander.Subtitle);
        Assert.Equal(Localizer.Text("CompressorNote"), compressor.Note);
        Assert.Equal(Localizer.Format("DynamicsDecibels", "-18"), compressor.Threshold.Text);
        Assert.Equal(Localizer.Format("DynamicsRatioValue", "3"), compressor.Advanced[0].Text);
        Assert.Equal(Localizer.Format("DynamicsMilliseconds", "50"), expander.Advanced[3].Text);
        pair.ChannelName = "Main mic";
        Assert.Equal(Localizer.Format("ExpanderWindowTitle", "Main mic"), expander.Title);
    });

    [Fact]
    public void AChangeSendsItsOneFieldAndAFreshPressBeatsAnOlderReport() => TestUi.Run(() =>
    {
        var sent = new Recorder();
        var time = new ManualTime();
        var pair = new ChannelDynamicsViewModel(sent, "guest", "Guest", time);
        pair.Apply(Compressor(), Expander());
        Assert.False(pair.IsLit);
        Assert.Equal(Localizer.Format("DynamicsButtonNameOff", "Guest"), pair.ButtonName);

        pair.Compressor.Enabled = true;
        Assert.True(sent.Sent.TryDequeue(out var press));
        Assert.Equal(("compressor", "guest", "enabled", (object)true), press);
        // An older report still says off: the switch stays where the user put it.
        pair.Apply(Compressor(enabled: false), Expander());
        Assert.True(pair.Compressor.Enabled);
        Assert.True(pair.IsLit);
        time.Advance(MicDynamicsViewModel.EchoWait);
        pair.Apply(Compressor(enabled: true), Expander());
        Assert.True(pair.Compressor.Enabled);
        Assert.Equal(Localizer.Format("DynamicsButtonNameOn", "Guest"), pair.ButtonName);

        // Sliders send on the trailing edge, snapped and inside the range.
        pair.Expander.Threshold.Value = -44.4;
        pair.Expander.Advanced[1].Value = 200;
        pair.Compressor.Makeup!.Value = 6.2;
        SliderSync.FlushPending();
        Assert.Equal(-44, pair.Expander.Threshold.Value);
        Assert.Equal(80, pair.Expander.Advanced[1].Value);
        var rest = sent.Sent.ToArray();
        Assert.Contains(("expander", "guest", "threshold", (object)-44d), rest);
        Assert.Contains(("expander", "guest", "range", (object)80d), rest);
        Assert.Contains(("compressor", "guest", "makeup", (object)6d), rest);
        Assert.Equal(3, rest.Length);

        // A daemon that cannot run it says so on the switch's button.
        time.Advance(MicDynamicsViewModel.EchoWait);
        pair.Apply(Compressor(enabled: true, error: "no host here"), Expander());
        Assert.True(pair.IsFaulty);
        Assert.Equal("no host here", pair.Compressor.Status);
        Assert.Equal(Localizer.Format("DynamicsButtonNameUnavailable", "Guest"), pair.ButtonName);

        // Detached, nothing more goes out.
        pair.Detach();
        sent.Sent.Clear();
        pair.Compressor.Enabled = false;
        pair.Compressor.Threshold.Value = -30;
        SliderSync.FlushPending();
        Assert.Empty(sent.Sent);
    });

    [Fact]
    public void LevelsFillTheMetersAndTheHistoryAndLapseWhenTheyStop() => TestUi.Run(() =>
    {
        var time = new ManualTime();
        var sent = new Recorder();
        var pair = new ChannelDynamicsViewModel(sent, "xlr1", "XLR 1", time);
        pair.Apply(Compressor(enabled: true), Expander(enabled: true, range: 40));
        MicDynamicsViewModel expander = pair.Expander;
        Assert.False(expander.HasLevels);
        Assert.Equal(Localizer.Text("DynamicsNoLevels"), expander.Status);
        Assert.Equal(Localizer.Text("DynamicsSilent"), expander.InputText);

        expander.ApplyLevels(new JsonArray(-40.0, 10.0, -50.0));
        Assert.True(expander.HasLevels);
        Assert.False(expander.HasStatus);
        Assert.Equal(0.5, expander.InputLevel);
        Assert.Equal(0.25, expander.ReductionLevel);   // 10 of a 40 dB range
        Assert.Equal(Localizer.Format("DynamicsDecibels", "-40"), expander.InputText);
        Assert.Equal(Localizer.Format("DynamicsDecibels", "10"), expander.ReductionText);
        Assert.Equal(Localizer.Format("DynamicsInputName", expander.InputText), expander.InputName);
        // Each push adds to the history, below the floor held at the floor.
        expander.ApplyLevels(new JsonArray(-120.0, 40.0, -120.0));
        Assert.Equal(2, expander.History.Count);
        Assert.Equal(-40, expander.History[0]);
        Assert.Equal(-80, expander.History[1]);
        for (int i = 0; i < DynamicsHistory.Capacity + 5; i++) expander.ApplyLevels(new JsonArray(-30.0, 0.0, -30.0));
        Assert.Equal(DynamicsHistory.Capacity, expander.History.Count);
        // A malformed push is ignored.
        expander.ApplyLevels(new JsonArray(-10.0, "x", -10.0));
        Assert.Equal(-30, expander.InputDb);
        // The compressor's reduction meter fills at 30 dB.
        pair.Compressor.ApplyLevels(new JsonArray(-10.0, 15.0, -25.0));
        Assert.Equal(0.5, pair.Compressor.ReductionLevel);

        time.Advance(TimeSpan.FromSeconds(1));
        expander.CheckLevels();
        Assert.True(expander.HasLevels);
        time.Advance(TimeSpan.FromSeconds(1));
        expander.CheckLevels();
        Assert.False(expander.HasLevels);
        Assert.Equal(0, expander.History.Count);
        Assert.Equal(0, expander.InputLevel);
        Assert.Equal(Localizer.Text("DynamicsNoLevels"), expander.Status);

        _ = expander.KeepLevelsAsync();
        Assert.Equal(1, sent.Watches);
    });
}
