using OpenXLR.UI.Localization;
using System;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>Shared restart state for the header and upgrade banner buttons.</summary>
public sealed class DaemonRestartViewModel : ViewModelBase
{
    private readonly Func<Task<bool>> _restart;
    private bool _busy;
    private bool _reconnected;
    private string? _status;

    public DaemonRestartViewModel()
        : this(() => Task.Run(StartupIntegration.RestartDaemon)) { }

    internal DaemonRestartViewModel(Func<Task<bool>> restart) => _restart = restart;

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
    public async Task<bool> RestartAsync()
    {
        if (_busy) return false;
        _reconnected = false;
        _busy = true;
        Raise(nameof(CanRestart));
        Status = Localizer.Text("RestartingDaemon");
        try
        {
            bool restarted = await _restart();
            Status = restarted
                ? _reconnected ? null : Localizer.Text("ServiceRestartedWaiting")
                : Localizer.Text("RestartFailedManualDaemon");
            return restarted;
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
