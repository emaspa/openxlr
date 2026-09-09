using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenXLR.Core;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

// Publishes a token file under a redirected runtime directory, like the other store tests.
[Collection("xdg-config")]
public sealed class HttpApiTests
{
    [Fact]
    public async Task ChunkedBodyCannotBypassTheSizeLimit()
    {
        using var stream = new MemoryStream(new byte[ApiEndpoints.MaxCommandBytes + 1]);
        Assert.Null(await ApiEndpoints.ReadCommandAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("application/json; charset=utf-16", false)]
    [InlineData("text/plain", false)]
    [InlineData("application/x-www-form-urlencoded", false)]
    [InlineData(null, false)]
    public void JsonContentTypeIsRequired(string? type, bool accepted)
        => Assert.Equal(accepted, ApiEndpoints.IsJson(type));

    [Fact]
    public async Task RealHttpRequestsEnforceAuthenticationOriginAndCommandLimits()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // Deliberately do not register hosted services: no USB/audio graph starts.
        builder.Services.AddSingleton<DeviceManager>();
        builder.Services.AddSingleton<MixerService>();
        builder.Services.AddSingleton<WebSocketHub>();
        await using var app = builder.Build();
        app.UseWebSockets();
        // The endpoints read the daemon's live token per request; publish one
        // into a scratch runtime directory the way the daemon does at start.
        string runtimeDir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string? previousRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtimeDir);
        string token;
        try { ApiToken.Initialize(); token = ApiToken.Current!; }
        finally { Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousRuntime); try { Directory.Delete(runtimeDir, recursive: true); } catch (IOException) { } }
        ApiEndpoints.Map(app);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            using var denied = await http.GetAsync("/api/v1");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            foreach (string path in new[] { "/api/v1/state", "/API/V1/state", "/api/v1/state/", "/api/v1/plugins" })
            {
                using var protectedRequest = await http.GetAsync(path);
                Assert.Equal(HttpStatusCode.Unauthorized, protectedRequest.StatusCode);
            }
            using var deniedCommand = await http.PostAsync("/API/V1/commands/",
                new StringContent("{\"cmd\":\"getDiagnostics\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, deniedCommand.StatusCode);
            using var health = await http.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var noUpgrade = await http.GetAsync("/api/v1/events");
            Assert.Equal(HttpStatusCode.BadRequest, noUpgrade.StatusCode);
            http.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
            using var foreignEvents = await http.GetAsync("/api/v1/events");
            Assert.Equal(HttpStatusCode.Forbidden, foreignEvents.StatusCode);
            http.DefaultRequestHeaders.Remove("Origin");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var accepted = await http.GetAsync("/api/v1");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            http.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
            using var foreign = await http.GetAsync("/api/v1");
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            http.DefaultRequestHeaders.Remove("Origin");
            using var plain = await http.PostAsync("/api/v1/commands", new StringContent("{}"));
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, plain.StatusCode);
            using var oversized = await http.PostAsync("/api/v1/commands",
                new StringContent(new string(' ', ApiEndpoints.MaxCommandBytes + 1), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            using var invalid = await http.PostAsync("/api/v1/commands",
                new StringContent("null", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var valid = await http.PostAsync("/api/v1/commands",
                new StringContent("{\"cmd\":\"getDiagnostics\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            Assert.Contains("\"ok\":true", await valid.Content.ReadAsStringAsync());
            using var pluginDiagnostics = await http.PostAsync("/api/v1/commands",
                new StringContent("{\"cmd\":\"getPluginDiagnostics\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, pluginDiagnostics.StatusCode);
            using var evidence = System.Text.Json.JsonDocument.Parse(await pluginDiagnostics.Content.ReadAsStringAsync());
            var discovery = evidence.RootElement.GetProperty("messages")[0];
            Assert.Equal("pluginDiagnostics", discovery.GetProperty("type").GetString());
            Assert.True(discovery.GetProperty("discovery").TryGetProperty("searchPaths", out _));
            Assert.True(discovery.GetProperty("discovery").TryGetProperty("scans", out _));
        }
        finally { await app.StopAsync(); }
    }
}
