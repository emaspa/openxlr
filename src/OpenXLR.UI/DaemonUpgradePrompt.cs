using System;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>The upgrade question never changes the service before the user accepts.</summary>
internal sealed class DaemonUpgradePrompt(
    Func<int, Func<bool>, Task<bool>> claim,
    Func<Task<bool>> ask,
    Func<int, Func<bool>, Task<bool>> restart,
    Action relaunch,
    Action failed)
{
    private bool _checked;

    internal async Task CheckAsync(string? version, int pid, Func<bool> isCurrent)
    {
        if (_checked || version is null || !UpdateChecker.Newer(AppVersion.Current, version)) return;
        _checked = true;
        try
        {
            if (!isCurrent() || !await claim(pid, isCurrent)) return;
            if (!isCurrent() || !await ask() || !isCurrent()) return;
            if (await restart(pid, isCurrent)) relaunch();
            else failed();
        }
        catch (Exception) { failed(); }
    }
}
