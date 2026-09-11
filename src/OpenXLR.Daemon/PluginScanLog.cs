using Microsoft.Extensions.Logging;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// What the last plugin scan could not read, in the daemon's log. The scan
/// itself runs in Core, which has no logger and keeps its evidence bounded
/// for the diagnostics archive; without this the only record of a bundle
/// the scan lost was that archive, and a user who never collects one has
/// nothing to go on but a picker that is missing a plugin.
/// </summary>
internal static class PluginScanLog
{
    /// <summary>Log one line per failure and hand the caller the same list.</summary>
    internal static IReadOnlyList<PluginScanFailure> Write(ILogger log)
    {
        IReadOnlyList<PluginScanFailure> failures = PluginScanDiagnostics.Failures();
        foreach (PluginScanFailure failure in failures)
            log.LogWarning("plugin scan: {kind} bundle not read: {path}: {outcome}{exit}",
                failure.Kind, failure.Path, failure.Outcome,
                failure.ExitCode is int code ? $" (exit {code})" : "");
        return failures;
    }
}
