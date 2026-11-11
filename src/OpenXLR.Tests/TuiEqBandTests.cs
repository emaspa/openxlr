using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

public sealed class TuiEqBandTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string State(Action<JsonNode>? change = null, string channel = "xlr1")
    {
        JsonNode root = JsonNode.Parse("""
            {"type":"state","connected":true,"capabilities":{"xlrInputs":1},"state":{},
             "mixer":{"channels":[{"id":"xlr1","name":"My microphone","hardware":true,"microphone":true}],
                      "mixes":[],"inserts":{}},"devices":[],"profiles":[]}
            """)!;
        JsonNode input = root["mixer"]!["channels"]![0]!;
        input["id"] = channel;
        input["eq"] = JsonSerializer.SerializeToNode(MicEq.Default, Web);
        input["eq"]!["available"] = true;
        change?.Invoke(root);
        return root.ToJsonString();
    }

    private static JsonNode Input(JsonNode root) => root["mixer"]!["channels"]![0]!;

    private static (App App, List<JsonObject> Sent) Ready(string? state = null)
    {
        DaemonLink link = new();
        List<JsonObject> sent = [];
        link.Sent += json => sent.Add(JsonNode.Parse(json)!.AsObject());
        link.Receive(state ?? State());
        App app = new(link, Theme.Material);
        app.ShowTab(2);
        return (app, sent);
    }

    private static string[] Frame(App app, int width = 160, int height = 60)
    {
        Screen screen = new(width, height);
        app.Draw(screen);
        return Lines(screen);
    }

    private static string[] Lines(Screen screen) => [.. Enumerable.Range(0, screen.Height)
        .Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray()))];

    private static void Select(App app, string label)
        => Select(label, () => Frame(app), app.Handle);

    private static void Select(string label, Func<string[]> draw, Action<KeyPress> handle)
    {
        for (int i = 0; i < 100; i++) handle(new KeyPress(Key.Up));
        for (int i = 0; i < 100; i++)
        {
            if (draw().Any(line => line.IndexOf(label, StringComparison.Ordinal) is int at and >= 2 && line[at - 2] == '┃')) return;
            handle(new KeyPress(Key.Down));
        }
        throw new Xunit.Sdk.XunitException("No selected row " + label);
    }

    private static void Open(App app)
    {
        Select(app, "My microphone bands");
        app.Handle(new KeyPress(Key.Enter));
        Assert.Contains(Frame(app), line => line.Contains("MICROPHONE EQ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("xlr1")]
    [InlineData("xlr2")]
    [InlineData("guest-mic")]
    public void InputsOpensEveryMicrophonesFourBandsAndEscapeReturns(string channel)
    {
        var (app, sent) = Ready(State(channel: channel));
        Open(app);
        for (int i = 1; i <= 4; i++) Assert.Contains(Frame(app), line => line.Contains($"Band {i} Q", StringComparison.Ordinal));
        Select(app, "Band 2 gain");
        app.Handle(new KeyPress(Key.Right));
        JsonObject command = Assert.Single(sent);
        Assert.Equal("setEqBand", command["cmd"]!.GetValue<string>());
        Assert.Equal(channel, command["channel"]!.GetValue<string>());
        Assert.Equal(2, command["band"]!.GetValue<int>());
        Assert.Equal(.25, command["gain"]!.GetValue<double>());
        Assert.Equal(4, command.Count);
        app.Handle(new KeyPress(Key.Escape));
        Assert.Contains(Frame(app), line => line.Contains("My microphone bands", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EachBandSwitchAndTypeUsesOnlyItsOwnPartialCommand(int number)
    {
        var (app, sent) = Ready(); Open(app);
        Select(app, $"Band {number} enabled");
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Equal(number, sent[^1]["band"]!.GetValue<int>());
        Assert.Equal(!MicEq.Default.Bands[number - 1].Enabled, sent[^1]["enabled"]!.GetValue<bool>());
        Assert.Equal(4, sent[^1].Count);
        Select(app, $"Band {number} type");
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal(MicEq.Types[(MicEq.TypeIndex(MicEq.Default.Bands[number - 1].Type) + 1) % MicEq.Types.Count], sent[^1]["type"]!.GetValue<string>());
        Assert.Equal(4, sent[^1].Count);
    }

    [Theory]
    [InlineData("frequency", "1234.6", 1235)]
    [InlineData("gain", "2.13", 2.25)]
    [InlineData("Q", "0.707", -1)]
    public void EnterAcceptsAnExactValueAndUsesTheSharedSnapping(string field, string text, double expected)
    {
        var (app, sent) = Ready(); Open(app);
        Select(app, "Band 2 " + field);
        app.Handle(new KeyPress(Key.Enter));
        Assert.True(app.Prompting);
        foreach (char c in text) app.Handle(new KeyPress(Key.Char, c));
        app.Handle(new KeyPress(Key.Enter));
        JsonObject command = Assert.Single(sent);
        string key = field == "Q" ? "q" : field;
        Assert.Equal(field == "Q" ? MicEq.SnapQ(.707) : expected, command[key]!.GetValue<double>(), 10);
        Assert.Equal(4, command.Count);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("-21")]
    [InlineData("20.1")]
    [InlineData("1,5")]
    [InlineData("oops")]
    public void InvalidExactValuesSendNothing(string text)
    {
        var (app, sent) = Ready(); Open(app); Select(app, "Band 2 gain");
        app.Handle(new KeyPress(Key.Enter));
        foreach (char c in text) app.Handle(new KeyPress(Key.Char, c));
        app.Handle(new KeyPress(Key.Enter));
        Assert.Empty(sent);
        Assert.False(app.Prompting);
    }

    [Fact]
    public void FrequencyFineStepsBoundsAndQGridAreUsableAtTheEnds()
    {
        var (app, sent) = Ready(); Open(app); Select(app, "Band 2 frequency");
        app.Handle(new KeyPress(Key.Char, ']'));
        Assert.Equal(319, sent[^1]["frequency"]!.GetValue<double>());
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal(Math.Round(319 * Math.Pow(2, 1.0 / 6)), sent[^1]["frequency"]!.GetValue<double>());
        app.Handle(new KeyPress(Key.End));
        Assert.Equal(MicEq.MaxFrequency, sent[^1]["frequency"]!.GetValue<double>());
        app.Handle(new KeyPress(Key.Home));
        Assert.Equal(MicEq.MinFrequency, sent[^1]["frequency"]!.GetValue<double>());
        // A twenty-fourth of an octave still moves a whole hertz at the bottom of the range.
        app.Handle(new KeyPress(Key.Char, ']'));
        Assert.Equal(MicEq.MinFrequency + 1, sent[^1]["frequency"]!.GetValue<double>());
        Select(app, "Band 2 Q");
        app.Handle(new KeyPress(Key.Home));
        Assert.Equal(MicEq.MinQ, sent[^1]["q"]!.GetValue<double>(), 10);
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal(Math.Pow(10, -1 + MicEq.QLog10Step), sent[^1]["q"]!.GetValue<double>(), 10);
        app.Handle(new KeyPress(Key.End));
        Assert.Equal(MicEq.MaxQ, sent[^1]["q"]!.GetValue<double>(), 10);
    }

    [Fact]
    public void UnusedGainIsKeptButCannotBeEditedUntilTheTypeUsesIt()
    {
        var (app, sent) = Ready(); Open(app); Select(app, "Band 1 gain");
        app.Handle(new KeyPress(Key.Right)); app.Handle(new KeyPress(Key.Enter));
        Assert.Empty(sent); Assert.False(app.Prompting);
        Select(app, "Band 1 type"); app.Handle(new KeyPress(Key.Right));
        Select(app, "Band 1 gain"); app.Handle(new KeyPress(Key.Right));
        Assert.Equal(.25, sent[^1]["gain"]!.GetValue<double>());
    }

    [Fact]
    public void ARemovedMicrophoneCannotReceiveAPromptThatWasAlreadyOpen()
    {
        var (app, sent) = Ready(); Open(app); Select(app, "Band 2 gain");
        app.Handle(new KeyPress(Key.Enter));
        app.Handle(new KeyPress(Key.Char, '2'));
        app.Link.Receive(State(root => root["mixer"]!["channels"] = new JsonArray()));
        app.Handle(new KeyPress(Key.Enter));
        Assert.Empty(sent);
        Assert.DoesNotContain(Frame(app), line => line.Contains("Band 2 gain", StringComparison.Ordinal));
    }

    [Fact]
    public void DisconnectCancelsOldEditsAndAnOpenPromptBeforeTheSameChannelReturns()
    {
        var (app, sent) = Ready(); Open(app); Select(app, "Band 2 gain");
        app.Handle(new KeyPress(Key.Enter)); app.Handle(new KeyPress(Key.Char, '2'));
        app.Link.EndSession();
        app.Link.Receive(State());
        app.Handle(new KeyPress(Key.Enter));
        Assert.Empty(sent);
        Select(app, "EQ enabled"); app.Handle(new KeyPress(Key.Char, ' '));
        Assert.True(sent[^1]["value"]!.GetValue<bool>());
        sent.Clear();
        app.Link.EndSession();
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Empty(sent);
        Assert.Contains(Frame(app), line => line.Contains("Waiting for the daemon", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeletedAndRecreatedChannelCannotReceiveAnOldExactValue(bool drawWhileMissing)
    {
        var (app, sent) = Ready(); Open(app); Select(app, "Band 2 gain");
        app.Handle(new KeyPress(Key.Enter)); app.Handle(new KeyPress(Key.Char, '2'));
        app.Link.Receive(State(root => root["mixer"]!["channels"] = new JsonArray()));
        if (drawWhileMissing) Frame(app);
        app.Link.Receive(State());
        app.Handle(new KeyPress(Key.Enter));
        Assert.Empty(sent);
    }

    [Fact]
    public void ResetIsOneWholeEqAndKeepsItsSwitchWithoutTouchingOtherMicrophones()
    {
        var (app, sent) = Ready(State(root => Input(root)["eq"]!["enabled"] = true)); Open(app);
        Select(app, "Reset bands"); app.Handle(new KeyPress(Key.Enter));
        JsonObject command = Assert.Single(sent);
        Assert.Equal("setEq", command["cmd"]!.GetValue<string>());
        Assert.Equal("xlr1", command["channel"]!.GetValue<string>());
        Assert.True(command["eq"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(4, command["eq"]!["bands"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void AnIncompleteBandTableDoesNotOfferAnEditor(int count)
    {
        var (app, _) = Ready(State(root => Input(root)["eq"]!["bands"] = new JsonArray(
            Enumerable.Range(0, count).Select(_ => JsonSerializer.SerializeToNode(MicEq.Default.Bands[0], Web)).ToArray())));
        Assert.DoesNotContain(Frame(app), line => line.Contains("edit EQ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEditorScrollsAt80By24AndKeepsTheLastBandSelectedAfterResize()
    {
        var (app, _) = Ready(); Open(app); Select(app, "Band 4 Q");
        Assert.Contains(Frame(app, 80, 24), line => line.Contains("Band 4 Q", StringComparison.Ordinal));
        Assert.Contains(Frame(app, 200, 70), line => line.Contains("Band 4 Q", StringComparison.Ordinal));
    }

    [Fact]
    public void ManyMicrophonesStillExposeTheirEditorOnAShortWideTerminal()
    {
        var (app, _) = Ready(State(root =>
        {
            JsonArray channels = root["mixer"]!["channels"]!.AsArray();
            for (int i = 0; i < 20; i++)
            {
                JsonNode capture = channels[0]!.DeepClone();
                capture["id"] = "guest" + i;
                capture["name"] = "Guest " + i;
                capture["hardware"] = false;
                capture["captureSource"] = "guest-source";
                channels.Add(capture);
            }
        }));
        Select(app, "Guest 19 bands");
        Assert.Contains(Frame(app, 160, 40), line => line.Contains("Guest 19 bands", StringComparison.Ordinal));
    }

    [Fact]
    public void RapidStepsKeepEveryPressAndThenAcceptAnExternalEditWithoutResendingIt()
    {
        var (app, sent) = Ready();
        Clock clock = new();
        EqEditor editor = new("xlr1", app.Link.SessionRevision, clock);
        string[] Draw()
        {
            Screen screen = new(160, 60);
            Assert.True(editor.Draw(screen, new Rect(0, 0, 160, 60), app));
            return Lines(screen);
        }
        Select("Band 2 gain", Draw, key => editor.Handle(key, app));
        for (int i = 0; i < 3; i++) editor.Handle(new KeyPress(Key.Right), app);
        Assert.Equal(new[] { .25, .5, .75 }, sent.Select(command => command["gain"]!.GetValue<double>()));
        app.Link.Receive(State(root => Input(root)["eq"]!["bands"]![1]!["gain"] = -4));
        Assert.Contains(Draw(), line => line.Contains("Band 2 gain", StringComparison.Ordinal) && line.Contains("+0.75 dB", StringComparison.Ordinal));
        clock.Ticks += TimeSpan.FromSeconds(2).Ticks;
        Assert.Contains(Draw(), line => line.Contains("Band 2 gain", StringComparison.Ordinal) && line.Contains("-4 dB", StringComparison.Ordinal));
        Assert.Equal(3, sent.Count);
        editor.Handle(new KeyPress(Key.Right), app);
        Assert.Equal(-3.75, sent[^1]["gain"]!.GetValue<double>());
        app.Link.EndSession();
        app.Link.Receive(State());
        editor.Handle(new KeyPress(Key.Right), app);
        Assert.Equal(.25, sent[^1]["gain"]!.GetValue<double>());

        Select("EQ enabled", Draw, key => editor.Handle(key, app));
        editor.Handle(new KeyPress(Key.Char, ' '), app);
        editor.Handle(new KeyPress(Key.Char, ' '), app);
        Draw(); // The unchanged report happens to match the second press.
        app.Link.Receive(State(root => Input(root)["eq"]!["enabled"] = true)); // The first press's late echo.
        editor.Handle(new KeyPress(Key.Char, ' '), app);
        Assert.True(sent[^1]["value"]!.GetValue<bool>()); // The third press must still turn it on.
    }

    [Fact]
    public void SixteenBandsScrollAndOnlyTheSelectedLastBandChanges()
    {
        var (app, sent) = Ready(State(root => Input(root)["eq"]!["bands"] = new JsonArray(
            Enumerable.Range(0, 16).Select(_ => JsonSerializer.SerializeToNode(MicEq.Default.Bands[1], Web)).ToArray())));
        Open(app); Select(app, "Band 16 gain");
        Assert.Contains(Frame(app, 80, 24), line => line.Contains("Band 16 gain", StringComparison.Ordinal));
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal(16, sent[^1]["band"]!.GetValue<int>());
        Assert.Equal(.25, sent[^1]["gain"]!.GetValue<double>());
        app.Link.Receive(State());
        Assert.Contains(Frame(app), line => line.Contains("Band 4", StringComparison.Ordinal));
    }

    [Fact]
    public void AChangedBandTableCancelsAnOpenPromptEvenWhenItsCountReturns()
    {
        var (app, sent) = Ready(); Open(app); Select(app, "Band 2 gain");
        app.Handle(new KeyPress(Key.Enter)); app.Handle(new KeyPress(Key.Char, '2'));
        app.Link.Receive(State(root => Input(root)["eq"]!["bands"]!.AsArray().RemoveAt(0)));
        app.Link.Receive(State());
        app.Handle(new KeyPress(Key.Enter));
        Assert.Empty(sent);
    }

    private sealed class Clock : TimeProvider
    {
        public long Ticks { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
}
