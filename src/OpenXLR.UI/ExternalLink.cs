using System;
using System.Diagnostics;

namespace OpenXLR.UI;

/// <summary>
/// The one way the window hands a link to the desktop: https to a host we
/// name, through xdg-open, never through a shell. Anything else is refused.
/// </summary>
internal static class ExternalLink
{
    private static readonly string[] Hosts = ["github.com", "www.reddit.com", "discord.gg", "buymeacoffee.com"];

    public static bool Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || Array.IndexOf(Hosts, uri.Host) < 0)
            return false;
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", uri.ToString()) { UseShellExecute = false });
            return true;
        }
        catch (Exception) { return false; }
    }
}
