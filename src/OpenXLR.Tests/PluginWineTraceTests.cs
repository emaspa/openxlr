using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class PluginWineTraceTests
{
    [Fact]
    public async Task EnablingTraceLeavesTheCacheIntactAndRescanRetriesFailuresWithWineDebug()
    {
        await using var fixture = new PluginWineTraceFixture();
        string scanner = NativePluginHost.Executable;
        string backup = scanner + ".wine-trace-backup";
        bool existed = File.Exists(scanner);
        if (existed) File.Move(scanner, backup);
        try
        {
            string launches = Path.Combine(fixture.Root, "launches");
            ExecutableScript.Write(scanner, $"printf '%s\\n' \"$WINEDEBUG\" >> '{launches}'\nprintf '%s\\n' \"$WINEDEBUG\" >&2\nexit 1");
            string failed = Path.Combine(fixture.Root, "Failed.vst3");
            string good = Path.Combine(fixture.Root, "Good.vst3");
            File.WriteAllText(failed, "fixture");
            File.WriteAllText(good, "fixture");
            string cacheDirectory = Path.Combine(fixture.Root, "cache");
            var cache = new ScanCache(cacheDirectory);
            cache.Store(good, System.Text.Encoding.UTF8.GetBytes("""{"plugins":[]}"""));
            cache.Save();
            string kind = "wine-trace-" + Guid.NewGuid().ToString("N");
            string logs = Path.Combine(fixture.Root, "logs");
            void Scan(bool retry = false) => HostScan.Run(kind, "scan-vst3", [fixture.Root], Vst3Catalog.Bundles,
                scanCache: new ScanCache(cacheDirectory), logs: new PluginScanLogStore(logs), retryFailures: retry);
            Scan();
            Assert.True(new ScanCache(cacheDirectory).KnownFailure(failed));
            var saved = Directory.GetFiles(cacheDirectory).ToDictionary(p => p, File.ReadAllBytes);

            await fixture.Hub.ExecuteForApiAsync("""{"cmd":"setPluginWineTrace","value":true}""");
            foreach (var (path, bytes) in saved) Assert.Equal(bytes, File.ReadAllBytes(path));
            Scan();
            Assert.Single(File.ReadAllLines(launches));
            Scan(retry: true);
            Assert.Equal(new[] { "", "+seh,+unwind,+loaddll" }, File.ReadAllLines(launches));
            var report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);
            var failure = Assert.Single(report.Entries, e => e.Path == failed);
            string log = File.ReadAllText(Path.Combine(logs, failure.LogId + ".log"));
            Assert.Contains("wineTrace: true", log);
            Assert.Contains("+seh,+unwind,+loaddll", log);
            Assert.NotNull(new ScanCache(cacheDirectory).Lookup(good));
        }
        finally
        {
            File.Delete(scanner);
            File.Delete(scanner + ".body");
            if (existed) File.Move(backup, scanner);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public async Task SetupReadsTheStartupEnvironmentAndCommandsRoundTrip(string? startup, bool enabled)
    {
        await using var fixture = new PluginWineTraceFixture();
        Environment.SetEnvironmentVariable(PluginHostEnvironment.WineTraceVariable, startup);
        await using var client = new DaemonClient();
        var vm = new OptionsViewModel(client, new MainViewModel(client));
        var initial = await fixture.Hub.ExecuteForApiAsync("""{"cmd":"getPluginSetup"}""");
        var setup = Assert.IsType<PluginSetupMessage>(Assert.Single(initial.Messages));
        Assert.Equal(enabled, setup.WineTrace);
        vm.ApplyPluginSetup(JsonSerializer.SerializeToNode(setup));
        Assert.Equal(enabled, vm.PluginWineTrace);
        Assert.True(vm.CanSetPluginWineTrace);
        var saved = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.ReadAllBytes);

        foreach (bool choice in new[] { true, false, true })
        {
            var result = await fixture.Hub.ExecuteForApiAsync(JsonSerializer.Serialize(
                new { cmd = "setPluginWineTrace", value = choice, requestId = "trace" }));
            Assert.True(result.Ok);
            Assert.Equal(3, result.Messages.Count);
            setup = Assert.IsType<PluginSetupMessage>(result.Messages[0]);
            Assert.Equal(choice, setup.WineTrace);
            Assert.IsType<StateMessage>(result.Messages[1]);
            var done = Assert.IsType<CommandResultMessage>(result.Messages[2]);
            Assert.Equal("trace", done.RequestId);
            Assert.Null(done.Error);
            Assert.Equal(choice ? "1" : null, Environment.GetEnvironmentVariable(PluginHostEnvironment.WineTraceVariable));
            vm.ApplyPluginSetup(JsonSerializer.SerializeToNode(setup));
            Assert.Equal(choice, vm.PluginWineTrace);
        }
        vm.ApplyPluginSetup(JsonNode.Parse("{}"));
        Assert.Null(vm.PluginWineTrace);
        Assert.False(vm.CanSetPluginWineTrace);
        vm.ApplyPluginSetup(JsonSerializer.SerializeToNode(setup));
        vm.ApplyPluginSetup(null);
        Assert.Null(vm.PluginWineTrace);
        Assert.False(vm.CanSetPluginWineTrace);
        // Probing yabridgectl can create its default config on the first read.
        // Switching the trace must leave those files as they were.
        Assert.Equal(saved.Keys.Order(), Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in saved) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"value\":null}")]
    [InlineData("{\"value\":1}")]
    [InlineData("{\"value\":\"true\"}")]
    [InlineData("{\"value\":[]}")]
    [InlineData("{\"value\":{}}")]
    public async Task InvalidValuesAreRejectedWithoutChangingTheEnvironment(string fields)
    {
        await using var fixture = new PluginWineTraceFixture();
        Environment.SetEnvironmentVariable(PluginHostEnvironment.WineTraceVariable, "1");
        var command = JsonNode.Parse(fields)!;
        command["cmd"] = "setPluginWineTrace";
        command["requestId"] = "invalid-trace";
        var dto = command.Deserialize<Command>()!;
        Assert.Equal("setPluginWineTrace: value must be a boolean",
            CommandValidation.CheckPluginWineTrace(dto));
        var result = await fixture.Hub.ExecuteForApiAsync(command.ToJsonString());
        Assert.False(result.Ok);
        Assert.Equal(2, result.Messages.Count);
        Assert.IsType<StateMessage>(result.Messages[0]);
        var done = Assert.IsType<CommandResultMessage>(result.Messages[1]);
        Assert.Equal("invalid-trace", done.RequestId);
        Assert.Equal("setPluginWineTrace: value must be a boolean", done.Error);
        Assert.Equal("1", Environment.GetEnvironmentVariable(PluginHostEnvironment.WineTraceVariable));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-all")]
    [InlineData("+seh")]
    public async Task RuntimeSwitchKeepsExplicitWineDebugAndOnlyChangesFutureScanners(string? wineDebug)
    {
        await using var fixture = new PluginWineTraceFixture();
        Environment.SetEnvironmentVariable("WINEDEBUG", wineDebug);
        var before = new PluginHostEnvironment(null, scanner: true);
        await fixture.Hub.ExecuteForApiAsync("""{"cmd":"setPluginWineTrace","value":true}""");
        var after = new PluginHostEnvironment(null, scanner: true);
        Assert.False(before.WineTrace);
        Assert.True(after.WineTrace);
        Assert.Equal(PluginScanLogStore.TraceCaptureBytes, after.StderrCap);
        Assert.Equal(wineDebug ?? PluginHostEnvironment.WineTraceChannels, after.WineDebug);
        Assert.Equal(wineDebug, Environment.GetEnvironmentVariable("WINEDEBUG"));
        Assert.False(new PluginHostEnvironment(null).WineTrace);
        var evidence = await fixture.Hub.ExecuteForApiAsync("""{"cmd":"getPluginDiagnostics"}""");
        var discovery = JsonSerializer.SerializeToNode(Assert.Single(evidence.Messages))!["discovery"]!;
        Assert.True(discovery["hostEnvironment"]!["wineTrace"]!.GetValue<bool>());
        Assert.Equal(after.WineDebug, discovery["hostEnvironment"]!["scannerWineDebug"]!.GetValue<string>());
        await fixture.Hub.ExecuteForApiAsync("""{"cmd":"setPluginWineTrace","value":false}""");
        Assert.False(new PluginHostEnvironment(null, scanner: true).WineTrace);
        Assert.Equal(wineDebug, Environment.GetEnvironmentVariable("WINEDEBUG"));
    }
}

