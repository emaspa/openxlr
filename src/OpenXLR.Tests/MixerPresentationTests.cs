using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Layout;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// The mix banks and the input rack as projections, with no window: they
/// hand out the mixer's own channel, mix and send objects, follow every
/// change to them, keep two banks off one mix, start from the user's choice
/// or the skin's defaults, and never send a layout command.
///
/// The window's model reads ui.json when it is made, from the configuration
/// directory the tests of that collection redirect for the whole process,
/// so these run with them rather than beside them.
/// </summary>
[Collection("xdg-config")]
public sealed class MixerPresentationTests
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

    private static SkinSubmixerOptions Banks(int count = 2, params string[] defaults) =>
        SkinSubmixerOptions.Default with { Template = SkinLayoutTemplates.MixBanks, VisibleBanks = count, SelectionDefaults = defaults };

    private static MixerPresentationViewModel Presentation(MainViewModel model, SkinSubmixerOptions options, IReadOnlyList<string>? saved = null)
    {
        var presentation = new MixerPresentationViewModel(model);
        presentation.Configure(options, saved);
        return presentation;
    }

    [Fact]
    public void BankCellsHoldTheChannelsOwnSendsAndTheBanksTheMixersOwnMixes()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game", "music", "chat"], ["monitor", "stream", "chat"]);
        using var presentation = Presentation(model, Banks(2, "stream", "monitor"));
        Assert.Equal(["stream", "monitor"], presentation.Selection);
        foreach (MixBankViewModel bank in presentation.Banks)
        {
            Assert.Same(model.Mixes.Single(m => m.Id == bank.Mix!.Id), bank.Mix);
            Assert.Same(bank.Mix, bank.Master!.Mix);
            Assert.Same(presentation.Available, bank.Available);
            Assert.Equal(model.Channels, bank.Cells.Select(c => c.Channel));
            foreach (BankCellViewModel cell in bank.Cells)
                Assert.Same(cell.Channel.Sends.Single(s => s.MixId == bank.Mix!.Id), cell.Send);
        }
        Assert.Equal(model.Mixes, presentation.Available);
        // No send was made for the projection.
        Assert.All(model.Channels, c => Assert.Equal(model.Mixes.Count, c.Sends.Count));
    }

    [Fact]
    public void ChannelsAddedRemovedRenamedAndReorderedReachEveryBank()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game", "music"], ["monitor", "stream"]);
        using var presentation = Presentation(model, Banks());
        BankCellViewModel gameCell = presentation.Banks[0].Cells[0];
        SendViewModel gameSend = gameCell.Send!;
        var raised = new List<string?>();
        gameCell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Apply(model, ["music", "game", "voice"], ["monitor", "stream"], new() { ["game"] = "Games" });
        foreach (MixBankViewModel bank in presentation.Banks)
        {
            Assert.Equal(["music", "game", "voice"], bank.Cells.Select(c => c.Channel.Id));
            foreach (BankCellViewModel cell in bank.Cells)
                Assert.Same(cell.Channel.Sends.Single(s => s.MixId == bank.Mix!.Id), cell.Send);
        }
        // The game cell is the same cell, moved, and its names followed the rename.
        Assert.Same(gameCell, presentation.Banks[0].Cells[1]);
        Assert.Same(gameSend, gameCell.Send);
        Assert.Contains(nameof(BankCellViewModel.SendName), raised);
        Assert.Contains("Games", gameCell.SendName, StringComparison.Ordinal);
        Assert.Contains("Games", gameCell.LeftLevelName, StringComparison.Ordinal);

        Apply(model, ["voice"], ["monitor", "stream"]);
        Assert.All(presentation.Banks, bank => Assert.Equal(["voice"], bank.Cells.Select(c => c.Channel.Id)));
    }

    [Fact]
    public void MixesAddedRenamedAndReorderedKeepEachBanksMix()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game"], ["monitor", "stream"]);
        using var presentation = Presentation(model, Banks());
        MixViewModel stream = presentation.Banks[1].Mix!;
        Apply(model, ["game"], ["stream", "chat", "monitor"], new() { ["stream"] = "Broadcast" });
        Assert.Equal(["monitor", "stream"], presentation.Selection);
        Assert.Same(stream, presentation.Banks[1].Mix);
        Assert.Equal("Broadcast", presentation.Banks[1].Mix!.Name);
        Assert.Equal(["stream", "chat", "monitor"], presentation.Available.Select(m => m.Id));
        Assert.Contains("Broadcast", presentation.Banks[1].Cells[0].SendName, StringComparison.Ordinal);
        // A new mix's send reaches the cells once the channel has it.
        presentation.Banks[0].Mix = model.Mixes.Single(m => m.Id == "chat");
        Assert.Same(model.Channels[0].Sends.Single(s => s.MixId == "chat"), presentation.Banks[0].Cells[0].Send);
    }

    [Fact]
    public void ABankWhoseMixGoesAwayMovesToTheNextDistinctMix()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game"], ["monitor", "monitor2", "stream", "chat"]);
        using var presentation = Presentation(model, Banks(2, "monitor2", "stream"));
        var chosen = new List<IReadOnlyList<string>>();
        presentation.SelectionChosen += chosen.Add;
        Assert.Equal(["monitor2", "stream"], presentation.Selection);

        // Monitor B goes: the next mix in order is Stream, which the other bank shows, so Chat.
        Apply(model, ["game"], ["monitor", "stream", "chat"]);
        Assert.Equal(["chat", "stream"], presentation.Selection);
        Assert.Same(model.Channels[0].Sends.Single(s => s.MixId == "chat"), presentation.Banks[0].Cells[0].Send);

        // From the last mix the search wraps round to the first.
        Apply(model, ["game"], ["monitor", "stream"]);
        Assert.Equal(["monitor", "stream"], presentation.Selection);

        // A mix the view stops showing counts as gone.
        model.Mixes.Single(m => m.Id == "monitor").DisplayVisible = false;
        Assert.Single(presentation.Banks);
        Assert.Equal(["stream"], presentation.Selection);
        Assert.Equal(["stream"], presentation.Available.Select(m => m.Id));
        model.Mixes.Single(m => m.Id == "monitor").DisplayVisible = true;
        Assert.Equal(2, presentation.Banks.Count);
        Assert.Equal(2, presentation.Selection.Distinct().Count());

        // None of that was the user's choice.
        Assert.Empty(chosen);
    }

    [Fact]
    public void TwoBanksNeverShowOneMixAndAPickTradesPlaces()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game"], ["monitor", "stream", "chat"]);
        using var presentation = Presentation(model, Banks(3));
        Assert.Equal(["monitor", "stream", "chat"], presentation.Selection);
        var chosen = new List<IReadOnlyList<string>>();
        presentation.SelectionChosen += chosen.Add;
        presentation.Banks[0].Mix = model.Mixes.Single(m => m.Id == "chat");
        Assert.Equal(["chat", "stream", "monitor"], presentation.Selection);
        Assert.Equal(["chat", "stream", "monitor"], Assert.Single(chosen));
        // Picking what a bank already shows, a mix that is not listed, or nothing changes nothing.
        presentation.Banks[1].Mix = presentation.Banks[1].Mix;
        presentation.Banks[1].Mix = new MixViewModel(new DaemonClient(), "elsewhere", "Elsewhere");
        presentation.Banks[1].Mix = null;
        Assert.Equal(["chat", "stream", "monitor"], presentation.Selection);
        Assert.Single(chosen);
    }

    [Fact]
    public void KeysFollowTheMixesAndAPressTradesPlacesLikeTheDropdown()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game"], ["monitor", "stream", "chat"]);
        using var presentation = Presentation(model, Banks(2) with { Picker = SkinLayoutTemplates.PickerButtons });
        MixBankViewModel first = presentation.Banks[0], second = presentation.Banks[1];
        Assert.True(first.PickerButtons);
        Assert.False(first.PickerDropdown);
        string[] Ids(MixBankViewModel bank) => [.. bank.Choices.Select(c => c.Mix.Id)];
        string[] Lit(MixBankViewModel bank) => [.. bank.Choices.Where(c => c.IsSelected).Select(c => c.Mix.Id)];
        Assert.Equal(["monitor", "stream", "chat"], Ids(first));
        Assert.Equal(["monitor"], Lit(first));
        Assert.Equal(["stream"], Lit(second));
        Assert.All(first.Choices, c => Assert.Same(model.Mixes.Single(m => m.Id == c.Mix.Id), c.Mix));

        var chosen = new List<IReadOnlyList<string>>();
        presentation.SelectionChosen += chosen.Add;
        var raised = new List<string>();
        foreach (BankChoiceViewModel choice in first.Choices.Concat(second.Choices))
            choice.PropertyChanged += (_, e) => raised.Add(choice.Mix.Id + ":" + e.PropertyName);
        first.Choices.Single(c => c.Mix.Id == "stream").Select();
        Assert.Equal(["stream", "monitor"], presentation.Selection);
        Assert.Equal(["stream"], Lit(first));
        Assert.Equal(["monitor"], Lit(second));
        Assert.Equal(["stream", "monitor"], Assert.Single(chosen));
        Assert.Contains("monitor:IsSelected", raised);
        // Pressing the lit key changes nothing and says it is still lit.
        raised.Clear();
        first.Choices.Single(c => c.Mix.Id == "stream").Select();
        Assert.Single(chosen);
        Assert.Contains("stream:IsSelected", raised);

        // The keys follow the mixes: added, reordered and removed.
        Apply(model, ["game"], ["chat", "stream", "monitor", "aux"]);
        Assert.Equal(["chat", "stream", "monitor", "aux"], Ids(first));
        Assert.Equal(Ids(first), Ids(second));
        Apply(model, ["game"], ["chat", "stream"]);
        Assert.Equal(["chat", "stream"], Ids(first));
        Assert.Equal(["stream"], Lit(first));
        Assert.Single(Lit(second));

        // A dropdown skin's banks draw the dropdown.
        presentation.Configure(Banks(2), null);
        Assert.All(presentation.Banks, b => Assert.True(b.PickerDropdown));
    }

    [Fact]
    public void TheBankCountIsClampedToTheMixesThereAre()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game"], ["monitor", "stream"]);
        using var presentation = Presentation(model, Banks(4));
        Assert.Equal(2, presentation.Banks.Count);
        Assert.Equal(2, presentation.BankColumns);
        Assert.Equal(1, presentation.BankRows);
        Apply(model, ["game"], ["monitor"]);
        Assert.Equal(["monitor"], presentation.Selection);
        Apply(model, ["game"], []);
        Assert.Empty(presentation.Banks);
        Assert.Empty(presentation.Available);
        Apply(model, ["game"], ["monitor", "stream", "chat", "aux", "extra"]);
        Assert.Equal(4, presentation.Banks.Count);
        Assert.Equal(4, presentation.Selection.Distinct().Count());
        presentation.Configure(Banks(4) with { Orientation = SkinLayoutOrientation.Vertical }, null);
        Assert.Equal(1, presentation.BankColumns);
        Assert.Equal(4, presentation.BankRows);
        Assert.Equal(Orientation.Vertical, presentation.BankOrientation);
    }

    [Fact]
    public void UnknownDefaultsAreSkippedAndASavedChoiceBeatsTheDefaults()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game"], ["monitor", "stream", "chat"]);
        using (var presentation = Presentation(model, Banks(2, "gone", "chat", "chat", "monitor")))
            Assert.Equal(["chat", "monitor"], presentation.Selection);
        using (var presentation = Presentation(model, Banks(3, "gone")))
            Assert.Equal(["monitor", "stream", "chat"], presentation.Selection);
        using (var presentation = Presentation(model, Banks(2, "chat", "monitor"), ["stream", "gone"]))
        {
            Assert.Equal(["stream", "monitor"], presentation.Selection);
            // The same options and choice again leave the banks alone.
            MixBankViewModel first = presentation.Banks[0];
            presentation.Configure(Banks(2, "chat", "monitor"), ["stream", "gone"]);
            Assert.Same(first, presentation.Banks[0]);
            // Without a saved choice the skin's defaults apply.
            presentation.Configure(Banks(2, "chat", "monitor"), []);
            Assert.Equal(["chat", "monitor"], presentation.Selection);
        }
    }

    [Fact]
    public void WhatTheViewHidesStaysHiddenInTheBanks()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game", "music"], ["monitor", "stream"]);
        using var presentation = Presentation(model, Banks());
        BankCellViewModel music = presentation.Banks[0].Cells[1];
        Assert.True(music.Shown);
        Assert.Equal(2, presentation.VisibleChannels);
        music.Channel.DisplayVisible = false;
        Assert.False(music.Shown);
        Assert.Equal(1, presentation.VisibleChannels);
        music.Channel.DisplayVisible = true;
        music.Send!.DisplayVisible = false;
        Assert.False(music.Shown);
    }

    [Fact]
    public void OnlyTheFirstBankCarriesTheChannelHandlesAndNoneWhileTheRackDoes()
    {
        var model = new MainViewModel(new DaemonClient());
        ApplyMixer.Invoke(model, [JsonNode.Parse("""
            {"mixes":[{"id":"monitor","name":"Monitor"},{"id":"stream","name":"Stream"}],
             "channels":[{"id":"xlr1","name":"XLR 1","hardware":true},{"id":"game","name":"Game"}]}
            """)]);
        using var presentation = Presentation(model, Banks());
        Assert.Equal([true, false], presentation.Banks[0].Cells.Select(c => c.HandleHidden));
        Assert.All(presentation.Banks[1].Cells, c => Assert.True(c.HandleHidden));
        presentation.OwnsHandles = false;
        Assert.All(presentation.Banks.SelectMany(b => b.Cells), c => Assert.True(c.HandleHidden));
    }

    [Fact]
    public void ASkinsStripOptionsBecomeTheCellsAndMastersSpecs()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game"], ["monitor", "stream"]);
        var options = Banks() with
        {
            Strip = SkinStripOptions.BankDefault with { Template = SkinLayoutTemplates.SendList, Hidden = ["meter"] },
            Master = SkinMasterOptions.Default with { Template = SkinLayoutTemplates.MasterStrip },
        };
        using var presentation = Presentation(model, options);
        StripSpec spec = presentation.Banks[0].Cells[0].Spec;
        Assert.Equal(["identity", "fader", "value", "mute"], spec.Parts);
        Assert.Equal(Orientation.Horizontal, spec.Flow);
        Assert.Null(spec.MeterAnchor);
        Assert.Equal(Orientation.Vertical, presentation.CellOrientation);
        Assert.Equal(Avalonia.Controls.Dock.Bottom, presentation.Banks[0].MasterDock);
        Assert.Equal(SkinStripParts.Send, presentation.MasterSpec.Parts);
        Assert.Equal(SkinLayoutTemplates.SendList, StripTemplateSelector.Key(presentation.Banks[0].Cells[0]));
        Assert.Equal(SkinLayoutTemplates.MasterStrip, StripTemplateSelector.Key(presentation.Banks[0].Master));

        presentation.Configure(Banks() with { Master = SkinMasterOptions.Default with { Template = SkinLayoutTemplates.MasterStrip } }, null);
        Assert.Equal(Orientation.Horizontal, presentation.CellOrientation);
        Assert.Equal(Avalonia.Controls.Dock.Right, presentation.Banks[0].MasterDock);
        Assert.Equal(SkinStripParts.Fader, presentation.StripSpec.MeterAnchor);
        Assert.True(presentation.StripSpec.FaderUpright);
    }

    [Fact]
    public void ADisposedProjectionStopsFollowing()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game"], ["monitor", "stream"]);
        var presentation = Presentation(model, Banks());
        var rack = new InputRackViewModel(model);
        presentation.Dispose();
        rack.Dispose();
        Apply(model, ["game", "music"], ["monitor", "stream", "chat"]);
        Assert.Empty(presentation.Banks);
        Assert.Empty(presentation.Available);
        Assert.Empty(rack.Strips);
    }

    /// <summary>
    /// The projection over a connected client: a write through a cell is the
    /// send's own command for that channel and mix, and nothing the banks do,
    /// picks, fallbacks, new options or layout changes, sends anything else.
    /// </summary>
    [Fact]
    public async Task AWriteThroughACellIsTheSendsOwnAndTheBanksSendNoCommand()
    {
        var commands = new ConcurrentQueue<string>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                // The mixes' chains ask for the plugin catalogue when the state
                // arrives; that request is theirs, not the banks'. Whether an
                // earlier test's request was still cached decided it before.
                if (command["cmd"]?.GetValue<string>() == "listPlugins") continue;
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

        // The model itself stays off the connection, so nothing here posts to
        // a UI thread; its channels and mixes write through the connected client.
        var model = new MainViewModel(new DaemonClient());
        foreach (string id in new[] { "monitor", "stream", "chat" }) model.Mixes.Add(new MixViewModel(client, id, id));
        foreach (string id in new[] { "game", "music" })
            model.Channels.Add(new ChannelViewModel(client, id, id, [.. model.Mixes.Select(m => m.Id)]));
        using var presentation = Presentation(model, Banks(2, "stream"));
        using var rack = new InputRackViewModel(model);
        presentation.Banks[0].Mix = model.Mixes.Single(m => m.Id == "chat");
        presentation.Banks[1].Mix = model.Mixes.Single(m => m.Id == "chat");
        Apply(model, ["music", "game", "voice"], ["chat", "monitor"]);
        presentation.Configure(Banks(3), null);
        rack.Configure(SkinStripOptions.InputRowDefault with { Hidden = ["meter"] });
        presentation.OwnsHandles = false;

        BankCellViewModel cell = presentation.Banks[0].Cells.Single(c => c.Channel.Id == "game");
        SendViewModel send = model.Channels.Single(c => c.Id == "game").Sends.Single(s => s.MixId == presentation.Banks[0].Mix!.Id);
        Assert.Same(send, cell.Send);
        cell.Send!.Muted = true;
        Assert.True(send.Muted);
        Assert.All(model.Channels.SelectMany(c => c.Sends).Where(s => !ReferenceEquals(s, send)), s => Assert.False(s.Muted));

        // A layout edit waits for its answer, so everything sent before it has arrived.
        Assert.Null(await client.DeleteExclusiveGroupAsync("barrier").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([$"setChannelMuted:game:{send.MixId}", "deleteExclusiveGroup::"], commands.ToArray());
    }

    [Fact]
    public void TheRackGivesEachChannelTheStripOfWhatItIs()
    {
        var model = new MainViewModel(new DaemonClient());
        ApplyMixer.Invoke(model, [JsonNode.Parse("""
            {"mixes":[{"id":"monitor","name":"Monitor"}],
             "channels":[{"id":"xlr1","name":"XLR 1","hardware":true},{"id":"xlr2","name":"XLR 2","hardware":true},
                         {"id":"aux","name":"Aux In","hardware":true},{"id":"game","name":"Game"},
                         {"id":"mic2","name":"Second mic","captureSource":"alsa_input.usb"}]}
            """)]);
        using var rack = new InputRackViewModel(model);
        Assert.Same(model, rack.Main);
        Assert.Equal(model.Channels, rack.Strips.Select(s => s.Channel));
        Assert.Equal([InputStripKind.Xlr1, InputStripKind.Xlr2, InputStripKind.AuxIn, InputStripKind.Application, InputStripKind.Capture],
            rack.Strips.Select(s => s.Kind));
        InputStripViewModel Strip(string id) => rack.Strips.Single(s => s.Channel.Id == id);

        // The XLR inputs have every part, each with its own chain; the hardware strips bind the window's model.
        Assert.Equal(SkinStripParts.InputRow, Strip("xlr1").Spec.Parts);
        Assert.Same(model.Inserts, Strip("xlr1").Inserts);
        Assert.Equal("xlr1", Strip("xlr1").InsertsKey);
        Assert.Same(model.Inserts2, Strip("xlr2").Inserts);
        Assert.Equal("xlr2", Strip("xlr2").InsertsKey);
        Assert.All(rack.Strips, s => Assert.Same(model, s.Main));
        // Aux In has its level and lock and no chain.
        Assert.Equal(["identity", "meter", "gain", "hardware-controls"], Strip("aux").Spec.Parts);
        Assert.Null(Strip("aux").Inserts);
        // An application or capture channel has only what it has, whatever the skin orders.
        rack.Configure(SkinStripOptions.InputRowDefault with { Order = ["gain", "effects", "hardware-controls", "meter", "identity"] });
        foreach (string id in new[] { "game", "mic2" })
        {
            Assert.Equal(["meter", "identity"], Strip(id).Spec.Parts);
            Assert.Null(Strip(id).Inserts);
            Assert.False(Strip(id).Has(SkinStripParts.Gain));
            Assert.False(Strip(id).Has(SkinStripParts.HardwareControls));
            Assert.False(Strip(id).Has(SkinStripParts.Effects));
        }
        Assert.Equal(["gain", "effects", "hardware-controls", "meter", "identity"], Strip("xlr1").Spec.Parts);
        // A hidden part is gone from every strip; the meter beside the gain falls back where there is no gain.
        rack.Configure(SkinStripOptions.InputRowDefault with
        {
            Hidden = ["effects"], Meter = new SkinMeterOptions(SkinLayoutOrientation.Vertical, SkinMeterPlacement.BesideFader),
        });
        Assert.DoesNotContain(SkinStripParts.Effects, Strip("xlr1").Spec.Parts);
        Assert.Equal(SkinStripParts.Gain, Strip("xlr1").Spec.MeterAnchor);
        Assert.Null(Strip("game").Spec.MeterAnchor);

        // Only channels that can move carry a handle.
        Assert.True(Strip("xlr1").HandleHidden);
        Assert.False(Strip("game").HandleHidden);
    }

    [Fact]
    public void TheRackShowsTheSecondInputOnlyWhereTheInterfaceHasOneAndWhatTheViewShows()
    {
        var model = new MainViewModel(new DaemonClient());
        ApplyMixer.Invoke(model, [JsonNode.Parse("""
            {"mixes":[{"id":"monitor","name":"Monitor"}],
             "channels":[{"id":"xlr1","name":"XLR 1","hardware":true},{"id":"xlr2","name":"XLR 2","hardware":true},{"id":"game","name":"Game"}]}
            """)]);
        using var rack = new InputRackViewModel(model);
        InputStripViewModel xlr2 = rack.Strips[1];
        Assert.True(xlr2.Shown);
        model.HasXlr2 = false;
        Assert.False(xlr2.Shown);
        Assert.True(rack.Strips[0].Shown);
        model.HasXlr2 = true;
        rack.Strips[2].Channel.DisplayVisible = false;
        Assert.False(rack.Strips[2].Shown);
        // A device without the jack marks the channel absent, and the view leaves it out.
        ApplyMixer.Invoke(model, [JsonNode.Parse("""
            {"mixes":[{"id":"monitor","name":"Monitor"}],
             "channels":[{"id":"xlr1","name":"XLR 1","hardware":true},{"id":"xlr2","name":"XLR 2","hardware":true,"present":false},{"id":"game","name":"Game"}]}
            """)]);
        Assert.Same(xlr2, rack.Strips[1]);
        Assert.False(xlr2.Shown);
    }

    [Fact]
    public void TheRackFollowsChannelsAddedRemovedAndReordered()
    {
        var model = new MainViewModel(new DaemonClient());
        Apply(model, ["game", "music"], ["monitor"]);
        using var rack = new InputRackViewModel(model);
        InputStripViewModel game = rack.Strips[0];
        Apply(model, ["music", "voice", "game"], ["monitor"]);
        Assert.Equal(["music", "voice", "game"], rack.Strips.Select(s => s.Channel.Id));
        Assert.Same(game, rack.Strips[2]);
        Apply(model, ["voice"], ["monitor"]);
        Assert.Equal(["voice"], rack.Strips.Select(s => s.Channel.Id));
    }

    [Fact]
    public void AStripSpecKeepsOnlyThePartsAChannelHasAndAnchorsTheMeterWhereItCan()
    {
        SkinStripOptions options = SkinStripOptions.BankDefault with { Hidden = ["fader"] };
        StripSpec spec = StripSpec.From(options, Orientation.Vertical, SkinStripParts.Send, SkinStripParts.Fader);
        Assert.Equal(["identity", "meter", "value", "mute"], spec.Parts);
        Assert.Null(spec.MeterAnchor);
        Assert.Equal(Orientation.Horizontal, spec.MeterPairOrientation);
        StripSpec beside = StripSpec.From(options with { Hidden = [], Meter = new SkinMeterOptions(SkinLayoutOrientation.Horizontal, SkinMeterPlacement.BesideLabel) },
            Orientation.Horizontal, SkinStripParts.Send, SkinStripParts.Fader);
        Assert.Equal(SkinStripParts.Identity, beside.MeterAnchor);
        Assert.Equal(Orientation.Vertical, beside.MeterPairOrientation);
        Assert.True(StripSpec.From(options with { Label = SkinLabelStyle.Icon }, Orientation.Vertical, SkinStripParts.Send, SkinStripParts.Fader) is
            { WantsIcon: true, WantsName: false });
        Assert.True(StripSpec.From(options with { Label = SkinLabelStyle.Text }, Orientation.Vertical, SkinStripParts.Send, SkinStripParts.Fader) is
            { WantsIcon: false, WantsName: true });
        StripSpec reversed = StripSpec.From(SkinMasterOptions.Default with { Fader = new SkinFaderOptions(SkinLayoutOrientation.Horizontal, SkinFaderDirection.Reversed) });
        Assert.True(reversed.FaderReversed);
        Assert.False(reversed.FaderUpright);
    }
}
