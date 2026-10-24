using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
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
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Task<string> CollectAsync(DaemonClient client)
        => CollectAsync(client, Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Collect into <paramref name="destination"/>, masking <paramref name="login"/>
    /// wherever it appears as a login name (see <see cref="Redact(string, IEnumerable{string}, string?)"/>).
    /// </summary>
    internal static async Task<string> CollectAsync(DaemonClient client, string? login, string destination)
    {
        string[] secrets = DefaultSecrets().ToArray();
        string Clean(string text) => Redact(text, secrets, login);
        (string Text, bool Valid) CleanJson(string text)
        {
            string redacted = RedactJsonText(text, secrets, login, out bool valid);
            return (redacted, valid);
        }
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string work = Directory.CreateTempSubdirectory("openxlr-diag-").FullName;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(work, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                       UnixFileMode.UserExecute);
        Task Capture(string file, string program, params string[] args)
            => WriteCmd(Clean, CleanJson, work, file, program, args);
        try
        {
            var meta = new StringBuilder();
            meta.AppendLine($"OpenXLR diagnostics {stamp}");
            meta.AppendLine($"version: {AppVersion.Current}");
            var (unameOutput, unameErrors) = await RunCommandAsync("uname", ["-a"]);
            meta.AppendLine($"uname: {Clean(unameOutput + unameErrors)}");
            meta.AppendLine($"os-release: {TryReadFile("/etc/os-release")}");
            meta.AppendLine($"dotnet: {Environment.Version}");
            await File.WriteAllTextAsync(Path.Combine(work, "meta.txt"), meta.ToString());
            await File.WriteAllTextAsync(Path.Combine(work, "PRIVACY.txt"), """
                This archive is created locally and is never uploaded automatically.
                It contains OpenXLR state, USB control blocks, PipeWire topology,
                recent openxlr-daemon journal entries or Flatpak logs,
                native audio-server and session-manager journal entries,
                short PipeWire node statistics, application audio metadata,
                configuration files, plugin names and paths, the plugin catalogue,
                Wine/yabridge versions and status, latest scan results, the saved
                output of plugin scans that failed, and system
                version information. Collecting it copies files that already
                exist: no plugin, scanner, bridge or Wine process is started.
                No plugin binaries, presets, Wine registry
                files or API token are collected. The home
                path, the login name, the host name, the serial numbers of
                attached USB devices (including inside PipeWire node names) and
                process-id fields are redacted, and so are browser tab titles
                and the addresses of browser tabs not routed to a channel.
                Files in the archive carry no
                owner name or id. Review the archive before attaching it to a
                public issue.
                """);

            // Daemon views: the newest state push plus a fresh vendor-block dump.
            await File.WriteAllTextAsync(Path.Combine(work, "daemon-state.json"),
                CleanJson(StateForArchive(client.LastStateJson)).Text);
            var blocks = await client.RequestDiagnosticsAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(Path.Combine(work, "device-blocks.json"),
                RedactHex(blocks?.ToJsonString() ?? "unavailable (daemon not running or no device)", secrets));

            await WritePluginDataAsync(client, work, TimeSpan.FromSeconds(15), login);

            // Audio stack.
            await Capture("pw-dump.json", "pw-dump");
            await Capture("pactl-info.txt", "pactl", "info");
            await Capture("wpctl-status.txt", "wpctl", "status");
            await Capture("sinks.txt", "pactl", "list", "short", "sinks");
            await Capture("sources.txt", "pactl", "list", "short", "sources");
            await Capture("modules.txt", "pactl", "list", "short", "modules");
            await Capture("lsusb.txt", "lsusb");
            await Capture("pw-top.txt", "pw-top", "--batch-mode", "--iterations", "3");
            await Capture("sink-inputs.json", "pactl", "--format=json", "list", "sink-inputs");
            await Capture("source-outputs.json", "pactl", "--format=json", "list", "source-outputs");
            if (FlatpakEnvironment.IsFlatpak)
                CopyDaemonLogs(Clean, OpenXlrPaths.StateDir, work);
            else
            {
                await Capture("journal.txt", "journalctl", "--user", "-u", "openxlr-daemon",
                    "--since", "2 hours ago", "--no-pager");
                await Capture("audio-journal.txt", "journalctl", "--user",
                    "-u", "pipewire.service", "-u", "pipewire-pulse.service", "-u", "wireplumber.service",
                    "--since", "2 hours ago", "--no-pager");
            }

            // Configs may include remembered application identities and device
            // names; redact common personal fields and disclose them above.
            CopyRedactedIfExists(text => CleanJson(text).Text, UiSettings.ConfigDir, "mixer.json", work);
            CopyRedactedIfExists(text => CleanJson(text).Text, UiSettings.ConfigDir, "ui.json", work);
            return await WriteArchiveAsync(work, destination, stamp);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Write the archive as a new private file and remove it again if the
    /// write fails. An existing file of the same name is refused untouched.
    /// Every entry is owned by uid and gid 0 with no owner or group name:
    /// a tar header otherwise carries the collecting account's login name,
    /// which the text passes cannot reach.
    /// </summary>
    internal static async Task<string> WriteArchiveAsync(string work, string destination, string stamp)
    {
        string outPath = Path.Combine(destination, $"openxlr-diagnostics-{stamp}.tar.gz");
        bool created = false;
        try
        {
            await using (var fs = OpenXlrPaths.CreatePrivate(outPath))
            {
                created = true;
                await using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
                await using var tar = new TarWriter(gz, TarEntryFormat.Pax);
                // Ordinal order puts every directory before the files inside it.
                foreach (string path in Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories)
                             .Order(StringComparer.Ordinal))
                {
                    string name = Path.GetRelativePath(work, path).Replace(Path.DirectorySeparatorChar, '/');
                    bool directory = Directory.Exists(path);
                    var entry = new PaxTarEntry(directory ? TarEntryType.Directory : TarEntryType.RegularFile,
                        directory ? name + "/" : name)
                    {
                        Uid = 0,
                        Gid = 0,
                        UserName = "",
                        GroupName = "",
                        ModificationTime = File.GetLastWriteTimeUtc(path),
                    };
                    if (!OperatingSystem.IsWindows()) entry.Mode = File.GetUnixFileMode(path);
                    if (directory) { await tar.WriteEntryAsync(entry); continue; }
                    await using FileStream data = File.OpenRead(path);
                    entry.DataStream = data;
                    await tar.WriteEntryAsync(entry);
                }
            }
            return outPath;
        }
        catch
        {
            if (created)
            {
                try { File.Delete(outPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    internal static async Task WritePluginDataAsync(DaemonClient client, string directory, TimeSpan timeout, string? login = null)
    {
        // Independent reply types; all requests use the daemon's environment.
        var requests = new[]
        {
            ("plugins.json", client.RequestPluginsAsync(timeout)),
            ("plugin-setup.json", client.RequestPluginSetupAsync(timeout)),
            ("plugin-discovery.json", client.RequestPluginDiagnosticsAsync(timeout))
        };
        string[] secrets = DefaultSecrets().ToArray();
        string? scanLogs = null;
        JsonNode? discovery = null;
        foreach (var (file, request) in requests)
        {
            JsonNode? reply = await request;
            // Where the daemon says it kept failed scans, read before the copy
            // is redacted, since redaction rewrites the home path inside it.
            if (file == "plugin-discovery.json") scanLogs = Text(reply, "discovery", "scanLogs", "directory");
            var data = reply?.DeepClone() ?? new JsonObject
            {
                ["unavailable"] = "No reply: daemon disconnected, query timed out, or command unsupported."
            };
            RedactJson(data, secrets, login);
            if (file == "plugin-discovery.json") discovery = data;
            await File.WriteAllTextAsync(Path.Combine(directory, file), data.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        await WriteScanLogsAsync(scanLogs, discovery, directory, secrets, login);
    }

    /// <summary>
    /// The daemon's newest state as the archive holds it. A browser tab's
    /// title says what someone is reading or watching, so every title is
    /// masked. A tab routed to a channel keeps its origin, which is what a
    /// routing report needs; the origin of a tab left at the browser default
    /// is masked as well.
    /// </summary>
    internal static string StateForArchive(string? state)
    {
        if (state is null) return "no state received (daemon not running?)";
        JsonNode? node;
        try { node = JsonNode.Parse(state); }
        catch (JsonException) { return state; }
        if (node is not JsonObject root || root["mixer"] is not JsonObject mixer
            || mixer["browserTabs"] is not JsonArray tabs || tabs.Count == 0) return state;
        foreach (JsonObject tab in tabs.OfType<JsonObject>())
        {
            if (tab.ContainsKey("title")) tab["title"] = "<redacted>";
            if (tab.ContainsKey("site") && tab["channelId"] is null) tab["site"] = "<redacted>";
        }
        return root.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>One string from a path of object keys, or null if it is not there or is not a string.</summary>
    private static string? Text(JsonNode? node, params string[] path)
    {
        foreach (string key in path)
        {
            if (node is not JsonObject obj) return null;
            node = obj[key];
        }
        return node is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0 ? text : null;
    }

    /// <summary>
    /// The scanner output the daemon saved for scans that failed, copied into
    /// the archive. This is a copy of files already on disk and nothing else:
    /// no plugin, scanner, bridge or Wine process is started, no rescan is
    /// asked for, and the daemon is not even involved beyond having said where
    /// the directory is. What is copied is bounded the way the daemon's own
    /// retention is, and only files that directory could itself have written
    /// are taken: a name outside the pattern, a symbolic link and anything
    /// that is not a plain readable file are listed in the index and skipped,
    /// so nothing the archive holds was reached by following a link out.
    /// </summary>
    internal static async Task WriteScanLogsAsync(string? source, JsonNode? discovery, string work, string[] secrets, string? login = null)
    {
        // The daemon's own bounds, and one more on each file, since a log is
        // read here rather than written here.
        const int MaxLogs = 24;
        const long MaxTotalBytes = 4L * 1024 * 1024;
        // A deep trace holds 1 MiB of stderr, 64 KiB of stdout and a bounded header.
        const int MaxFileBytes = 1152 * 1024;

        string destination = Path.Combine(work, "plugin-scan-logs");
        Directory.CreateDirectory(destination);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var index = new StringBuilder();
        index.AppendLine("The scanner's own output for plugin scans that failed, as the daemon saved it.");
        index.AppendLine("Collecting these copies files that were already on disk. No plugin, scanner,");
        index.AppendLine("bridge or Wine process is started by collecting them, and nothing is uploaded.");
        index.AppendLine("Each failed entry in plugin-discovery.json names its file in logId; an entry");
        index.AppendLine("with logNote instead is one whose output could not be saved at the time.");
        index.AppendLine("Paths, login and host names are redacted here as they are elsewhere in the archive.");
        index.AppendLine();

        var collected = new HashSet<string>(StringComparer.Ordinal);
        if (source is null)
            index.AppendLine("The daemon did not say where it keeps them, so none were collected.");
        // The one directory this collects from, whatever a reply says. A
        // daemon that answered with somewhere else, by fault or otherwise,
        // would have the archive copying files nobody meant to share.
        else if (!string.Equals(Path.GetFileName(source.TrimEnd('/')), "plugin-scan-logs", StringComparison.Ordinal))
            index.AppendLine("The daemon named a directory this does not collect from, so none were collected.");
        else if (!Directory.Exists(source))
            index.AppendLine("The directory does not exist: no scan has failed since it was last cleared.");
        else
        {
            List<FileInfo> logs = [];
            try
            {
                logs = [.. new DirectoryInfo(source).EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                index.AppendLine("The directory could not be read: " + ex.Message);
            }
            long total = 0;
            foreach (FileInfo log in logs)
            {
                string name = log.Name;
                if (!IsScanLogName(name)) { index.AppendLine($"skipped: a name this directory should not hold ({Printable(name)})"); continue; }
                if (log.LinkTarget is not null) { index.AppendLine($"skipped {name}: a link, not a file of its own"); continue; }
                if (collected.Count >= MaxLogs) { index.AppendLine($"skipped {name}: the archive collects the {MaxLogs} newest"); continue; }
                if (total >= MaxTotalBytes) { index.AppendLine($"skipped {name}: the archive's {MaxTotalBytes} byte budget is used up"); continue; }
                string text;
                long had;
                try
                {
                    string path = Path.Combine(source, name);
                    // Opening a FIFO already blocks, before CanSeek can help.
                    RegularFile.Require(path);
                    await using FileStream stream = File.OpenRead(path);
                    if (!stream.CanSeek) { index.AppendLine($"skipped {name}: not a plain file"); continue; }
                    had = stream.Length;
                    int room = (int)Math.Min(MaxFileBytes, MaxTotalBytes - total);
                    byte[] buffer = new byte[Math.Min(room, (int)Math.Min(had, int.MaxValue))];
                    int read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
                    text = Encoding.UTF8.GetString(buffer, 0, read);
                    total += read;
                    if (read < had) text += $"\n[the archive kept the first {read} of {had} bytes of this log]\n";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    index.AppendLine($"skipped {name}: {ex.Message}");
                    continue;
                }
                await File.WriteAllTextAsync(Path.Combine(destination, name),
                    RedactSecretValues(Redact(text, secrets, login)));
                collected.Add(Path.GetFileNameWithoutExtension(name));
                index.AppendLine($"{name}: {had} bytes, last written {log.LastWriteTimeUtc:O}");
            }
        }

        // A summary entry whose log is gone says so here rather than leaving
        // the reader to wonder which file the id points at.
        foreach (string id in ReferencedLogIds(discovery).Where(id => !collected.Contains(id)).Order(StringComparer.Ordinal))
            index.AppendLine($"{id}.log: named by a scan entry but not collected (aged out of the daemon's retention, "
                + "or never written because a scan happened before this version, or the copy above skipped it)");
        await File.WriteAllTextAsync(Path.Combine(destination, "index.txt"), index.ToString());
    }

    /// <summary>Exactly the names the daemon's log store writes, and nothing else.</summary>
    internal static bool IsScanLogName(string name)
        => name.Length is > 5 and <= 96 && name.EndsWith(".log", StringComparison.Ordinal)
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');

    /// <summary>Every logId a scan report names, however deep it sits.</summary>
    private static IEnumerable<string> ReferencedLogIds(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? child) in obj)
                if (key == "logId" && child is JsonValue value && value.TryGetValue(out string? id) && id.Length > 0) yield return id;
                else foreach (string found in ReferencedLogIds(child)) yield return found;
        }
        else if (node is JsonArray array)
            foreach (JsonNode? child in array)
                foreach (string found in ReferencedLogIds(child)) yield return found;
    }

    /// <summary>A name from an unexpected file, with nothing in it that could rewrite the line it lands on.</summary>
    private static string Printable(string name)
        => new([.. name.Take(64).Select(c => char.IsControl(c) ? '?' : c)]);

    /// <summary>
    /// The value of any field whose name says it holds a credential. The
    /// archive's other passes look for known strings; this one looks for the
    /// shape, because a scanner's output is whatever a plugin decided to
    /// print and nobody enumerated what a bridge might echo.
    /// </summary>
    internal static string RedactSecretValues(string text, TimeSpan? timeout = null)
    {
        try
        {
            // A name, then an explicit assignment, then one value. The
            // assignment is what makes it a field rather than the same word in
            // a sentence, and a scheme in front of the value ("Bearer x") is
            // part of the value, not the end of the match. Logs can quote
            // field names and escape quotes or spaces inside a value. A
            // truncated quoted value is masked through the end of its line.
            return Regex.Replace(text,
                """\b(token|password|passphrase|secret|api[-_]?key|apikey|authorization)\b(["']?\s*[:=]\s*)""" +
                """(?:(?:Bearer|Basic|Token|Digest)\s+)?(?:"(?:\\[^\r\n]|[^"\\\r\n])*\\?"?""" +
                """|'(?:\\[^\r\n]|[^'\\\r\n])*\\?'?""" +
                """|(?:\\[^\r\n]|[^\s\\,;)}\]])*\\?)""",
                "$1$2<redacted>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                timeout ?? TimeSpan.FromSeconds(5));
        }
        catch (RegexMatchTimeoutException)
        {
            // A scan log is private until the whole redaction succeeds.
            return "[diagnostic text omitted: credential redaction timed out]";
        }
    }

    private static void RedactJson(JsonNode node, string[] secrets, string? login)
        => RedactJson(node, CreateRedactor(secrets, login));

    private static void RedactJson(JsonNode node, Func<string, string> clean)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(p => p.Key).ToArray())
            {
                if (PrivateJsonFields.Contains(key)) obj[key] = "<redacted>";
                else if (obj[key] is JsonValue value && value.TryGetValue<string>(out string? text)) obj[key] = clean(text);
                else if (obj[key] is { } child) RedactJson(child, clean);
                string redacted = clean(key);
                if (redacted == key) continue;
                // Two private paths can become the same key after masking.
                // Keep both values without putting the private key back.
                string unique = redacted;
                for (int suffix = 1; obj.ContainsKey(unique); suffix++) unique = redacted + "_" + suffix;
                JsonNode? contents = obj[key];
                obj.Remove(key);
                obj[unique] = contents;
            }
        }
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
                if (array[i] is JsonValue value && value.TryGetValue<string>(out string? text)) array[i] = clean(text);
                else if (array[i] is { } child) RedactJson(child, clean);
    }

    private static readonly HashSet<string> PrivateJsonFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "device.serial", "object.serial", "application.process.id", "application.process.user", "application.process.host",
        "user-name", "host-name",
    };

    /// <summary>Redact string values and identity fields without rewriting coincidentally equal numbers.</summary>
    internal static string RedactJsonText(string text, string[] secrets, string? login, out bool valid)
    {
        try
        {
            JsonNode? data = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (data is not null) RedactJson(data, secrets, login);
            valid = true;
            return data?.ToJsonString(ReportJson) ?? "null";
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            valid = false;
            return Redact(text, secrets, login);
        }
    }

    private static async Task WriteCmd(Func<string, string> clean, Func<string, (string Text, bool Valid)> cleanJson,
        string dir, string file, string exe, string[] args)
    {
        var (output, errors) = await RunCommandAsync(exe, args);
        bool json = file.EndsWith(".json", StringComparison.Ordinal);
        string saved;
        if (json)
        {
            var result = cleanJson(file == "pw-dump.json" ? GraphForArchive(output) : output);
            saved = result.Text;
            if (!result.Valid) errors += $"\n[{exe} did not return complete valid JSON]";
        }
        else saved = clean(output);
        string separator = output.Length > 0 && errors.Length > 0 && !output.EndsWith('\n') ? "\n" : "";
        await File.WriteAllTextAsync(Path.Combine(dir, file), json ? saved : saved + separator + clean(errors));
        if (json && errors.Length > 0)
            await File.WriteAllTextAsync(Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + "-errors.txt"), clean(errors));
    }

    internal static string GraphForArchive(string output)
    {
        try
        {
            using JsonDocument snapshot = PipeWireSnapshot.Parse(Encoding.UTF8.GetBytes(output));
            return snapshot.RootElement.GetRawText();
        }
        catch (JsonException) { return output; }
    }

    internal static string CommandNotes(string exe, ProcessResult result)
    {
        var notes = new List<string>();
        if (result.TimedOut) notes.Add($"[{exe} killed after 15 s]");
        if (result.Truncated) notes.Add($"[{exe} output truncated; limits: stdout 8 MiB, stderr 64 KiB]");
        if (result.Incomplete) notes.Add($"[{exe} output ended before the helper closed it]");
        if (result.Cancelled) notes.Add($"[{exe} collection cancelled]");
        if (result.ExitCode != 0) notes.Add($"[{exe} exited with status {result.ExitCode}]");
        return notes.Count == 0 ? "" : "\n" + string.Join("\n", notes);
    }

    internal static async Task<(string Output, string Errors)> RunCommandAsync(string exe, string[] args)
    {
        try
        {
            // 15 s and 8 MiB per command; a helper that goes past either is
            // killed with its children and the archive says so.
            ProcessResult r = await ProcessRunner.RunAsync(exe, args, TimeSpan.FromSeconds(15),
                stdoutCap: 8 * 1024 * 1024, stderrCap: 64 * 1024);
            return (r.StdoutText, r.Stderr + CommandNotes(exe, r));
        }
        catch (Exception ex) { return ("", $"failed to run {exe}: {ex.Message}"); }
    }

    private static string TryReadFile(string path)
    {
        try { return File.ReadAllText(path).ReplaceLineEndings(" | "); }
        catch (IOException) { return "unreadable"; }
        catch (UnauthorizedAccessException) { return "unreadable"; }
    }

    internal static string Redact(string text) => Redact(text, DefaultSecrets(), Environment.UserName);

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
    /// Replaces every secret with a placeholder, then the login name where it
    /// stands as one, then the well-known fields. The login name is not a
    /// plain secret: a short name such as "max" occurs inside unrelated tokens
    /// ("clock.max-quantum") and would corrupt the graph dump. So it is
    /// masked only in the shapes that make it a login name: before an "@"
    /// ("max@host", as wpctl lists every client), inside PipeWire's core name
    /// ("pipewire-max-1585") and as the directory under /home, /media,
    /// /run/media or /mnt. The fields that name an account or a host,
    /// whoever's they are, are masked by their name: pactl's "User Name" and
    /// "Host Name" lines, the user@host part of a wpctl client line and the
    /// JSON fields below.
    /// </summary>
    internal static string Redact(string text, IEnumerable<string> secrets, string? login = null)
        => CreateRedactor(secrets, login)(text);

    private static Func<string, string> CreateRedactor(IEnumerable<string> secrets, string? login)
    {
        var rules = new List<(Regex Pattern, string Mask)>();
        void Add(string pattern, string mask, RegexOptions options = RegexOptions.None)
            => rules.Add((new Regex(pattern, options), mask));

        // Build once per report. Applying Redact separately to every JSON
        // field churns the global regex cache when many devices have serials.
        foreach (string value in secrets
                     .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length >= 3)
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(v => v.Length))
            Add("(?<![A-Za-z0-9])" + Regex.Escape(value) + "(?![A-Za-z0-9])", "<redacted>");

        if (!string.IsNullOrWhiteSpace(login))
        {
            string name = Regex.Escape(login);
            Add("(?<![A-Za-z0-9._-])" + name + "(?=@)", "<redacted>");
            Add("(?<=\\bpipewire-)" + name + "(?=-[0-9]+\\b)", "<redacted>");
            Add("(?<=/(?:home|var/home|media|run/media|mnt)/)" + name + "(?![A-Za-z0-9._-])", "<redacted>");
        }

        Add("(?m)^((?:User|Host) Name:[ \\t]*)[^\\r\\n]+", "$1<redacted>");
        Add("(\\[[^\\[\\],\\r\\n]*, )[^\\s,@\\[\\]]+@[^\\s,\\[\\]]+(?=[,\\]])", "$1<redacted>@<redacted>");
        Add("(\"(?:device\\.serial|object\\.serial|application\\.process\\.id|" +
            "application\\.process\\.user|application\\.process\\.host|user-name|host-name)\"\\s*:\\s*)" +
            "(?:\"[^\"]*\"|[0-9]+)", "$1\"<redacted>\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text =>
        {
            foreach (var (pattern, mask) in rules) text = pattern.Replace(text, mask);
            return text;
        };
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

    internal const int DaemonLogCopyLimit = 1152 * 1024;

    internal static void CopyDaemonLogs(Func<string, string> clean, string dir, string dest)
    {
        foreach (string name in new[] { "daemon.log", "daemon.log.old" })
        {
            CopyRedactedIfExists(text => clean(RedactSecretValues(text)), dir, name, dest, DaemonLogCopyLimit);
        }
    }

    internal static void CopyRedactedIfExists(Func<string, string> clean, string dir, string file, string dest, int? maximum = null)
    {
        string src = Path.Combine(dir, file);
        string text;
        try
        {
            if (new FileInfo(src).LinkTarget is not null)
                text = "File unavailable: symbolic links are not collected.";
            else if (!File.Exists(src)) return;
            else text = RegularFile.ReadText(src, maximum ?? OpenXlrPaths.MaxPreferenceBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { text = "File unavailable: " + ex.Message; }
        File.WriteAllText(Path.Combine(dest, file), clean(text));
    }
}