// The real command dispatcher, with no hosted services, USB or audio graph.
// Environment changes belong to this test process and are restored afterward.
internal sealed class PluginWineTraceFixture : IAsyncDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("wine-trace-").FullName;
    private readonly Dictionary<string, string?> _before = new();
    private readonly WebApplication _app;
    public WebSocketHub Hub { get; }

    /// <param name="log">Where the daemon's journal lines go, for a test that reads them; nowhere when null.</param>
    public PluginWineTraceFixture(ILoggerProvider? log = null)
    {
        foreach (var (name, value) in new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = Path.Combine(Root, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(Root, "data"),
            ["WINEPREFIX"] = Path.Combine(Root, "wine"),
            ["OPENXLR_YABRIDGE"] = "system",
            [PluginHostEnvironment.WineTraceVariable] = null,
            ["WINEDEBUG"] = null,
        })
        {
            _before[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (log is not null) builder.Logging.AddProvider(log);
        builder.Services.AddSingleton<DeviceManager>();
        builder.Services.AddSingleton<MixerService>();
        builder.Services.AddSingleton<WebSocketHub>();
        _app = builder.Build();
        Hub = _app.Services.GetRequiredService<WebSocketHub>();
    }

    public Task<SocketTestServer> StartServerAsync() => SocketTestServer.Start(async (socket, stop) =>
    {
        while (!stop.IsCancellationRequested)
        {
            var command = await SocketTestServer.Receive(socket, stop);
            string cmd = command["cmd"]!.GetValue<string>();
            if (cmd == "listPlugins")
            {
                // Collecting evidence must not scan the user's installed plugins.
                await SocketTestServer.Send(socket, new PluginsMessage([]), stop);
                await SocketTestServer.Send(socket, new CommandResultMessage(command["requestId"]!.GetValue<string>(), null), stop);
                continue;
            }
            var result = await Hub.ExecuteForApiAsync(command.ToJsonString());
            foreach (object message in result.Messages) await SocketTestServer.Send(socket, message, stop);
        }
    });

    public async ValueTask DisposeAsync()
    {
        _app.Lifetime.StopApplication();
        await _app.DisposeAsync();
        foreach (var (name, value) in _before) Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(Root, recursive: true);
    }
}
