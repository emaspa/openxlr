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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PluginOperationRepliesCarryTheirFailureIntoTheCommandOutcome(bool ok)
    {
        string? expected = ok ? null : "operation failed";
        Assert.Equal(expected, WebSocketHub.OperationError(new PluginInstallMessage(ok, "operation failed", [], 0, 0)));
        Assert.Equal(expected, WebSocketHub.OperationError(new WindowsPluginFilesMessage(new(ok, "operation failed", []))));
    }

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealHttpRequestsEnforceAuthenticationOriginAndCommandLimits(bool enabled)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // Deliberately do not register hosted services: no USB/audio graph starts.
        builder.Services.AddSingleton<DeviceManager>();
        builder.Services.AddSingleton<MixerService>();
        builder.Services.AddSingleton<WebSocketHub>();
        string policyDirectory = Path.Combine(Path.GetTempPath(), "openxlr-editor-policy-api-" + Guid.NewGuid());
        builder.Services.AddSingleton(new OpenXLR.Core.Mixing.NativeEditorPolicy(Path.Combine(policyDirectory, "rules.json")));
        await using var app = builder.Build();
        app.UseWebSockets();
        TaskCompletionSource? commandReading = null;
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Test-Observe-Body") && commandReading is { } reading)
                context.Request.Body = new ObservedReadStream(context.Request.Body, reading);
            await next(context);
        });
        // The endpoints read the daemon's live token per request; publish one
        // into a scratch runtime directory the way the daemon does at start.
        string runtimeDir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string? previousRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtimeDir);
        string token;
        try { ApiToken.Initialize(); token = ApiToken.Current!; }
        finally { Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousRuntime); try { Directory.Delete(runtimeDir, recursive: true); } catch (IOException) { } }
        ApiEndpoints.Map(app, enabled);
        app.MapGet("/ws", () => "internal client endpoint remains available");
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            using var denied = await http.GetAsync("/api/v1");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            foreach (string path in new[] { "/api/v1/state", "/API/V1/state", "/api/v1/state/", "/api/v1/plugins", "/api/v1/devices", "/api/v1/mixer", "/api/v1/channels/music", "/api/v1/plugin-setup" })
            {
                using var protectedRequest = await http.GetAsync(path);
                Assert.Equal(HttpStatusCode.Unauthorized, protectedRequest.StatusCode);
            }
            foreach (string header in new[] { "Basic " + token, "Bearer " + token[..63],
                "Bearer " + (token[0] == '0' ? "1" : "0") + token[1..], "Bearer " + token + ", Bearer " + token })
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);
                using var rejected = await http.GetAsync("/api/v1/state");
                Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
                Assert.True(rejected.Headers.CacheControl?.NoStore);
                http.DefaultRequestHeaders.Remove("Authorization");
            }
            using var queryToken = await http.GetAsync("/api/v1/state?token=" + token);
            Assert.Equal(HttpStatusCode.Unauthorized, queryToken.StatusCode);
            using var deniedCommand = await http.PostAsync("/API/V1/commands/",
                new StringContent("{\"cmd\":\"getDiagnostics\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, deniedCommand.StatusCode);
            using var health = await http.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var noUpgrade = await http.GetAsync("/api/v1/events");
            Assert.Equal(enabled ? HttpStatusCode.BadRequest : HttpStatusCode.ServiceUnavailable, noUpgrade.StatusCode);
            http.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
            using var foreignEvents = await http.GetAsync("/api/v1/events");
            Assert.Equal(HttpStatusCode.Forbidden, foreignEvents.StatusCode);
            http.DefaultRequestHeaders.Remove("Origin");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!enabled)
            {
                foreach (string path in new[] { "/api/v1", "/API/V1/state/", "/api/v1/devices", "/api/v1/channels/music", "/api/v1/events", "/api/v1/plugin-setup" })
                {
                    using var off = await http.GetAsync(path);
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, off.StatusCode);
                    Assert.True(off.Headers.CacheControl?.NoStore);
                }
                using var commandOff = await http.PostAsync("/api/v1/commands", new StringContent("{\"cmd\":\"getState\"}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, commandOff.StatusCode);
                using var internalClient = await http.GetAsync("/ws");
                Assert.Equal(HttpStatusCode.OK, internalClient.StatusCode);
                return;
            }
            foreach (string path in new[] { "devices", "profiles", "diagnostics", "editor-rules", "plugin-diagnostics" })
            {
                using var resource = await http.GetAsync("/api/v1/" + path);
                Assert.Equal(HttpStatusCode.OK, resource.StatusCode);
                Assert.True(resource.Headers.CacheControl?.NoStore);
            }
            foreach (string path in new[] { "mixer", "channels", "channels/missing", "mixes", "inserts" })
            {
                using var resource = await http.GetAsync("/api/v1/" + path);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, resource.StatusCode);
            }
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
            using var chunked = new HttpRequestMessage(HttpMethod.Post, "/api/v1/commands")
            {
                Content = new StringContent(new string(' ', ApiEndpoints.MaxCommandBytes + 1), Encoding.UTF8, "application/json"),
            };
            chunked.Headers.TransferEncodingChunked = true;
            using var chunkedOversize = await http.SendAsync(chunked);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedOversize.StatusCode);
            using var invalid = await http.PostAsync("/api/v1/commands",
                new StringContent("null", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var invalidUtf8Body = new ByteArrayContent([0xc3, 0x28]);
            invalidUtf8Body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var invalidUtf8 = await http.PostAsync("/api/v1/commands", invalidUtf8Body);
            Assert.Equal(HttpStatusCode.BadRequest, invalidUtf8.StatusCode);
            using var valid = await http.PostAsync("/api/v1/commands",
                new StringContent("{\"cmd\":\"getDiagnostics\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            Assert.Contains("\"ok\":true", await valid.Content.ReadAsStringAsync());
            foreach (string command in new[] { "addWindowsPluginFolder", "removeWindowsPluginFolder", "getWindowsPluginFiles", "removeWindowsPluginInserts", "setWindowsPluginEnabled", "deleteWindowsPlugin" })
            {
                using var folder = await http.PostAsync("/api/v1/commands",
                    new StringContent(System.Text.Json.JsonSerializer.Serialize(new { cmd = command, path = "relative/plugins" }),
                        Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.BadRequest, folder.StatusCode);
                Assert.Contains("absolute path", await folder.Content.ReadAsStringAsync());
            }
            foreach (string? requestId in new string?[] { null, "failed-folder" })
            {
                using var folder = await http.PostAsync("/api/v1/commands", new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { cmd = "getWindowsPluginFiles", path = policyDirectory, requestId }),
                    Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.BadRequest, folder.StatusCode);
                using var result = System.Text.Json.JsonDocument.Parse(await folder.Content.ReadAsStringAsync());
                Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
                var messages = result.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                Assert.Equal("windowsPluginFiles", messages[0].GetProperty("type").GetString());
                Assert.False(messages[0].GetProperty("ok").GetBoolean());
                if (requestId is not null)
                {
                    var commandResult = messages.Single(m => m.GetProperty("type").GetString() == "commandResult");
                    Assert.False(string.IsNullOrWhiteSpace(commandResult.GetProperty("error").GetString()));
                    Assert.Equal(requestId, commandResult.GetProperty("requestId").GetString());
                }
            }
            const string deEsser = "ABCDEF019182FAEB4D616E75466C7665";
            foreach (bool? blocked in new bool?[] { false, null })
            {
                using var rule = await http.PostAsync("/api/v1/commands", new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { cmd = "setNativeEditorRule", kind = "vst3", plugin = deEsser, name = "Elgato De-Esser", blocked }),
                    Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, rule.StatusCode);
                using var policy = System.Text.Json.JsonDocument.Parse(await rule.Content.ReadAsStringAsync());
                var rules = policy.RootElement.GetProperty("messages")[0];
                Assert.Equal("nativeEditorRules", rules.GetProperty("type").GetString());
                Assert.Equal(blocked ?? true, rules.GetProperty("rules")[0].GetProperty("blocked").GetBoolean());
            }
            using var pluginDiagnostics = await http.PostAsync("/api/v1/commands",
                new StringContent("{\"cmd\":\"getPluginDiagnostics\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, pluginDiagnostics.StatusCode);
            using var evidence = System.Text.Json.JsonDocument.Parse(await pluginDiagnostics.Content.ReadAsStringAsync());
            var discovery = evidence.RootElement.GetProperty("messages")[0];
            Assert.Equal("pluginDiagnostics", discovery.GetProperty("type").GetString());
            Assert.True(discovery.GetProperty("discovery").TryGetProperty("searchPaths", out _));
            Assert.True(discovery.GetProperty("discovery").TryGetProperty("scans", out _));
            Assert.True(discovery.GetProperty("discovery").TryGetProperty("memoryLockHardLimitBytes", out _));
            Assert.True(discovery.GetProperty("discovery").TryGetProperty("hostEnvironment", out var hostEnvironment));
            Assert.True(hostEnvironment.TryGetProperty("wineRunner", out _));
            Assert.True(hostEnvironment.TryGetProperty("loaderEnvironment", out _));
            Assert.True(discovery.GetProperty("discovery").TryGetProperty("skippedFailedCount", out _));
            Assert.True(discovery.GetProperty("discovery").TryGetProperty("skippedFailedBundles", out _));

            // A slow streamed command occupies the shared command slot, but
            // snapshot reads remain available and no second command is queued.
            using var body = new PausedCommandContent();
            commandReading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var pendingRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/commands") { Content = body };
            pendingRequest.Headers.Add("X-Test-Observe-Body", "true");
            Task<HttpResponseMessage> pending = http.SendAsync(pendingRequest);
            HttpStatusCode completedStatus = default;
            try
            {
                await commandReading.Task.WaitAsync(TimeSpan.FromSeconds(3));
                using var busy = await http.GetAsync("/api/v1/diagnostics");
                Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
                using var snapshot = await http.GetAsync("/api/v1/state");
                Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
                using var concurrent = await http.PostAsync("/api/v1/commands",
                    new StringContent("{\"cmd\":\"getState\"}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.TooManyRequests, concurrent.StatusCode);
            }
            finally
            {
                body.Release();
                using var completed = await pending;
                completedStatus = completed.StatusCode;
            }
            Assert.Equal(HttpStatusCode.OK, completedStatus);
            using var afterCommand = await http.GetAsync("/api/v1/diagnostics");
            Assert.Equal(HttpStatusCode.OK, afterCommand.StatusCode);

            using var abortedBody = new PausedCommandContent();
            using var cancel = new CancellationTokenSource();
            commandReading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var abortedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/commands") { Content = abortedBody };
            abortedRequest.Headers.Add("X-Test-Observe-Body", "true");
            Task<HttpResponseMessage> aborted = http.SendAsync(abortedRequest, cancel.Token);
            try
            {
                await commandReading.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                cancel.Cancel();
                abortedBody.Release();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted.WaitAsync(TimeSpan.FromSeconds(3)));
            }
            using (var recovered = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                while (true)
                {
                    using var retry = await http.GetAsync("/api/v1/diagnostics", recovered.Token);
                    if (retry.StatusCode == HttpStatusCode.OK) break;
                    Assert.Equal(HttpStatusCode.TooManyRequests, retry.StatusCode);
                    await Task.Delay(10, recovered.Token);
                }
            }

            // Rotation must affect already mapped endpoints and existing HTTP
            // connections, without writing a token into the user's runtime.
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtimeDir);
            try { ApiToken.Initialize(); }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousRuntime);
                Directory.Delete(runtimeDir, recursive: true);
            }
            using var staleToken = await http.GetAsync("/api/v1/state");
            Assert.Equal(HttpStatusCode.Unauthorized, staleToken.StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken.Current!);
            using var rotatedToken = await http.GetAsync("/api/v1/state");
            Assert.Equal(HttpStatusCode.OK, rotatedToken.StatusCode);

        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(policyDirectory)) Directory.Delete(policyDirectory, recursive: true);
        }
    }
    // Signal only once the endpoint owns its command slot and starts reading.
    // Polling a command-backed GET here could itself take the slot first.
    private sealed class ObservedReadStream(Stream inner, TaskCompletionSource reading) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken stop = default)
        {
            reading.TrySetResult();
            return inner.ReadAsync(buffer, stop);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PausedCommandContent : HttpContent
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PausedCommandContent() => Headers.ContentType = new MediaTypeHeaderValue("application/json");
        public void Release() => _release.TrySetResult();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{"));
            await stream.FlushAsync();
            await _release.Task;
            await stream.WriteAsync(Encoding.UTF8.GetBytes("\"cmd\":\"getDiagnostics\"}"));
        }
    }

}
