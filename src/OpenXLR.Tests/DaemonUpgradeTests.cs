using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using OpenXLR.Core;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// After a package upgrade the previous release's daemon keeps running and
/// serves only its loopback port. Every client must tell that apart from a
/// daemon that is not running, without stealing the lock or talking to the
/// old daemon.
/// </summary>
[Collection("xdg-config")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class DaemonUpgradeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ox-upgrade-").FullName;
    private string Socket => Path.Combine(_directory, "control.sock");
    private string Lock => Path.Combine(_directory, "daemon.lock");

    public void Dispose() => Directory.Delete(_directory, true);

    /// <summary>A loopback port nothing listens on.</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task ADaemonOfThisReleaseThatIsRestartingIsNotAnOlderDaemon()
    {
        // Between taking the lock and starting its mixer and devices, a
        // daemon of this release has bound its socket but does not accept.
        // Shutting down, it stops accepting and holds the lock for seconds.
        using FileStream? held = DaemonLock.TryAcquire(Lock);
        Assert.NotNull(held);
        using var listener = ControlSocketListener.Bind(Socket);
        Assert.Equal(DaemonPresence.NotRunning, await DaemonProbe.ProbeAsync(Socket, ClosedPort()));
        File.Delete(Socket);
        Assert.Equal(DaemonPresence.NotRunning, await DaemonProbe.ProbeAsync(Socket, ClosedPort()));
        // The probe took nothing: the holder still excludes a second daemon.
        Assert.Null(DaemonLock.TryAcquire(Lock));
    }

    [Fact]
    public async Task TheOldLoopbackPortAloneIsAnOlderDaemonAndReceivesNothing()
    {
        var old = new TcpListener(IPAddress.Loopback, 0);
        old.Start();
        int port = ((IPEndPoint)old.LocalEndpoint).Port;
        try
        {
            Task<System.Net.Sockets.Socket> accepted = old.AcceptSocketAsync();
            Assert.Equal(DaemonPresence.OlderDaemon, await DaemonProbe.ProbeAsync(Socket, port));
            using System.Net.Sockets.Socket peer = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
            // A bare connect: the probe closed it without sending a byte.
            var buffer = new byte[16];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(0, await peer.ReceiveAsync(buffer, SocketFlags.None, timeout.Token));
        }
        finally { old.Stop(); }
        Assert.Equal(DaemonPresence.NotRunning, await DaemonProbe.ProbeAsync(Socket, port));
    }

    [Fact]
    public async Task AStaleSocketAndAFreeLockAreNotRunningAndALiveSocketIsReachable()
    {
        using (var dead = new System.Net.Sockets.Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            dead.Bind(new UnixDomainSocketEndPoint(Socket));
        File.WriteAllText(Lock, "");
        Assert.Equal(DaemonPresence.NotRunning, await DaemonProbe.ProbeAsync(Socket, ClosedPort()));
        File.Delete(Socket);
        using var listener = ControlSocketListener.Bind(Socket);
        listener.Socket.Listen(4);
        Assert.Equal(DaemonPresence.Reachable, await DaemonProbe.ProbeAsync(Socket, ClosedPort()));
    }

    [Fact]
    public async Task ADaemonStartingDuringAProbeStillTakesTheLock()
    {
        // The shared lock of a client from 0.3.0 to 0.7.1, held across the
        // daemon's first attempt.
        SafeFileHandle probe = File.OpenHandle(Lock, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
        Task release = Task.Run(async () => { await Task.Delay(60); probe.Dispose(); });
        using FileStream? daemon = DaemonLock.TryAcquire(Lock);
        await release;
        Assert.NotNull(daemon);
    }

    [Fact]
    public void TheOldTokenIsRemovedWithoutARuntimeDirectoryAndALinkIsRemovedAsALink()
    {
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
            string token = Path.Combine(_directory, "openxlr", "token");
            Assert.Equal(token, OpenXlrPaths.LegacyTokenPath);
            Assert.Equal(Path.Combine(_directory, "openxlr", "daemon.lock"), DaemonLock.Path);
            OpenXlrPaths.WriteAtomic(token, new string('a', 64) + "\n");
            DaemonLock.RemoveLegacyToken();
            Assert.False(File.Exists(token));
            string target = Path.Combine(_directory, "kept");
            File.WriteAllText(target, "kept");
            File.CreateSymbolicLink(token, target);
            DaemonLock.RemoveLegacyToken();
            Assert.Null(new FileInfo(token).LinkTarget);
            Assert.Equal("kept", File.ReadAllText(target));
            DaemonLock.RemoveLegacyToken(); // nothing left is not an error
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        }
    }

    [Theory]
    [InlineData(DaemonPresence.OlderDaemon, "olderDaemon", "systemctl --user restart openxlr-daemon")]
    [InlineData(DaemonPresence.NotRunning, "notRunning", "is not running")]
    [InlineData(DaemonPresence.OtherUser, "otherUser", "another user")]
    public async Task TheAdapterReportsWhyItCouldNotConnectInPlaceOfItsReadyLine(DaemonPresence presence, string reason, string words)
    {
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var input = new Pipe();
            var output = new Pipe();
            int exit = await ControlStdio.RunAsync(input.Reader.AsStream(), output.Writer.AsStream(), timeout.Token,
                _ => Task.FromResult(presence));
            await output.Writer.CompleteAsync();
            using var reader = new StreamReader(output.Reader.AsStream());
            JsonNode line = JsonNode.Parse(await reader.ReadLineAsync(timeout.Token) ?? "")!;
            Assert.Null(await reader.ReadLineAsync(timeout.Token));
            Assert.Equal(1, exit);
            Assert.Equal("controlError", line["type"]!.GetValue<string>());
            Assert.Equal(reason, line["reason"]!.GetValue<string>());
            Assert.Contains(words, line["message"]!.GetValue<string>());
        }
        finally { Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime); }
    }

    [Fact]
    public async Task TheInstalledAdapterReportsNoDaemonWhileOneHoldsTheLockWithoutAccepting()
    {
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _directory);
        try
        {
            using FileStream? held = DaemonLock.TryAcquire();
            Assert.NotNull(held);
            string launcher = Path.Combine(Path.GetDirectoryName(typeof(WebSocketHub).Assembly.Location)!, "openxlr-control");
            string helper = Path.Combine(_directory, "client");
            ExecutableScript.Write(helper, "#!/bin/sh\nexec \"$1\" </dev/null\n");
            var result = await ProcessRunner.RunAsync(helper, [launcher], TimeSpan.FromSeconds(10),
                environment: new Dictionary<string, string> { ["FLATPAK_ID"] = "" });
            Assert.Equal(1, result.ExitCode);
            JsonNode line = JsonNode.Parse(result.StdoutText.Trim())!;
            Assert.Equal("controlError", line["type"]!.GetValue<string>());
            Assert.Equal("notRunning", line["reason"]!.GetValue<string>());
            Assert.Equal("The OpenXLR daemon is not running.", line["message"]!.GetValue<string>());
            Assert.DoesNotContain("before the update", result.Stderr);
            // The adapter is a client: it left the daemon's lock alone.
            Assert.Null(DaemonLock.TryAcquire());
        }
        finally { Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime); }
    }

    private static async Task WaitFor(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(ready());
    }
}
