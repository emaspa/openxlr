using OpenXLR.UI.Localization;
using System;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>Shared restart state for the header and upgrade banner buttons.</summary>
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
    public async Task RestartAsync()
    {
        if (_busy) return;
        _busy = true;
        Raise(nameof(CanRestart));
        Status = Localizer.Text("RestartingDaemon");
        try
        {
            Status = await _restart()
                ? Localizer.Text("ServiceRestartedWaiting")
                : Localizer.Text("RestartFailedManualDaemon");
        }
        catch (Exception)
        {
            Status = Localizer.Text("RestartFailed");
        }
        finally
        {
            _busy = false;
            Raise(nameof(CanRestart));
        }
    }
}
