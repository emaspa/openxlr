using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class FlowGraphTests
{
    [Theory]
    [InlineData(1280, 1150, 2560, 1440, 1, 1280, 1150)]
    [InlineData(1280, 1800, 1920, 1080, 1, 1280, 1040)]
    [InlineData(1280, 1800, 2560, 1440, 2, 1264, 680)]
    [InlineData(1280, 1800, 800, 480, 1, 784, 440)]
    public void InitialSizeFitsGraphWithinScaledWorkAreaAndWindowFrame(
        double width, double height, int screenWidth, int screenHeight, double scaling,
        double expectedWidth, double expectedHeight)
    {
        var size = FlowWindow.FitClientSize(new(width, height), new(screenWidth, screenHeight), scaling, new(16, 40));
        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
    }

    [Fact]
    public async Task OutputsFollowSelectedFeedsAndCustomVirtualMicrophones()
    {
        await using var client = new DaemonClient();
        var vm = new MainViewModel(client);
        vm.Mixes.Add(new(client, "monitor", "Monitor A"));
        vm.Mixes.Add(new(client, "monitor2", "Monitor B"));
        vm.Mixes.Add(new(client, "podcast", "Podcast") { Kind = "virtualMic" });
        var output = new MonitorOutputItem("headset", "Headset", () => { }, (_, _) => { });
        output.Sync(true);
        output.SyncFeed([new("monitor2", "Monitor B")], "monitor2");
        vm.MonitorOutputs.Add(output);
        FlowGraph graph = FlowGraph.From(vm);
        Assert.Contains(graph.Routes, r => r.From == "mix:monitor2" && r.To == "out:headset");
        Assert.DoesNotContain(graph.Routes, r => r.From == "mix:monitor" && r.To == "out:headset");
        Assert.Contains(graph.Nodes, n => n.Key == "vm:podcast" && n.Label == "OpenXLR Podcast");
        Assert.DoesNotContain(graph.Nodes, n => n.Key is "vm:stream" or "vm:chat");

        output.SyncFeed([new("monitor+monitor2", "Monitor A+B")], "monitor+monitor2");
        graph = FlowGraph.From(vm);
        Assert.Equal(2, graph.Routes.Count(r => r.To == "out:headset"));
        output.Sync(false);
        Assert.DoesNotContain(FlowGraph.From(vm).Nodes, n => n.Key == "out:headset");
    }

    [Fact]
    public void InputTraceDoesNotSpreadToOtherInputsSharingItsMix()
    {
        FlowRoute[] routes =
        [
            new("app:music", "ch:music", FlowStage.Input, true),
            new("app:game", "ch:game", FlowStage.Input, true),
            new("ch:music", "mix:monitor", FlowStage.Channel, true),
            new("ch:game", "mix:monitor", FlowStage.Channel, true),
            new("mix:monitor", "out:headset", FlowStage.Mix, true),
            new("mix:other", "out:headset", FlowStage.Mix, true),
        ];
        var graph = new FlowGraph([], routes);
        Assert.Equal([routes[0], routes[2], routes[4]], routes.Where(graph.Trace("app:music").Contains));
        Assert.Equal(routes, routes.Where(graph.Trace("out:headset").Contains));
        Assert.DoesNotContain(routes[5], graph.Trace("mix:monitor"));
        Assert.Empty(graph.Trace("removed"));
    }

    [Fact]
    public async Task MutedAndZeroLevelRoutesHaveDifferentRepresentations()
    {
        await using var client = new DaemonClient();
        var vm = new MainViewModel(client);
        var channel = new ChannelViewModel(client, "music", "Music", ["monitor", "stream", "auxout"]);
        channel.Sends[0].ApplyFromDaemon(0.8, true);
        channel.Sends[1].ApplyFromDaemon(0, false);
        channel.Sends[2].ApplyFromDaemon(1, false);
        vm.Channels.Add(channel);
        var monitor = new MixViewModel(client, "monitor", "Monitor A");
        monitor.ApplyFromDaemon(System.Text.Json.Nodes.JsonNode.Parse("""{"muted":true}""")!);
        vm.Mixes.Add(monitor);
        vm.Mixes.Add(new(client, "stream", "Stream") { Kind = "virtualMic" });
        var aux = new MixViewModel(client, "auxout", "Aux");
        aux.ApplyAuxPort(false);
        vm.Mixes.Add(aux);
        var output = new MonitorOutputItem("headset", "Headset", () => { }, (_, _) => { });
        output.Sync(true);
        vm.MonitorOutputs.Add(output);
        FlowGraph graph = FlowGraph.From(vm);
        Assert.Contains(graph.Routes, r => r.From == "ch:music" && r.To == "mix:monitor" && !r.Active);
        Assert.DoesNotContain(graph.Routes, r => r.From == "ch:music" && r.To == "mix:stream");
        Assert.All(graph.Routes.Where(r => r.Stage == FlowStage.Mix && r.To != "vm:stream"), r => Assert.False(r.Active));
        Assert.Contains(graph.Nodes, n => n.Key == "aux:port" && n.Detail == "Off" && !n.Active);
    }

    [Fact]
    public async Task MultipleAppsAndUnmanagedAppsDoNotOverlapOrInventRoutes()
    {
        await using var client = new DaemonClient();
        var vm = new MainViewModel(client);
        vm.Channels.Add(new(client, "music", "Music", []));
        vm.Channels.Add(new(client, "game", "Game", []));
        for (int i = 0; i < 12; i++)
        {
            var app = new AppStreamViewModel(client, $"app{i}", $"Player {i}", []);
            app.ApplyFromDaemon(i == 11 ? AppStreamViewModel.Ignore : "music", i != 0, true);
            vm.ActiveApps.Add(app);
        }
        FlowGraph graph = FlowGraph.From(vm);
        foreach (var column in graph.Nodes.GroupBy(n => n.Stage))
        {
            var nodes = column.OrderBy(n => n.Y).ToList();
            for (int i = 1; i < nodes.Count; i++) Assert.True(nodes[i].Y >= nodes[i - 1].Y + nodes[i - 1].Height);
        }
        Assert.Contains(graph.Nodes, n => n.Key == "app:app11" && n.Detail == "Desktop routing");
        Assert.DoesNotContain(graph.Routes, r => r.From == "app:app11");
        Assert.Contains(graph.Routes, r => r.From == "app:app0" && !r.Active);
    }

    [Fact]
    public async Task ProcessingKeepsSignalOrderAndFitsInsideFourColumns()
    {
        await using var client = new DaemonClient();
        var vm = new MainViewModel(client);
        vm.Channels.Add(new(client, "xlr1", "Microphone", ["stream"]) { IsHardware = true });
        vm.Channels.Add(new(client, "xlr2", "Second microphone", ["stream"]) { IsHardware = true });
        vm.Mixes.Add(new(client, "stream", "Stream") { Kind = "virtualMic" });
        vm.Inserts.Apply(System.Text.Json.Nodes.JsonNode.Parse("""
            [{"insert":{"id":"eq","plugin":"urn:eq","label":"EQ"}},
             {"insert":{"id":"comp","plugin":"urn:comp","label":"Compressor","bypass":true}},
             {"insert":{"id":"gate","plugin":"urn:gate","label":"Gate"},"error":"unavailable"}]
            """));
        FlowGraph graph = FlowGraph.From(vm);
        FlowNode channel = Assert.Single(graph.Nodes, n => n.Key == "ch:xlr1");
        Assert.Equal("EQ\nCompressor (bypassed)\nGate (problem)", channel.Processing);
        Assert.True(graph.Nodes.Single(n => n.Key == "ch:xlr2").Y >= channel.Y + channel.Height);
        Assert.All(graph.Nodes, n => Assert.InRange((int)n.Stage, 0, 3));
        Assert.Contains(graph.Routes, r => r.From == "hw:xlr1" && r.To == "ch:xlr1");
    }

    [Fact]
    public async Task EmptyStateHasNoPhantomOutputs()
    {
        await using var client = new DaemonClient();
        FlowGraph graph = FlowGraph.From(new MainViewModel(client));
        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Routes);
    }
}
