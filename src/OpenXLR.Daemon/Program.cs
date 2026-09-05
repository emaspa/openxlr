using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using OpenXLR.Daemon;

const int ApiPort = 37890;

var builder = WebApplication.CreateBuilder(args);

// Start the notifier before graph construction so progressing startup work
// can extend systemd's deadline. Readiness still waits for ApplicationStarted.
builder.Services.AddHostedService<ServiceWatchdog>();

// The DeviceManager is both a singleton (queried by the hub) and the hosted
// background service that runs the poll/reconnect loop.
builder.Services.AddSingleton<DeviceManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceManager>());

// The submixer is likewise a singleton the hub queries and a hosted service that
// builds the graph on start and tears it down on shutdown.
builder.Services.AddSingleton<MixerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MixerService>());

builder.Services.AddSingleton<WebSocketHub>();

// Local-only control API. 127.0.0.1 keeps the device off the network.
builder.WebHost.UseUrls($"http://127.0.0.1:{ApiPort}");
// A handful of local clients (the window, the deck plugin, a script or
// two); anything beyond that is a runaway client, not a use case.
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxConcurrentUpgradedConnections = 32;
    k.Limits.MaxConcurrentConnections = 64;
});

var app = builder.Build();
app.Services.GetRequiredService<WebSocketHub>();   // construct so it subscribes to StateChanged

app.UseWebSockets();

app.Map("/ws", async (HttpContext ctx, WebSocketHub hub) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    // Browser pages from other origins do not get to drive the hardware.
    if (!OpenXLR.Core.LoopbackOrigin.IsAllowed(ctx.Request.Headers.Origin))
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.HandleAsync(socket);
});

app.MapGet("/", () => Results.Text($"OpenXLR daemon. Control API: ws://127.0.0.1:{ApiPort}/ws"));

// The hosted services (device connect, PipeWire graph build) start before
// Kestrel binds, so a busy port used to mean: build the whole submix graph,
// fail to bind, abort with a core dump, get restarted by systemd, repeat.
// Every cycle tore the user's sinks down and back up. 37890 sits inside the
// kernel's ephemeral range, so any local program's outgoing connection can
// hold it for a while (the packages reserve it via sysctl; source installs
// may not). Wait for the port first, before anything touches PipeWire.
DateTime deadline = DateTime.UtcNow.AddSeconds(60);
for (int attempt = 0; ; attempt++)
{
    try
    {
        var probe = new TcpListener(IPAddress.Loopback, ApiPort);
        probe.Start();
        probe.Stop();
        break;
    }
    catch (SocketException) when (DateTime.UtcNow < deadline)
    {
        if (attempt % 10 == 0)
            app.Logger.LogWarning("port {Port} is in use by another local socket; waiting for it", ApiPort);
        await Task.Delay(1000);
    }
    catch (SocketException ex)
    {
        app.Logger.LogError("port {Port} still busy after 60 s ({Error}); exiting for systemd to retry", ApiPort, ex.Message);
        return 75;   // EX_TEMPFAIL: a clean exit, no core dump; Restart=on-failure tries again
    }
}

try
{
    app.Run();
}
catch (IOException ex) when (ex.InnerException is AddressInUseException)
{
    // Lost the race between the probe and Kestrel's own bind.
    app.Logger.LogError("port {Port} was taken between probe and bind; exiting for systemd to retry", ApiPort);
    return 75;
}
return 0;
