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
        const string token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        ApiEndpoints.Map(app, token);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            using var denied = await http.GetAsync("/api/v1");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
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
        }
        finally { await app.StopAsync(); }
    }
}
