using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// The mix matrix as a projection, with no window: its cells hand out the
/// channels' own sends and its columns the mixer's own mixes, rows and
/// columns follow every change to the channels and mixes, a channel with no
/// send into a mix has an empty cell, the compact and mini views narrow it,
/// a write through a cell reaches that send and nothing else, nothing it
/// does is a layout command, and each column's outputs line counts the
/// monitor outputs that hear the mix.
///
/// The window's model reads ui.json when it is made, from the configuration
/// directory the tests of that collection redirect for the whole process,
/// so these run with them rather than beside them.
/// </summary>
[Collection("xdg-config")]
public sealed class MixMatrixTests
{
    private static readonly MethodInfo ApplyMixer =
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>A state push: channels and mixes by id, each named after its id unless a name is given.</summary>
    private static void Apply(MainViewModel model, string[] channels, string[] mixes, Dictionary<string, string>? names = null)
    {
        JsonNode Node(string id) => new JsonObject { ["id"] = id, ["name"] = names?.GetValueOrDefault(id) ?? id };
        ApplyMixer.Invoke(model, [new JsonObject
        {
            ["channels"] = new JsonArray([.. channels.Select(Node)]),
            ["mixes"] = new JsonArray([.. mixes.Select(Node)]),
        }]);
    }

    private static void AssertCellsAreTheSends(MixMatrixViewModel matrix)
    {
        foreach (MatrixRowViewModel row in matrix.Rows)
        {
            Assert.Equal(matrix.Columns, row.Cells.Select(c => c.Column));
            foreach (MatrixCellViewModel cell in row.Cells)
            {
                Assert.Same(row.Channel, cell.Channel);
                Assert.Same(cell.Channel.Sends.SingleOrDefault(s => s.MixId == cell.Mix.Id), cell.Send);
            }
        }
    }

