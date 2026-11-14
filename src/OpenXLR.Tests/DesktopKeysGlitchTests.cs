using System.Collections.Concurrent;
using OpenXLR.UI;
using Tmds.DBus.Protocol;

namespace OpenXLR.Tests;

// The key that marks a glitch, against the private session bus and a daemon
// that records every markGlitch with its label.
public sealed partial class DesktopKeysTests
{
    [DesktopBusFact]
    public async Task TheGlitchSettingIsSavedAndOffInAFileFromBeforeIt()
    {
        await using var environment = await PrivateBus.Start();
        Directory.CreateDirectory(Path.GetDirectoryName(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-keys.json"))!);
        File.WriteAllText(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-keys.json"), """{ "enabled": true, "micMute": true }""");
        Assert.False(DesktopKeySettings.Load().GlitchMark);
        new DesktopKeySettings { Enabled = true, GlitchMark = true }.Save();
        Assert.True(DesktopKeySettings.Load().GlitchMark);
        Assert.Contains("\"glitchMark\": true", File.ReadAllText(OpenXLR.Core.OpenXlrPaths.ConfigFile("desktop-keys.json")));
    }

    [DesktopBusFact]
    public async Task TheGlitchKeyMarksOncePerPressWithItsOwnLabel()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(DesktopBus.Portal);
        var fake = new DesktopBackend(new DesktopBus(desktop));
        desktop.AddMethodHandler(fake);
        string? refusal = null;
        var (server, labels) = await GlitchDaemon(() => refusal);
        await using var hosted = server;
        await using var client = await Connect(server);
        using var keys = new DesktopKeys(client);
        var notices = new ConcurrentQueue<string>();
        keys.GlitchMarked += notices.Enqueue;

        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, GlitchMark = true }, save: false);
        Assert.Equal([DesktopKeys.GlitchKey], fake.ShortcutIds);
        Assert.Equal("Mark a glitch for the diagnostics archive", fake.Descriptions[DesktopKeys.GlitchKey]);
        Assert.False(fake.PreferredTriggers.ContainsKey(DesktopKeys.GlitchKey)); // no key to suggest
        Assert.Empty(labels);

        // A press marks one glitch, labelled as a desktop key's, however
        // often the desktop repeats the key while it is down.
        fake.Activate(DesktopKeys.GlitchKey);
        fake.Activate(DesktopKeys.GlitchKey);
        await Wait(() => labels.Count == 1);
        await Flush(desktop);
        await Task.Delay(80);
        Assert.Equal(["Desktop key"], labels);
        await Wait(() => keys.Status.StartsWith("Glitch marked at ", StringComparison.Ordinal));
        Assert.Matches(@"^Glitch marked at \d\d:\d\d:\d\d$", keys.Status);
        await Wait(() => notices.Count == 1);
        Assert.Equal(keys.Status, Assert.Single(notices)); // the main window shows the same sentence

        // Let go and press again: a second marker.
        fake.Deactivate(DesktopKeys.GlitchKey);
        fake.Activate(DesktopKeys.GlitchKey);
        await Wait(() => labels.Count == 2);

        // A refusal shows the daemon's sentence, in the status line and the notice.
        refusal = "The daemon is shutting down.";
        fake.Deactivate(DesktopKeys.GlitchKey);
        fake.Activate(DesktopKeys.GlitchKey);
        await Wait(() => keys.Status == "Not marked: The daemon is shutting down.");
        await Wait(() => notices.Count == 3);
        Assert.Equal("Not marked: The daemon is shutting down.", notices.Last());

        // Turned off, the key is gone and a stale press marks nothing.
        await keys.ConfigureAsync(new DesktopKeySettings { Enabled = true, MicMute = true }, save: false);
        Assert.DoesNotContain(DesktopKeys.GlitchKey, fake.ShortcutIds);
        fake.Deactivate(DesktopKeys.GlitchKey);
        fake.Activate(DesktopKeys.GlitchKey);
        await Flush(desktop);
        await Task.Delay(80);
        Assert.Equal(3, labels.Count);
    }

    [Fact]
    public async Task TheUiStillLabelsItsOwnGlitchesUi()
    {
        var (server, labels) = await GlitchDaemon(() => null);
        await using var hosted = server;
        await using var client = await Connect(server);
        Assert.Null(await client.MarkGlitchAsync());
        Assert.Null(await client.MarkGlitchAsync(DesktopKeys.GlitchLabel));
        Assert.Equal(["UI", "Desktop key"], labels);
    }

    /// <summary>A daemon that records the label of every markGlitch and answers it, refused while <paramref name="refuse"/> says so.</summary>
    private static async Task<(SocketTestServer Server, ConcurrentQueue<string> Labels)> GlitchDaemon(Func<string?> refuse)
    {
        var labels = new ConcurrentQueue<string>();
        var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["requestId"] is not { } id || command["cmd"]?.GetValue<string>() != "markGlitch") continue;
                labels.Enqueue(command["label"]?.GetValue<string>() ?? "");
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = id.GetValue<string>(), error = refuse() }, stop);
            }
        });
        return (server, labels);
    }
}
