using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>
/// Bundles everything a useful bug report needs into one tar.gz a tester can
/// attach to an issue: app/device state, raw vendor blocks, the PipeWire
/// graph, daemon logs, configs and system info. Nothing is uploaded anywhere;
/// the file lands in the user's home directory.
/// </summary>
public static class Diagnostics
{
    public static async Task<string> CollectAsync(DaemonClient client)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string work = Directory.CreateTempSubdirectory("openxlr-diag-").FullName;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(work, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                       UnixFileMode.UserExecute);
        try
        {
            var meta = new StringBuilder();
            meta.AppendLine($"OpenXLR diagnostics {stamp}");
            meta.AppendLine($"version: {AppVersion.Current}");
            meta.AppendLine($"uname: {await RunAsync("uname", "-a")}");
            meta.AppendLine($"os-release: {TryReadFile("/etc/os-release")}");
            meta.AppendLine($"dotnet: {Environment.Version}");
            await File.WriteAllTextAsync(Path.Combine(work, "meta.txt"), meta.ToString());
            await File.WriteAllTextAsync(Path.Combine(work, "PRIVACY.txt"), """
                This archive is created locally and is never uploaded automatically.
                It contains OpenXLR state, USB control blocks, PipeWire topology,
                recent openxlr-daemon journal entries, application audio metadata,
                configuration files, and system version information. The home
                path, the host name, the serial numbers of attached USB devices
                (including inside PipeWire node names) and process-id fields are
                redacted, but review the archive before attaching it to a public
                issue.
                """);

            // Daemon views: the newest state push plus a fresh vendor-block dump.
            await File.WriteAllTextAsync(Path.Combine(work, "daemon-state.json"),
                Redact(client.LastStateJson ?? "no state received (daemon not running?)"));
            var blocks = await client.RequestDiagnosticsAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(Path.Combine(work, "device-blocks.json"),
                RedactHex(blocks?.ToJsonString() ?? "unavailable (daemon not running or no device)", DefaultSecrets()));

            // Audio stack.
            await WriteCmd(work, "pw-dump.json", "pw-dump");
            await WriteCmd(work, "pactl-info.txt", "pactl", "info");
            await WriteCmd(work, "wpctl-status.txt", "wpctl", "status");
            await WriteCmd(work, "sinks.txt", "pactl", "list", "short", "sinks");
            await WriteCmd(work, "sources.txt", "pactl", "list", "short", "sources");
            await WriteCmd(work, "modules.txt", "pactl", "list", "short", "modules");
            await WriteCmd(work, "lsusb.txt", "lsusb");
            await WriteCmd(work, "journal.txt", "journalctl", "--user", "-u", "openxlr-daemon",
                "--since", "2 hours ago", "--no-pager");

            // Configs may include remembered application identities and device
            // names; redact common personal fields and disclose them above.
            CopyRedactedIfExists(UiSettings.ConfigDir, "mixer.json", work);
            CopyRedactedIfExists(UiSettings.ConfigDir, "ui.json", work);

            string outPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                $"openxlr-diagnostics-{stamp}.tar.gz");
            await using (var fs = OpenXlrPaths.CreatePrivate(outPath))
            await using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize))
                await TarFile.CreateFromDirectoryAsync(work, gz, includeBaseDirectory: false);
            return outPath;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task WriteCmd(string dir, string file, string exe, params string[] args)
        => await File.WriteAllTextAsync(Path.Combine(dir, file), await RunAsync(exe, args));

    private static async Task<string> RunAsync(string exe, params string[] args)
    {
        try
        {
            // 15 s and 8 MiB per command; a helper that goes past either is
            // killed with its children and the archive says so.
            ProcessResult r = await ProcessRunner.RunAsync(exe, args, TimeSpan.FromSeconds(15),
                stdoutCap: 8 * 1024 * 1024, stderrCap: 64 * 1024, cLocale: false);
            string note = r.TimedOut ? $"\n[{exe} killed after 15 s]" : r.Truncated ? $"\n[{exe} output truncated at 8 MiB]" : "";
            return Redact(r.StdoutText + r.Stderr + note);
        }
        catch (Exception ex) { return Redact($"failed to run {exe}: {ex.Message}"); }
    }

    private static string TryReadFile(string path)
    {
        try { return File.ReadAllText(path).ReplaceLineEndings(" | "); }
        catch (IOException) { return "unreadable"; }
        catch (UnauthorizedAccessException) { return "unreadable"; }
    }

    internal static string Redact(string text) => Redact(text, DefaultSecrets());

    /// <summary>
    /// Strings that identify the machine or its owner: the home path, the
    /// host name, and the serial number of every attached USB device. Serials
    /// matter most: PipeWire embeds them in node and card names
    /// (alsa_input.usb-Elgato_..._&lt;serial&gt;-00...), so they surface in the
    /// graph dump, the sink and source listings, and mixer.json.
    /// </summary>
    private static IEnumerable<string> DefaultSecrets()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.MachineName;
        foreach (string serial in UsbSerials()) yield return serial;
    }

    private static IEnumerable<string> UsbSerials()
    {
        const string root = "/sys/bus/usb/devices";
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root); }
        catch (Exception) { yield break; }
        foreach (string dir in dirs)
        {
            string? serial = null;
            try
            {
                string file = Path.Combine(dir, "serial");
                if (File.Exists(file)) serial = File.ReadAllText(file).Trim();
            }
            catch (Exception) { /* unreadable: nothing to redact */ }
            if (serial is { Length: >= 4 }) yield return serial;
        }
    }

    /// <summary>
    /// Replaces every secret with a placeholder, then the well-known JSON
    /// fields. The plain user name is deliberately not on the list: a short
    /// name such as "max" occurs inside unrelated tokens
    /// ("clock.max-quantum") and would corrupt the graph dump; the home path
    /// covers the places it actually appears.
    /// </summary>
    internal static string Redact(string text, IEnumerable<string> secrets)
    {
        // Whole tokens only: a numeric USB serial once matched inside the
        // number 2147483647 in a pw-dump and left "2<redacted>", which broke
        // the JSON. A serial in a node name is bounded by "_" and "-", a host
        // name by spaces, a path by quotes, so alphanumeric lookarounds keep
        // those and skip digits inside larger numbers.
        foreach (string value in secrets
                     .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length >= 3)
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(v => v.Length))
            text = Regex.Replace(text,
                "(?<![A-Za-z0-9])" + Regex.Escape(value) + "(?![A-Za-z0-9])", "<redacted>");

        return Regex.Replace(text,
            "(\"(?:device\\.serial|object\\.serial|application\\.process\\.id|" +
            "application\\.process\\.user|application\\.process\\.host)\"\\s*:\\s*)" +
            "(?:\"[^\"]*\"|[0-9]+)",
            "$1\"<redacted>\"", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// The raw vendor blocks are hex, and a device can carry its USB serial
    /// inside one of them as ASCII (the XLR Dock's devinfo block 0x000A does,
    /// verified on hardware), where the plain-text pass cannot see it. Each
    /// secret is also looked for as the hex of its ASCII bytes and replaced by
    /// the hex of "?" characters of the same length, so offsets stay valid
    /// for anyone decoding the block.
    /// </summary>
    internal static string RedactHex(string text, IEnumerable<string> secrets)
    {
        foreach (string value in secrets
                     .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length >= 4 && v.All(char.IsAscii))
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(v => v.Length))
        {
            string hex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(value));
            string mask = string.Concat(Enumerable.Repeat("3F", value.Length));
            text = Regex.Replace(text, Regex.Escape(hex), mask, RegexOptions.IgnoreCase);
        }
        return text;
    }

    private static void CopyRedactedIfExists(string dir, string file, string dest)
    {
        string src = Path.Combine(dir, file);
        if (File.Exists(src))
            File.WriteAllText(Path.Combine(dest, file), Redact(File.ReadAllText(src)));
    }
}
