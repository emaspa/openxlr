using System;
using System.Diagnostics;
using System.Globalization;
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
public static partial class Diagnostics
{
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Task<string> CollectAsync(DaemonClient client, bool deepTrace = false)
        => CollectAsync(client, Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), deepTrace);

    /// <summary>
    /// Collect into <paramref name="destination"/>, masking <paramref name="login"/>
    /// wherever it appears as a login name (see <see cref="Redact(string, IEnumerable{string}, string?)"/>).
    /// </summary>
    internal static async Task<string> CollectAsync(DaemonClient client, string? login, string destination, bool deepTrace = false)
    {
        string[] secrets = DefaultSecrets().ToArray();
        var clean = CreateRedactor(secrets, login);
        string Clean(string text) => clean(text);
        string CleanJson(string text) => RedactJsonText(text, secrets, login, out _);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string work = Directory.CreateTempSubdirectory("openxlr-diag-").FullName;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(work, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                       UnixFileMode.UserExecute);
        Task<AudioGraphSummary?> Capture(string file, string program, params string[] args)
            => WriteCmd(Clean, work, file, program, args);
        try
        {
            var observation = client.StateForDiagnostics;
            DateTimeOffset observedAt = DateTimeOffset.UtcNow;
            if (deepTrace)
            {
                string trace = await CollectDeepTraceAsync(client);
                await File.WriteAllTextAsync(Path.Combine(work, "deep-trace.json"), CleanJson(trace));
            }
            string osRelease = TryReadFile("/etc/os-release");
            var meta = new StringBuilder();
            meta.AppendLine($"OpenXLR diagnostics {stamp}");
            meta.AppendLine($"state collected at: {observedAt:O}");
            meta.AppendLine($"version: {AppVersion.Current}");
            meta.AppendLine($"window build version: {Clean(AppVersion.BuildVersion)}");
            meta.AppendLine($"window build id: {typeof(AppVersion).Assembly.ManifestModule.ModuleVersionId:D}");
            meta.AppendLine($"daemon connected at state collection: {observation.Connected}");
            meta.AppendLine($"daemon state received at: {observation.ReceivedAt?.ToString("O") ?? "unavailable"}");
            var (unameOutput, unameErrors) = await RunCommandAsync("uname", ["-a"]);
            meta.AppendLine($"uname: {Clean(unameOutput + unameErrors)}");
            meta.AppendLine($"os-release: {Clean(osRelease)}");
            meta.AppendLine($"dotnet: {Environment.Version}");
            foreach (string line in CardProfileLines(observation.Json)) meta.AppendLine(Clean(line));
            foreach (string line in ClockLines(observation.Json)) meta.AppendLine(Clean(line));
            await File.WriteAllTextAsync(Path.Combine(work, "meta.txt"), meta.ToString());
            await File.WriteAllTextAsync(Path.Combine(work, "PRIVACY.txt"), """
                This archive is created locally and is never uploaded automatically.
                It contains OpenXLR state, USB control blocks and topology, the USB controllers, PipeWire topology,
                openxlr-daemon journal entries from the current boot or Flatpak logs,
                native audio-server and session-manager journal entries from the current boot,
                kernel messages about USB, sound and power management from the current boot,
                the status of open ALSA devices, application audio metadata,
                a summary of playback and recording streams and their connected
                audio nodes, system and desktop session details, and the
                versions reported by local audio tools,
                configuration files, plugin names and paths, the plugin catalogue,
                Wine/yabridge versions and status, latest scan results, the saved
                output of plugin scans that failed, and system
                version and assembly build information, including source
                revisions when available. Collecting it copies files that already
                exist: no plugin, scanner, bridge or Wine process is started.
                No plugin binaries, presets, Wine registry
                files or API token are collected. The home
                path, the login name, the host name, the serial numbers of
                attached USB devices (including inside PipeWire node names, but not a USB controller's PCI address) and
                structured process-id fields are redacted, and so are browser tab titles
                and the addresses of browser tabs not routed to a channel.
                The audio graph and stream listings mask media names, tags,
                URLs and process arguments. They retain executable basenames
                and audio node names. Invalid JSON is omitted.
                Files in the archive carry no
                owner name or id. Review the archive before attaching it to a
                public issue.
                """ + (deepTrace ? "\n\n" + """
                Deep Trace was selected for this export. It also includes the
                process names and counts visible to OpenXLR for this user, memory
                totals for the system, and memory and thread counts for OpenXLR
                and common audio services. Process names may reveal applications
                unrelated to audio. The snapshot contains no process ids, command
                lines, environment variables or memory contents. It also includes
                saved daemon preferences, current and historical package build
                metadata and managed heap statistics for the window and daemon.
                """ : ""));

            // Keep the saved state and its receive time from one observation.
            // The vendor blocks are a separate fresh query.
            await File.WriteAllTextAsync(Path.Combine(work, "daemon-state.json"),
                CleanJson(StateForArchive(observation.Json)));
            var blocks = await client.RequestDiagnosticsAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(Path.Combine(work, "device-blocks.json"),
                blocks is null ? JsonOmission("No reply from the daemon.")
                    : CleanJson(RedactHex(blocks.ToJsonString(), secrets)));

            await WritePluginDataAsync(client, work, TimeSpan.FromSeconds(15), login);

            // Audio stack.
            DateTimeOffset graphAt = DateTimeOffset.UtcNow;
            AudioGraphSummary graph = await Capture("pw-dump.json", "pw-dump") ?? UnavailableAudioGraph;
            string applications = JsonSerializer.Serialize(new
            {
                collectedAt = graphAt,
                graph.Available,
                graph.Truncated,
                graph.Streams,
            }, ReportJson);
            await File.WriteAllTextAsync(Path.Combine(work, "audio-applications.json"), CleanJson(applications));
            await WriteSystemInfoAsync(work, observation, observedAt, osRelease, graph, CleanJson);
            await Capture("pactl-info.txt", "pactl", "info");
            await Capture("wpctl-status.txt", "wpctl", "status");
            await Capture("sinks.txt", "pactl", "list", "short", "sinks");
            await Capture("sources.txt", "pactl", "list", "short", "sources");
            await Capture("modules.txt", "pactl", "list", "short", "modules");
            await Capture("lsusb.txt", "lsusb");
            await Capture("usb-topology.txt", "lsusb", "-t");
            // Which chip runs each USB controller and the driver bound to it:
            // a USB interface's audio can depend on the controller, not only the port.
            await Capture("usb-controllers.txt", "lspci", "-nnk", "-d", "::0c03");
            await File.WriteAllTextAsync(Path.Combine(work, "alsa-pcm.txt"), Clean(AlsaSnapshot()));
            await Capture("sink-inputs.json", "pactl", "--format=json", "list", "sink-inputs");
            await Capture("source-outputs.json", "pactl", "--format=json", "list", "source-outputs");
            if (FlatpakEnvironment.IsFlatpak)
                CopyDaemonLogs(Clean, OpenXlrPaths.StateDir, work);
            else
            {
                await WriteJournalAsync(Clean, work, "journal.txt", "journalctl",
                    ["--user", "-u", "openxlr-daemon.service"]);
                await WriteJournalAsync(Clean, work, "audio-journal.txt", "journalctl",
                    ["--user", "-u", "pipewire.service", "-u", "pipewire-pulse.service", "-u", "wireplumber.service"]);
                await WriteKernelLogAsync(Clean, work, "journalctl", "dmesg");
            }

            // Configs may include remembered application identities and device
            // names; redact common personal fields and disclose them above.
            CopyRedactedIfExists(CleanJson, UiSettings.ConfigDir, "mixer.json", work);
            CopyRedactedIfExists(CleanJson, UiSettings.ConfigDir, "ui.json", work);
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
        if (state is null) return JsonOmission("No daemon state was received.");
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

    /// <summary>
    /// One line per driven unit's audio card from the daemon's state: the
    /// card, its profile, whether that profile has a microphone input, a
    /// switch the daemon made and the problem it reports, so an archive says
    /// in one line why a microphone has no input. The daemon's check is
    /// what it last saw; pw-dump.json beside it is the graph at collection.
    /// </summary>
    internal static IReadOnlyList<string> CardProfileLines(string? state)
    {
        JsonNode? node = null;
        if (state is not null)
            try { node = JsonNode.Parse(state); }
            catch (JsonException) { }
        List<string> lines = [];
        void Add(string unit, JsonNode? card)
        {
            if (card is not JsonObject) return;
            bool capture = card["capture"] is JsonValue c && c.TryGetValue(out bool yes) && yes;
            var line = new StringBuilder($"audio card of {unit} (last checked by the daemon): {Text(card, "card") ?? "unknown"}, " +
                                         $"profile {Text(card, "profile") ?? "unknown"}, microphone input {(capture ? "yes" : "no")}");
            if (Text(card, "switchedFrom") is { } from) line.Append($", switched from {from} by OpenXLR");
            if (Text(card, "problem") is { } problem) line.Append($", problem: {problem}");
            lines.Add(line.ToString());
        }
        if (node is JsonObject root)
        {
            if (root["connected"] is JsonValue connected && connected.TryGetValue(out bool on) && on)
                Add($"{Text(root, "device", "vendor")} {Text(root, "device", "model")}".Trim(), root["cardProfile"]);
            if (root["waveInterfaces"] is JsonArray units)
                foreach (JsonObject unit in units.OfType<JsonObject>())
                    if (unit["active"] is JsonValue active && active.TryGetValue(out bool primary) && !primary)
                        Add(Text(unit, "name") ?? Text(unit, "id") ?? "an additional interface", unit["cardProfile"]);
        }
        if (lines.Count == 0)
            lines.Add("audio cards: not checked (no unit driven, the software mixer off, or a daemon without the check)");
        return lines;
    }

    /// <summary>
    /// The audio clock from the daemon's state in plain lines: the node that
    /// drives the mixer's graph, then each of the mixer's devices that follows
    /// it and whether it is resampled, microphones first, so an archive says
    /// at a glance when a microphone runs on another device's clock.
    /// </summary>
    internal static IReadOnlyList<string> ClockLines(string? state)
    {
        JsonNode? node = null;
        if (state is not null)
            try { node = JsonNode.Parse(state); }
            catch (JsonException) { }
        if (AudioClock.From(node?["mixer"]) is not { } clock)
            return ["audio clock: not reported (the software mixer off, no driver in the graph, or a daemon without the readout)"];
        string? priority = node?["mixer"]?["clock"]?["driver"]?["priorityDriver"]?.ToJsonString();
        List<string> lines = [$"audio clock (last read by the daemon): {clock.DriverName} drives the mixer's graph ({clock.Driver}, priority.driver {(priority is null or "null" ? "unset" : priority)})"];
        foreach (AudioClockFollower follower in clock.Followers.OrderByDescending(f => f.Microphone).ThenByDescending(f => f.Resampled))
        {
            string kind = follower.Microphone ? "microphone" : follower.Role switch
            {
                "capture" => "capture input", "unitInput" => "interface input", "unitOutput" => "interface output", _ => "output",
            };
            lines.Add($"{kind} {follower.Description} follows {clock.DriverName} ({(follower.Resampled ? "resampled" : "same clock")}), node {follower.Name}");
        }
        // A device on its own clock, and what its bridge measured, so a
        // report shows whether the bridge holds.
        JsonArray? channels = node?["mixer"]?["channels"] as JsonArray;
        foreach (AudioClockBridged bridged in clock.Bridged)
        {
            lines.Add($"capture input {bridged.Description} runs on its own clock, bridged into the mixer's graph"
                + (bridged.Separate ? "" : " (but it runs in the mixer's graph: something else links it there)") + $", node {bridged.Name}");
            foreach (string id in bridged.Channels)
                if (channels?.OfType<JsonObject>().FirstOrDefault(c => Text(c, "id") == id)?["ownClock"] is JsonObject own)
                    lines.Add($"  {id}: {OwnClockFigures(own)}");
        }
        return lines;
    }

    /// <summary>A bridge's figures in one line: running or waiting, delay, target, underruns, overruns and drift.</summary>
    internal static string OwnClockFigures(JsonObject own)
    {
        static string Number(JsonNode? node, string unit)
            => node is JsonValue value && value.TryGetValue(out double number)
                ? number.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + unit : "unknown";
        bool running = own["running"] is JsonValue r && r.TryGetValue(out bool on) && on;
        return $"{(running ? "running" : "waiting for the device")}, delay {Number(own["delayMs"], " ms")}, target {Number(own["targetMs"], " ms")}, "
            + $"{Number(own["underruns"], "")} underruns, {Number(own["overruns"], "")} overruns, drift {Number(own["driftPpm"], " ppm")}"
            + (Text(own, "error") is { } error ? $", error: {error}" : "");
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
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            valid = false;
            return JsonOmission("JSON could not be safely decoded.");
        }
    }

    private static string JsonOmission(string reason) => JsonSerializer.Serialize(new { unavailable = reason }, ReportJson);

    private static async Task<AudioGraphSummary?> WriteCmd(Func<string, string> clean, string dir, string file, string exe, string[] args)
    {
        var (output, errors, exitCode, complete) = await RunHelperAsync(exe, args);
        bool json = file.EndsWith(".json", StringComparison.Ordinal);
        AudioGraphSummary? graph = null;
        string saved;
        if (json)
        {
            var result = await Task.Run(() => AudioJsonForArchive(output, file == "pw-dump.json", clean));
            saved = result.Text;
            graph = result.Graph;
            if (!result.Valid) errors += $"\n[{exe} did not return complete valid JSON]";
        }
        else saved = clean(output);
        string separator = output.Length > 0 && errors.Length > 0 && !output.EndsWith('\n') ? "\n" : "";
        await File.WriteAllTextAsync(Path.Combine(dir, file), json ? saved : saved + separator + clean(errors));
        if (json && errors.Length > 0)
            await File.WriteAllTextAsync(Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + "-errors.txt"), clean(errors));
        return complete && exitCode == 0 ? graph : null;
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
        var (output, errors, _, _) = await RunHelperAsync(exe, args);
        return (output, errors);
    }

    /// <summary>
    /// <see cref="RunCommandAsync"/> with the helper's exit status as well:
    /// -1 when it could not be started or did not finish on its own.
    /// </summary>
    private static async Task<(string Output, string Errors, int ExitCode, bool Complete)> RunHelperAsync(string exe, string[] args)
    {
        try
        {
            // 15 s and 8 MiB per command; a helper that goes past either is
            // killed with its children and the archive says so.
            ProcessResult r = await ProcessRunner.RunAsync(exe, args, TimeSpan.FromSeconds(15),
                stdoutCap: 8 * 1024 * 1024, stderrCap: 64 * 1024);
            return (r.StdoutText, r.Stderr + CommandNotes(exe, r), r.TimedOut || r.Cancelled ? -1 : r.ExitCode,
                !r.TimedOut && !r.Cancelled && !r.Truncated && !r.Incomplete);
        }
        catch (Exception ex) { return ("", $"failed to run {exe}: {ex.Message}", -1, false); }
    }

    /// <summary>
    /// How much of one boot's journal a file in the archive keeps: the newest
    /// lines, up to both limits. A night of audio-server warnings fits; a
    /// journal flooded by a failing device is cut from the oldest end, and
    /// the file says so on its first line.
    /// </summary>
    internal const int JournalLineLimit = 20_000;
    internal const int JournalByteLimit = 2 * 1024 * 1024;

    /// <summary>
    /// Kernel messages worth having beside an audio report: USB devices,
    /// their controllers and hubs coming and going or being reset, sound
    /// drivers, and suspend and resume. The same pattern serves journalctl's
    /// PCRE2 --grep and the filter applied here when that is unavailable.
    /// </summary>
    internal const string KernelLogPattern = @"usb|xhci|snd|sound|alsa|hub|disconnect|reset|suspend|resume|\bpm:";

    /// <summary>
    /// The current boot's journal of the units <paramref name="selection"/>
    /// names, newest lines last, kept within <see cref="JournalLineLimit"/>
    /// and <see cref="JournalByteLimit"/>. A break in the night is in the
    /// file the next morning, which a window counted back from the moment of
    /// collection would miss.
    /// </summary>
    internal static async Task WriteJournalAsync(Func<string, string> clean, string dir, string file,
        string journalctl, string[] selection)
    {
        // One line more than is kept: when it arrives, something was left out.
        string[] args = [.. selection, "-b", "-o", "short-iso-precise", "--no-pager",
            "-n", (JournalLineLimit + 1).ToString(CultureInfo.InvariantCulture)];
        var (output, errors, _, _) = await RunHelperAsync(journalctl, args);
        string kept = KeepNewest(output, out string? cut);
        var text = new StringBuilder();
        if (cut is not null) text.Append(cut).Append('\n');
        text.Append(kept);
        if (kept.Length > 0 && errors.Length > 0 && !kept.EndsWith('\n')) text.Append('\n');
        text.Append(errors);
        await File.WriteAllTextAsync(Path.Combine(dir, file), clean(text.ToString()));
    }

    /// <summary>
    /// Kernel messages of the current boot that match <see cref="KernelLogPattern"/>,
    /// as kernel-log.txt. The system journal is read first. When this account
    /// may not read it, the kernel's own buffer is tried through
    /// <paramref name="dmesg"/>, which many distributions also restrict; the
    /// file then says which source answered, or that neither did and why.
    /// </summary>
    internal static async Task WriteKernelLogAsync(Func<string, string> clean, string dir, string journalctl, string dmesg)
    {
        var text = new StringBuilder();
        // No line limit here: journalctl lists a grep with --lines newest
        // first on some versions, and the filter already keeps this small.
        var (output, errors, status, _) = await RunHelperAsync(journalctl,
            ["-k", "-b", "-o", "short-iso-precise", "--no-pager", "--case-sensitive=false", "-g", KernelLogPattern]);
        string body;
        if (JournalRefused(output, errors))
        {
            text.Append("The system journal holds the kernel log, and this account may not read it: on most\n")
                .Append("distributions members of the systemd-journal, adm or wheel group can. `journalctl -k -b`\n")
                .Append("run as such a member, or with sudo, shows it. What journalctl said:\n")
                .Append(errors.TrimEnd()).Append("\n\n");
            var (kernel, dmesgErrors, dmesgStatus, _) = await RunHelperAsync(dmesg, ["--time-format=iso"]);
            if (dmesgStatus != 0 || !HasLogLines(kernel))
            {
                text.Append("The kernel's own buffer could not be read either (dmesg; kernel.dmesg_restrict is\n")
                    .Append("set on many distributions). What dmesg said:\n").Append(dmesgErrors.TrimEnd()).Append('\n');
                await File.WriteAllTextAsync(Path.Combine(dir, "kernel-log.txt"), clean(text.ToString()));
                return;
            }
            text.Append("Read from the kernel's own buffer instead (dmesg), which holds only its newest messages,\n")
                .Append("filtered here for USB, sound and power management lines.\n");
            body = FilterKernelLines(kernel);
            errors = dmesgErrors;
        }
        else if (status != 0 && !HasLogLines(output)
                 && (errors.Contains("pattern", StringComparison.OrdinalIgnoreCase) || errors.Contains("pcre", StringComparison.OrdinalIgnoreCase)))
        {
            // A journalctl built without pattern matching: take the newest
            // kernel lines and filter them here.
            var (all, allErrors, _, _) = await RunHelperAsync(journalctl,
                ["-k", "-b", "-o", "short-iso-precise", "--no-pager", "-n", (JournalLineLimit + 1).ToString(CultureInfo.InvariantCulture)]);
            text.Append($"This journalctl cannot filter by pattern, so the newest {JournalLineLimit} kernel lines were\n")
                .Append("filtered here for USB, sound and power management lines.\n");
            body = FilterKernelLines(all);
            errors = allErrors;
        }
        else
        {
            text.Append("Kernel messages of the current boot about USB, sound and power management (journalctl -k).\n");
            body = output;
        }
        string kept = KeepNewest(body, out string? cut);
        if (cut is not null) text.Append(cut).Append('\n');
        text.Append(kept);
        if (kept.Length > 0 && errors.Length > 0 && !kept.EndsWith('\n')) text.Append('\n');
        text.Append(errors);
        await File.WriteAllTextAsync(Path.Combine(dir, "kernel-log.txt"), clean(text.ToString()));
    }

    /// <summary>
    /// Whether journalctl answered with no entries because the account may
    /// not read the system journal, as opposed to having nothing to show.
    /// </summary>
    internal static bool JournalRefused(string output, string errors)
        => !HasLogLines(output)
           && (errors.Contains("not seeing messages", StringComparison.OrdinalIgnoreCase)
               || errors.Contains("insufficient permissions", StringComparison.OrdinalIgnoreCase)
               || errors.Contains("permission denied", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether any line is a log line rather than journalctl's own "-- No entries --" marker.</summary>
    private static bool HasLogLines(string output)
        => output.Split('\n').Any(line => line.Length > 0 && !line.StartsWith("-- ", StringComparison.Ordinal));

    private static readonly Regex KernelLogLine = new(KernelLogPattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static string FilterKernelLines(string text)
    {
        var kept = new StringBuilder();
        foreach (string line in text.Split('\n'))
            if (KernelLogLine.IsMatch(line)) kept.Append(line).Append('\n');
        return kept.ToString();
    }

    /// <summary>
    /// The newest whole lines of <paramref name="text"/> within
    /// <see cref="JournalLineLimit"/> and <see cref="JournalByteLimit"/>.
    /// <paramref name="cut"/> is the line that says what was left out, or
    /// null when nothing was.
    /// </summary>
    internal static string KeepNewest(string text, out string? cut)
    {
        cut = null;
        if (text.Length == 0) return text;
        string[] lines = text.EndsWith('\n') ? text[..^1].Split('\n') : text.Split('\n');
        int first = Math.Max(0, lines.Length - JournalLineLimit);
        long bytes = 0;
        int start = lines.Length;
        while (start > first)
        {
            long line = Encoding.UTF8.GetByteCount(lines[start - 1]) + 1;
            if (bytes + line > JournalByteLimit) break;
            bytes += line;
            start--;
        }
        if (start == 0) return text;
        cut = $"[cut: older lines were left out; this file keeps the newest {lines.Length - start} lines "
              + $"(at most {JournalLineLimit} lines and {JournalByteLimit} bytes)]";
        return string.Join('\n', lines[start..]) + "\n";
    }

    /// <summary>
    /// The sound cards and their open PCM devices as the kernel reports them
    /// under <paramref name="root"/>: the rate, period and buffer the audio
    /// server opened each with, its state and fill, and for USB interfaces
    /// the stream file with the frequency the device is actually running at.
    /// This replaces pw-top, whose figures come from PipeWire's profiler: the
    /// profiler sends a driver's report only when it fits in 16 KiB, and a
    /// driver with OpenXLR's hundred or so followers needs more, so every row
    /// came out as zero. Reading these files starts no process.
    /// </summary>
    internal static string AlsaSnapshot(string root = "/proc/asound")
    {
        const int MaxFileBytes = 64 * 1024;
        var text = new StringBuilder();
        text.AppendLine($"ALSA sound cards and their open PCM devices, read from {root}.");
        text.AppendLine("hw_params is what the audio server opened a device with; status is its state and fill now.");
        text.AppendLine("A USB card's stream file has the rate the device actually runs at (Momentary freq).");
        text.AppendLine("PipeWire's quantum and driver for each node are in pw-dump.json (node.driver-id, clock settings).");
        string Read(string path)
        {
            try { return RegularFile.ReadText(path, MaxFileBytes); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"unreadable: {ex.Message}\n"; }
        }
        void Section(string title, string body)
        {
            text.Append("\n== ").Append(title).Append(" ==\n").Append(body);
            if (!body.EndsWith('\n')) text.Append('\n');
        }
        static IEnumerable<string> Numbered(string dir, string pattern)
        {
            var match = new Regex(pattern, RegexOptions.CultureInvariant);
            IEnumerable<string> found;
            try { found = Directory.EnumerateDirectories(dir).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
            foreach (string path in found
                         .Where(p => match.IsMatch(Path.GetFileName(p)) && new DirectoryInfo(p).LinkTarget is null)
                         .OrderBy(p => int.Parse(Regex.Match(Path.GetFileName(p), "[0-9]+").Value, CultureInfo.InvariantCulture))
                         .ThenBy(p => p, StringComparer.Ordinal))
                yield return path;
        }

        if (!Directory.Exists(root))
        {
            text.AppendLine().AppendLine($"{root} does not exist: no ALSA sound cards, or not a Linux kernel.");
            return text.ToString();
        }
        Section("cards", Read(Path.Combine(root, "cards")));
        foreach (string card in Numbered(root, "^card[0-9]+$"))
        {
            string name = Path.GetFileName(card);
            foreach (string file in new[] { "id", "usbid" })
                if (File.Exists(Path.Combine(card, file))) Section($"{name}/{file}", Read(Path.Combine(card, file)));
            IEnumerable<string> streams;
            try { streams = Directory.EnumerateFiles(card, "stream*").Order(StringComparer.Ordinal).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { streams = []; }
            foreach (string stream in streams) Section($"{name}/{Path.GetFileName(stream)}", Read(stream));
            foreach (string pcm in Numbered(card, "^pcm[0-9]+[pc]$"))
                foreach (string sub in Numbered(pcm, "^sub[0-9]+$"))
                {
                    string where = $"{name}/{Path.GetFileName(pcm)}/{Path.GetFileName(sub)}";
                    string parameters = Read(Path.Combine(sub, "hw_params"));
                    // A closed device has nothing more to say.
                    if (parameters.Trim() == "closed") { Section(where, "closed\n"); continue; }
                    Section($"{where}/hw_params", parameters);
                    // The process that holds the device is an id, which the
                    // archive does not keep.
                    Section($"{where}/status", Regex.Replace(Read(Path.Combine(sub, "status")),
                        @"(?m)^(owner_pid\s*:\s*)[0-9]+", "${1}<redacted>"));
                }
        }
        return text.ToString();
    }

    private static string TryReadFile(string path)
    {
        try { return RegularFile.ReadText(path, 64 * 1024).ReplaceLineEndings(" | "); }
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
            if (serial is not null && IsDeviceSerial(serial)) yield return serial;
        }
    }

    /// <summary>
    /// Whether a USB serial names the device and is masked. A root hub's
    /// serial is its controller's PCI address ("0000:0e:00.0"), the same on
    /// every machine with that board and nothing personal; it is what tells
    /// which controller a kernel line is about, so it stays.
    /// </summary>
    internal static bool IsDeviceSerial(string? serial)
        => serial is { Length: >= 4 } && !PciAddress.IsMatch(serial);

    private static readonly Regex PciAddress = new(@"^[0-9a-fA-F]{4}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}\.[0-7]$", RegexOptions.CultureInvariant);

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
        string text = "";
        string? problem = null;
        try
        {
            if (new FileInfo(src).LinkTarget is not null)
                problem = "Symbolic links are not collected.";
            else if (!File.Exists(src)) return;
            else text = RegularFile.ReadText(src, maximum ?? OpenXlrPaths.MaxPreferenceBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { problem = ex.Message; }
        if (problem is not null)
            text = file.EndsWith(".json", StringComparison.Ordinal) ? JsonOmission(problem) : "File unavailable: " + problem;
        File.WriteAllText(Path.Combine(dest, file), clean(text));
    }
}
