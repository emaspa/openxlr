using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class BrowserSessionSocketTests
{
    [Fact]
    public async Task OnlyTheOwningSocketCanPublishTabsAndDisconnectRemovesThem()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-browser-socket-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", directory);
        try
        {
            ApiToken.Initialize();
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            using var service = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            using var lifetime = new Lifetime();
            var hub = new WebSocketHub(devices, service, NullLogger<WebSocketHub>.Instance, lifetime);
            Mixer mixer = (Mixer)typeof(MixerService).GetField("_mixer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = await SocketTestServer.Start(async (socket, _) =>
            {
                try { await hub.HandleAsync(socket); }
                finally { disconnected.TrySetResult(); }
            });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var first = new ClientWebSocket();
            using var second = new ClientWebSocket();
            try
            {
                await first.ConnectAsync(new Uri(server.Url), deadline.Token);
                await second.ConnectAsync(new Uri(server.Url), deadline.Token);
                await SocketTestServer.Send(first, new { cmd = "auth", token = ApiToken.Current }, deadline.Token);
                await SocketTestServer.Send(second, new { cmd = "auth", token = ApiToken.Current }, deadline.Token);
                var one = await Query(first, new { cmd = "getBrowserSession" }, deadline.Token);
                var two = await Query(second, new { cmd = "getBrowserSession" }, deadline.Token);
                string session = one.Single(m => m["type"]?.GetValue<string>() == "browserSession")["session"]!.GetValue<string>();
                string other = two.Single(m => m["type"]?.GetValue<string>() == "browserSession")["session"]!.GetValue<string>();
                Assert.NotEqual(session, other);
                Assert.True(BrowserTabRouting.ValidSession(session));
                await Query(first, new { cmd = "syncBrowserTabs", session, tabs = new[] { new { id = 1, site = "https://teams.microsoft.com", title = "Call", capturing = false } } }, deadline.Token);
                await Query(second, new { cmd = "syncBrowserTabs", session = other, tabs = new[] { new { id = 2, site = "https://youtube.com", title = "Video", capturing = true } } }, deadline.Token);
                Assert.Equal(2, mixer.BrowserTabs.Count);
                var denied = await Query(second, new { cmd = "syncBrowserTabs", session, tabs = Array.Empty<object>() }, deadline.Token);
                Assert.Contains("does not own", denied.Last()["error"]!.GetValue<string>());
                Assert.Equal(2, mixer.BrowserTabs.Count);
                var repeated = await Query(first, new { cmd = "getBrowserSession" }, deadline.Token);
                Assert.Equal(session, repeated.Single(m => m["type"]?.GetValue<string>() == "browserSession")["session"]!.GetValue<string>());
                Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"getBrowserSession"}""")).Ok);
                Assert.False((await hub.ExecuteForApiAsync(JsonSerializer.Serialize(new { cmd = "syncBrowserTabs", session, tabs = Array.Empty<object>() }))).Ok);
                await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", deadline.Token);
                await disconnected.Task.WaitAsync(deadline.Token);
                Assert.Equal(other, Assert.Single(mixer.BrowserTabs).Session);
            }
            finally { lifetime.StopApplication(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previous);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ActivationReachesOnlyTheCompanionThatRegisteredTheTab()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-browser-socket-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", directory);
        try
        {
            ApiToken.Initialize();
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            using var service = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            using var lifetime = new Lifetime();
            var hub = new WebSocketHub(devices, service, NullLogger<WebSocketHub>.Instance, lifetime);
            Mixer mixer = (Mixer)typeof(MixerService).GetField("_mixer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            await using var server = await SocketTestServer.Start((socket, _) => hub.HandleAsync(socket));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var companion = new ClientWebSocket();
            using var controller = new ClientWebSocket();
            try
            {
                await companion.ConnectAsync(new Uri(server.Url), deadline.Token);
                await controller.ConnectAsync(new Uri(server.Url), deadline.Token);
                await SocketTestServer.Send(companion, new { cmd = "auth", token = ApiToken.Current }, deadline.Token);
                await SocketTestServer.Send(controller, new { cmd = "auth", token = ApiToken.Current }, deadline.Token);
                string session = (await Query(companion, new { cmd = "getBrowserSession" }, deadline.Token))
                    .Single(m => m["type"]?.GetValue<string>() == "browserSession")["session"]!.GetValue<string>();
                await Query(companion, new { cmd = "syncBrowserTabs", session, tabs = new[] { new { id = 1, site = "https://teams.microsoft.com", title = "Call", capturing = false } } }, deadline.Token);
                var activate = new { cmd = "activateBrowserTab", session, tabId = 1, site = "https://TEAMS.microsoft.com:443/" };

                // The tab needs routing on and a channel.
                Assert.Contains("enable individual browser tab routing", Error(await Query(controller, activate, deadline.Token)));
                mixer.SetPreserveBrowserOutputs(true);
                Assert.Contains("choose a channel", Error(await Query(controller, activate, deadline.Token)));
                mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", false, _ => null);

                List<JsonNode> replies = await Query(controller, activate, deadline.Token);
                Assert.Null(Error(replies));
                Assert.DoesNotContain(replies, m => m["type"]?.GetValue<string>() == "browserTabActivation");
                JsonNode request = await Next(companion, "browserTabActivation", deadline.Token);
                Assert.Equal(1, request["tabId"]!.GetValue<int>());
                Assert.Equal("https://teams.microsoft.com", request["site"]!.GetValue<string>());
                Assert.DoesNotContain(session, request.ToJsonString(), StringComparison.Ordinal);

                // HTTP reaches the same companion; a stale origin reaches nobody.
                Assert.True((await hub.ExecuteForApiAsync(JsonSerializer.Serialize(activate))).Ok);
                Assert.Equal(1, (await Next(companion, "browserTabActivation", deadline.Token))["tabId"]!.GetValue<int>());
                Assert.Contains("changed website", Error(await Query(controller, activate with { site = "https://youtube.com" }, deadline.Token)));

                // Once the browser reports the request, a second one is refused.
                await Query(companion, new { cmd = "syncBrowserTabs", session, tabs = new[] { new { id = 1, site = "https://teams.microsoft.com", title = "Call", capturing = false, activation = "popup" } } }, deadline.Token);
                Assert.Equal("popup", Assert.Single(mixer.BrowserTabs).Activation);
                Assert.Contains("already activating", Error(await Query(controller, activate, deadline.Token)));
            }
            finally { lifetime.StopApplication(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previous);
            Directory.Delete(directory, true);
        }

        static string? Error(List<JsonNode> replies) => replies.Last()["error"]?.GetValue<string>();
    }

    private static async Task<JsonNode> Next(WebSocket socket, string type, CancellationToken stop)
    {
        while (true)
        {
            JsonNode message = await SocketTestServer.Receive(socket, stop);
            if (message["type"]?.GetValue<string>() == type) return message;
        }
    }

    private static async Task<List<JsonNode>> Query(WebSocket socket, object command, CancellationToken stop)
    {
        string request = Guid.NewGuid().ToString("N");
        JsonObject body = JsonSerializer.SerializeToNode(command)!.AsObject();
        body["requestId"] = request;
        await SocketTestServer.Send(socket, body, stop);
        var replies = new List<JsonNode>();
        while (true)
        {
            JsonNode message = await SocketTestServer.Receive(socket, stop);
            replies.Add(message);
            if (message["type"]?.GetValue<string>() == "commandResult" && message["requestId"]?.GetValue<string>() == request) return replies;
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => _stop.Token;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
    }
}
