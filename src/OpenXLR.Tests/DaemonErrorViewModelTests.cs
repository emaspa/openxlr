using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// What the UI does when the daemon says no, goes away or sends a state this
/// build cannot read: the refusal shows as the main window's notice and the
/// controls go back to the daemon's values, the sections grey and the meters
/// fall to zero while it is gone, and an unreadable push keeps the last
/// values and raises the version banner instead of ending the UI.
/// </summary>
/// <remarks>
/// In the xdg-config collection, which runs alone: a refusal releases the
/// fader guards, which are static and shared with the other UI tests.
/// </remarks>
[Collection("xdg-config")]
public sealed class DaemonErrorViewModelTests
{
    private sealed class NoHold : IDisposable { public void Dispose() { } }

    private static MainViewModel Model(DaemonClient client, List<TimeSpan>? holds = null)
        => new(client) { StartNoticeHold = (hold, _) => { holds?.Add(hold); return new NoHold(); } };

    private static void Invoke(MainViewModel model, string method, params object?[] args)
        => typeof(MainViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, args);

    /// <summary>A push as it comes off the socket: the client hands over the node and its text.</summary>
    private static void Push(MainViewModel model, JsonNode state)
        => Invoke(model, "ApplyPush", state, state.ToJsonString(), SliderSync.Now());

    private static void Disconnect(MainViewModel model) => Invoke(model, "OnConnectionChanged", false);

    /// <summary>
    /// The client's connection had come up. Raised for real it would also
    /// fetch the plugin catalogue, which posts to the dispatcher.
    /// </summary>
    private static void WasConnected(MainViewModel model)
        => typeof(MainViewModel).GetField("_wasConnected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, true);

    private static void Refuse(MainViewModel model, string message) => Invoke(model, "OnDaemonError", message);

    private static bool Applying(MainViewModel model)
        => (bool)typeof(MainViewModel).GetField("_applying", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;

    /// <summary>A Wave XLR Pro with XLR 1 in the mixer, its mute and gain as given.</summary>
    private static JsonObject State(bool mute = false, int gainDb = 20, string? version = null)
    {
        var state = JsonNode.Parse(InputBindingModelTests.ProState(
            InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1))))!.AsObject();
        state["daemonVersion"] = version ?? AppVersion.Current;
        state["state"] = new JsonObject { ["mute"] = mute, ["gainDb"] = gainDb };
        return state;
    }

    /// <summary>Put a value on a control the way a user's change does, without a daemon to send it to.</summary>
    private static void UserSets(MainViewModel model, Action change)
    {
        FieldInfo applying = typeof(MainViewModel).GetField("_applying", BindingFlags.Instance | BindingFlags.NonPublic)!;
        applying.SetValue(model, true);
        try { change(); }
        finally { applying.SetValue(model, false); }
    }

    [Fact]
    public async Task ARefusalShowsTheDaemonsSentenceAndPutsTheDaemonsValuesBack()
    {
        await using var client = new DaemonClient();
        var holds = new List<TimeSpan>();
        MainViewModel model = Model(client, holds);
        Push(model, State(mute: false, gainDb: 20));
        Assert.Null(model.Notice);

        // The user turned mute on and pulled the gain up; the gain is still
        // inside its drag guard, which would keep a plain state off it.
        UserSets(model, () => { model.Mute = true; model.GainDb = 35; });
        SliderSync.Touch("gain");
        try
        {
            Refuse(model, "set: the unit refused the change");

            Assert.Equal("set: the unit refused the change", model.Notice);
            Assert.Equal([MainViewModel.ErrorNoticeHold], holds);
            Assert.False(model.Mute);
            Assert.Equal(20, model.GainDb);
            Assert.False(SliderSync.RecentlyTouched("gain"));
            Assert.False(Applying(model));
        }
        finally { SliderSync.Forget("gain"); }
    }

