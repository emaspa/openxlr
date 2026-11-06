using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenXLR.Core;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ControlSocketTests
{
    [Fact]
    public async Task ConnectionsCheckBothPeersAndNeedNoTokenOrLoopbackListener()
    {
        string directory = Directory.CreateTempSubdirectory("ox-unix-").FullName;
        try
        {
            using var listener = ControlSocketListener.Bind(Path.Combine(directory, "control.sock"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(listener.Path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            await using var app = Echo(listener);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            Assert.All(addresses, address => Assert.Contains("unix:", address));
            using var http = new HttpClient(ControlConnection.HttpHandler(listener.Path));
            Assert.Equal("private", await http.GetStringAsync("http://localhost/private"));
            using var link = new ControlConnection(socketPath: listener.Path);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await link.ConnectAsync(timeout.Token);
            Assert.Equal(Environment.ProcessId, link.PeerProcessId);
            await SocketTestServer.Send(link.Socket, new { cmd = "getState" }, timeout.Token);
            Assert.Equal("getState", (await SocketTestServer.Receive(link.Socket, timeout.Token))["cmd"]!.GetValue<string>());
            Assert.False(File.Exists(Path.Combine(directory, "token")));
            await app.StopAsync(timeout.Token);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExistingFilesLinksAndLiveListenersAreNeverRemoved()
    {
        string directory = Directory.CreateTempSubdirectory("ox-unix-").FullName;
        string path = Path.Combine(directory, "control.sock");
        try
        {
            OpenXlrPaths.WriteAtomic(path, "user file");
            Assert.Throws<IOException>(() => ControlSocketListener.Bind(path));
            Assert.Equal("user file", File.ReadAllText(path));
            File.Move(path, path + ".saved");
            File.CreateSymbolicLink(path, path + ".saved");
            Assert.Throws<IOException>(() => ControlSocketListener.Bind(path));
            File.Delete(path);
            using (var first = ControlSocketListener.Bind(path))
            {
                first.Socket.Listen(4);
                Assert.Throws<IOException>(() => ControlSocketListener.Bind(path));
                Assert.True(File.Exists(path));
                // Closing the listener must not unlink a new file at its old name.
                File.Move(path, path + ".old");
                OpenXlrPaths.WriteAtomic(path, "replacement");
            }
            Assert.Equal("replacement", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ADeadSocketIsReplacedAndTheListenerCleansUpOnlyItsOwnInode()
    {
        string directory = Directory.CreateTempSubdirectory("ox-unix-").FullName;
        string path = Path.Combine(directory, "control.sock");
        try
        {
            using (var old = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                old.Bind(new UnixDomainSocketEndPoint(path + ".temporary"));
                File.Move(path + ".temporary", path);
            }
            Assert.True(File.Exists(path));
            using (var listener = ControlSocketListener.Bind(path)) Assert.True(File.Exists(path));
            Assert.False(File.Exists(path));
            Assert.Throws<IOException>(() => ControlSocketListener.Bind("relative.sock"));
            Assert.Throws<IOException>(() => ControlSocketListener.Bind("/" + new string('x', 107)));
            Assert.Throws<IOException>(() => ControlSocketListener.Bind("/" + new string('ä', 54)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LinkedSocketDirectoriesAndTcpPeersAreRefused()
    {
        string directory = Directory.CreateTempSubdirectory("ox-unix-").FullName;
        try
        {
            Directory.CreateSymbolicLink(directory + ".link", directory);
            Assert.Throws<IOException>(() => ControlSocketListener.Bind(Path.Combine(directory + ".link", "control.sock")));
            using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Assert.Throws<IOException>(() => UnixPeerCredentials.RequireSameUser(tcp));
            var context = new DefaultHttpContext();
            context.Request.Headers.Authorization = "Bearer " + new string('a', 64);
            Assert.False(ControlApi.Authorized(context));
        }
        finally { Directory.Delete(directory + ".link"); Directory.Delete(directory, true); }
    }

    [Fact]
    public void TheDaemonLockRemainsExclusiveUntilItsHolderExits()
    {
        string directory = Directory.CreateTempSubdirectory("ox-unix-").FullName;
        try
        {
            string path = Path.Combine(directory, "daemon.lock");
            using (var first = DaemonLock.TryAcquire(path))
            {
                Assert.NotNull(first);
                Assert.Null(DaemonLock.TryAcquire(path));
            }
            using var next = DaemonLock.TryAcquire(path);
            Assert.NotNull(next);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheStdioAdapterSignalsReadyThenForwardsJsonAndStopsOnEof(bool oversized)
    {
        string directory = Directory.CreateTempSubdirectory("ox-unix-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var listener = ControlSocketListener.Bind();
            await using var app = Echo(listener);
            await app.StartAsync(timeout.Token);
            var input = new Pipe();
            var output = new Pipe();
            Task<int> running = ControlStdio.RunAsync(input.Reader.AsStream(), output.Writer.AsStream(), timeout.Token);
            using var reader = new StreamReader(output.Reader.AsStream());
            Assert.Equal("{\"type\":\"controlReady\"}", await reader.ReadLineAsync(timeout.Token));
            if (oversized)
                await input.Writer.WriteAsync(new byte[ApiEndpoints.MaxCommandBytes + 1], timeout.Token);
            else
            {
                await input.Writer.WriteAsync("{\"cmd\":\"getState\"}\n"u8.ToArray(), timeout.Token);
                await input.Writer.CompleteAsync();
                Assert.Equal("{\"cmd\":\"getState\"}", await reader.ReadLineAsync(timeout.Token));
            }
            if (oversized) await input.Writer.CompleteAsync();
            Assert.Equal(oversized ? 1 : 0, await running.WaitAsync(timeout.Token));
            await output.Writer.CompleteAsync();
            await app.StopAsync(timeout.Token);
        }
        finally { Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previous); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TheWindowAndTerminalDefaultToTheSameSocketWithoutAToken()
    {
        string directory = Directory.CreateTempSubdirectory("ox-unix-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var listener = ControlSocketListener.Bind();
            await using var app = Echo(listener, state: true);
            await app.StartAsync(timeout.Token);
            await using var window = new OpenXLR.UI.DaemonClient();
            await using var terminal = new OpenXLR.Tui.DaemonLink();
            var uiState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tuiState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.StateReceived += _ => uiState.TrySetResult();
            terminal.Changed += () => { if (terminal.State is not null) tuiState.TrySetResult(); };
            window.Start(); terminal.Start();
            await Task.WhenAll(uiState.Task, tuiState.Task).WaitAsync(timeout.Token);
            Assert.True(terminal.Connected);
            Assert.False(File.Exists(Path.Combine(OpenXlrPaths.RuntimeDir, "token")));
        }
        finally { Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previous); Directory.Delete(directory, true); }
    }

    private static WebApplication Echo(ControlSocketListener listener, bool state = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0").PreferHostingUrls(true);
        builder.Configuration["Kestrel:Endpoints:Injected:Url"] = "http://127.0.0.1:0";
        ControlApi.Configure(builder, listener);
        var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/private", (HttpContext context) => ControlApi.Authorized(context) ? Results.Text("private") : Results.Unauthorized());
        app.Map("/ws", async (HttpContext context) =>
        {
            Assert.True(ControlApi.Authorized(context));
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                if (state) await SocketTestServer.Send(socket, new { type = "state", connected = false,
                    profiles = Array.Empty<object>(), detected = Array.Empty<object>(), devices = Array.Empty<object>(),
                    mixer = new { channels = Array.Empty<object>(), mixes = Array.Empty<object>(), inserts = new { } } }, app.Lifetime.ApplicationStopping);
                while (true)
                {
                    var (outcome, message) = await SocketGuard.ReceiveMessageAsync(socket, new byte[8192], 64 * 1024,
                        SocketGuard.MessageDeadline, app.Lifetime.ApplicationStopping);
                    if (outcome != SocketGuard.Outcome.Message) return;
                    await socket.SendAsync(message!, WebSocketMessageType.Text, true, app.Lifetime.ApplicationStopping);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        });
        return app;
    }

    [Fact]
    public async Task TheInstalledAdapterModeIsAClientAndDoesNotAcquireTheDaemonLock()
    {
        string directory = Directory.CreateTempSubdirectory("ox-unix-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", directory);
        try
        {
            using var listener = ControlSocketListener.Bind();
            await using var app = Echo(listener, state: true);
            await app.StartAsync();
            string launcher = Path.Combine(Path.GetDirectoryName(typeof(WebSocketHub).Assembly.Location)!, "openxlr-control");
            // Source installations put a symlink on PATH. Spaces must survive
            // resolution to the daemon installed beside the real launcher.
            string link = Path.Combine(directory, "source client");
            File.CreateSymbolicLink(link, launcher);
            string helper = Path.Combine(directory, "client");
            ExecutableScript.Write(helper, "#!/bin/sh\nexec \"$1\" </dev/null\n");
            var result = await ProcessRunner.RunAsync(helper, [link], TimeSpan.FromSeconds(8));
            Assert.True(result.Ok, result.Stderr);
            Assert.Contains("\"type\":\"controlReady\"", result.StdoutText);
            Assert.Contains("\"type\":\"state\"", result.StdoutText);
            Assert.False(File.Exists(DaemonLock.Path));
        }
        finally { Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previous); Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheAdapterExitsWhenTheServerClosesWithItsRealStdinPipeStillOpen(bool abort)
    {
        string directory = Directory.CreateTempSubdirectory("ox-unix-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var listener = ControlSocketListener.Bind();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            ControlApi.Configure(builder, listener);
            await using var app = builder.Build();
            app.UseWebSockets();
            app.Map("/ws", async (HttpContext context) =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                // Receiving a command proves the adapter has entered its stdin loop.
                await SocketTestServer.Receive(socket, timeout.Token);
                if (abort) socket.Abort();
                else await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "restart", timeout.Token);
            });
            await app.StartAsync(timeout.Token);
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(typeof(WebSocketHub).Assembly.Location);
            start.ArgumentList.Add("--control-stdio");
            using var child = Process.Start(start)!;
            try
            {
                Assert.Equal("{\"type\":\"controlReady\"}", await child.StandardOutput.ReadLineAsync(timeout.Token));
                await child.StandardInput.WriteLineAsync("{\"cmd\":\"getState\"}");
                await child.StandardInput.FlushAsync(timeout.Token);
                // Do not close StandardInput. Console's pipe read ignores cancellation.
                await child.WaitForExitAsync(timeout.Token);
                Assert.False(File.Exists(DaemonLock.Path));
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
        finally { Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previous); Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvalidRuntimeDirectoriesAreStartupErrorsRatherThanExistingDaemons(bool flatpak, bool socketPath)
    {
        string directory = Directory.CreateTempSubdirectory("ox-unix-").FullName;
        try
        {
            string blocked = socketPath ? Path.Combine(directory, "openxlr", "control.sock") : Path.Combine(directory, "openxlr");
            OpenXlrPaths.WriteAtomic(blocked, "keep this file");
            var result = await ProcessRunner.RunAsync("dotnet", [typeof(WebSocketHub).Assembly.Location],
                TimeSpan.FromSeconds(8), environment: new Dictionary<string, string>
                {
                    ["XDG_RUNTIME_DIR"] = directory,
                    ["FLATPAK_ID"] = flatpak ? "io.github.emaspa.openxlr" : "",
                });
            Assert.Equal(1, result.ExitCode);
            Assert.Contains(socketPath ? "control socket unavailable:" : "daemon lock unavailable:", result.Stderr);
            Assert.DoesNotContain("already running", result.Stderr);
            Assert.Equal("keep this file", File.ReadAllText(blocked));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealLockContentionKeepsItsExistingDaemonExitCode(bool flatpak)
    {
        string directory = Directory.CreateTempSubdirectory("ox-unix-").FullName;
        try
        {
            using var held = DaemonLock.TryAcquire(Path.Combine(directory, "openxlr", "daemon.lock"));
            Assert.NotNull(held);
            var result = await ProcessRunner.RunAsync("dotnet", [typeof(WebSocketHub).Assembly.Location],
                TimeSpan.FromSeconds(8), environment: new Dictionary<string, string>
                {
                    ["XDG_RUNTIME_DIR"] = directory,
                    ["FLATPAK_ID"] = flatpak ? "io.github.emaspa.openxlr" : "",
                });
            Assert.Equal(flatpak ? 73 : 75, result.ExitCode);
            Assert.Contains("already running", result.Stderr);
        }
        finally { Directory.Delete(directory, true); }
    }
}
