using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProcessResult = OpenXLR.Core.ProcessResult;
using ProcessRunner = OpenXLR.Core.ProcessRunner;
using OpenXLR.Daemon;
using OpenXLR.UI;
using Tmds.DBus.Protocol;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed partial class DesktopKeysTests
{
    [DesktopBusTheory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task FlatpakAutostartUsesTheBackgroundPortal(bool audio, bool window, bool grant)
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var backend = new BackgroundBackend(new DesktopBus(desktop), grant);
        desktop.AddMethodHandler(backend);
        using var client = new DBusConnection(environment.Address);
        await client.ConnectAsync();
        Task<bool> configure = FlatpakBackground.ConfigureAsync(new DesktopBus(client), audio, window, CancellationToken.None);
        if (grant || (!audio && !window)) Assert.Equal(grant, await configure.WaitAsync(TimeSpan.FromSeconds(5)));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => configure);
        Assert.Equal(audio || window, backend.Autostart);
        Assert.Equal(window ? new[] { "openxlr" } : new[] { "openxlr", "--background" }, backend.Command);
    }

    [DesktopBusFact]
    public async Task BackgroundRefusalDoesNotHideAnAcceptedAutostartEntry()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync(); await desktop.RequestNameAsync(DesktopBus.Portal);
        desktop.AddMethodHandler(new BackgroundBackend(new DesktopBus(desktop), grant: false, grantAutostart: true));
        using var client = new DBusConnection(environment.Address);
        await client.ConnectAsync();
        Assert.False(await FlatpakBackground.ConfigureAsync(new DesktopBus(client), true, false, CancellationToken.None));
    }

    private sealed class BackgroundBackend(DesktopBus bus, bool grant, bool? grantAutostart = null) : IPathMethodHandler
    {
        public string Path => "/";
        public bool HandlesChildPaths => true;
        internal bool Autostart;
        internal string[] Command = [];
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.InterfaceAsString != FlatpakBackground.Interface) { context.ReplyUnknownMethodError(); return ValueTask.CompletedTask; }
            var reader = context.Request.GetBodyReader();
            Assert.Equal("", reader.ReadString());
            var options = reader.ReadDictionaryOfStringToVariantValue();
            Autostart = options["autostart"].GetBool();
            Command = options["commandline"].GetArray<string>();
            string sender = context.Request.SenderAsString!;
            string path = "/org/freedesktop/portal/desktop/request/" + sender[1..].Replace('.', '_') + "/" + options["handle_token"].GetString();
            using var reply = context.CreateReplyWriter("o"); reply.WriteObjectPath(path); context.Reply(reply.CreateMessage());
            using var signal = bus.Connection.GetMessageWriter();
            signal.WriteSignalHeader(destination: sender, path: path, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
            signal.WriteUInt32(0);
            signal.WriteDictionary(new Dictionary<string, VariantValue> { ["background"] = grant, ["autostart"] = grantAutostart ?? (grant && Autostart) });
            bus.Connection.TrySendMessage(signal.CreateMessage());
            return ValueTask.CompletedTask;
        }
    }

    [DesktopBusFact]
    public async Task PortalKeysAndDeckQueriesUseTheSameFocusServiceAndCleanUp()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        await desktop.RequestNameAsync("org.kde.KWin");
        var fake = new DesktopBackend(new DesktopBus(desktop));
        desktop.AddMethodHandler(fake);
        var commands = new ConcurrentQueue<string>();
        var outputCommands = new ConcurrentQueue<string>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd == "routeFocusedApp") commands.Enqueue(command["channel"]!.GetValue<string>());
                else if (cmd == "toggleOutputMute") outputCommands.Enqueue(cmd);
                else continue;
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var keys = new DesktopKeys(client);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, FocusChannels = ["music", "browser"], OutputControls = true, MainOutputs = ["headset"] }, save: false);
        Assert.StartsWith("Desktop keys are active", keys.Status);
        Assert.Equal(["focus_music", "focus_browser", "output_up", "output_down", "output_mute", DesktopKeySettings.MainKey("headset")], fake.ShortcutIds);
        fake.Activate("focus_music");
        await Wait(() => keys.Status.StartsWith("Completed:", StringComparison.Ordinal));
        Assert.Equal("music", Assert.Single(commands));
        fake.Activate("output_mute");
        await Wait(() => keys.Status == "Completed: Toggle output mute.");
        Assert.Equal("toggleOutputMute", Assert.Single(outputCommands));
        fake.Activate("focus_deleted");
        fake.Activate("focus_browser", "/not_the_session");
        await Task.Delay(80);
        Assert.Single(commands);
        Assert.Equal(["4242"], await Task.Run(() => DesktopFocusQuery.ReadAudioProcesses(["4242", "4343"])));
        Assert.True(fake.Unloads >= 2);
        Assert.False(File.Exists(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-focus.js")));
        Directory.CreateDirectory(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-keys.json"));
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = false });
        Assert.StartsWith("Desktop keys:", keys.Status);
        Assert.Equal(["4242"], await Task.Run(() => DesktopFocusQuery.ReadAudioProcesses(["4242", "4343"]))); // failed save kept the running service
        Directory.Delete(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-keys.json"));
        fake.CloseSession();
        await Wait(() => keys.Status.StartsWith("Shortcut session closed", StringComparison.Ordinal));
        fake.Activate("focus_browser");
        await Task.Delay(80);
        Assert.Single(commands);
        fake.CancelBinding = true;
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, FocusChannels = ["music"] }, save: false);
        Assert.Contains("cancelled", keys.Status);
        Assert.Throws<InvalidOperationException>(() => DesktopFocusQuery.ReadAudioProcesses(["4242"]));
        await keys.ConfigureAsync(new DesktopKeySettings(), save: false);
        Assert.Equal("Desktop keys are disabled.", keys.Status);
    }

    [DesktopBusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RapidOutputKeysAreOrderedBoundedAndDiscardedWhenDisabled(bool disable)
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop)) { RequireHostIdentity = true };
        desktop.AddMethodHandler(fake);
        var received = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["requestId"] is not { } id) continue;
                received.Enqueue(command["cmd"]!.GetValue<string>());
                await release.Task.WaitAsync(stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = id.GetValue<string>(),
                    error = received.Count == 1 ? "first command failed" : null }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var keys = new DesktopKeys(client);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, MonitorControls = true, OutputControls = true, MainOutputs = ["headset"] }, save: false);
        Assert.Equal("XF86AudioMute", Assert.Single(fake.PreferredTriggers).Value);
        Assert.True(fake.PreferredTriggers.ContainsKey("monitor_mute"));
        fake.Activate("output_up");
        await Wait(() => received.Count == 1);
        string[] ids = [DesktopKeySettings.MainKey("headset"), "output_down", "monitor_mute", "output_mute", "output_up"];
        for (int i = 0; i < 20; i++) fake.Activate(ids[i % ids.Length]);
        // A round trip on the sending connection lets all preceding portal
        // signals arrive before we release the blocked daemon reply.
        await new DesktopBus(desktop).Call(DesktopFocus.Service, "/org/openxlr/Desktop",
            "org.freedesktop.DBus.Introspectable", "Introspect", null, null,
            static (message, _) => message.GetBodyReader().ReadString());
        await Wait(() => keys.Status.Contains("queue is full", StringComparison.Ordinal));
        Assert.Single(received);
        if (disable) await keys.ConfigureAsync(new DesktopKeySettings(), save: false);
        release.TrySetResult();
        var invoking = typeof(DesktopKeys).GetField("_invoking", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await Wait(() => (int)invoking.GetValue(keys)! == 0);
        if (disable)
        {
            Assert.Single(received);
            Assert.Equal("Desktop keys are disabled.", keys.Status);
        }
        else
        {
            string[] group = ["setMainOutput", "adjustOutputVolume", "toggleMonitorMuted", "toggleOutputMute", "adjustOutputVolume"];
            Assert.Equal(new[] { "adjustOutputVolume" }.Concat(Enumerable.Range(0, 16).Select(i => group[i % group.Length])), received);
            Assert.StartsWith("Completed:", keys.Status);
        }
    }

    [DesktopBusTheory]
    [InlineData("disable", null)]
    [InlineData("disable", "old command failed")]
    [InlineData("close", null)]
    [InlineData("close", "old command failed")]
    [InlineData("replace", null)]
    [InlineData("replace", "old command failed")]
    public async Task LateCommandRepliesCannotReplaceTheCurrentSessionStatus(string transition, string? error)
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop));
        desktop.AddMethodHandler(fake);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int commands = 0;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() != "routeFocusedApp") continue;
                bool first = Interlocked.Increment(ref commands) == 1;
                arrived.TrySetResult();
                await release.Task.WaitAsync(stop);
                await SocketTestServer.Send(socket, new { type = "commandResult",
                    requestId = command["requestId"]!.GetValue<string>(), error = first ? error : null }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var keys = new DesktopKeys(client);
        var enabled = new DesktopKeySettings { Enabled = true, FocusChannels = ["music"] };
        await keys.ConfigureAsync(enabled, save: false);
        fake.Activate("focus_music");
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (transition == "close")
        {
            fake.CloseSession();
            await Wait(() => keys.Status.StartsWith("Shortcut session closed", StringComparison.Ordinal));
        }
        else await keys.ConfigureAsync(transition == "replace" ? enabled : new DesktopKeySettings(), save: false);
        string status = keys.Status;
        release.TrySetResult();
        // Wait for the handler itself to finish, not merely for the server to
        // send its reply, so a delayed UI continuation cannot escape the check.
        var invoking = typeof(DesktopKeys).GetField("_invoking", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await Wait(() => (int)invoking.GetValue(keys)! == 0);
        Assert.Equal(status, keys.Status);
        await keys.ConfigureAsync(enabled, save: false);
        string active = keys.Status;
        fake.Activate("focus_music");
        await Wait(() => Volatile.Read(ref commands) == 2 && keys.Status != active);
        Assert.DoesNotContain("failed", keys.Status);
    }

    [DesktopBusTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonedMethodCallsCloseTheConnectionAndReleasePendingReplies(bool cancelled)
    {
        await using var environment = await PrivateBus.Start();
        using var server = new DBusConnection(environment.Address);
        await server.ConnectAsync();
        await server.RequestNameAsync("org.openxlr.Silent");
        using var handler = new SilentDesktop();
        server.AddMethodHandler(handler);
        using var connection = new DBusConnection(environment.Address);
        await connection.ConnectAsync();
        Task disconnected = connection.DisconnectedAsync();
        using var stop = new CancellationTokenSource();
        Task pending = new DesktopBus(connection).Empty("org.openxlr.Silent", "/silent", "org.openxlr.Silent", "Wait", cancel: stop.Token);
        await handler.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (cancelled)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else await Assert.ThrowsAsync<TimeoutException>(() => pending);
        await disconnected.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class SilentDesktop : IPathMethodHandler, IDisposable
    {
        public string Path => "/silent";
        public bool HandlesChildPaths => false;
        internal TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private MethodContext? _context;
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            context.DisposesAsynchronously = true;
            _context = context;
            Arrived.TrySetResult();
            return ValueTask.CompletedTask;
        }
        public void Dispose() => _context?.Dispose();
    }

    [DesktopBusFact]
    public async Task DeckOnlyIntegrationReportsALostDesktopConnection()
    {
        var environment = await PrivateBus.Start();
        bool stopped = false;
        try
        {
            await using var client = new DaemonClient();
            using var keys = new DesktopKeys(client);
            await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true }, save: false);
            Assert.StartsWith("OpenDeck focus routing is enabled", keys.Status);
            await environment.DisposeAsync();
            stopped = true;
            await Wait(() => keys.Status.StartsWith("Desktop connection lost", StringComparison.Ordinal));
        }
        finally { if (!stopped) await environment.DisposeAsync(); }
    }

    [DesktopBusFact]
    public async Task ExplicitApplyReopensTheShortcutEditorAfterBindingWhileAutostartStaysSilent()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop)) { RequireHostIdentity = true, HoldBindingResponse = true };
        desktop.AddMethodHandler(fake);
        await using var client = new DaemonClient();
        using var keys = new DesktopKeys(client);
        var settings = new DesktopKeySettings { Enabled = true, MonitorControls = true };
        Task applying = keys.ConfigureAsync(settings);
        await Wait(() => fake.HasPendingBinding);
        Assert.Equal(["CreateSession", "BindShortcuts"], fake.ShortcutMethods);
        Assert.Equal(0, fake.ConfigureRequests);
        fake.CompleteBinding();
        await applying.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["CreateSession", "BindShortcuts", "ConfigureShortcuts"], fake.ShortcutMethods);
        Assert.Equal(1, fake.ConfigureRequests);
        Assert.StartsWith("Desktop keys are active", keys.Status);

        fake.HoldBindingResponse = false;
        await keys.ConfigureAsync(settings, save: false);
        Assert.Equal(1, fake.ConfigureRequests);
        Assert.StartsWith("Desktop keys are active", keys.Status);
        await keys.ConfigureAsync(settings);
        Assert.Equal(2, fake.ConfigureRequests);
    }

    [DesktopBusTheory]
    [InlineData("org.freedesktop.DBus.Error.UnknownMethod")]
    [InlineData("org.freedesktop.DBus.Error.UnknownInterface")]
    public async Task OlderPortalsKeepTheirBindingsAndExplainWhereToEditThem(string error)
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop)) { ConfigureError = error };
        desktop.AddMethodHandler(fake);
        await using var client = new DaemonClient();
        using var keys = new DesktopKeys(client);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, MonitorControls = true });
        Assert.Equal(1, fake.ConfigureRequests);
        Assert.StartsWith("Desktop keys are active", keys.Status);
        Assert.Contains("desktop's shortcut settings", keys.Status);
        fake.Activate("monitor_mute");
        await Wait(() => keys.Status.Contains("disconnected", StringComparison.Ordinal));
    }

    [DesktopBusTheory]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied")]
    [InlineData("org.freedesktop.portal.Error.NotAllowed")]
    [InlineData("org.freedesktop.portal.Error.Failed")]
    [InlineData("org.freedesktop.DBus.Error.UnknownObject")]
    public async Task ARefusedShortcutEditorReportsItsErrorWithoutDroppingWorkingBindings(string error)
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop)) { ConfigureError = error };
        desktop.AddMethodHandler(fake);
        await using var client = new DaemonClient();
        using var keys = new DesktopKeys(client);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, MonitorControls = true });
        Assert.Equal(1, fake.ConfigureRequests);
        Assert.StartsWith("Desktop keys:", keys.Status);
        Assert.Contains("The portal rejected configuration", keys.Status);
        fake.Activate("monitor_mute");
        await Wait(() => keys.Status.Contains("disconnected", StringComparison.Ordinal));
    }

    [DesktopBusTheory]
    [InlineData("org.freedesktop.portal.Error.Failed")]
    [InlineData("org.freedesktop.portal.Error.NotAllowed")]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied")]
    public async Task ARefusedPortalIdentityKeepsEveryKeyAndFocusRoutingWorking(string error)
    {
        // A source install has no openxlr.desktop, so a current portal refuses Register.
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        await desktop.RequestNameAsync("org.kde.KWin");
        var fake = new DesktopBackend(new DesktopBus(desktop)) { RequireHostIdentity = true, RegisterError = error };
        desktop.AddMethodHandler(fake);
        var commands = new ConcurrentQueue<string>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd is not ("routeFocusedApp" or "toggleMonitorMuted" or "toggleOutputMute")) continue;
                commands.Enqueue(cmd);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var keys = new DesktopKeys(client);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, FocusChannels = ["music"], MonitorControls = true, OutputControls = true }, save: false);

        Assert.StartsWith("Desktop keys are active", keys.Status);
        Assert.Equal(["CreateSession", "BindShortcuts"], fake.ShortcutMethods);
        fake.Activate("monitor_mute");
        await Wait(() => commands.Count == 1);
        fake.Activate("output_mute");
        await Wait(() => commands.Count == 2);
        fake.Activate("focus_music");
        await Wait(() => commands.Count == 3);
        Assert.Equal(["toggleMonitorMuted", "toggleOutputMute", "routeFocusedApp"], commands.ToArray());
        Assert.Equal(["4242"], await Task.Run(() => DesktopFocusQuery.ReadAudioProcesses(["4242", "4343"])));
    }

    [DesktopBusFact]
    public async Task SettingsFailureDoesNotReportAnEnabledIntegration()
    {
        await using var environment = await PrivateBus.Start();
        Directory.CreateDirectory(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-keys.json"));
        await using var client = new DaemonClient();
        using var keys = new DesktopKeys(client);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true });
        Assert.StartsWith("Desktop keys:", keys.Status);
    }

    private static async Task Wait(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(25); }
        Assert.True(condition());
    }

    [DesktopBusTheory]
    [InlineData("org.freedesktop.DBus.Error.UnknownMethod")]
    [InlineData("org.freedesktop.DBus.Error.UnknownInterface")]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown")]
    public async Task AnUnavailableShortcutPortalKeepsDeckFocusRoutingAvailable(string error)
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        await desktop.RequestNameAsync("org.kde.KWin");
        var fake = new DesktopBackend(new DesktopBus(desktop)) { CreateError = error };
        desktop.AddMethodHandler(fake);
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        using var keys = new DesktopKeys(client);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, FocusChannels = ["music"] }, save: false);
        Assert.Contains("focus routing is enabled", keys.Status);
        Assert.Contains("does not offer global shortcuts", keys.Status);
        Assert.Equal(["4242"], await Task.Run(() => DesktopFocusQuery.ReadAudioProcesses(["4242", "4343"])));
        Assert.Equal(["CreateSession"], fake.ShortcutMethods);
    }

    // --- soundboard keys ---

    private static async Task<(SocketTestServer Server, ConcurrentQueue<string> Commands)> SoundboardDaemon(Func<string, string?>? refuse = null)
    {
        var commands = new ConcurrentQueue<string>();
        var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["requestId"] is not { } id) continue;
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd is not ("playClip" or "releaseClip" or "stopClips")) continue;
                string entry = cmd + (command["clip"] is { } clip ? " " + clip.GetValue<string>() : "")
                    + (command["mode"] is { } mode ? " " + mode.GetValue<string>() : "");
                commands.Enqueue(entry);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = id.GetValue<string>(), error = refuse?.Invoke(entry) }, stop);
            }
        });
        return (server, commands);
    }

    private static async Task<DaemonClient> Connect(SocketTestServer server)
    {
        var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return client;
    }

    // A round trip on the portal's connection lets every signal sent before it arrive.
    private static Task Flush(DBusConnection desktop) => new DesktopBus(desktop).Call(DesktopFocus.Service, "/org/openxlr/Desktop",
        "org.freedesktop.DBus.Introspectable", "Introspect", null, null, static (message, _) => message.GetBodyReader().ReadString());

    [DesktopBusFact]
    public async Task ClipKeysFollowTheLibraryOnTheirOwnSessionAndHoldWhileTheKeyIsDown()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop));
        desktop.AddMethodHandler(fake);
        var (server, commands) = await SoundboardDaemon(entry => entry.StartsWith("playClip off", StringComparison.Ordinal)
            ? "The soundboard is off. Turn it on first." : null);
        await using var hosted = server;
        await using var client = await Connect(server);
        using var keys = new DesktopKeys(client);
        Assert.True(keys.DeliversRelease); // the private bus names KDE

        // Before the daemon reports the board, every chosen clip keeps its key under its id.
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, Clips = ["horn", "drum", "off", "Bad_Id"], ClipsStop = true }, save: false);
        Assert.StartsWith("Desktop keys are active", keys.Status);
        Assert.Equal(["clip_horn", "clip_drum", "clip_off", "clips_stop"], fake.ShortcutIds);
        Assert.Equal("Play clip horn", fake.Descriptions["clip_horn"]);
        Assert.Equal("Stop all clips", fake.Descriptions["clips_stop"]);

        // The board arrives: the same session binds the names, in library order, without a clip nobody chose.
        keys.SetClips([new("drum", "Drum roll", "overlap"), new("horn", "Air\nhorn", "hold"), new("off", "Off", "hold"), new("other", "Other", "overlap")], fresh: true);
        await Wait(() => fake.Descriptions.GetValueOrDefault("clip_horn") == "Play clip Airhorn");
        Assert.Equal(["CreateSession", "BindShortcuts", "BindShortcuts"], fake.ShortcutMethods);
        Assert.Equal(["clip_drum", "clip_horn", "clip_off", "clips_stop"], fake.ShortcutIds);
        keys.SetClips([new("drum", "Drum roll", "overlap"), new("horn", "Air\nhorn", "hold"), new("off", "Off", "hold"), new("other", "Other", "overlap")], fresh: false);
        keys.SetClips([new("drum", "Drum roll", "toggle"), new("horn", "Air\nhorn", "hold"), new("off", "Off", "hold"), new("other", "Other renamed", "overlap")], fresh: false);
        await Task.Delay(80);
        Assert.Equal(3, fake.ShortcutMethods.Count); // same library, a mode, or a clip without a key: nothing to bind

        // A hold clip: the press holds, a repeat while down is ignored, the release ends it.
        fake.Activate("clip_horn");
        fake.Activate("clip_horn");
        fake.Deactivate("clip_horn");
        await Wait(() => commands.Count == 2);
        // Other modes are sent as they are, and their key coming up sends nothing.
        fake.Activate("clip_drum");
        fake.Deactivate("clip_drum");
        fake.Activate("clips_stop");
        await Wait(() => commands.Count == 4);
        await Flush(desktop);
        await Task.Delay(80);
        Assert.Equal(["playClip horn hold", "releaseClip horn", "playClip drum toggle", "stopClips"], commands);
        Assert.Equal("Completed: Stop all clips.", keys.Status);

        // A refusal shows the daemon's sentence, and a refused hold has nothing to release.
        fake.Activate("clip_off");
        await Wait(() => keys.Status == "The soundboard is off. Turn it on first.");
        fake.Deactivate("clip_off");
        await Flush(desktop);
        await Task.Delay(80);
        Assert.Equal(5, commands.Count);

        // A rename binds the new name on the same session; a deletion drops the key.
        keys.SetClips([new("drum", "Snare", "toggle"), new("off", "Off", "hold"), new("other", "Other renamed", "overlap")], fresh: false);
        // The keys bound before answer until the desktop has confirmed the new list.
        await Wait(() => keys.Status.StartsWith("Desktop keys are active", StringComparison.Ordinal));
        Assert.Equal(["clip_drum", "clip_off", "clips_stop"], fake.ShortcutIds);
        Assert.Equal("Play clip Snare", fake.Descriptions["clip_drum"]);
        fake.Activate("clip_horn");
        await Flush(desktop);
        await Task.Delay(80);
        Assert.Equal(5, commands.Count);
        Assert.Equal(["CreateSession", "BindShortcuts", "BindShortcuts", "BindShortcuts"], fake.ShortcutMethods);
        Assert.Equal(["drum", "off"], DesktopKeySettings.Load().Clips); // the deleted clip's tick is forgotten

        // A session that ends while a key holds a clip ends the hold.
        keys.SetClips([new("drum", "Snare", "hold"), new("off", "Off", "hold")], fresh: false);
        fake.Activate("clip_drum");
        await Wait(() => commands.Count == 6);
        await Task.Delay(80);
        fake.CloseSession();
        await Wait(() => commands.Count == 7);
        Assert.Equal(["playClip drum hold", "releaseClip drum"], commands.Skip(5));
    }

    [DesktopBusFact]
    public async Task HoldClipsPlayAsOverlapUntilTheDesktopReportsAKeyComingUp()
    {
        await using var environment = await PrivateBus.Start();
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "LXQt");
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop));
        desktop.AddMethodHandler(fake);
        var (server, commands) = await SoundboardDaemon();
        await using var hosted = server;
        await using var client = await Connect(server);
        using var keys = new DesktopKeys(client);
        Assert.False(keys.DeliversRelease);
        keys.SetClips([new("horn", "Air horn", "hold")], fresh: true);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, Clips = ["horn"] }, save: false);
        Assert.Equal("Play clip Air horn", Assert.Single(fake.Descriptions).Value);

        // Without a release a hold could never end: every press plays as overlap, and the status says why.
        fake.Activate("clip_horn");
        fake.Activate("clip_horn");
        await Wait(() => commands.Count == 2);
        await Wait(() => keys.Status.StartsWith("Played Air horn as Overlap", StringComparison.Ordinal));
        Assert.Equal(["playClip horn overlap", "playClip horn overlap"], commands);

        // The first key coming up shows this desktop reports releases: holds work from then on.
        fake.Deactivate("clip_horn");
        await Wait(() => keys.DeliversRelease);
        fake.Activate("clip_horn");
        fake.Deactivate("clip_horn");
        await Wait(() => commands.Count == 4);
        Assert.Equal(["playClip horn hold", "releaseClip horn"], commands.Skip(2));
    }

    [DesktopBusFact]
    public async Task ADesktopThatBindsASessionOnceGetsANewSessionForTheNewList()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop)) { RefuseRebind = true };
        desktop.AddMethodHandler(fake);
        var (server, commands) = await SoundboardDaemon();
        await using var hosted = server;
        await using var client = await Connect(server);
        using var keys = new DesktopKeys(client);
        keys.SetClips([new("horn", "Air horn", "overlap"), new("drum", "Drum", "overlap")], fresh: true);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, Clips = ["horn", "drum"], MonitorControls = true }, save: false);
        keys.SetClips([new("horn", "Horn", "overlap")], fresh: false);
        await Wait(() => fake.ShortcutMethods.Count == 5);
        Assert.Equal(["CreateSession", "BindShortcuts", "BindShortcuts", "CreateSession", "BindShortcuts"], fake.ShortcutMethods);
        Assert.Equal(["monitor_mute", "clip_horn"], fake.ShortcutIds);
        Assert.Equal("Play clip Horn", fake.Descriptions["clip_horn"]);
        Assert.StartsWith("Desktop keys are active", keys.Status);
        fake.Activate("clip_horn");
        await Wait(() => commands.Count == 1);
        Assert.Equal("playClip horn overlap", Assert.Single(commands));
    }

    [DesktopBusFact]
    public async Task OnlyADeletionSeenOnOneDaemonConnectionDropsAClipsKey()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop));
        desktop.AddMethodHandler(fake);
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        using var keys = new DesktopKeys(client);
        keys.SetClips([new("horn", "Air horn", "overlap"), new("drum", "Drum", "overlap")], fresh: true);
        var settings = new DesktopKeySettings { Enabled = true, Clips = ["horn", "drum"] };
        await keys.ConfigureAsync(settings);
        Assert.Equal(["clip_horn", "clip_drum"], fake.ShortcutIds);
        int binds = fake.ShortcutMethods.Count;

        // The daemon restarts and cannot read its library: an empty first
        // report says nothing was deleted, so every key keeps its name.
        keys.SetClips([], fresh: true);
        keys.SetClips([], fresh: false);
        // Another restart, and the library loads without a clip it refused.
        keys.SetClips([new("drum", "Drum", "overlap")], fresh: true);
        await Task.Delay(120);
        Assert.Equal(binds, fake.ShortcutMethods.Count);
        Assert.Equal("Play clip Air horn", fake.Descriptions["clip_horn"]);
        Assert.Equal(["horn", "drum"], DesktopKeySettings.Load().Clips);
        Assert.False(keys.IsDeleted("horn"));

        // The Desktop keys window keeps the tick of a clip the daemon does not report now.
        Assert.Equal([new("drum", "Play clip Drum", true), new("horn", "Play clip Air horn (the daemon does not report it now)", true)],
            keys.ClipChoices(DesktopKeySettings.Load(), [("drum", "Drum")]));
        Assert.Equal([new("horn", "Play clip Air horn (the daemon does not report it now)", true), new("drum", "Play clip Drum (the daemon does not report it now)", true)],
            keys.ClipChoices(DesktopKeySettings.Load(), []));

        // In one report and missing from the next on the same connection: deleted.
        keys.SetClips([], fresh: false);
        await Wait(() => fake.ShortcutMethods.Count == binds + 1);
        await Wait(() => keys.Status.StartsWith("Desktop keys are active", StringComparison.Ordinal));
        Assert.Equal(["clip_horn"], fake.ShortcutIds);
        Assert.True(keys.IsDeleted("drum"));
        Assert.Equal(["horn"], DesktopKeySettings.Load().Clips);
        Assert.Equal([new("horn", "Play clip Air horn (the daemon does not report it now)", true)],
            keys.ClipChoices(new DesktopKeySettings { Clips = ["horn", "drum"] }, []));
        // An Apply that still names the deleted clip leaves it out.
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, Clips = ["horn", "drum"] }, save: false);
        Assert.Equal(["clip_horn"], fake.ShortcutIds);
    }

    [DesktopBusFact]
    public async Task DeletingEveryTickedClipTellsTheDesktopBeforeTheSessionCloses()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop));
        desktop.AddMethodHandler(fake);
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        using var keys = new DesktopKeys(client);
        keys.SetClips([new("horn", "Air horn", "overlap")], fresh: true);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, Clips = ["horn"] }, save: false);
        keys.SetClips([], fresh: false);
        await Wait(() => keys.Status.StartsWith("OpenDeck focus routing is enabled", StringComparison.Ordinal));
        Assert.Equal(["CreateSession", "BindShortcuts", "BindShortcuts"], fake.ShortcutMethods);
        Assert.Empty(fake.ShortcutIds); // KDE forgets the keys of what an empty bind leaves out
    }

    [DesktopBusFact]
    public async Task AHoldWhoseReleaseWasLostEndsWithTheClipOrItsLength()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop));
        desktop.AddMethodHandler(fake);
        var (server, commands) = await SoundboardDaemon();
        await using var hosted = server;
        await using var client = await Connect(server);
        using var keys = new DesktopKeys(client);
        DesktopKeys.SoundClip loop = new("loop", "Loop", "hold", 4, Loop: true), blip = new("blip", "Blip", "hold", .2);
        keys.SetClips([loop, blip], fresh: true);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, Clips = ["loop", "blip"] }, save: false);

        // A window that grabbed the keyboard took the release. The key still
        // counts as down, but once the daemon reports the clip stopped, the
        // hold is over and the next press plays.
        fake.Activate("clip_loop");
        await Wait(() => commands.Count == 1);
        await Task.Delay(80);
        fake.Activate("clip_loop");
        await Flush(desktop);
        await Task.Delay(80);
        Assert.Single(commands);
        keys.SetClips([loop with { Playing = true }, blip], fresh: false);
        keys.SetClips([loop, blip], fresh: false); // Stop all
        await Wait(() => commands.Count == 2);
        fake.Activate("clip_loop");
        await Wait(() => commands.Count == 3);
        Assert.Equal(["playClip loop hold", "releaseClip loop", "playClip loop hold"], commands);

        // A key down longer than its clip lost its release: the next press is new.
        fake.Activate("clip_blip");
        fake.Activate("clip_blip");
        await Wait(() => commands.Count == 4);
        await Flush(desktop);
        await Task.Delay(80);
        Assert.Equal(4, commands.Count);
        await Task.Delay(600);
        fake.Activate("clip_blip");
        await Wait(() => commands.Count == 5);
        Assert.Equal(["playClip blip hold", "playClip blip hold"], commands.Skip(3));

        // A clip deleted while a key holds it ends its hold.
        keys.SetClips([loop with { Playing = true }], fresh: false);
        await Wait(() => commands.Contains("releaseClip blip"));
        await Wait(() => fake.ShortcutIds.SequenceEqual(["clip_loop"]));
    }

    [DesktopBusFact]
    public async Task AReplacementSessionThatFailsKeepsTheWorkingKeys()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop)) { RefuseRebind = true, FailLaterSessions = true };
        desktop.AddMethodHandler(fake);
        var (server, commands) = await SoundboardDaemon();
        await using var hosted = server;
        await using var client = await Connect(server);
        using var keys = new DesktopKeys(client);
        keys.SetClips([new("horn", "Air horn", "overlap"), new("drum", "Drum", "overlap")], fresh: true);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, Clips = ["horn", "drum"] }, save: false);
        keys.SetClips([new("horn", "Horn", "overlap"), new("drum", "Drum", "overlap")], fresh: false);
        await Wait(() => keys.Status.Contains("No second session", StringComparison.Ordinal));
        Assert.Equal(["CreateSession", "BindShortcuts", "BindShortcuts", "CreateSession"], fake.ShortcutMethods);
        fake.Activate("clip_drum");
        await Wait(() => commands.Count == 1);
        Assert.Equal("playClip drum overlap", Assert.Single(commands));
    }

    [DesktopBusFact]
    public async Task ABindAnsweredAfterItsTimeoutIsTakenFromTheDesktopsList()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop));
        desktop.AddMethodHandler(fake);
        var (server, commands) = await SoundboardDaemon();
        await using var hosted = server;
        await using var client = await Connect(server);
        using var keys = new DesktopKeys(client) { BindTimeout = TimeSpan.FromMilliseconds(300) };
        keys.SetClips([new("horn", "Air horn", "overlap"), new("drum", "Drum", "overlap")], fresh: true);
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, Clips = ["horn", "drum"] }, save: false);
        Assert.Equal(["clip_horn", "clip_drum"], fake.Bound);

        // The desktop sits on the new list past the timeout: the keys bound before keep working.
        fake.HoldBindingResponse = true;
        keys.SetClips([new("horn", "Air horn", "overlap")], fresh: false);
        await Wait(() => keys.Status.Contains("did not answer", StringComparison.Ordinal));
        await Wait(() => fake.ListRequests == 1);
        fake.Activate("clip_horn");
        fake.Deactivate("clip_horn");
        await Wait(() => commands.Count == 1);

        // It answers later. A key the list in use does not have makes the UI
        // ask again, and it takes the list the desktop now has.
        fake.HoldBindingResponse = false;
        fake.CompleteBinding();
        Assert.Equal(["clip_horn"], fake.Bound);
        fake.Activate("clip_other");
        await Wait(() => fake.ListRequests == 2);
        await Wait(() => keys.Status.StartsWith("Desktop keys are active", StringComparison.Ordinal));
        fake.Activate("clip_drum");
        fake.Activate("clip_horn");
        await Wait(() => commands.Count == 2);
        await Flush(desktop);
        await Task.Delay(80);
        Assert.Equal(["playClip horn overlap", "playClip horn overlap"], commands);
    }

    [Theory]
    [InlineData("KDE", true)]
    [InlineData("ubuntu:GNOME", true)]
    [InlineData("Hyprland", true)]
    [InlineData("sway", false)]
    [InlineData("", false)]
    public void TheDesktopsKnownToReportReleases(string desktop, bool releases)
        => Assert.Equal(releases, DesktopKeys.ReportsRelease(name => name == "XDG_CURRENT_DESKTOP" ? desktop : null));

    [Fact]
    public void ClipChoicesKeepOnlyClipIds()
    {
        var normalized = new DesktopKeySettings { Clips = [null!, "", "Horn", "horn", "horn", "drum_roll", "9lives", "drum-roll",
            new string('a', 41), .. Enumerable.Range(0, 80).Select(i => "clip" + i)] }.Normalize();
        Assert.Equal(["horn", "drum-roll"], normalized.Clips.Take(2));
        Assert.Equal(64, normalized.Clips.Count);
    }

    private sealed class DesktopBackend(DesktopBus bus) : IPathMethodHandler
    {
        public string Path => "/";
        public bool HandlesChildPaths => true;
        public List<string> ShortcutIds { get; } = [];
        public Dictionary<string, string> Descriptions { get; } = [];
        public Dictionary<string, string> PreferredTriggers { get; } = [];
        /// <summary>Answer a second BindShortcuts on one session with 2, as GNOME does.</summary>
        public bool RefuseRebind { get; init; }
        /// <summary>Refuse every CreateSession after the first.</summary>
        public bool FailLaterSessions { get; init; }
        private int _sessions;
        private string _boundSession = "";
        /// <summary>The ids of the last bind the desktop answered, which ListShortcuts reports.</summary>
        public List<string> Bound { get; private set; } = [];
        public int ListRequests { get; private set; }
        public bool RequireHostIdentity { get; init; }
        public string? RegisterError { get; init; }
        private string? _hostSender;
        public bool CancelBinding { get; set; }
        public bool HoldBindingResponse { get; set; }
        public string? ConfigureError { get; init; }
        public string? CreateError { get; init; }
        public int ConfigureRequests { get; private set; }
        public List<string> ShortcutMethods { get; } = [];
        private Action? _pendingBinding;
        internal bool HasPendingBinding => Volatile.Read(ref _pendingBinding) is not null;
        internal void CompleteBinding() => Interlocked.Exchange(ref _pendingBinding, null)?.Invoke();
        public int Unloads { get; private set; }
        private string _session = "", _destination = "", _cookie = "";

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            var reader = request.GetBodyReader();
            string member = request.MemberAsString!;
            if (RequireHostIdentity && request.InterfaceAsString == "org.freedesktop.host.portal.Registry" && member == "Register")
            {
                Assert.Equal("openxlr", reader.ReadString());
                Assert.Empty(reader.ReadDictionaryOfStringToVariantValue());
                _hostSender = request.SenderAsString;
                if (RegisterError is { } refusal) context.ReplyError(refusal, "Could not register app ID: App info not found for 'openxlr'");
                else { using var reply = context.CreateReplyWriter(null); context.Reply(reply.CreateMessage()); }
            }
            else if (request.InterfaceAsString == DesktopBus.Shortcuts)
            {
                if (RequireHostIdentity) Assert.Equal(_hostSender, request.SenderAsString);
                ShortcutMethods.Add(member);
                if (member == "ConfigureShortcuts")
                {
                    Assert.Equal(_session, reader.ReadObjectPath().ToString());
                    Assert.Equal("", reader.ReadString());
                    Assert.Empty(reader.ReadDictionaryOfStringToVariantValue());
                    Assert.False(HasPendingBinding);
                    ConfigureRequests++;
                    if (ConfigureError is { } error) context.ReplyError(error, "The portal rejected configuration.");
                    else { using var empty = context.CreateReplyWriter(null); context.Reply(empty.CreateMessage()); }
                    return ValueTask.CompletedTask;
                }
                Dictionary<string, VariantValue> options;
                if (member == "CreateSession")
                {
                    if (CreateError is { } error) { context.ReplyError(error, "Global shortcuts are unavailable."); return ValueTask.CompletedTask; }
                    if (FailLaterSessions && ++_sessions > 1)
                    {
                        context.ReplyError("org.freedesktop.portal.Error.Failed", "No second session.");
                        return ValueTask.CompletedTask;
                    }
                    options = reader.ReadDictionaryOfStringToVariantValue();
                }
                else if (member == "ListShortcuts")
                {
                    Assert.Equal(_session, reader.ReadObjectPath().ToString());
                    options = reader.ReadDictionaryOfStringToVariantValue();
                    ListRequests++;
                }
                else
                {
                    Assert.Equal("BindShortcuts", member);
                    Assert.Equal(_session, reader.ReadObjectPath().ToString());
                    ShortcutIds.Clear();
                    Descriptions.Clear();
                    PreferredTriggers.Clear();
                    var end = reader.ReadArrayStart(DBusType.Struct);
                    while (reader.HasNext(end))
                    {
                        reader.AlignStruct();
                        string id = reader.ReadString();
                        ShortcutIds.Add(id);
                        var properties = reader.ReadDictionaryOfStringToVariantValue();
                        Descriptions[id] = properties["description"].GetString();
                        if (properties.TryGetValue("preferred_trigger", out VariantValue trigger))
                            PreferredTriggers[id] = trigger.GetString();
                    }
                    reader.ReadString(); options = reader.ReadDictionaryOfStringToVariantValue();
                }
                string token = options["handle_token"].GetString();
                string owner = request.SenderAsString![1..].Replace('.', '_');
                string path = "/org/freedesktop/portal/desktop/request/" + owner + "/" + token;
                if (member == "CreateSession") _session = "/org/freedesktop/portal/desktop/session/" + owner + "/" + token;
                using var reply = context.CreateReplyWriter("o"); reply.WriteObjectPath(path); context.Reply(reply.CreateMessage());
                string destination = request.SenderAsString!;
                bool refused = false;
                List<string> requested = [.. ShortcutIds];
                if (member == "BindShortcuts")
                {
                    refused = RefuseRebind && _boundSession == _session;
                    _boundSession = _session;
                }
                void Respond()
                {
                    using var signal = bus.Connection.GetMessageWriter();
                    signal.WriteSignalHeader(destination: destination, path: path, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
                    uint code = refused ? 2u : member == "BindShortcuts" && CancelBinding ? 1u : 0u;
                    signal.WriteUInt32(code);
                    if (member == "BindShortcuts" && code == 0) Bound = requested;
                    if (member == "ListShortcuts")
                    {
                        var results = signal.WriteDictionaryStart();
                        signal.WriteDictionaryEntryStart();
                        signal.WriteString("shortcuts");
                        signal.WriteSignature("a(sa{sv})");
                        var list = signal.WriteArrayStart(DBusType.Struct);
                        foreach (string id in Bound)
                        {
                            signal.WriteStructureStart();
                            signal.WriteString(id);
                            signal.WriteDictionary(new Dictionary<string, VariantValue> { ["description"] = id });
                        }
                        signal.WriteArrayEnd(list);
                        signal.WriteDictionaryEnd(results);
                    }
                    else signal.WriteDictionary(member == "CreateSession" ? new Dictionary<string, VariantValue> { ["session_handle"] = _session } : []);
                    bus.Connection.TrySendMessage(signal.CreateMessage());
                }
                if (member == "BindShortcuts" && HoldBindingResponse) Volatile.Write(ref _pendingBinding, Respond);
                else Respond();
            }
            else if (member == "unloadScript")
            {
                Unloads++;
                using var reply = context.CreateReplyWriter("b"); reply.WriteBool(true); context.Reply(reply.CreateMessage());
            }
            else if (member == "loadScript")
            {
                string file = reader.ReadString();
                string[] literals = Regex.Matches(File.ReadAllText(file), "\"(?:\\\\.|[^\"\\\\])*\"")
                    .Select(m => JsonSerializer.Deserialize<string>(m.Value)!).ToArray();
                _destination = literals[0]; _cookie = literals[4];
                using var reply = context.CreateReplyWriter("i"); reply.WriteInt32(7); context.Reply(reply.CreateMessage());
            }
            else if (member == "run")
            {
                using var reply = context.CreateReplyWriter(null); context.Reply(reply.CreateMessage());
                _ = bus.Empty(_destination, "/org/openxlr/Desktop", "org.openxlr.Desktop", "ReportFocus", "ss",
                    (ref MessageWriter w) => { w.WriteString(_cookie); w.WriteString("4242"); });
            }
            else context.ReplyUnknownMethodError();
            return ValueTask.CompletedTask;
        }

        internal void CloseSession()
        {
            using var signal = bus.Connection.GetMessageWriter();
            signal.WriteSignalHeader(path: _session, @interface: "org.freedesktop.portal.Session", member: "Closed", signature: "a{sv}");
            signal.WriteDictionary(new Dictionary<string, VariantValue>());
            bus.Connection.TrySendMessage(signal.CreateMessage());
        }

        internal void Deactivate(string id) => Activate(id, member: "Deactivated");

        internal void Activate(string id, string? session = null, string member = "Activated")
        {
            using var signal = bus.Connection.GetMessageWriter();
            signal.WriteSignalHeader(path: DesktopBus.PortalPath, @interface: DesktopBus.Shortcuts, member: member, signature: "osta{sv}");
            signal.WriteObjectPath(session ?? _session); signal.WriteString(id); signal.WriteUInt64(100);
            signal.WriteDictionary(new Dictionary<string, VariantValue>());
            bus.Connection.TrySendMessage(signal.CreateMessage());
        }
    }

    internal sealed class PrivateBus : IAsyncDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openxlr-keys-" + Guid.NewGuid());
        private readonly string? _oldBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly string? _oldDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        private readonly CancellationTokenSource _stop = new();
        private Task<ProcessResult>? _process;
        internal string Address => "unix:path=" + _directory + "/bus";
        internal static async Task<PrivateBus> Start()
        {
            var value = new PrivateBus();
            try
            {
                Directory.CreateDirectory(value._directory);
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", value._directory);
                Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "KDE");
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", value.Address);
                value._process = ProcessRunner.RunAsync("dbus-daemon", ["--session", "--nofork", "--address=" + value.Address],
                    TimeSpan.FromSeconds(40), cancel: value._stop.Token);
                await Wait(() => File.Exists(value._directory + "/bus"));
                return value;
            }
            catch { await value.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_process is not null) await _process;
            _stop.Dispose();
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", _oldBus);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", _oldDesktop);
            Directory.Delete(_directory, recursive: true);
        }
    }
}

/// <summary>The private session bus of these tests needs <c>dbus-daemon</c> on PATH.</summary>
internal static class DesktopBusSkip
{
    public static readonly string? Reason = Environment.GetEnvironmentVariable("PATH")?
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(dir => Executable(Path.Combine(dir, "dbus-daemon"))) == true
        ? null : "dbus-daemon is not installed; these tests start a private session bus.";

    private static bool Executable(string file)
        => File.Exists(file) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0);
}

internal sealed class DesktopBusFactAttribute : FactAttribute
{
    public DesktopBusFactAttribute() { if (DesktopBusSkip.Reason is { } reason) Skip = reason; }
}

internal sealed class DesktopBusTheoryAttribute : TheoryAttribute
{
    public DesktopBusTheoryAttribute() { if (DesktopBusSkip.Reason is { } reason) Skip = reason; }
}
