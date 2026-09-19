using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

public sealed class TuiInsertTests
{
    private const string StateJson = """
        {"type":"state","connected":true,"capabilities":{"xlrInputs":1},
         "mixer":{"mixes":[{"id":"stream","name":"Stream"}],"inserts":{
          "xlr1":[{"insert":{"id":"eq","kind":"lv2","plugin":"urn:eq","label":"Saved EQ",
            "nativeHost":true,"params":{"enabled":0.75,"mode":7,"voices":5.6,"gain":6.125,"freq":200,"old":99}},
            "nativeUiBlocked":true,"nativeUiBlockReason":"Editor input is unreliable"}]}}}
        """;

    private const string PluginsJson = """
        {"type":"plugins","plugins":[
          {"kind":"lv2","plugin":"urn:eq","name":"Studio EQ","nativeUiBlocked":false,"params":[
            {"symbol":"enabled","name":"Enabled","min":0,"max":1,"default":1,"toggled":true},
            {"symbol":"mode","name":"Mode","min":-3,"max":42,"default":-3,"enumeration":true,
             "scalePoints":[{"label":"Clean","value":-3},{"label":"Warm","value":7},{"label":"Bright","value":42}]},
            {"symbol":"voices","name":"Voices","min":1,"max":12,"default":3,"integer":true},
            {"symbol":"gain","name":"Gain","min":-24,"max":24,"default":0},
            {"symbol":"freq","name":"Frequency","min":20,"max":20000,"default":1000,"logarithmic":true},
            {"symbol":"missing","name":"Unsaved","min":0,"max":10,"default":1.5}]},
          {"kind":"clap","plugin":"urn:eq","name":"Same id, other format","params":[
            {"symbol":"wrong","name":"Not in a chain","min":0,"max":1,"default":0}]},
          {"kind":"lv2","plugin":"urn:unused","name":"Unused","params":[]}]}
        """;

    private sealed class Session : IAsyncDisposable
    {
        public DaemonLink Link { get; } = new();
        public App App { get; }
        public List<string> Sent { get; } = [];
        public JsonNode State { get; } = JsonNode.Parse(StateJson)!;
        public JsonNode Catalogue { get; } = JsonNode.Parse(PluginsJson)!;
        public Screen Screen { get; } = new(150, 42) { TrueColor = true };
        public JsonNode Entry => State["mixer"]!["inserts"]!["xlr1"]![0]!;
        public JsonNode Plugin => Catalogue["plugins"]![0]!;

        public Session(bool blocked = true, bool catalogue = true)
        {
            Entry["nativeUiBlocked"] = blocked;
            Link.Sent += Sent.Add;
            Echo();
            App = new(Link, Theme.Material);
            App.ShowTab(5);
            Draw();
            Assert.Equal("listPlugins", Command()["cmd"]!.GetValue<string>());
            Sent.Clear();
            if (catalogue) ReceiveCatalogue();
        }

        public void Echo() => Link.Receive(State.ToJsonString());
        public void ReceiveCatalogue() => Link.Receive(Catalogue.ToJsonString());
        public void Key(Key key) => App.Handle(new KeyPress(key));
        public void Key(char key) => App.Handle(new KeyPress(OpenXLR.Tui.Key.Char, key));
        public void Edit() { Key(OpenXLR.Tui.Key.Down); Key('e'); Draw(); }
        public string Draw()
        {
            App.Draw(Screen);
            return string.Join('\n', Enumerable.Range(0, Screen.Height).Select(y =>
                new string(Enumerable.Range(0, Screen.Width).Select(x => Screen.At(x, y).Ch).ToArray())));
        }
        public JsonNode Command() => JsonNode.Parse(Assert.Single(Sent))!;
        public void Reply(JsonNode command, string? error) => Link.Receive(new JsonObject
        {
            ["type"] = "commandResult", ["requestId"] = command["requestId"]!.GetValue<string>(), ["error"] = error,
        }.ToJsonString());
        public ValueTask DisposeAsync() => Link.DisposeAsync();
    }

