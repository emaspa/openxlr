using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class DaemonRestartCommandTests
{
    [Fact]
    public async Task RestartDaemonStopsWithTheCodeAServiceManagerRetries()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-restart-command-").FullName;
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", Path.Combine(directory, "run"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(directory, "config"));
        try
        {
            var settings = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, settings, () => []);
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, settings, devices);
            using var lifetime = new Lifetime();
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);

            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"getState"}""")).Ok);
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"restartDaemon"}""")).Ok);
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.Equal(RestartRequest.TemporaryFailure, RestartRequest.ExitCode);
        }
        finally
        {
            RestartRequest.Ask(0);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Directory.Delete(directory, true);
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
