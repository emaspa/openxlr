using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// A failure inside the daemon is answered, not taken for a client that went
/// away: a command that throws gets its error and the connection stays open,
/// a new client gets a state while the full one cannot be built, and a unit
/// that fails the readback after a write it took does not fail the write.
/// </summary>
[Collection("xdg-config")]
public sealed class HubErrorBoundaryTests
{
    [Fact]
    public async Task ACommandThatFailsInsideTheDaemonIsAnsweredAndTheConnectionStaysOpen()
    {
        using var f = new Fixture();
        f.AudioServerAway = true;
        await using var server = await SocketTestServer.Start((socket, _) => f.Hub.HandleAsync(socket));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(server.Url), deadline.Token);

        // The first message is still a state: the device half, the failure in its warning.
        JsonNode first = await SocketTestServer.Receive(client, deadline.Token);
        Assert.Equal("state", first["type"]!.GetValue<string>());
        Assert.Contains("pw-dump", first["warning"]!.GetValue<string>());
        Assert.Null(first["mixer"]);

        // getState builds the state again, and pw-dump fails with an InvalidOperationException.
        List<JsonNode> refused = await Query(client, "getState", deadline.Token);
        Assert.Contains("pw-dump", refused.Last()["error"]!.GetValue<string>());
        await SocketTestServer.Send(client, new { cmd = "getState" }, deadline.Token);
        JsonNode error = await Next(client, "error", deadline.Token);
        Assert.Contains("pw-dump", error["message"]!.GetValue<string>());

        // Once the audio server answers again, the same connection reads the whole state.
        f.AudioServerAway = false;
        List<JsonNode> answered = await Query(client, "getState", deadline.Token);
        Assert.Null(answered.Last()["error"]?.GetValue<string>());
        Assert.NotNull(answered.Last(m => m["type"]?.GetValue<string>() == "state")["mixer"]);
        Assert.Equal(WebSocketState.Open, client.State);
    }

    [Fact]
    public async Task AnHttpCommandThatFailsInsideTheDaemonIsAFailedResult()
    {
        using var f = new Fixture();
        f.AudioServerAway = true;
        ApiCommandResult result = await f.Hub.ExecuteForApiAsync("""{"cmd":"getState"}""");
        Assert.False(result.Ok);
        Assert.Contains("pw-dump", Assert.Single(result.Messages.OfType<ErrorMessage>()).Message);
    }

    [Fact]
    public void AStateReadAnswers503WhileTheStateCannotBeBuilt()
    {
        using var f = new Fixture();
        f.AudioServerAway = true;
        IResult result = ApiEndpoints.StateResource(f.Hub, state => Results.Json(state));
        Assert.Equal(503, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains("pw-dump", Assert.IsType<ErrorMessage>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value).Message);

        f.AudioServerAway = false;
        Assert.IsType<StateMessage>(Assert.IsAssignableFrom<IValueHttpResult>(
            ApiEndpoints.StateResource(f.Hub, state => Results.Json(state))).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReadbackThatFailsAfterAWriteDoesNotFailTheWrite(bool hung)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-readback-").FullName;
        string? prev = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            var device = new ReadbackDevice();
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [device]);
            manager.SweepOnce();
            Assert.True(manager.Snapshot().Connected);

            device.FailReads = hung ? new UsbHungException("USB control transfer did not return (test)") : new IOException("read failed (test)");
            using (JsonDocument gain = JsonDocument.Parse("30"))
                Assert.Null(manager.Apply(ControlNames.Gain, gain.RootElement));
            Assert.Equal(30, device.GainDb);
            // A transfer that never returned still reaches the hang handling.
            Assert.Equal(hung, manager.DumpBlocks().ContainsKey("usbFault"));

            if (!hung)
            {
                // A profile's writes that landed are not reported as failed by their readback either.
                Assert.Null(manager.ApplyProfile(new DeviceState { GainDb = 20 }));
                Assert.Equal(20, device.GainDb);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prev);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A Wave XLR that takes every write and fails reads once <see cref="FailReads"/> is set.</summary>
    private sealed class ReadbackDevice : IAudioDevice
    {
        public Exception? FailReads;
        public int GainDb = 40;
        public void Dispose() { }
        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR", 0x0fd9, 0x007d);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true, HpVolume = true };
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public DeviceState ReadState()
        {
            if (FailReads is UsbHungException) Connected = false;   // what a real transport does once the helper is killed
            return FailReads is { } failure ? throw failure : new DeviceState { GainDb = GainDb };
        }
        public void SetGainDb(int db) => GainDb = db;
        public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    private static async Task<JsonNode> Next(WebSocket socket, string type, CancellationToken stop)
    {
        while (true)
        {
            JsonNode message = await SocketTestServer.Receive(socket, stop);
            if (message["type"]?.GetValue<string>() == type) return message;
        }
    }

    private static async Task<List<JsonNode>> Query(WebSocket socket, string command, CancellationToken stop)
    {
        string request = Guid.NewGuid().ToString("N");
        await SocketTestServer.Send(socket, new { cmd = command, requestId = request }, stop);
        var replies = new List<JsonNode>();
        while (true)
        {
            JsonNode message = await SocketTestServer.Receive(socket, stop);
            replies.Add(message);
            if (message["type"]?.GetValue<string>() == "commandResult" && message["requestId"]?.GetValue<string>() == request) return replies;
        }
    }

    /// <summary>A hub over a built mixer whose pw-dump fails while the audio server is away.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("openxlr-hub-boundary-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly DeviceManager _devices;
        private readonly MixerService _service;
        private readonly Lifetime _lifetime = new();

        public WebSocketHub Hub { get; }

        public bool AudioServerAway
        {
            set
            {
                string flag = Path.Combine(_dir, "away");
                if (value) File.WriteAllText(flag, "");
                else File.Delete(flag);
            }
        }

        public Fixture()
        {
            ExecutableScript.Write(Path.Combine(_dir, "pactl"), "exit 0");
            ExecutableScript.Write(Path.Combine(_dir, "pw-dump"), """
                dir=${0%/*}
                if [ -e "$dir/away" ]; then echo 'failed to connect to PipeWire' >&2; exit 1; fi
                echo '[]'
                """);
            ExecutableScript.Write(Path.Combine(_dir, "pw-link"), "exit 0");
            Environment.SetEnvironmentVariable("PATH", _dir);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_dir, "config"));
            var config = new ConfigurationBuilder().Build();
            _devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            _service = new MixerService(NullLogger<MixerService>.Instance, config, _devices);
            Mixer mixer = (Mixer)typeof(MixerService).GetField("_mixer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)!;
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, true);
            Hub = new WebSocketHub(_devices, _service, NullLogger<WebSocketHub>.Instance, _lifetime);
        }

        public void Dispose()
        {
            try
            {
                _lifetime.StopApplication();
                _service.Dispose();
                _devices.Dispose();
                _lifetime.Dispose();
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", _path);
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config);
                Directory.Delete(_dir, true);
            }
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