    private static void Parameter(Session session, string symbol, double value, string channel = "xlr1")
    {
        JsonNode command = session.Command();
        Assert.Equal("setInsertParam", command["cmd"]!.GetValue<string>());
        Assert.Equal(channel, command["channel"]!.GetValue<string>());
        Assert.Equal("eq", command["insertId"]!.GetValue<string>());
        Assert.Equal(symbol, command["symbol"]!.GetValue<string>());
        Assert.Equal(value, command["value"]!.GetValue<double>(), 8);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ABlockedEditorShowsTheCatalogueControlsAndReason(bool stateBlocked, bool catalogueBlocked)
    {
        await using Session session = new(stateBlocked);
        session.Plugin["nativeUiBlocked"] = catalogueBlocked;
        session.Plugin["nativeUiBlockReason"] = "Editor input is unreliable";
        session.ReceiveCatalogue();
        session.Edit();
        string frame = session.Draw();
        Assert.Empty(session.Sent);
        Assert.Contains("STUDIO EQ / XLR 1", frame, StringComparison.Ordinal);
        Assert.Contains("Editor input is unreliable", frame, StringComparison.Ordinal);
        Assert.Contains("Warm", frame, StringComparison.Ordinal);
        Assert.Contains("6.125", frame, StringComparison.Ordinal);
        Assert.Contains("1.500", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("Not in a chain", frame, StringComparison.Ordinal);
        string[] names = ["Enabled", "Mode", "Voices", "Gain", "Frequency", "Unsaved"];
        int last = -1;
        foreach (string name in names)
        {
            int at = frame.IndexOf(name, StringComparison.Ordinal);
            Assert.True(at > last);
            last = at;
        }
        Assert.Single(session.Link.Plugins);
        session.Key(' ');
        Parameter(session, "enabled", 0);
    }

    [Theory]
    [InlineData(1, '+', "mode", 42)]
    [InlineData(2, '+', "voices", 7)]
    [InlineData(2, ']', "voices", 7)]
    [InlineData(3, '+', "gain", 8.525)]
    [InlineData(3, '[', "gain", 5.645)]
    [InlineData(5, '+', "missing", 2)]
    public async Task EachParameterKindSendsItsOwnSymbolAndValue(int row, char key, string symbol, double value)
    {
        await using Session session = new();
        session.Edit();
        for (int i = 0; i < row; i++) session.Key(Key.Down);
        if (row == 1) session.Key(Key.Right); else session.Key(key);
        Parameter(session, symbol, value);
    }

    [Theory]
    [InlineData('+', 0.05)]
    [InlineData(']', 0.01)]
    [InlineData('-', -0.05)]
    [InlineData('[', -0.01)]
    public async Task LogarithmicMovementUsesARatioAcrossThePositiveRange(char key, double fraction)
    {
        await using Session session = new();
        session.Edit();
        for (int i = 0; i < 4; i++) session.Key(Key.Down);
        session.Key(key);
        Parameter(session, "freq", 200 * Math.Pow(1000, fraction));
    }

    [Theory]
    [InlineData((int)Key.Home, 20)]
    [InlineData((int)Key.End, 20000)]
    public async Task LogarithmicLimitsAreTheDeclaredBounds(int key, double value)
    {
        await using Session session = new();
        session.Edit();
        for (int i = 0; i < 4; i++) session.Key(Key.Down);
        session.Key((Key)key);
        Parameter(session, "freq", value);
    }

    [Fact]
    public async Task DefaultsSendsEveryDeclaredDefaultAndNoStaleSymbols()
    {
        await using Session session = new();
        session.Edit();
        session.Key('r');
        JsonNode[] commands = session.Sent.Select(json => JsonNode.Parse(json)!).ToArray();
        Assert.Equal(6, commands.Length);
        foreach ((string symbol, double value) in new Dictionary<string, double>
            { ["enabled"] = 1, ["mode"] = -3, ["voices"] = 3, ["gain"] = 0, ["freq"] = 1000, ["missing"] = 1.5 })
        {
            JsonNode command = Assert.Single(commands, command => command["symbol"]!.GetValue<string>() == symbol);
            Assert.Equal("setInsertParam", command["cmd"]!.GetValue<string>());
            Assert.Equal("xlr1", command["channel"]!.GetValue<string>());
            Assert.Equal("eq", command["insertId"]!.GetValue<string>());
            Assert.Equal(value, command["value"]!.GetValue<double>());
        }
    }

    [Fact]
    public async Task RulesRefreshAnOpenControlsScreenAndAllowRetryingTheNativeEditor()
    {
        await using Session session = new();
        session.Edit();
        session.Link.Receive("""{"type":"nativeEditorRulesChanged"}""");
        Assert.Equal("listPlugins", session.Command()["cmd"]!.GetValue<string>());
        session.Entry["nativeUiBlocked"] = false;
        session.Echo();
        session.ReceiveCatalogue();
        Assert.Contains("Native editor is allowed", session.Draw(), StringComparison.Ordinal);
        session.Sent.Clear();
        session.Key('e');
        JsonNode open = session.Command();
        Assert.Equal("showInsertUi", open["cmd"]!.GetValue<string>());
        session.Reply(open, null);
        Assert.DoesNotContain("Generated controls", session.Draw(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 100, 0, ']', 1)]
    [InlineData(20, 20000, 19999, '+', 20000)]
    [InlineData(20, 20000, 20, '-', 20)]
    [InlineData(20, 20, 20, '+', 20)]
    public async Task LogarithmicControlsRespectBoundsAndUseLinearStepsWithoutAPositiveRange(
        double min, double max, double current, char key, double expected)
    {
        await using Session session = new();
        session.Plugin["params"]![4]!["min"] = min;
        session.Plugin["params"]![4]!["max"] = max;
        session.Entry["insert"]!["params"]!["freq"] = current;
        session.Echo();
        session.ReceiveCatalogue();
        session.Edit();
        for (int i = 0; i < 4; i++) session.Key(Key.Down);
        session.Key(key);
        Parameter(session, "freq", expected);
    }

    [Fact]
    public async Task NativeOpenUsesACorrelatedReplyAndOnlyItsOwnRefusalOpensControls()
    {
        await using Session session = new(blocked: false);
        session.Edit();
        JsonNode open = session.Command();
        Assert.Equal("showInsertUi", open["cmd"]!.GetValue<string>());
        Assert.Equal("xlr1", open["channel"]!.GetValue<string>());
        Assert.Equal("eq", open["insertId"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(open["requestId"]!.GetValue<string>()));
        session.Link.Receive("""{"type":"error","message":"An unrelated command failed"}""");
        session.Link.Receive("""{"type":"commandResult","requestId":"unrelated","error":"Other failure"}""");
        Assert.DoesNotContain("Generated controls", session.Draw(), StringComparison.Ordinal);
        session.Reply(open, "Native host is not running");
        string frame = session.Draw();
        Assert.Contains("Generated controls", frame, StringComparison.Ordinal);
        Assert.Contains("Native host is not running", frame, StringComparison.Ordinal);
        session.Sent.Clear();
        session.Key(' ');
        Parameter(session, "enabled", 0);
    }

    [Fact]
    public async Task ASuccessfulNativeOpenKeepsTheChainAndEscapeCancelsAPendingFallback()
    {
        await using Session session = new(blocked: false);
        session.Edit();
        session.Reply(session.Command(), null);
        Assert.DoesNotContain("Generated controls", session.Draw(), StringComparison.Ordinal);
        session.Sent.Clear();
        session.Key('e');
        JsonNode open = session.Command();
        session.Key(Key.Escape);
        session.Reply(open, "The editor failed later");
        Assert.DoesNotContain("Generated controls", session.Draw(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlsWaitForTheCatalogueAndDistinguishEmptyFromMissingPlugins()
    {
        await using Session session = new(catalogue: false);
        session.Edit();
        Assert.Contains("Waiting for the plugin catalogue", session.Draw(), StringComparison.Ordinal);
        session.Plugin["params"] = new JsonArray();
        session.ReceiveCatalogue();
        Assert.Contains("No declared controls", session.Draw(), StringComparison.Ordinal);
        session.Key('r');
        Assert.Empty(session.Sent);
        session.Catalogue["plugins"] = new JsonArray();
        session.ReceiveCatalogue();
        Assert.Contains("Plugin not found in the catalogue", session.Draw(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorIsVisibleAndTheDaemonEchoSuppliesTheDisplayedValue()
    {
        await using Session session = new();
        session.Edit();
        for (int i = 0; i < 3; i++) session.Key(Key.Down);
        session.Key('+');
        session.Link.Receive("""{"type":"error","message":"setInsertParam: value refused"}""");
        session.Entry["insert"]!["params"]!["gain"] = -2.25;
        session.Echo();
        string frame = session.Draw();
        Assert.Contains("setInsertParam: value refused", frame, StringComparison.Ordinal);
        Assert.Contains("-2.250", frame, StringComparison.Ordinal);
        session.Sent.Clear();
        session.Key('+');
        Parameter(session, "gain", 0.15);
    }

    [Fact]
    public async Task CatalogueRequestsFollowChainsAndRulesRatherThanFramesOrParameterEchoes()
    {
        await using Session session = new();
        for (int i = 0; i < 20; i++) session.Draw();
        session.Entry["insert"]!["params"]!["gain"] = 3;
        session.Entry["insert"]!["bypass"] = true;
        session.Echo();
        session.Link.Receive("""{"type":"meters","levels":{"ch:xlr1":[0.1,0.1]}}""");
        session.App.ShowTab(0);
        session.App.ShowTab(5);
        session.Draw();
        Assert.Empty(session.Sent);
        session.Link.Receive("""{"type":"nativeEditorRulesChanged"}""");
        Assert.Equal("listPlugins", session.Command()["cmd"]!.GetValue<string>());
        session.Sent.Clear();
        session.Entry["nativeUiBlocked"] = false;
        session.Echo();
        session.ReceiveCatalogue();
        session.Edit();
        Assert.Equal("showInsertUi", session.Command()["cmd"]!.GetValue<string>());
        session.Reply(session.Command(), null);
        session.Sent.Clear();
        JsonNode second = session.Entry.DeepClone();
        second["insert"]!["id"] = "second";
        ((JsonArray)session.State["mixer"]!["inserts"]!["xlr1"]!).Add(second);
        session.Echo();
        Assert.Equal("listPlugins", session.Command()["cmd"]!.GetValue<string>());
        session.Sent.Clear();
        session.State["mixer"]!["inserts"]!["xlr1"] = new JsonArray();
        session.Echo();
        Assert.Equal("listPlugins", session.Command()["cmd"]!.GetValue<string>());
        Assert.Empty(session.Link.Plugins);
    }

    [Fact]
    public async Task OtherSectionsDoNotRequestTheCatalogueBeforeInsertsIsShown()
    {
        await using DaemonLink link = new();
        List<string> sent = [];
        link.Sent += sent.Add;
        link.Receive(StateJson);
        link.Receive("""{"type":"nativeEditorRulesChanged"}""");
        App app = new(link, Theme.Material);
        app.Draw(new Screen(80, 24));
        Assert.Empty(sent);
        app.ShowTab(5);
        app.Draw(new Screen(80, 24));
        Assert.Single(sent);
    }

    [Fact]
    public async Task ReconnectingWithTheSameChainsRefetchesControlsAfterOfflineFrames()
    {
        await using Session session = new();
        session.Edit();
        session.Link.EndSession();
        Assert.Empty(session.Link.Plugins);
        for (int i = 0; i < 10; i++) session.Draw();
        Assert.Empty(session.Sent);
        session.Echo();
        Assert.Equal("listPlugins", session.Command()["cmd"]!.GetValue<string>());
        session.ReceiveCatalogue();
        Assert.Contains("Frequency", session.Draw(), StringComparison.Ordinal);
        session.Sent.Clear();
        session.Draw();
        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task AMixEditKeepsItsIdentityThroughReorderAndClosesWhenTheInsertIsReplaced()
    {
        await using Session session = new();
        JsonNode mixInsert = session.Entry.DeepClone();
        session.State["mixer"]!["inserts"]!["mix:stream"] = new JsonArray(mixInsert);
        session.Echo();
        session.ReceiveCatalogue();
        session.Sent.Clear();
        session.Key(Key.Right);
        session.Edit();
        Assert.Contains("STUDIO EQ / STREAM", session.Draw(), StringComparison.Ordinal);
        session.Key(' ');
        Parameter(session, "enabled", 0, "mix:stream");
        session.Sent.Clear();
        JsonNode first = mixInsert.DeepClone();
        first["insert"]!["id"] = "first";
        ((JsonArray)session.State["mixer"]!["inserts"]!["mix:stream"]!).Insert(0, first);
        session.Echo();
        session.Draw();
        session.Sent.Clear();
        session.Key(' ');
        Parameter(session, "enabled", 0, "mix:stream");
        mixInsert["insert"]!["plugin"] = "urn:replacement";
        session.Echo();
        Assert.DoesNotContain("Generated controls", session.Draw(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(150, 42)]
    [InlineData(80, 24)]
    public async Task TwoHundredControlsScrollAndOnlyChangedCellsAreWritten(int width, int height)
    {
        await using Session session = new();
        JsonArray controls = [];
        for (int i = 0; i < 200; i++) controls.Add(new JsonObject
        {
            ["symbol"] = $"p{i}", ["name"] = $"Parameter {i:000}", ["min"] = 0, ["max"] = 100, ["default"] = 50,
        });
        session.Plugin["params"] = controls;
        session.ReceiveCatalogue();
        session.Screen.Resize(width, height);
        session.Edit();
        string first = session.Screen.Render();
        session.Draw();
        Assert.Empty(session.Screen.Render());
        session.Entry["insert"]!["params"]!["p0"] = 55;
        session.Echo();
        session.Draw();
        string changed = session.Screen.Render();
        Assert.NotEmpty(changed);
        Assert.True(changed.Length < first.Length / 10, $"Changed {changed.Length}, full frame {first.Length}");
        session.Entry["insert"]!["params"]!["p199"] = 75;
        session.Echo();
        session.Draw();
        Assert.Empty(session.Screen.Render());
        for (int i = 0; i < 199; i++) session.Key(Key.Down);
        string frame = session.Draw();
        Assert.Contains("STUDIO EQ / XLR 1", frame, StringComparison.Ordinal);
        Assert.Contains("Parameter 199", frame, StringComparison.Ordinal);
        Assert.Contains("Esc back", frame, StringComparison.Ordinal);
        Assert.Contains("r defaults", frame, StringComparison.Ordinal);
        Assert.Contains("F1/? help", frame, StringComparison.Ordinal);
        Assert.Contains("q/Ctrl+C quit", frame, StringComparison.Ordinal);
        Assert.Equal(' ', session.Screen.At(width - 1, height - 1).Ch);
        session.Key('+');
        Parameter(session, "p199", 80);
        session.Sent.Clear();
        session.Key('1');
        Assert.Equal("Mixer", session.App.Current.Title);
        Assert.Empty(session.Sent);
        session.Key('6');
        session.Key(Key.Escape);
        Assert.DoesNotContain("Generated controls", session.Draw(), StringComparison.Ordinal);
        session.Key(' ');
        Assert.Equal("setInsertBypass", session.Command()["cmd"]!.GetValue<string>());
    }
}
