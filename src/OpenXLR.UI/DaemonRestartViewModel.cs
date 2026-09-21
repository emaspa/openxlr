using System;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>Shared restart state for the header, upgrade banner and daemon settings.</summary>
public sealed class DaemonRestartViewModel : ViewModelBase
{
    private readonly Func<Task<bool>> _restart;
    private bool _busy;
    private string? _status;

    public DaemonRestartViewModel()
        : this(() => Task.Run(StartupIntegration.RestartDaemon)) { }

    internal DaemonRestartViewModel(Func<Task<bool>> restart) => _restart = restart;

    public bool CanRestart => !_busy;
    public string? Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>
    /// The daemon connection came back after a restart: the "waiting for
    /// the daemon connection" line has served its purpose and goes away.
    /// </summary>
    public void ConnectionRestored()
    {
        if (!_busy) Status = null;
    }

    /// <summary>
    /// Run systemctl off the UI thread. Only one restart runs at a time, and a
    /// failed request leaves the buttons usable for another attempt.
    /// </summary>
    public async Task<bool> RestartAsync()
    {
        if (_busy) return false;
        _busy = true;
        Raise(nameof(CanRestart));
        Status = "Restarting daemon...";
        try
        {
            bool restarted = await _restart();
            Status = restarted
                ? "Service restarted. Waiting for the daemon connection."
                : "Restart failed. Check the user service logs; a manually started daemon must be restarted by hand.";
            return restarted;
        }
        catch (Exception)
        {
            Status = "Restart failed. Check the user service logs.";
            return false;
        }
        finally
        {
            _busy = false;
            Raise(nameof(CanRestart));
        }
    }
}
