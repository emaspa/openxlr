using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

public sealed partial class MainViewModel
{
    private bool? _routeAppsByDefault;
    private bool _appRoutingPending;
    private long _appRoutingVersion;
    public bool AppRoutingSupported => _routeAppsByDefault.HasValue;
    public bool RouteAppsByDefault => _routeAppsByDefault ?? true;
    public bool CanSetAppRouting => AppRoutingSupported && DaemonConnected && HasMixer && !_appRoutingPending;

    /// <summary>Show only reported values; a failed command snaps the checkbox back.</summary>
    public async Task SetRouteAppsByDefaultAsync(bool enabled)
    {
        if (!CanSetAppRouting) { Raise(nameof(RouteAppsByDefault)); return; }
        long version = ++_appRoutingVersion;
        _appRoutingPending = true;
        Raise(nameof(CanSetAppRouting));
        string? error = await _client.SetRouteAppsByDefaultAsync(enabled);
        if (version != _appRoutingVersion) return;
        _appRoutingPending = false;
        if (error is not null) ReportError(error);
        Raise(nameof(RouteAppsByDefault)); Raise(nameof(CanSetAppRouting));
    }

    private void ApplyAppRouting(JsonNode? mixer)
    {
        if (mixer?["routeAppsByDefault"] is not JsonValue value || !value.TryGetValue<bool>(out bool enabled))
        { ResetAppRouting(); return; }
        _routeAppsByDefault = enabled;
        Raise(nameof(RouteAppsByDefault)); Raise(nameof(AppRoutingSupported)); Raise(nameof(CanSetAppRouting));
    }

    private void ResetAppRouting()
    {
        _appRoutingVersion++;
        _appRoutingPending = false;
        _routeAppsByDefault = null;
        Raise(nameof(RouteAppsByDefault)); Raise(nameof(AppRoutingSupported)); Raise(nameof(CanSetAppRouting));
    }
}
