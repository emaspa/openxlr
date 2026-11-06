using System;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>Restart only with the user's consent, for the current systemd connection.</summary>
internal sealed class DaemonUpgradePrompt(
    Func<int, Func<bool>, Task<bool>> isManaged,
    Func<bool> claim,
    Func<Task<bool>> consent,
    Func<int, Func<bool>, Task<bool>> restart,
    Action relaunch,
    Action failed)
{
    private long? _checkedConnection;
    private bool _checking;
    private bool _offered;

    internal async Task CheckAsync(string? version, int pid, long connection, Func<bool> isCurrent, Func<bool> canAsk)
    {
        if (_offered || _checking || _checkedConnection == connection || pid <= 0 ||
            version is null || !UpdateChecker.Newer(AppVersion.Current, version) ||
            !isCurrent() || !canAsk()) return;
        _checking = true;
        try
        {
            bool managed = await isManaged(pid, isCurrent);
            if (!isCurrent() || !canAsk()) return;
            _checkedConnection = connection;
            if (!managed) return;

            _offered = true;
            if (!claim() || !await consent() || !isCurrent()) return;
            if (await restart(pid, isCurrent)) relaunch();
            else failed();
        }
        catch (Exception) { failed(); }
        finally { _checking = false; }
    }
}
