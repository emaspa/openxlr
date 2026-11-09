using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using OpenXLR.Daemon;
using OpenXLR.Core;

// The same binary is its own USB helper (see UsbHelperMain): nothing of the
// host below runs in that mode.
if (args.Length == 1 && args[0] == "--usb-helper") return UsbHelperMain.Run();

// The stdio adapter is a client only, before any device or audio work.
if (args.Contains("--control-stdio"))
{
    if (args.Length != 1) { Console.Error.WriteLine("--control-stdio takes no other arguments."); return 2; }
    return await ControlStdio.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
}

FileStream? acquiredLock;
try { acquiredLock = DaemonLock.TryAcquire(); }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("daemon lock unavailable: " + ex.Message);
    return 1;
}
using FileStream? instanceLock = acquiredLock;
if (instanceLock is null)
{
    Console.Error.WriteLine($"another OpenXLR daemon is already running (it holds {DaemonLock.Path})");
    return OpenXLR.Core.FlatpakEnvironment.IsFlatpak ? 73 : 75;
}
OpenXLR.Core.ControlSocketListener control;
try { control = OpenXLR.Core.ControlSocketListener.Bind(); }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException or TimeoutException)
{
    Console.Error.WriteLine("control socket unavailable: " + ex.Message);
    return 1;
}
using var controlLease = control;
// The token file of a release before the control socket, wherever it was
// written: the runtime directory, or the configuration directory without one.
DaemonLock.RemoveLegacyToken();

// Snapshot the system default sink and source before any hosted service
// runs. Once DeviceManager connects the interface it parks the card in the
// pro-audio profile, WirePlumber re-creates the card's nodes and may move
// the defaults to them, and a snapshot taken later would defend the wrong
// value for the rest of the session (see StartupDefaults).
StartupDefaults startupDefaults = StartupDefaults.Capture();

// The content root is where the host looks for appsettings.json, and it
// defaults to the working directory: the home directory under the user
// unit. A Kestrel endpoint section in a file there would replace the
// listener below, so the root is pinned to the install directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddSingleton(startupDefaults);
// The unit allows 45 s to stop (TimeoutStopSec). Past this limit the host
// stops waiting for the device loop and the remaining services, which leaves
// room for the process to exit before systemd kills it. The mixer keeps its
// own, shorter limit for the audio server (MixerService.StopBudget).
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
if (OpenXLR.Core.FlatpakEnvironment.IsFlatpak)
{
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(new FlatpakLog(Path.Combine(OpenXLR.Core.OpenXlrPaths.StateDir, "daemon.log")));
    builder.Services.AddHostedService<FlatpakParentLifetime>();
}

// Start the notifier before graph construction so progressing startup work
// can extend systemd's deadline. Readiness still waits for ApplicationStarted.
if (!OpenXLR.Core.FlatpakEnvironment.IsFlatpak) builder.Services.AddHostedService<ServiceWatchdog>();

// The DeviceManager is both a singleton (queried by the hub) and the hosted
// background service that runs the poll/reconnect loop.
builder.Services.AddSingleton<DeviceManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceManager>());

// The submixer is likewise a singleton the hub queries and a hosted service that
// builds the graph on start and tears it down on shutdown.
builder.Services.AddSingleton<MixerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MixerService>());
// Additional interfaces, each driven by its own device manager.
builder.Services.AddSingleton<WaveInterfaces>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WaveInterfaces>());

builder.Services.AddSingleton<WebSocketHub>();

ControlApi.Configure(builder, control);

var app = builder.Build();
app.Services.GetRequiredService<WebSocketHub>();   // construct so it subscribes to StateChanged

app.UseWebSockets();
ApiEndpoints.Map(app);

app.Map("/ws", async (HttpContext ctx, WebSocketHub hub) =>
{
    if (!ControlApi.Authorized(ctx)) { ctx.Response.StatusCode = 403; return; }
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    // Browser pages from other origins do not get to drive the hardware.
    if (!OpenXLR.Core.LoopbackOrigin.IsAllowed(ctx.Request.Headers.Origin))
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    // Transport pings every 30 s; a peer that stops answering them for 30 s
    // is dropped, while a quiet but live client (the window listens for
    // hours without sending) is never touched.
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
    {
        KeepAliveInterval = SocketGuard.KeepAliveInterval,
        KeepAliveTimeout = SocketGuard.KeepAliveTimeout,
    });
    await hub.HandleAsync(socket);
});

app.MapGet("/", () => Results.Text("OpenXLR control API on the private Unix socket."));

// Preserve the installed payload inventory before an upgrade or removal can
// replace it. A failed inventory must never keep the microphone off.
foreach (string receipt in OpenXLR.Core.InstallationHistory.PackageReceipts(AppContext.BaseDirectory))
{
    try { OpenXLR.Core.InstallationHistory.Remember(receipt); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    { app.Logger.LogWarning("Could not record installation receipt {Path}: {Error}", receipt, ex.Message); }
}

try
{
    app.Run();
}
catch (IOException ex) when (ex.InnerException is AddressInUseException)
{
    // Lost the race between the probe and Kestrel's own bind.
    app.Logger.LogError("control socket could not be bound; exiting for systemd to retry");
    return 75;
}
return RestartRequest.ExitCode;   // 0, or a service asked to be started afresh