    [Fact]
    public void AnApplicationRowsGainIsItsChannelVolumeOnceTheDaemonReportsOne()
    {
        var model = new MainViewModel(new DaemonClient());
        string State(string volume) => $$"""
            {"channels":[{"id":"xlr1","name":"XLR 1","hardware":true{{volume}}},{"id":"game","name":"Game"{{volume}}}],
             "mixes":[{"id":"monitor","name":"Monitor A"}]}
            """;
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(""))]);
        using var matrix = new MixMatrixViewModel(model);
        using var rack = new InputRackViewModel(model);
        MatrixRowViewModel row = matrix.Rows.Single(r => r.Channel.Id == "game");
        InputStripViewModel strip = rack.Strips.Single(s => s.Channel.Id == "game");
        // A daemon without channel volumes: the name and the meter only.
        Assert.False(row.Channel.HasVolume);
        Assert.DoesNotContain(SkinStripParts.Gain, row.Spec.Parts);
        Assert.Contains(SkinStripParts.Meter, row.Spec.Parts);
        Assert.DoesNotContain(SkinStripParts.Gain, strip.Spec.Parts);

        // Once it reports one, the volume is the row's gain with the meter under it.
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(",\"volume\":0.4"))]);
        Assert.True(row.Channel.HasVolume);
        Assert.Equal(0.4, row.Channel.Volume);
        Assert.Contains(SkinStripParts.Gain, row.Spec.Parts);
        Assert.DoesNotContain(SkinStripParts.Meter, row.Spec.Parts);
        Assert.Contains(SkinStripParts.Gain, strip.Spec.Parts);
        Assert.False(row.Stacked);
        // XLR 1 takes one too.
        Assert.True(model.Channels.Single(c => c.Id == "xlr1").HasVolume);
    }

    [Fact]
    public void AHardwareRowStacksItsGainOverItsVolumeAndABoundOneKeepsTheVolume()
    {
        var model = new MainViewModel(new DaemonClient());
        string State(string xlr1, string aux) => $$"""
            {"channels":[{"id":"xlr1","name":"XLR 1","hardware":true{{xlr1}}},{"id":"aux","name":"Aux In","hardware":true{{aux}}},
                         {"id":"game","name":"Game","volume":1}],
             "mixes":[{"id":"monitor","name":"Monitor A"}]}
            """;
        // A daemon that gave volumes to application channels only: XLR 1's
        // head is the gain alone, as it always was.
        ApplyMixer.Invoke(model, [JsonNode.Parse(State("", ""))]);
        model.RefreshBindings();
        using var matrix = new MixMatrixViewModel(model);
        MatrixRowViewModel Row(string id) => matrix.Rows.Single(r => r.Channel.Id == id);
        Assert.True(Row("xlr1").CarriesUnit);
        Assert.True(Row("xlr1").HasGainFader);
        Assert.False(Row("xlr1").ShowsVolume);
        Assert.False(Row("xlr1").Stacked);
        Assert.Contains(SkinStripParts.Gain, Row("xlr1").Spec.Parts);

        // With a volume, the gain stands over it: two faders in the gain
        // part, the meter under the gain only.
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(",\"volume\":0.7", ",\"volume\":0.9"))]);
        model.RefreshBindings();
        Assert.True(Row("xlr1").ShowsVolume);
        Assert.True(Row("xlr1").Stacked);
        Assert.False(Row("xlr1").VolumeShowsMeter);
        Assert.DoesNotContain(SkinStripParts.Meter, Row("xlr1").Spec.Parts);
        Assert.Equal(0.7, Row("xlr1").Channel.Volume);
        // Aux In stacks its level over its volume where the interface has the
        // stage, and keeps the volume, with the meter under it, where it has not.
        model.CapAuxInput = true;
        Assert.True(Row("aux").Stacked);
        model.CapAuxInput = false;
        Assert.False(Row("aux").HasGainFader);
        Assert.False(Row("aux").Stacked);
        Assert.True(Row("aux").VolumeShowsMeter);
        Assert.Contains(SkinStripParts.Gain, Row("aux").Spec.Parts);

        // Bound to another source, XLR 1 has no gain: the volume alone, with
        // the meter under it, where an application row has its own.
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(",\"volume\":0.7,\"binding\":{\"kind\":\"source\",\"source\":\"alsa_input.webcam\"}", ",\"volume\":0.9"))]);
        model.RefreshBindings();
        Assert.False(Row("xlr1").CarriesUnit);
        Assert.False(Row("xlr1").Stacked);
        Assert.True(Row("xlr1").VolumeShowsMeter);
        Assert.Contains(SkinStripParts.Gain, Row("xlr1").Spec.Parts);
        Assert.Equal(0.7, Row("xlr1").Channel.Volume);
    }

    [Fact]
    public void AMicrophoneRowReadsItsGainAndItsVolumeWhileTheVolumeFaderMoves()
    {
        var model = new MainViewModel(new DaemonClient());
        string State(string xlr1) => $$"""
            {"channels":[{"id":"xlr1","name":"XLR 1","hardware":true{{xlr1}}},{"id":"game","name":"Game","volume":1}],
             "mixes":[{"id":"monitor","name":"Monitor A"}]}
            """;
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(",\"volume\":0.7"))]);
        model.RefreshBindings();
        using var matrix = new MixMatrixViewModel(model);
        MatrixRowViewModel xlr1 = matrix.Rows.Single(r => r.Channel.Id == "xlr1");
        var raised = new List<string?>();
        xlr1.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // The gain, always, and the row tall enough for the button and the readout.
        Assert.True(xlr1.ShowsReadout);
        Assert.True(xlr1.Tall);
        Assert.Equal(model.GainText, xlr1.Readout);
        Assert.Equal(model.UnitInput1.GainName, xlr1.ReadoutName);
        model.GainDb = 31;
        Assert.Contains(nameof(MatrixRowViewModel.Readout), raised);
        Assert.Equal("31 dB", xlr1.Readout);

        // The volume while its fader moves, following it, then the gain again.
        xlr1.VolumeAdjusting = true;
        Assert.Equal("70%", xlr1.Readout);
        Assert.Equal(Localizer.Format("ChannelVolumeName", "XLR 1"), xlr1.ReadoutName);
        raised.Clear();
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(",\"volume\":0.5"))]);
        Assert.Contains(nameof(MatrixRowViewModel.Readout), raised);
        Assert.Equal("50%", xlr1.Readout);
        xlr1.VolumeAdjusting = false;
        Assert.Equal("31 dB", xlr1.Readout);

        // Rows without the interface's gain have no readout.
        Assert.False(matrix.Rows.Single(r => r.Channel.Id == "game").ShowsReadout);
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(",\"volume\":0.5,\"binding\":{\"kind\":\"source\",\"source\":\"alsa_input.webcam\"}"))]);
        model.RefreshBindings();
        Assert.False(xlr1.ShowsReadout);
        Assert.False(xlr1.Tall);
    }

    [Fact]
    public void CellsHoldTheChannelsOwnSendsAndColumnsTheMixersOwnMixes()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game", "music", "chat"], ["monitor", "stream", "chat"]);
        using var matrix = new MixMatrixViewModel(model);
        Assert.Equal(model.Channels, matrix.Rows.Select(r => r.Channel));
        Assert.Equal(model.Mixes, matrix.Columns.Select(c => c.Mix));
        AssertCellsAreTheSends(matrix);
        foreach (MatrixRowViewModel row in matrix.Rows)
            foreach (MatrixCellViewModel cell in row.Cells)
            {
                Assert.NotNull(cell.Send);
                Assert.False(cell.IsEmpty);
                Assert.Contains(cell.Send, cell.Channel.Sends);
            }
        // No send was made for the projection, and nothing was added to the mixer.
        Assert.All(model.Channels, c => Assert.Equal(model.Mixes.Count, c.Sends.Count));
        Assert.Equal(3, model.Channels.Count);
        Assert.Equal(3, model.Mixes.Count);
    }

    [Fact]
    public void RowsFollowChannelsAddedRemovedRenamedAndReordered()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game", "music"], ["monitor", "stream"]);
        using var matrix = new MixMatrixViewModel(model);
        int shapes = 0;
        matrix.ShapeChanged += () => shapes++;
        MatrixRowViewModel gameRow = matrix.Rows[0];
        MatrixCellViewModel gameCell = gameRow.Cells[1];
        SendViewModel gameSend = gameCell.Send!;
        var raised = new List<string?>();
        gameCell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Apply(model, ["music", "game", "voice"], ["monitor", "stream"], new() { ["game"] = "Games" });
        Assert.Equal(["music", "game", "voice"], matrix.Rows.Select(r => r.Channel.Id));
        AssertCellsAreTheSends(matrix);
        // The game row is the same row, moved, with the same cell and send, and its names followed the rename.
        Assert.Same(gameRow, matrix.Rows[1]);
        Assert.Same(gameCell, gameRow.Cells[1]);
        Assert.Same(gameSend, gameCell.Send);
        Assert.Contains(nameof(MatrixCellViewModel.SendName), raised);
        Assert.Contains("Games", gameCell.SendName, StringComparison.Ordinal);
        Assert.Contains("Games", gameCell.MuteName, StringComparison.Ordinal);
        Assert.Contains("Games", gameCell.LeftLevelName, StringComparison.Ordinal);
        Assert.DoesNotContain("stream", gameCell.LeftLevelName, StringComparison.Ordinal);
        // A row more is a shape the window has to measure again.
        Assert.True(shapes > 0);
        int before = shapes;

        Apply(model, ["voice"], ["monitor", "stream"]);
        Assert.Equal(["voice"], matrix.Rows.Select(r => r.Channel.Id));
        AssertCellsAreTheSends(matrix);
        Assert.True(shapes > before);
    }

    [Fact]
    public void ColumnsFollowMixesAddedRemovedRenamedAndReordered()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game", "music"], ["monitor", "stream"]);
        using var matrix = new MixMatrixViewModel(model);
        MatrixColumnViewModel stream = matrix.Columns[1];
        MatrixCellViewModel cell = matrix.Rows[0].Cells[1];

        Apply(model, ["game", "music"], ["stream", "chat", "monitor"], new() { ["stream"] = "Broadcast" });
        Assert.Equal(["stream", "chat", "monitor"], matrix.Columns.Select(c => c.Mix.Id));
        Assert.Same(stream, matrix.Columns[0]);
        Assert.Same(cell, matrix.Rows[0].Cells[0]);
        Assert.Equal("Broadcast", stream.Mix.Name);
        Assert.Contains("Broadcast", cell.SendName, StringComparison.Ordinal);
        Assert.Contains("Broadcast", stream.MasterName, StringComparison.Ordinal);
        AssertCellsAreTheSends(matrix);
        // The new mix's cells hold the sends the channels have into it.
        Assert.All(matrix.Rows, row => Assert.NotNull(row.Cells[1].Send));

        Apply(model, ["game", "music"], ["monitor"]);
        Assert.Equal(["monitor"], matrix.Columns.Select(c => c.Mix.Id));
        Assert.All(matrix.Rows, row => Assert.Single(row.Cells));
        AssertCellsAreTheSends(matrix);
    }

    [Fact]
    public void AChannelWithoutASendIntoAMixHasAnEmptyCell()
    {
        var client = new DaemonClient();
        var model = new MainViewModel(client);
        foreach (string id in new[] { "monitor", "stream" }) model.Mixes.Add(new MixViewModel(client, id, id));
        model.Channels.Add(new ChannelViewModel(client, "game", "game", ["monitor", "stream"]));
        model.Channels.Add(new ChannelViewModel(client, "mic", "mic", ["monitor"]));
        using var matrix = new MixMatrixViewModel(model);
        MatrixCellViewModel empty = matrix.Rows[1].Cells[1];
        Assert.Null(empty.Send);
        Assert.True(empty.IsEmpty);
        Assert.False(empty.HasSend);
        Assert.False(empty.Tinted);
        Assert.NotNull(matrix.Rows[1].Cells[0].Send);

        // The send arrives: the same cell takes it.
        var raised = new List<string?>();
        empty.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        model.Channels[1].SyncSends(["monitor", "stream"]);
        Assert.Same(model.Channels[1].Sends.Single(s => s.MixId == "stream"), empty.Send);
        Assert.False(empty.IsEmpty);
        Assert.Contains(nameof(MatrixCellViewModel.IsEmpty), raised);
        // A send the view does not draw counts as none.
        empty.Send!.DisplayVisible = false;
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public async Task TheCompactViewShowsOneRowAndTheMiniViewOneRowAndOneColumn()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-matrix-").FullName;
        string? old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            var model = new MainViewModel(client);
            Apply(model, ["game", "music", "chat"], ["monitor", "stream", "chat"]);
            using var matrix = new MixMatrixViewModel(model);
            MatrixRowViewModel music = matrix.Rows[1];
            Assert.Equal(3, matrix.Rows.Count);

            model.SelectedCompactChannel = model.Channels.Single(c => c.Id == "music");
            Assert.Null(model.ChooseMixerView(MainViewModel.CompactView));
            Assert.Same(music, Assert.Single(matrix.Rows));
            Assert.Equal(["monitor", "stream", "chat"], matrix.Columns.Select(c => c.Mix.Id));
            AssertCellsAreTheSends(matrix);

            model.SelectedCompactMix = model.Mixes.Single(m => m.Id == "stream");
            Assert.Null(model.ChooseMixerView(MainViewModel.MiniView));
            Assert.Same(music, Assert.Single(matrix.Rows));
            Assert.Equal("stream", Assert.Single(matrix.Columns).Mix.Id);
            MatrixCellViewModel cell = Assert.Single(music.Cells);
            Assert.Same(model.Channels.Single(c => c.Id == "music").Sends.Single(s => s.MixId == "stream"), cell.Send);

            Assert.Null(model.ChooseMixerView(MainViewModel.FullView));
            Assert.Equal(3, matrix.Rows.Count);
            Assert.Same(music, matrix.Rows[1]);
            Assert.Equal(3, matrix.Columns.Count);
            // A hidden channel has no row in the full view.
            model.Channels.Single(c => c.Id == "chat").DisplayVisible = false;
            Assert.Equal(["game", "music"], matrix.Rows.Select(r => r.Channel.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RowHeadsAreTheInputsPanelsControlsForTheKindOfChannel()
    {
        var model = new MainViewModel(new DaemonClient());
        ApplyMixer.Invoke(model, [JsonNode.Parse("""
            {"mixes":[{"id":"monitor","name":"Monitor"}],
             "channels":[{"id":"xlr1","name":"XLR 1","hardware":true},{"id":"xlr2","name":"XLR 2","hardware":true},
                         {"id":"aux","name":"Aux In","hardware":true},{"id":"game","name":"Game"},
                         {"id":"cam","name":"Camera","captureSource":"alsa_input.cam"}]}
            """)]);
        model.HasXlr2 = false;
        using var matrix = new MixMatrixViewModel(model);
        Assert.Equal(["xlr1", "aux", "game", "cam"], matrix.Rows.Select(r => r.Channel.Id));
        model.HasXlr2 = true;
        Assert.Equal(["xlr1", "xlr2", "aux", "game", "cam"], matrix.Rows.Select(r => r.Channel.Id));
        MatrixRowViewModel Row(string id) => matrix.Rows.Single(r => r.Channel.Id == id);
        // The meter runs under the gain fader, so it is not a part of its own beside it.
        Assert.Equal(["gain", "mute", "effects"], Row("xlr1").Spec.Parts);
        Assert.Same(model.Inserts, Row("xlr1").Inserts);
        Assert.Same(model.Inserts2, Row("xlr2").Inserts);
        Assert.Equal("xlr2", Row("xlr2").InsertsKey);
        Assert.Equal(["gain"], Row("aux").Spec.Parts);
        Assert.Null(Row("aux").Inserts);
        Assert.Equal(["meter"], Row("game").Spec.Parts);
        Assert.Equal(["meter"], Row("cam").Spec.Parts);
        Assert.Equal(InputStripKind.Capture, Row("cam").Kind);
        Assert.Equal("xlr1", StripTemplateSelector.Key(Row("xlr1")));
        Assert.Equal("aux", StripTemplateSelector.Key(Row("aux")));
        Assert.Equal("channel", StripTemplateSelector.Key(Row("cam")));
        // Hardware inputs keep their place; an application channel carries a handle until the rack does.
        Assert.Equal([true, true, true, false, false], matrix.Rows.Select(r => r.HandleHidden));
        matrix.OwnsHandles = false;
        Assert.All(matrix.Rows, r => Assert.True(r.HandleHidden));

        // A skin's order and its hidden parts, and the meter as a part of its own when it is not under the faders.
        matrix.Configure(SkinMatrixOptions.Default with
        {
            RowHead = new SkinMatrixPartOptions(["effects", "meter", "mute", "gain", "identity"], ["mute"], SkinLabelStyle.Icon),
            Meter = SkinMatrixMeter.None,
        });
        Assert.Equal(["effects", "meter", "gain"], Row("xlr1").Spec.Parts);
        Assert.Equal(Avalonia.Controls.Dock.Right, Row("xlr1").IdentityDock);
        Assert.False(Row("xlr1").MeterUnderFader);
        Assert.False(Row("xlr1").Spec.WantsName);
    }

    [Fact]
    public void ColumnHeadsSplitTheirLinesFromTheirControlsAndCountTheOutputsThatHearTheMix()
    {
        var model = new MainViewModel(new DaemonClient());
        ApplyMixer.Invoke(model, [JsonNode.Parse("""
            {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"monitor2","name":"Monitor B","kind":"monitor"},
                      {"id":"stream","name":"Stream","kind":"virtualMic"},{"id":"auxout","name":"Aux","kind":"auxPort"}],
             "channels":[{"id":"game","name":"Game"}]}
            """)]);
        using var matrix = new MixMatrixViewModel(model);
        MatrixColumnViewModel Column(string id) => matrix.Columns.Single(c => c.Mix.Id == id);
        Assert.Equal(["identity", "outputs"], Column("monitor").TextSpec.Parts);
        Assert.Equal(["master", "mute", "effects"], Column("monitor").ControlSpec.Parts);
        Assert.Equal(Avalonia.Controls.Dock.Bottom, Column("monitor").ControlsDock);
        Assert.Equal([true, true, false, true], matrix.Columns.Select(c => c.HandleHidden));
        Assert.Equal(Localizer.Text("MatrixNoOutput"), Column("monitor").OutputsText);
        Assert.Equal(Localizer.Text("VirtualMicrophone"), Column("stream").OutputsText);
        Assert.Equal(Localizer.Text("UsbAuxPortKind"), Column("auxout").OutputsText);

        MixOption[] feeds = [new("monitor", "Monitor A"), new("monitor2", "Monitor B")];
        MonitorOutputItem Output(string name, string feed, bool selected)
        {
            var item = new MonitorOutputItem(name, name, () => { }, (_, _) => Task.FromResult<string?>(null));
            item.SyncFeed(feeds, feed);
            item.Sync(selected);
            return item;
        }
        var raised = new List<string?>();
        Column("monitor").PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        model.MonitorOutputs.Add(Output("speakers", "monitor", true));
        Assert.Equal(1, Column("monitor").Outputs);
        Assert.Equal(Localizer.Text("MatrixOneOutput"), Column("monitor").OutputsText);
        Assert.Contains(nameof(MatrixColumnViewModel.OutputsText), raised);
        MonitorOutputItem headset = Output("headset", "monitor+monitor2", true);
        model.MonitorOutputs.Add(headset);
        model.MonitorOutputs.Add(Output("hdmi", "monitor", false));
        Assert.Equal(2, Column("monitor").Outputs);
        Assert.Equal(Localizer.Format("MatrixOutputs", 2), Column("monitor").OutputsText);
        Assert.Equal(1, Column("monitor2").Outputs);
        Assert.Equal(0, Column("stream").Outputs);
        // An output that stops hearing the mix is counted out at once.
        headset.Sync(false);
        Assert.Equal(1, Column("monitor").Outputs);
        Assert.Equal(Localizer.Text("MatrixNoOutput"), Column("monitor2").OutputsText);
        headset.SyncFeed(feeds, "monitor2");
        headset.Sync(true);
        Assert.Equal(1, Column("monitor").Outputs);
        Assert.Equal(1, Column("monitor2").Outputs);
        // A selected output whose device is missing hears nothing.
        var missing = new MonitorOutputItem("gone", "gone", () => { }, (_, _) => Task.FromResult<string?>(null), isPresent: false);
        missing.SyncFeed(feeds, "monitor");
        missing.Sync(true);
        model.MonitorOutputs.Add(missing);
        Assert.Equal(1, Column("monitor").Outputs);

        // Controls first in the skin's order put the controls row above the lines.
        matrix.Configure(SkinMatrixOptions.Default with
        {
            ColumnHead = new SkinMatrixPartOptions(["mute", "identity", "master", "effects", "outputs"], ["effects"], SkinLabelStyle.Text),
        });
        Assert.Equal(Avalonia.Controls.Dock.Top, Column("monitor").ControlsDock);
        Assert.Equal(["mute", "master"], Column("monitor").ControlSpec.Parts);
    }

    [Fact]
    public void AMutedCellIsTintedOrDimmedAsTheSkinSays()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game"], ["monitor"]);
        using var matrix = new MixMatrixViewModel(model);
        MatrixCellViewModel cell = matrix.Rows[0].Cells[0];
        Assert.False(cell.Tinted);
        cell.Send!.ApplyFromDaemon(cell.Send.Level, muted: true);
        Assert.True(cell.Tinted);
        Assert.False(cell.Dimmed);
        matrix.Configure(SkinMatrixOptions.Default with { Muted = SkinMatrixMuted.Dim });
        MatrixCellViewModel dimmed = matrix.Rows[0].Cells[0];
        Assert.Same(cell.Send, dimmed.Send);
        Assert.False(dimmed.Tinted);
        Assert.True(dimmed.Dimmed);
        Assert.Equal(["mute", "fader", "value"], dimmed.Spec.Parts);
    }

    [Fact]
    public void ACellDrawsItsValueBesideOrOverTheFaderAsTheSkinSays()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game"], ["monitor"]);
        using var matrix = new MixMatrixViewModel(model);
        MatrixCellViewModel beside = matrix.Rows[0].Cells[0];
        Assert.Equal(["mute", "fader", "value"], beside.Spec.Parts);
        Assert.False(beside.ValueOverFader);
        // Over the fader, the value is no part beside it.
        matrix.Configure(SkinMatrixOptions.Default with { Value = SkinMatrixValue.OverFader });
        MatrixCellViewModel over = matrix.Rows[0].Cells[0];
        Assert.Equal(["mute", "fader"], over.Spec.Parts);
        Assert.True(over.ValueOverFader);
        // A hidden value is drawn in neither place.
        SkinMatrixPartOptions hidden = SkinMatrixOptions.Default.Cell with { Hidden = ["value"] };
        matrix.Configure(SkinMatrixOptions.Default with { Cell = hidden, Value = SkinMatrixValue.OverFader });
        MatrixCellViewModel none = matrix.Rows[0].Cells[0];
        Assert.Equal(["mute", "fader"], none.Spec.Parts);
        Assert.False(none.ValueOverFader);
    }

    [Fact]
    public void ADisposedMatrixStopsFollowing()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game"], ["monitor", "stream"]);
        var matrix = new MixMatrixViewModel(model);
        matrix.Dispose();
        Apply(model, ["game", "music"], ["monitor", "stream", "chat"]);
        Assert.Empty(matrix.Rows);
        Assert.Empty(matrix.Columns);
    }

    /// <summary>
    /// The matrix over a connected client: a write through a cell is the
    /// send's own command for that channel and mix, and nothing the matrix
    /// does, following channels and mixes or taking new options, sends
    /// anything else, a layout order least of all.
    /// </summary>
    /// <summary>A full state push: the interface, its capabilities and the mixer, as the daemon sends them.</summary>
    private static void ApplyState(MainViewModel model, JsonNode state) =>
        typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [state]);

    /// <summary>A Wave XLR Pro's state with other capabilities and mixer flags in their place.</summary>
    private static JsonNode State(string channels, string? capabilities = null, string mixerExtra = "")
    {
        JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState(channels, mixerExtra: mixerExtra))!;
        if (capabilities is not null) state["capabilities"] = JsonNode.Parse(capabilities);
        return state;
    }

    [Fact]
    public async Task ARowsMicrophoneSwitchesFollowTheInputItCarriesAndWhatTheInterfaceHas()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        string pro = $"{InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1))},"
            + $"{InputBindingModelTests.Xlr("xlr2", 1, InputBindingModelTests.Input(2))},{{\"id\":\"game\",\"name\":\"Game\"}},"
            + "{\"id\":\"cam\",\"name\":\"Camera\",\"captureSource\":\"cam\"}";
        ApplyState(model, State(pro));
        using var matrix = new MixMatrixViewModel(model);
        MatrixRowViewModel Row(string id) => matrix.Rows.Single(r => r.Channel.Id == id);

        // A Wave XLR Pro: both XLR rows have the part, each for its own input; nothing else has it.
        Assert.Equal(["xlr1", "xlr2"], matrix.Rows.Where(r => r.ShowsProcessing).Select(r => r.Channel.Id));
        Assert.Same(model.UnitInput1, Row("xlr1").Unit);
        Assert.Same(model.UnitInput2, Row("xlr2").Unit);
        Assert.True(Row("xlr1").Has(SkinMatrixParts.Processing));
        // It stands in the name column, not among the source controls.
        Assert.DoesNotContain(SkinMatrixParts.Processing, Row("xlr1").Spec.Parts);
        Assert.True(Row("xlr1").HasPhantom);
        Assert.False(Row("xlr1").ShowsSoftware);

        // Lit by its own input's switches only, phantom power on its own, both said as they change.
        var raised = new List<string?>();
        Row("xlr2").PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(Row("xlr2").ProcessingOn);
        model.Compressor2 = true;
        Assert.True(Row("xlr2").ProcessingOn);
        Assert.False(Row("xlr1").ProcessingOn);
        Assert.Contains(nameof(MatrixRowViewModel.ProcessingOn), raised);
        model.Compressor2 = false;
        model.Phantom2 = true;
        Assert.False(Row("xlr2").ProcessingOn);
        Assert.True(Row("xlr2").PhantomOn);
        Assert.False(Row("xlr1").PhantomOn);
        Assert.Equal(Localizer.Format("MatrixProcessingPhantomName", "XLR 2"), Row("xlr2").ProcessingName);
        model.Phantom2 = false;
        model.PhantomSettling2 = true;
        Assert.True(Row("xlr2").PhantomOn);
        model.PhantomSettling2 = false;
        Assert.Equal(Localizer.Format("MatrixProcessingName", "XLR 2"), Row("xlr2").ProcessingName);
        // A switch the interface does not have does not light it.
        model.CapCompressor = false;
        model.Compressor2 = true;
        Assert.False(Row("xlr2").ProcessingOn);
        model.Compressor2 = false;

        // The first Wave XLR: phantom power only, with XLR 1's software low
        // cut and ClipGuard. The part stays for them; lit by the low cut.
        const string Phantom = """{"xlrInputs":1,"hpOutputs":1,"phantom":true,"physicalControls":true}""";
        const string Software = ",\"softLowCutApplicable\":true,\"softClipGuardApplicable\":true,\"lowCutHz\":80";
        string one = InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1));
        ApplyState(model, State(one, Phantom, Software));
        Assert.Equal(["xlr1"], matrix.Rows.Where(r => r.ShowsProcessing).Select(r => r.Channel.Id));
        Assert.True(Row("xlr1").ShowsSoftware);
        Assert.True(Row("xlr1").HasPhantom);
        Assert.True(Row("xlr1").ProcessingOn);
        // A USB microphone with neither: the software alone keeps it.
        ApplyState(model, State(one, """{"xlrInputs":1,"hpOutputs":1,"xlrJacks":false}""", Software.Replace("80", "0")));
        Assert.True(Row("xlr1").ShowsProcessing);
        Assert.False(Row("xlr1").HasPhantom);
        Assert.False(Row("xlr1").ProcessingOn);
        // Nothing to switch, the daemon saying no software processing applies: no part.
        ApplyState(model, State(one, """{"xlrInputs":1,"hpOutputs":1,"xlrJacks":false}""",
            ",\"softLowCutApplicable\":false,\"softClipGuardApplicable\":false"));
        Assert.False(Row("xlr1").ShowsProcessing);

        // XLR 1 bound to a webcam and the Pro's microphone on a capture channel:
        // the capture channel has the part, for input 1; XLR 1 keeps it only for its software.
        ApplyState(model, State($"{InputBindingModelTests.BoundXlr1(true)},{InputBindingModelTests.ProMic(InputBindingModelTests.Input(1))}"));
        Assert.Null(Row("xlr1").Unit);
        Assert.Equal(InputStripKind.Capture, Row("promic").Kind);
        Assert.Same(model.UnitInput1, Row("promic").Unit);
        Assert.True(Row("promic").ShowsProcessing);
        Assert.False(Row("promic").ShowsSoftware);
        Assert.Equal(model.ShowSoftwareControls, Row("xlr1").ShowsProcessing);
        model.LowCut = true;
        Assert.True(Row("promic").ProcessingOn);
        model.LowCut = false;

        // A skin that hides the part leaves it out everywhere.
        matrix.Configure(SkinMatrixOptions.Default with
        {
            RowHead = new SkinMatrixPartOptions(SkinMatrixParts.RowHead, [SkinMatrixParts.Processing], SkinLabelStyle.Text),
        });
        Assert.DoesNotContain(matrix.Rows, r => r.ShowsProcessing);

        // A row the matrix no longer draws stops following the input.
        MatrixRowViewModel gone = Row("promic");
        raised.Clear();
        gone.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        matrix.Dispose();
        model.LowCut = true;
        Assert.Empty(raised);
    }

    [Fact]
    public async Task AWriteThroughACellIsTheSendsOwnAndTheMatrixSendsNoCommand()
    {
        var commands = new ConcurrentQueue<string>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]?.GetValue<string>() != "auth")
                    commands.Enqueue($"{command["cmd"]}:{command["channel"]}:{command["mix"]}");
                if (command["requestId"] is { } requestId)
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = requestId.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var model = new MainViewModel(new DaemonClient());
        foreach (string id in new[] { "monitor", "stream", "chat" }) model.Mixes.Add(new MixViewModel(client, id, id));
        foreach (string id in new[] { "game", "music" })
            model.Channels.Add(new ChannelViewModel(client, id, id, [.. model.Mixes.Select(m => m.Id)]));
        using var matrix = new MixMatrixViewModel(model);
        matrix.Configure(SkinMatrixOptions.Default with { Muted = SkinMatrixMuted.Dim, Meter = SkinMatrixMeter.None });
        model.Channels.Move(0, 1);
        model.Mixes.Move(2, 0);
        model.Mixes[1].DisplayVisible = false;
        model.Mixes[1].DisplayVisible = true;
        matrix.OwnsHandles = false;

        MatrixCellViewModel cell = matrix.Rows.Single(r => r.Channel.Id == "game").Cells.Single(c => c.Mix.Id == "stream");
        SendViewModel send = model.Channels.Single(c => c.Id == "game").Sends.Single(s => s.MixId == "stream");
        Assert.Same(send, cell.Send);
        cell.Send!.Muted = true;
        Assert.True(send.Muted);
        Assert.All(model.Channels.SelectMany(c => c.Sends).Where(s => !ReferenceEquals(s, send)), s => Assert.False(s.Muted));
        Assert.All(model.Mixes, m => Assert.False(m.Muted));

        // A layout edit waits for its answer, so everything sent before it has arrived.
        Assert.Null(await client.DeleteExclusiveGroupAsync("barrier").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["setChannelMuted:game:stream", "deleteExclusiveGroup::"], commands.ToArray());
    }
}
