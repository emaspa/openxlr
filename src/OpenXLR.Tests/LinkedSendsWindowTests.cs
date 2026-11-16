using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The link chip of each channel's sends, drawn in every layout that draws
/// sends: after the volume fader of each classic tile, where a send row
/// has its mute and above the first send row; after the volume fader in
/// each mix matrix row head, before the row's cells; and with the mute of
/// each mix bank cell. It is lit while the channel is linked, named for a
/// screen reader, and absent against a daemon that reports no link. Run
/// from the window layout tests, on their UI thread, with preferences of
/// its own.
/// </summary>
internal static class LinkedSendsWindowTests
{
    private const string Mixes = """
        "mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"stream","name":"Stream","kind":"virtualMic"},
                 {"id":"chat","name":"Chat","kind":"virtualMic"}]
        """;

    /// <summary>A microphone and two applications, Game linked, from a daemon that reports the link, or one that does not.</summary>
    private static string Fixture(bool reportsLink = true)
    {
        string Link(bool on) => reportsLink ? $",\"linkedSends\":{(on ? "true" : "false")}" : "";
        return "{" + Mixes + $$$"""
            ,"channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"volume":0.9,"levels":{"monitor":1,"stream":1,"chat":1}{{{Link(false)}}}},
                         {"id":"game","name":"Game","volume":1,"levels":{"monitor":0.5,"stream":0.5,"chat":0.5}{{{Link(true)}}}},
                         {"id":"music","name":"Music","volume":0.8,"levels":{"monitor":0.8,"stream":0.3,"chat":0}{{{Link(false)}}}}],
             "inserts":{}}
            """;
    }

    internal static void Check()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-linked-sends-").FullName;
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        SkinEntry skin = SkinService.Current;
        bool touch = SkinService.TouchControls;
        MainWindow? window = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
            window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            var model = (MainViewModel)window.DataContext!;
            Apply(model, Fixture());
            window.Show();
            Wear(SkinPackage.DefaultId);
            foreach (bool enlarged in new[] { false, true })
            {
                SkinService.ApplyControlSizing(enlarged);
                foreach (double width in new[] { 640d, 1040 })
                {
                    Layout(window, width, 900);
                    string where = $"{(enlarged ? "touch" : "standard")} at {width}";
                    CheckTiles(window, model, where);
                    Assert.Equal(3, ChannelTileAlignmentWindowTests.AssertSendRowsLineUp(window, where).Length);
                    if (width == 1040) Capture(window, $"linked-sends-tiles-{(enlarged ? "touch" : "standard")}-1040");
                }
            }
            SkinService.ApplyControlSizing(false);
            Layout(window, 1040, 900);

            // A move of one linked send shows on the others before the daemon answers.
            ChannelViewModel game = model.Channels.Single(c => c.Id == "game");
            game.Sends.Single(s => s.MixId == "stream").Level = 0.25;
            Assert.All(game.Sends, s => Assert.Equal(0.25, s.Level, 6));
            ChannelViewModel music = model.Channels.Single(c => c.Id == "music");
            music.Sends.Single(s => s.MixId == "stream").Level = 0.6;
            Assert.Equal((0.8, 0.6), (music.Sends.Single(s => s.MixId == "monitor").Level, music.Sends.Single(s => s.MixId == "stream").Level));

            // A click lights the chip; the daemon's next state decides.
            ToggleButton musicChip = Chip(Tile(window, "music"), "ChannelLinkSends");
            Assert.False(musicChip.IsChecked);
            musicChip.IsChecked = true;
            Assert.True(music.LinkedSends);
            Apply(model, Fixture());
            Layout(window, 1040, 900);
            Assert.False(Chip(Tile(window, "music"), "ChannelLinkSends").IsChecked);

            // An older daemon reports no link: no chip, and the rows stay level.
            Apply(model, Fixture(reportsLink: false));
            Layout(window, 1040, 900);
            Assert.All(model.Channels, c => Assert.False(c.HasLinkedSends));
            foreach (ChannelViewModel channel in model.Channels)
                Assert.False(Chip(Tile(window, channel.Id), "ChannelLinkSends").IsEffectivelyVisible);
            Assert.Equal(3, ChannelTileAlignmentWindowTests.AssertSendRowsLineUp(window, "against an older daemon").Length);
            Apply(model, Fixture());

            Wear("broadcast");
            foreach (double width in new[] { 640d, 1040, 2560 })
            {
                Layout(window, width, 1400);
                CheckMatrix(window, model, $"broadcast at {width}");
            }
            Capture(window, "linked-sends-broadcast-1040");

