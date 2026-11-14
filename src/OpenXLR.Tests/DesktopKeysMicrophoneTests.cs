using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using Tmds.DBus.Protocol;

namespace OpenXLR.Tests;

// The microphone mute and push-to-talk keys, against the private session bus
// and a daemon that keeps the four microphones' mutes the way the real one
// reports them.
public sealed partial class DesktopKeysTests
{
    [Fact]
    public void MicrophonesAreReadWithTheMuteEachCarries()
    {
        var daemon = new MicDaemon();
        daemon.Muted["usb"] = true;
        JsonObject state = daemon.State();
        var mics = DesktopKeys.ReadMicrophones(state);
        Assert.Equal(["xlr1", "xlr2", "desk", "usb"], mics.Keys);
        Assert.Equal(new DesktopKeys.Microphone("xlr1", "XLR 1", false, null, "mute"), mics["xlr1"]);
        Assert.Equal(new DesktopKeys.Microphone("xlr2", "XLR 2", false, null, "mute2"), mics["xlr2"]);
        Assert.Equal(new DesktopKeys.Microphone("desk", "Desk mic", false, MicDaemon.Extra, "mute"), mics["desk"]);
        Assert.Equal(new DesktopKeys.Microphone("usb", "USB mic", true), mics["usb"]);

        // A one-input unit has no second mute, an interface that is not
        // connected has none at all, and a strip that is not present is left out.
        state["capabilities"]!["xlrInputs"] = 1;
        ((JsonArray)state["waveInterfaces"]!)[1]!["connected"] = false;
        ((JsonArray)state["mixer"]!["channels"]!)[3]!["present"] = false;
        Assert.Equal(["xlr1"], DesktopKeys.ReadMicrophones(state).Keys);

        // A daemon that names no unit inputs: XLR 1 and XLR 2 on the interface stand for them.
        JsonObject older = daemon.State();
        foreach (JsonNode? channel in (JsonArray)older["mixer"]!["channels"]!) ((JsonObject)channel!).Remove("unitInput");
        Assert.Equal(["xlr1", "xlr2", "usb"], DesktopKeys.ReadMicrophones(older).Keys);
        Assert.Equal("mute2", DesktopKeys.ReadMicrophones(older)["xlr2"].Control);
        // XLR 1 bound to another source has no unit mute to reach.
        ((JsonArray)older["mixer"]!["channels"]!)[0]!["binding"] = new JsonObject { ["kind"] = "source", ["source"] = "other" };
        Assert.DoesNotContain("xlr1", DesktopKeys.ReadMicrophones(older).Keys);

        Assert.Empty(DesktopKeys.ReadMicrophones(null));
        Assert.Empty(DesktopKeys.ReadMicrophones(new JsonObject { ["mixer"] = 3 }));
    }

    [Fact]
    public void TheMicrophoneChoiceDefaultsToXlr1AndStaysBounded()
    {
        Assert.Equal(["xlr1"], new DesktopKeySettings().Normalize().Microphones);
        Assert.Equal(["xlr1"], new DesktopKeySettings { Microphones = [] }.Normalize().Microphones);
        Assert.Equal(["xlr1"], new DesktopKeySettings { Microphones = null! }.Normalize().Microphones);
        var normalized = new DesktopKeySettings { Microphones = ["Bad", "usb", "usb", "", "desk", .. Enumerable.Range(0, 30).Select(i => "mic" + i)] }.Normalize();
        Assert.Equal(["usb", "desk"], normalized.Microphones.Take(2));
        Assert.Equal(16, normalized.Microphones.Count);
    }

