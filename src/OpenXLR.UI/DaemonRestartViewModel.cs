using OpenXLR.UI.Localization;
using System;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>Shared restart state for the header and upgrade banner buttons.</summary>
public sealed class DaemonRestartViewModel : ViewModelBase
{
    private readonly Func<Task<bool>> _restart;
    private bool _busy;
    private bool _upgradeChecked;
    private bool _reconnected;
    private string? _status;

    public DaemonRestartViewModel()
        : this(() => Task.Run(StartupIntegration.RestartDaemon)) { }

    internal DaemonRestartViewModel(Func<Task<bool>> restart) => _restart = restart;

    internal Func<int, Task<bool?>>? AutomaticRestart { get; set; }

    public bool CanRestart => !_busy;
    public string? Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>
    /// Keep connection events that arrive before the restart command exits.
    /// A later disconnect still requires waiting for another connection.
    /// </summary>
    public void ConnectionChanged(bool connected)
    {
        _reconnected = connected;
        if (connected && !_busy) Status = null;
    }

    /// <summary>
    /// Run systemctl off the UI thread. Only one restart runs at a time, and a
    /// failed request leaves the buttons usable for another attempt.
    /// </summary>
    public Task<bool> RestartAsync()
    {
        _upgradeChecked = true;
        return RestartAsync(async () => await _restart());
    }

    internal Task CheckForUpgradeAsync(string? daemonVersion, int peerPid)
    {
        if (_upgradeChecked || AutomaticRestart is not { } restart || daemonVersion is null)
            return Task.CompletedTask;

        // Use the first reported version. A reconnect or an unsuccessful
        // replacement must not keep interrupting audio in this UI session.
        _upgradeChecked = true;
        return UpdateChecker.Newer(AppVersion.Current, daemonVersion) ? RestartAsync(() => restart(peerPid)) : Task.CompletedTask;
    }

    private async Task<bool> RestartAsync(Func<Task<bool?>> restart)
    {
        if (_busy) return false;
        _reconnected = false;
        _busy = true;
        Raise(nameof(CanRestart));
        Status = Localizer.Text("RestartingDaemon");
        try
        {
            bool? restarted = await restart();
            Status = restarted switch
            {
                true when !_reconnected => Localizer.Text("ServiceRestartedWaiting"),
                false => Localizer.Text("RestartFailedManualDaemon"),
                _ => null,
            };
            return restarted == true;
        }
        catch (Exception)
        {
            Status = Localizer.Text("RestartFailed");
            return false;
        }
        finally
        {
            _busy = false;
            Raise(nameof(CanRestart));
        }
    }
}