            Wear("hifi");
            foreach (double width in new[] { 1040d, 2560 })
            {
                Layout(window, width, 1400);
                CheckBanks(window, model, $"hifi at {width}");
            }
            Capture(window, "linked-sends-hifi-2560");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            SkinService.ApplyControlSizing(touch);
            SkinService.Apply(skin);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Each tile's chip: after its volume fader on the same line, inside the tile, above the first send row.</summary>
    private static void CheckTiles(MainWindow window, MainViewModel model, string where)
    {
        foreach (ChannelViewModel channel in model.Channels)
        {
            string what = $"{channel.Name} {where}";
            Border tile = Tile(window, channel.Id);
            ToggleButton chip = Chip(tile, "ChannelLinkSends");
            StackPanel volumeRow = tile.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "ChannelVolume");
            Slider volume = volumeRow.GetVisualDescendants().OfType<Slider>().Single();
            CheckChip(chip, channel, tile, what);
            Point chipAt = chip.TranslatePoint(default, tile)!.Value;
            Point faderAt = volume.TranslatePoint(default, tile)!.Value;
            Assert.True(chipAt.X >= faderAt.X + volume.Bounds.Width, $"{what}: the chip is not after the volume fader");
            Assert.True(chipAt.Y < faderAt.Y + volume.Bounds.Height && faderAt.Y < chipAt.Y + chip.Bounds.Height,
                $"{what}: the chip is not on the volume fader's line");
            StackPanel first = tile.GetVisualDescendants().OfType<StackPanel>()
                .Where(p => p.DataContext is SendViewModel && p.IsEffectivelyVisible)
                .OrderBy(p => p.TranslatePoint(default, tile)!.Value.Y).First();
            Assert.True(chipAt.Y + chip.Bounds.Height <= first.TranslatePoint(default, tile)!.Value.Y + 0.5,
                $"{what}: the chip runs into the first send row");
        }
    }

    /// <summary>Each row head's chip: after its volume fader, inside the head, and the head's levels and cells as the matrix keeps them.</summary>
    private static void CheckMatrix(MainWindow window, MainViewModel model, string where)
    {
        MixMatrixViewModel matrix = Assert.IsType<MixMatrixViewModel>(window.MixMatrix);
        foreach (MatrixRowViewModel row in matrix.Rows)
        {
            string what = $"{row.Channel.Name} {where}";
            Border head = SkinLayoutWindowTests.RowHead(window, row.Channel.Id);
            ToggleButton chip = Chip(head, "MatrixLinkSends");
            CheckChip(chip, row.Channel, head, what);
            Slider volume = head.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "MatrixVolume");
            Assert.True(chip.TranslatePoint(default, head)!.Value.X >= volume.TranslatePoint(default, head)!.Value.X + volume.Bounds.Width,
                $"{what}: the chip is not after the volume fader");
            SkinLayoutWindowTests.CheckLevels(head, row, where);
        }
        SkinLayoutWindowTests.CheckMatrixCells(window, matrix, where);
        Assert.Contains(model.Channels, c => c.LinkedSends);
    }

    /// <summary>Each bank cell's chip, with the send's mute.</summary>
    private static void CheckBanks(MainWindow window, MainViewModel model, string where)
    {
        Border[] cells = [.. window.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "BankCell" && b.IsEffectivelyVisible)];
        Assert.NotEmpty(cells);
        foreach (Border cell in cells)
        {
            var bank = (BankCellViewModel)cell.DataContext!;
            CheckChip(Chip(cell, "BankLinkSends"), bank.Channel, cell, $"{bank.Channel.Name} {where}");
        }
        Assert.Contains(cells, c => ((BankCellViewModel)c.DataContext!).Channel.Id == "game");
    }

    private static void CheckChip(ToggleButton chip, ChannelViewModel channel, Control holder, string what)
    {
        Assert.True(chip.IsEffectivelyVisible, what);
        AssertInside(chip, holder);
        Assert.Equal(channel.LinkedSends, chip.IsChecked);
        Assert.Equal(channel.Id == "game", chip.IsChecked);
        Assert.Contains("linkChip", chip.Classes);
        Assert.Equal(Localizer.Text("LinkSends"), chip.Content);
        Assert.Equal(Localizer.Text("LinkSendsTip"), ToolTip.GetTip(chip));
        Assert.Equal(Localizer.Format("LinkSendsName", channel.Name), Avalonia.Automation.AutomationProperties.GetName(chip));
    }

    private static ToggleButton Chip(Control holder, string name)
        => holder.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == name);

    private static Border Tile(TopLevel root, string id) => root.GetVisualDescendants().OfType<Border>()
        .Single(b => b.DataContext is ChannelViewModel { Id: var c } && c == id && b.Classes.Contains("tile"));

    private static void Apply(MainViewModel model, string state)
        => typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(model, [JsonNode.Parse(state)]);

    private static void Wear(string id)
    {
        SkinEntry entry = id == SkinPackage.DefaultId ? new SkinEntry(SkinPackage.Default, []) : SkinCatalog.Find(id)!;
        Assert.Empty(entry.Errors);
        Assert.Empty(SkinService.Apply(entry));
        Dispatcher.UIThread.RunJobs();
    }
}