    [DesktopBusFact]
    public async Task AMicrophoneChoiceSavedBeforeTheKeysExistedReadsAsXlr1()
    {
        await using var environment = await PrivateBus.Start();
        Directory.CreateDirectory(Path.GetDirectoryName(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-keys.json"))!);
        File.WriteAllText(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-keys.json"), """{ "enabled": true, "clipsStop": true }""");
        DesktopKeySettings loaded = DesktopKeySettings.Load();
        Assert.Equal(["xlr1"], loaded.Microphones);
        Assert.False(loaded.MicMute);
        Assert.False(loaded.PushToTalk);
        new DesktopKeySettings { Enabled = true, MicMute = true, PushToTalk = true, Microphones = ["usb", "xlr2"] }.Save();
        loaded = DesktopKeySettings.Load();
        Assert.True(loaded.MicMute);
        Assert.True(loaded.PushToTalk);
        Assert.Equal(["usb", "xlr2"], loaded.Microphones);
    }

    [DesktopBusFact]
    public async Task TheMuteKeyMutesEveryChosenMicrophoneWhileAnyIsOnAndShowsARefusal()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop));
        desktop.AddMethodHandler(fake);
        await using var daemon = await MicDaemon.Start();
        daemon.Muted["desk"] = true;
        daemon.Muted["usb"] = true;
        await using var client = new DaemonClient(daemon.Server.Url);
        using var keys = new DesktopKeys(client);
        client.Start();
        await LongWait(() => keys.Microphones is { Count: 4 });
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, MicMute = true, Microphones = ["xlr1", "desk", "usb", "gone"] }, save: false);
        Assert.Equal([DesktopKeys.MicMuteKey], fake.ShortcutIds);
        Assert.Equal("Toggle microphone mute", fake.Descriptions[DesktopKeys.MicMuteKey]);
        Assert.Equal("XF86AudioMicMute", fake.PreferredTriggers[DesktopKeys.MicMuteKey]);
        Assert.Empty(daemon.Commands); // the mute key alone mutes nothing until it is pressed

        // One is on: the press mutes them all, through each one's own
        // command, and a repeat while the key is down toggles nothing.
        fake.Activate(DesktopKeys.MicMuteKey);
        fake.Activate(DesktopKeys.MicMuteKey);
        await Wait(() => daemon.Commands.Count == 3);
        await Flush(desktop);
        await Task.Delay(80);
        Assert.Equal(["set mute true", $"setWaveControl {MicDaemon.Extra} mute true", "setCaptureMuted usb true"], daemon.Commands);
        await Wait(() => keys.Status.StartsWith("Microphones muted", StringComparison.Ordinal));
        Assert.Equal("Microphones muted: XLR 1, Desk mic, USB mic. Not reported now: gone.", keys.Status);

        // Every one is muted: the next press turns them all on.
        fake.Deactivate(DesktopKeys.MicMuteKey);
        fake.Activate(DesktopKeys.MicMuteKey);
        await Wait(() => daemon.Commands.Count == 6);
        Assert.Equal(["set mute false", $"setWaveControl {MicDaemon.Extra} mute false", "setCaptureMuted usb false"], daemon.Commands.Skip(3));
        await Wait(() => keys.Status.StartsWith("Microphones on", StringComparison.Ordinal));

        // A refusal shows the daemon's sentence under the microphone's name; the others still mute.
        daemon.Refuse = entry => entry.StartsWith("setWaveControl", StringComparison.Ordinal) ? "The unit is not driven." : null;
        fake.Deactivate(DesktopKeys.MicMuteKey);
        fake.Activate(DesktopKeys.MicMuteKey);
        await Wait(() => daemon.Commands.Count == 9);
        await Wait(() => keys.Status == "Desk mic: The unit is not driven.");
        Assert.True(daemon.Muted["xlr1"]);
        Assert.False(daemon.Muted["desk"]);
        Assert.True(daemon.Muted["usb"]);

        // With none of them reported, the press says so and sends nothing.
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, MicMute = true, Microphones = ["gone"] }, save: false);
        fake.Deactivate(DesktopKeys.MicMuteKey);
        fake.Activate(DesktopKeys.MicMuteKey);
        await Wait(() => keys.Status.StartsWith("None of the chosen microphones", StringComparison.Ordinal));
        Assert.Equal(9, daemon.Commands.Count);
    }

    [DesktopBusFact]
    public async Task PushToTalkOpensTheMicrophonesWhileTheKeyIsDown()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop));
        desktop.AddMethodHandler(fake);
        await using var daemon = await MicDaemon.Start();
        await using var client = new DaemonClient(daemon.Server.Url);
        using var keys = new DesktopKeys(client);
        client.Start();
        await LongWait(() => keys.Microphones is not null);

        // Setting it up mutes the chosen microphones: the key is what opens them.
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, PushToTalk = true, Microphones = ["xlr1", "usb"] }, save: false);
        Assert.Equal("Push to talk", fake.Descriptions[DesktopKeys.TalkKey]);
        Assert.False(fake.PreferredTriggers.ContainsKey(DesktopKeys.TalkKey));
        await Wait(() => daemon.Commands.Count == 2);
        Assert.Equal(["set mute true", "setCaptureMuted usb true"], daemon.Commands);
        Assert.StartsWith("Desktop keys are active", keys.Status);

        // Down opens them once, however often the desktop repeats the key.
        fake.Activate(DesktopKeys.TalkKey);
        fake.Activate(DesktopKeys.TalkKey);
        fake.Activate(DesktopKeys.TalkKey);
        await Wait(() => daemon.Commands.Count == 4);
        await Flush(desktop);
        await Task.Delay(80);
        Assert.Equal(["set mute false", "setCaptureMuted usb false"], daemon.Commands.Skip(2));
        await Wait(() => keys.Status == "Push to talk: XLR 1, USB mic on while the key is down.");
        Assert.False(daemon.Muted["xlr1"]);
        Assert.True(daemon.Muted["desk"] == false && daemon.Muted["xlr2"] == false); // not covered, not touched

        // Up mutes them again.
        fake.Deactivate(DesktopKeys.TalkKey);
        await Wait(() => daemon.Commands.Count == 6);
        Assert.Equal(["set mute true", "setCaptureMuted usb true"], daemon.Commands.Skip(4));
        await Wait(() => keys.Status == "Push to talk: XLR 1, USB mic muted.");

        // A release with no hold behind it, as Plasma reports for another key, sends nothing.
        fake.Deactivate(DesktopKeys.TalkKey);
        await Flush(desktop);
        await Task.Delay(80);
        Assert.Equal(6, daemon.Commands.Count);

        // A press and release in quick succession leaves them muted.
        fake.Activate(DesktopKeys.TalkKey);
        fake.Deactivate(DesktopKeys.TalkKey);
        await Wait(() => daemon.Commands.Count >= 10);
        await Task.Delay(80);
        Assert.True(daemon.Muted["xlr1"]);
        Assert.True(daemon.Muted["usb"]);

        // A refused mute is shown, so a microphone left open does not go unnoticed.
        daemon.Refuse = entry => entry == "setCaptureMuted usb true" ? "The source is missing." : null;
        int sent = daemon.Commands.Count;
        fake.Activate(DesktopKeys.TalkKey);
        await Wait(() => daemon.Commands.Count == sent + 2);
        fake.Deactivate(DesktopKeys.TalkKey);
        await Wait(() => keys.Status == "Push to talk could not mute a microphone again. USB mic: The source is missing.");
        Assert.True(daemon.Muted["xlr1"]);
    }

    [DesktopBusFact]
    public async Task PushToTalkMutesAgainWhenAHoldEndsWithoutItsRelease()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop)) { RefuseRebind = true };
        desktop.AddMethodHandler(fake);
        await using var daemon = await MicDaemon.Start();
        await using var client = new DaemonClient(daemon.Server.Url);
        var keys = new DesktopKeys(client);
        try
        {
            client.Start();
            await LongWait(() => keys.Microphones is not null);
            keys.SetClips([new("horn", "Air horn", "overlap")], fresh: true);
            var settings = new DesktopKeySettings { Enabled = true, PushToTalk = true, Clips = ["horn"], Microphones = ["xlr1"] };
            await keys.ConfigureAsync(settings, save: false);
            await Wait(() => daemon.Commands.Count == 1);

            async Task Hold(int count)
            {
                fake.Activate(DesktopKeys.TalkKey);
                await Wait(() => daemon.Commands.Count == count);
                Assert.Equal("set mute false", daemon.Commands.Last());
                Assert.False(daemon.Muted["xlr1"]);
            }

            // The desktop closes the shortcut session during a hold.
            await Hold(2);
            fake.CloseSession();
            await Wait(() => daemon.Commands.Count == 3);
            Assert.True(daemon.Muted["xlr1"]);

            // The keys are set up again during a hold: the old session's hold
            // ends muted, and the new setup mutes as every setup does.
            await keys.ConfigureAsync(settings, save: false);
            await Wait(() => daemon.Commands.Count == 4);
            await Hold(5);
            await keys.ConfigureAsync(settings, save: false);
            await Wait(() => daemon.Commands.Count == 7);
            Assert.Equal(["set mute true", "set mute true"], daemon.Commands.Skip(5));
            Assert.True(daemon.Muted["xlr1"]);

            // A desktop that binds a session once gets a new one when the
            // clips change; the hold on the old one ends muted.
            await Hold(8);
            keys.SetClips([new("horn", "Horn", "overlap")], fresh: false);
            await Wait(() => daemon.Commands.Count == 9);
            await Wait(() => fake.Descriptions.GetValueOrDefault("clip_horn") == "Play clip Horn");
            Assert.True(daemon.Muted["xlr1"]);

            // The daemon connection drops during a hold: the microphones are
            // muted with the first state of the next connection.
            await Hold(10);
            daemon.Drop();
            await LongWait(() => keys.Status == "The daemon connection dropped during push to talk. OpenXLR mutes the microphones again as soon as the daemon is back.");
            await LongWait(() => daemon.Commands.Count == 11);
            Assert.Equal("set mute true", daemon.Commands.Last());
            Assert.True(daemon.Muted["xlr1"]);
            fake.Deactivate(DesktopKeys.TalkKey); // the release that comes later has nothing left to do
            await Flush(desktop);
            await Task.Delay(80);
            Assert.Equal(11, daemon.Commands.Count);

            // The UI quits during a hold: the mute goes out before the client is disposed.
            await Hold(12);
            await keys.ShutdownAsync();
            Assert.Equal(13, daemon.Commands.Count);
            Assert.True(daemon.Muted["xlr1"]);
        }
        finally { keys.Dispose(); }
    }

    [DesktopBusFact]
    public async Task PushToTalkWaitsForADesktopThatReportsKeysComingUp()
    {
        await using var environment = await PrivateBus.Start();
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "LXQt");
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop));
        desktop.AddMethodHandler(fake);
        await using var daemon = await MicDaemon.Start();
        await using var client = new DaemonClient(daemon.Server.Url);
        using var keys = new DesktopKeys(client);
        Assert.False(keys.DeliversRelease);
        client.Start();
        await LongWait(() => keys.Microphones is not null);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, PushToTalk = true, MicMute = true }, save: false);
        await Wait(() => daemon.Commands.Count == 1);
        Assert.Equal("set mute true", Assert.Single(daemon.Commands));

        // Nothing would end the hold here: the press leaves XLR 1 muted and says why.
        fake.Activate(DesktopKeys.TalkKey);
        await Wait(() => keys.Status.StartsWith("Push to talk left the microphones muted", StringComparison.Ordinal));
        await Flush(desktop);
        await Task.Delay(80);
        Assert.Single(daemon.Commands);

        // The mute key cannot tell a repeat from a press here, so each one toggles.
        fake.Activate(DesktopKeys.MicMuteKey);
        fake.Activate(DesktopKeys.MicMuteKey);
        await Wait(() => daemon.Commands.Count == 3);
        Assert.Equal(["set mute false", "set mute true"], daemon.Commands.Skip(1));

        // The first key coming up shows the desktop reports releases: push to talk works from then on.
        fake.Deactivate(DesktopKeys.TalkKey);
        await Wait(() => keys.DeliversRelease);
        await Flush(desktop);
        await Task.Delay(80);
        Assert.Equal(3, daemon.Commands.Count);
        fake.Activate(DesktopKeys.TalkKey);
        fake.Deactivate(DesktopKeys.TalkKey);
        await Wait(() => daemon.Commands.Count == 5);
        Assert.Equal(["set mute false", "set mute true"], daemon.Commands.Skip(3));
    }

    private static async Task LongWait(Func<bool> condition)
    {
        for (int i = 0; i < 400; i++) { if (condition()) return; await Task.Delay(25); }
        Assert.True(condition());
    }

    /// <summary>
    /// A daemon with XLR 1 and XLR 2 on the primary, an additional unit's
    /// input on a capture channel and a capture input with a source mute.
    /// Every mute command is recorded and answered with the state, then
    /// with its commandResult, as the daemon does.
    /// </summary>
    private sealed class MicDaemon : IAsyncDisposable
    {
        internal const string Primary = "0fd9:007d@0000000000000001";
        internal const string Extra = "0fd9:00aa@0000000000000002";
        private readonly object _gate = new();
        internal ConcurrentDictionary<string, bool> Muted { get; } = new() { ["xlr1"] = false, ["xlr2"] = false, ["desk"] = false, ["usb"] = false };
        internal ConcurrentQueue<string> Commands { get; } = new();
        internal Func<string, string?>? Refuse { get; set; }
        internal SocketTestServer Server { get; private set; } = null!;
        private CancellationTokenSource? _connection;

        internal static async Task<MicDaemon> Start()
        {
            var daemon = new MicDaemon();
            daemon.Server = await SocketTestServer.Start(daemon.Serve);
            return daemon;
        }

        /// <summary>Drop the connection the UI has; it connects again.</summary>
        internal void Drop() => Volatile.Read(ref _connection)?.Cancel();

        private async Task Serve(System.Net.WebSockets.WebSocket socket, CancellationToken stop)
        {
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
            Volatile.Write(ref _connection, connection);
            await SocketTestServer.Send(socket, State(), connection.Token);
            while (true)
            {
                JsonNode command = await SocketTestServer.Receive(socket, connection.Token);
                if (command["requestId"] is not { } id) continue;
                string cmd = command["cmd"]!.GetValue<string>();
                bool value = command["value"] is JsonValue v && v.TryGetValue(out bool b) && b;
                string word = value ? "true" : "false";
                (string? mic, string entry) = cmd switch
                {
                    "set" => (command["control"]!.GetValue<string>() switch { "mute" => "xlr1", "mute2" => "xlr2", _ => null },
                        $"set {command["control"]!.GetValue<string>()} {word}"),
                    "setWaveControl" => (command["device"]!.GetValue<string>() == Extra && command["control"]!.GetValue<string>() == "mute" ? "desk" : null,
                        $"setWaveControl {command["device"]!.GetValue<string>()} {command["control"]!.GetValue<string>()} {word}"),
                    "setCaptureMuted" => (command["channel"]!.GetValue<string>() == "usb" ? "usb" : null, $"setCaptureMuted {command["channel"]!.GetValue<string>()} {word}"),
                    _ => (null, cmd),
                };
                string? error = Refuse?.Invoke(entry) ?? (mic is null ? "unknown microphone" : null);
                if (error is null) Muted[mic!] = value;
                Commands.Enqueue(entry);
                await SocketTestServer.Send(socket, State(), connection.Token);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = id.GetValue<string>(), error }, connection.Token);
            }
        }

        internal JsonObject State()
        {
            lock (_gate)
            {
                static JsonObject Unit(string unit, int input, bool primary) => new() { ["unit"] = unit, ["input"] = input, ["primary"] = primary };
                return new JsonObject
                {
                    ["type"] = "state",
                    ["connected"] = true,
                    ["capabilities"] = new JsonObject { ["mute"] = true, ["xlrInputs"] = 2 },
                    ["state"] = new JsonObject { ["mute"] = Muted["xlr1"], ["mute2"] = Muted["xlr2"] },
                    ["waveInterfaces"] = new JsonArray(
                        new JsonObject { ["id"] = Primary, ["active"] = true, ["enabled"] = false, ["connected"] = true },
                        new JsonObject
                        {
                            ["id"] = Extra, ["active"] = false, ["enabled"] = true, ["connected"] = true,
                            ["capabilities"] = new JsonObject { ["mute"] = true, ["xlrInputs"] = 1 },
                            ["state"] = new JsonObject { ["mute"] = Muted["desk"] },
                        }),
                    ["mixer"] = new JsonObject
                    {
                        ["channels"] = new JsonArray(
                            new JsonObject { ["id"] = "xlr1", ["name"] = "XLR 1", ["hardware"] = true, ["present"] = true, ["unitInput"] = Unit(Primary, 1, true) },
                            new JsonObject { ["id"] = "xlr2", ["name"] = "XLR 2", ["hardware"] = true, ["present"] = true, ["unitInput"] = Unit(Primary, 2, true) },
                            new JsonObject { ["id"] = "desk", ["name"] = "Desk mic", ["hardware"] = false, ["captureSource"] = "alsa_input.desk", ["unitInput"] = Unit(Extra, 1, false) },
                            new JsonObject
                            {
                                ["id"] = "usb", ["name"] = "USB mic", ["hardware"] = false, ["captureSource"] = "alsa_input.usb",
                                ["captureGain"] = new JsonObject { ["gainDb"] = 0, ["minDb"] = -20, ["maxDb"] = 20, ["hardware"] = false, ["muted"] = Muted["usb"] },
                            },
                            new JsonObject { ["id"] = "aux", ["name"] = "Aux In", ["hardware"] = true, ["present"] = true },
                            new JsonObject { ["id"] = "music", ["name"] = "Music", ["hardware"] = false }),
                    },
                };
            }
        }

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }
}
