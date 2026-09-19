using System;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>
/// The one way the window hands a link to the desktop: https to a host we
/// name, through xdg-open, never through a shell. Anything else is refused.
/// </summary>
internal static class ExternalLink
{
    private static readonly string[] Hosts = ["github.com", "www.reddit.com", "discord.gg", "buymeacoffee.com"];

    /// <summary>The program handed the link; tests point it at a fake.</summary>
    internal static string Opener = "xdg-open";

    public static bool Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || Array.IndexOf(Hosts, uri.Host) < 0)
            return false;
        // The opener hands the link to the browser and returns on its own
        // time, so it runs as a program of the user's: no deadline, no
        // captured output. A start failure is already in the returned task.
        Task<int> run = ProcessRunner.RunInteractiveAsync(Opener, [uri.ToString()]);
        if (run.IsFaulted) return false;
        _ = run.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return true;
    }
}