    [Fact]
    public async Task TheSameRefusalAgainSoonShowsNoNewNotice()
    {
        Func<long> clock = SliderSync.Now;
        long now = 1_000_000;
        SliderSync.Now = () => now;
        try
        {
            await using var client = new DaemonClient();
            var holds = new List<TimeSpan>();
            MainViewModel model = Model(client, holds);
            Push(model, State());
            const string meter = "The input meter needs the software mixer to be running.";
            Refuse(model, meter);
            now += 5_000;
            Refuse(model, meter);
            Assert.Single(holds);

            // Another sentence shows at once; the first shows again once it has been quiet.
            Refuse(model, "set: the unit refused the change");
            Assert.Equal(2, holds.Count);
            Refuse(model, meter);
            Assert.Equal(3, holds.Count);
            now += MainViewModel.ErrorRepeatQuietMs - 1;
            Refuse(model, meter);
            Assert.Equal(3, holds.Count);
            now += MainViewModel.ErrorRepeatQuietMs;
            Refuse(model, meter);
            Assert.Equal(4, holds.Count);
            Assert.Equal(meter, model.Notice);
        }
        finally { SliderSync.Now = clock; }
    }

    [Fact]
    public async Task WithoutAConnectionAFailedChangeAddsNoNotice()
    {
        await using var client = new DaemonClient();
        var holds = new List<TimeSpan>();
        MainViewModel model = Model(client, holds);
        Refuse(model, "The daemon is not connected; the change was not made.");
        Assert.Null(model.Notice);
        Assert.Empty(holds);
        Assert.Equal("The daemon is not connected; the change was not made.", model.Status);
        Assert.False(model.DaemonConnected);
    }

    [Fact]
    public async Task ADisconnectGreysTheControlsAndDropsTheMetersUntilTheDaemonIsBack()
    {
        await using var client = new DaemonClient();
        MainViewModel model = Model(client);
        // Before the client has an answer the controls are live.
        Assert.True(model.ControlsEnabled);
        Push(model, State());
        WasConnected(model);
        Assert.True(model.ControlsEnabled);
        ChannelViewModel xlr1 = model.Channels.Single(c => c.Id == "xlr1");
        Invoke(model, "ApplyMeters", JsonNode.Parse("""{"ch:xlr1":[0.7,0.5],"duck:monitor":[6]}"""));
        Assert.Equal(0.7, xlr1.MeterL);
        Assert.Equal(0.5, xlr1.MeterR);
        Assert.Equal(6, model.Ducking.Reduction);

        Disconnect(model);
        Assert.False(model.DaemonConnected);
        Assert.False(model.ControlsEnabled);
        Assert.Equal(0, xlr1.MeterL);
        Assert.Equal(0, xlr1.MeterR);
        Assert.Equal(0, model.Ducking.Reduction);
        // Every failed retry says so again; the controls stay grey.
        Disconnect(model);
        Assert.False(model.ControlsEnabled);

        // A new connection's first state brings them back.
        Push(model, State());
        Assert.True(model.DaemonConnected);
        Assert.True(model.ControlsEnabled);
    }

    private static void MeterFrame(MainViewModel model, string json) => Invoke(model, "ApplyMeters", JsonNode.Parse(json));

    /// <summary>The state with XLR 1's compressor and expander on this computer, so its Dynamics windows have a model.</summary>
    private static JsonObject StateWithDynamics()
    {
        JsonObject state = State();
        JsonObject xlr1 = state["mixer"]!["channels"]!.AsArray().OfType<JsonObject>().Single(c => c["id"]!.GetValue<string>() == "xlr1");
        xlr1["compressor"] = JsonNode.Parse("""
            {"enabled":true,"threshold":-18,"ratio":3,"attack":5,"release":150,"knee":6,"makeup":0,"available":true,"error":null}
            """);
        xlr1["expander"] = JsonNode.Parse("""
            {"enabled":true,"threshold":-50,"ratio":3,"range":24,"attack":1,"release":150,"hold":50,"available":true,"error":null}
            """);
        return state;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MicSetupAndTheDynamicsWindowsDropTheirMetersWithTheMainOnes(bool graphLost)
    {
        await using var client = new DaemonClient();
        MainViewModel model = Model(client);
        Push(model, StateWithDynamics());
        WasConnected(model);
        ChannelViewModel xlr1 = model.Channels.Single(c => c.Id == "xlr1");
        ChannelDynamicsViewModel dynamics = xlr1.Dynamics!;
        MicSetupViewModel setup = model.Inserts.MicSetup;
        MeterFrame(model, """{"ch:xlr1":[0.7,0.5],"in:xlr1":[0.6,0.8,0],"comp:xlr1":[-12,6,-18],"exp:xlr1":[-12,0,-12]}""");
        Assert.Equal(0.6, setup.InputLevel);
        Assert.Equal(0.8, setup.InputPeak);
        Assert.True(dynamics.Compressor.HasLevels);
        Assert.True(dynamics.Compressor.InputLevel > 0);
        Assert.True(dynamics.Compressor.ReductionLevel > 0);
        Assert.True(dynamics.Expander.OutputLevel > 0);

        if (graphLost)
        {
            // The audio server restarted: the daemon stays, its graph and its meter frames do not.
            JsonObject lost = State();
            lost["mixer"] = null;
            lost["mixerWaiting"] = true;
            lost["mixerRebuilding"] = true;
            Push(model, lost);
            Assert.True(model.DaemonConnected);
        }
        else Disconnect(model);

        Assert.Equal(0, xlr1.MeterL);
        Assert.Equal(0, setup.InputLevel);
        Assert.Equal(0, setup.InputPeak);
        foreach (MicDynamicsViewModel processor in new[] { dynamics.Compressor, dynamics.Expander })
        {
            Assert.False(processor.HasLevels);
            Assert.Equal(0, processor.InputLevel);
            Assert.Equal(0, processor.ReductionLevel);
            Assert.Equal(0, processor.OutputLevel);
            Assert.Equal(0, processor.History.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoDaemonAtStartGreysTheControlsUntilOneConnects(bool older)
    {
        DaemonPresence presence = older ? DaemonPresence.OlderDaemon : DaemonPresence.NotRunning;
        await using var client = new DaemonClient();
        MainViewModel model = Model(client);
        Disconnect(model);
        Assert.True(model.ControlsEnabled);
        Invoke(model, "OnPresenceChanged", presence);
        Assert.False(model.ControlsEnabled);
        Disconnect(model);
        Assert.False(model.ControlsEnabled);
        Push(model, State());
        Assert.True(model.ControlsEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStateThatCannotBeReadKeepsTheLastValuesAndRaisesTheVersionBanner(bool otherVersion)
    {
        var logged = new List<string>();
        Action<string> write = UiErrors.Write;
        UiErrors.Write = logged.Add;
        try
        {
            await using var client = new DaemonClient();
            MainViewModel model = Model(client);
            Push(model, State(mute: true, gainDb: 20));
            Assert.False(model.DaemonVersionMismatch);

            // A daemon that sends mute as text: the gain before it would
            // already be applied when mute throws.
            string version = otherVersion ? "9.9.9" : AppVersion.Current;
            JsonObject unreadable = State(mute: true, gainDb: 40, version: version);
            unreadable["state"]!["mute"] = "yes";
            unreadable["device"]!["model"] = "Something else";
            Push(model, unreadable);

            Assert.Equal(20, model.GainDb);
            Assert.True(model.Mute);
            Assert.Equal("Elgato Wave XLR Pro", model.DeviceName);
            Assert.False(Applying(model));
            Assert.True(model.StateUnreadable);
            Assert.True(model.DaemonVersionMismatch);
            Assert.Equal(version, model.DaemonVersion);
            Assert.Equal(Localizer.Format("DaemonStateUnreadableNote", version, AppVersion.Current), model.DaemonVersionNote);
            Assert.Contains(logged, line => line.Contains("a daemon state could not be applied", StringComparison.Ordinal));

            // A readable push applies again; the banner stays until the connection goes.
            Push(model, State(mute: false, gainDb: 25));
            Assert.Equal(25, model.GainDb);
            Assert.True(model.DaemonVersionMismatch);
            Disconnect(model);
            Assert.False(model.StateUnreadable);
            Assert.False(model.DaemonVersionMismatch);
        }
        finally { UiErrors.Write = write; }
    }

    [Fact]
    public async Task AnUnreadableFirstStateRaisesTheBannerWithoutEndingTheUI()
    {
        Action<string> write = UiErrors.Write;
        UiErrors.Write = _ => { };
        try
        {
            await using var client = new DaemonClient();
            MainViewModel model = Model(client);
            JsonObject unreadable = State(version: "9.9.9");
            unreadable["capabilities"]!["xlrInputs"] = "two";
            Push(model, unreadable);
            Assert.True(model.DaemonVersionMismatch);
            Assert.Equal("9.9.9", model.DaemonVersion);
            Assert.False(Applying(model));
        }
        finally { UiErrors.Write = write; }
    }
}
