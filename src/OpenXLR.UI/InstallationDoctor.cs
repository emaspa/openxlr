using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

internal sealed record DoctorFinding(string Path, string Problem, string Repair);
internal sealed record DoctorReceipt(string Path, InstallationReceipt Installation, bool Archived = false);
internal sealed record DoctorRuntime(string Status, string? InstalledBuild = null, string? RunningBuild = null);
internal sealed record DoctorNote(string Path, string Information);
internal sealed record DoctorReport(IReadOnlyList<DoctorReceipt> Receipts, IReadOnlyList<DoctorFinding> Findings, DoctorRuntime? Daemon, IReadOnlyList<DoctorNote> Notes);

/// <summary>Read-only installation checks. This path never initializes Avalonia or starts the daemon.</summary>
internal static class InstallationDoctor
{
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    internal static string Repair(InstallationReceipt receipt) => receipt.Manager switch
    {
        "manual" => "Rebuild the companion and run sudo python3 packaging/yabridge/build.py stage --source /path/to/yabridge-source --output /path/to/yabridge-build --destdir / --prefix /usr/local --manager manual from its source checkout.",
        "debian" => "sudo apt-get install --reinstall " + Quote(receipt.Package),
        "rpm" => "sudo dnf reinstall " + Quote(receipt.Package),
        "arch" => "paru -S --rebuild " + Quote(receipt.Package),
        "nix" => "sudo nix-store --repair-path " + Quote(receipt.Prefix),
        "user" => "openxlr installation repair-startup",
        _ => throw new ArgumentException("Unsupported receipt package manager."),
    };

    internal static IEnumerable<string> DefaultReceipts(string baseDirectory)
    {
        foreach (string path in InstallationHistory.PackageReceipts(baseDirectory)) yield return path;
        if (File.Exists(StartupReceipt.Path) || new FileInfo(StartupReceipt.Path).LinkTarget is not null) yield return StartupReceipt.Path;
        foreach (string path in InstallationHistory.ArchivedReceipts()) yield return path;
    }

    internal static async Task<DoctorReport> Inspect(IEnumerable<string> paths, string? root, bool runtime,
        Func<CancellationToken, Task<string>>? readState = null, bool removed = false, IEnumerable<string>? prefixes = null,
        Func<CancellationToken, Task<DaemonPresence>>? probe = null,
        Func<CancellationToken, Task<string>>? readPluginDiagnostics = null)
    {
        // A supplied state reader stands in for this user's daemon, so only
        // the real one is probed by default.
        probe ??= readState is null ? token => DaemonProbe.ProbeAsync(cancel: token) : null;
        readPluginDiagnostics ??= readState is null ? ReadPluginDiagnostics : null;
        // A leftover gets a move command only under a prefix this build looks
        // in for packages. The content hash in an archive's name proves its
        // integrity, not who wrote it, so any other path is left to inspection.
        string[] movable = [.. (prefixes ?? InstallationHistory.PackagePrefixes(AppContext.BaseDirectory)).Select(p => p.TrimEnd('/') + '/')];
        var receipts = new List<DoctorReceipt>();
        var findings = new List<DoctorFinding>();
        long totalBytes = 0;
        bool currentInventoryComplete = true;
        foreach (string path in paths.Distinct(StringComparer.Ordinal))
        {
            bool archived = removed || root is null && System.IO.Path.GetDirectoryName(path) == InstallationHistory.DirectoryPath;
            try
            {
                if (root is not null && (!path.StartsWith(root.TrimEnd('/') + '/', StringComparison.Ordinal) ||
                    InstallationReceipt.HasLinkedParent(path, root)))
                    throw new IOException("The staged receipt is outside its root or has a symbolic link parent.");
                if ((totalBytes += new FileInfo(path).Length) > 16 * 1024 * 1024)
                {
                    currentInventoryComplete = false;
                    findings.Add(new(path, "combined receipt inventory exceeds 16 MiB", "Check older receipts separately with openxlr doctor --receipt PATH --no-runtime."));
                    break;
                }

                // A receipt reached through a profile link can point at the same
                // immutable Nix output as one reached through its store path.
                InstallationReceipt receipt = InstallationReceipt.Read(path, root, archived);
                if (!archived && receipts.Any(r => !r.Archived && r.Installation.Prefix == receipt.Prefix && r.Installation.Package == receipt.Package)) continue;
                receipts.Add(new(path, receipt, archived));
                if (archived) continue;
                foreach (InstalledFile file in receipt.Files)
                    if (InstallationReceipt.Problem(file, receipt.Prefix, root) is { } problem)
                        findings.Add(new(file.Path, problem, Repair(receipt)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (!archived) currentInventoryComplete = false;
                findings.Add(new(path, ex.Message + (!archived ? " Historical leftover checks were skipped." : ""), archived ?
                    "mv -T -i -- " + Quote(path) + " " + Quote(path + ".invalid-receipt") :
                    path == StartupReceipt.Path ? "openxlr installation reset-startup-receipt" :
                    "Restore this receipt by reinstalling its package. Do not remove files using an unreadable receipt."));
            }
        }
        HashSet<string> current = [.. receipts.Where(r => !r.Archived).SelectMany(r => r.Installation.Files).Select(f => f.Path)];
        foreach (DoctorReceipt history in receipts.Where(r => currentInventoryComplete && r.Archived && r.Installation.Manager != "nix"))
        {
            foreach (InstalledFile file in history.Installation.Files.Where(f => f.Kind != "receipt" && !current.Contains(f.Path)))
            {
                if (!File.Exists(file.Path) && new FileInfo(file.Path).LinkTarget is null && !Directory.Exists(file.Path)) continue;
                if (findings.Any(f => f.Path == file.Path)) continue;
                string? problem = InstallationReceipt.Problem(file, history.Installation.Prefix);
                bool move = problem is null && movable.Any(p => file.Path.StartsWith(p, StringComparison.Ordinal));
                string repair = move ? "sudo mv -T -i -- " + Quote(file.Path) + " " + Quote(file.Path + ".openxlr-leftover") : "namei -l -- " + Quote(file.Path);
                string detail = problem is not null ? ": " + problem + "; inspect before moving it" : move ? "" : "; outside the package prefixes, inspect before moving it";
                findings.Add(new(file.Path, "previously owned path remains outside the current inventory" + detail, repair));
            }
        }
        if (!removed && !receipts.Any(r => !r.Archived && r.Installation.Manager != "user"))
            findings.Add(new("installation", "no OpenXLR package receipt; this may be a source build or an older package", "Install an OpenXLR package that includes a receipt, then run openxlr doctor again."));

        DoctorRuntime? daemon = null;
        if (runtime)
        {
            var installed = receipts.FirstOrDefault(r => !r.Archived && r.Installation.Package == "openxlr");
            string? assembly = installed?.Installation.Files.FirstOrDefault(f => f.Kind == "file" && System.IO.Path.GetFileName(f.Path) == "OpenXLR.Daemon.dll")?.Path;
            if (assembly is null)
            {
                daemon = new("installed daemon build cannot be identified");
                findings.Add(new("daemon", daemon.Status, installed is null ? "Install a package with a receipt, then run openxlr doctor." : Repair(installed.Installation)));
            }
            else
            {
                try
                {
                    string id = ModuleId(assembly);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    string json = await (readState ?? ReadState)(timeout.Token);
                    using var state = JsonDocument.Parse(json);
                    string? running = state.RootElement.TryGetProperty("daemonBuildId", out var build) && build.ValueKind == JsonValueKind.String ? build.GetString() : null;
                    bool matches = Guid.TryParse(running, out var parsed) && parsed.ToString("D") == id;
                    daemon = new(matches ? "matches installed build" : running is null ? "running daemon predates build identification" : "running daemon differs from installed build", id, running);
                    if (!matches) findings.Add(new("daemon", daemon.Status, "systemctl --user restart openxlr-daemon.service"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or JsonException or BadImageFormatException or InvalidOperationException or FormatException)
                {
                    DaemonPresence presence = ex is HttpRequestException or IOException or OperationCanceledException && probe is not null
                        ? await Probe(probe) : DaemonPresence.Reachable;
                    daemon = new(presence switch
                    {
                        DaemonPresence.OlderDaemon => "a daemon from before the update is still running and does not serve the control socket",
                        DaemonPresence.NotRunning => "not running",
                        DaemonPresence.OtherUser => "the control socket belongs to another user",
                        _ => "not verified: " + ex.Message,
                    });
                    findings.Add(new("daemon", daemon.Status, presence switch
                    {
                        DaemonPresence.NotRunning => "systemctl --user start openxlr-daemon.service",
                        DaemonPresence.OtherUser => "namei -l -- " + Quote(OpenXlrPaths.ControlSocketPath),
                        _ => "systemctl --user restart openxlr-daemon.service",
                    }));
                }
            }
        }
        // Windows plugins are a question for the running daemon, whatever
        // installed it: a source build is the only one with the host today.
        IReadOnlyList<DoctorNote> notes = [];
        if (runtime && readPluginDiagnostics is not null)
        {
            try
            {
                // The daemon asks yabridgectl for its status while it answers,
                // which it gives five seconds.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var reply = JsonDocument.Parse(await readPluginDiagnostics(timeout.Token));
                notes = WindowsPluginNotes(reply.RootElement);
            }
            // A daemon that is not there is already a finding above, or the
            // receipts could not say which daemon to expect; either way there
            // is no catalogue to explain.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) { }
        }
        return new(receipts, findings, daemon, notes);
    }

    /// <summary>The most notes the Windows plugin checks add, so a folder of a thousand plugins stays readable.</summary>
    internal const int WindowsPluginNoteLimit = 32;

    /// <summary>
    /// What helps a user whose Windows plugin is not in the picker, from the
    /// daemon's own plugin diagnostics: where OpenXLR's Windows plugin host
    /// looks, which of those folders the daemon cannot see, and each bundle
    /// the last scan of them found and left out, with the reason. Before
    /// those, what removing the yabridge companion would cost: the inserts
    /// whose plugin only a yabridge wrapper lists, and the yabridge folders
    /// the daemon could not copy into the Windows plugin folders. A build
    /// without the host, whose Windows plugins come through yabridge alone,
    /// gets none of these. Either build is told when Wine is missing and an
    /// insert has a Windows plugin. Information only: none of it is a fault
    /// of the installation.
    /// </summary>
    internal static IReadOnlyList<DoctorNote> WindowsPluginNotes(JsonElement reply)
    {
        var notes = new List<DoctorNote>();
        JsonElement discovery = default;
        if (reply.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
            foreach (JsonElement message in messages.EnumerateArray())
                if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("discovery", out JsonElement found)
                    && found.ValueKind == JsonValueKind.Object)
                    discovery = found;
        if (discovery.ValueKind != JsonValueKind.Object) return notes;
        // Either host starts Wine, so a missing one is said with or without
        // OpenXLR's Windows plugin host.
        string[] needWine = [.. Items(discovery, "windowsInsertsNeedingWine").Select(InsertName)];
        if (needWine.Length > 0)
            notes.Add(new("wine", $"not installed, and {Count(needWine.Length, "insert uses", "inserts use")} a Windows plugin, which cannot start without it: "
                + string.Join(", ", needWine) + ". Install Wine with your distribution's package manager; the package is called wine"));
        if (!discovery.TryGetProperty("windowsHostInstalled", out JsonElement installed) || installed.ValueKind != JsonValueKind.True)
            return notes;
        // What would go with the yabridge companion: each insert whose plugin
        // only a wrapper lists, and each yabridge folder the one-time copy
        // could not add to the Windows plugin folders.
        foreach (JsonElement insert in Items(discovery, "bridgeOnlyInserts"))
        {
            if (Text(insert, "wrapper") is not { } wrapper) continue;
            string which = $"the plugin of the insert {InsertName(insert)} is in the plugin list only through this yabridge wrapper";
            notes.Add(new(wrapper, Text(insert, "reason") switch
            {
                "not-64-bit" => which + ". It is a 32-bit Windows plugin, which only yabridge runs, so the insert loses its plugin when yabridge is removed; its 64-bit build runs on OpenXLR's Windows plugin host",
                "module-missing" => which + ", and the Windows plugin behind the wrapper is missing. Install the plugin again",
                _ => which + $". Its Windows module, {Text(insert, "module") ?? "unknown"}, is in no folder OpenXLR's Windows plugin host searches, so the insert loses its plugin when the openxlr-yabridge companion is removed. Add the module's folder under Windows in the plugin manager, or install the plugin again with Install file",
            }));
        }
        if (discovery.TryGetProperty("yabridgeFolderCopy", out JsonElement copy))
            foreach (JsonElement folder in Items(copy, "notAdded"))
                if (Text(folder, "path") is { } path)
                    notes.Add(new(path, "not added to the Windows plugin folders when OpenXLR copied the yabridge companion's folders: "
                        + (Text(folder, "reason") ?? "no reason was recorded")
                        + " Plugins there reach the plugin list only through their yabridge wrappers; add the folder under Windows in the plugin manager once that is dealt with"));
        if (discovery.TryGetProperty("windowsHostDirectories", out JsonElement folders) && folders.ValueKind == JsonValueKind.Array)
            foreach (JsonElement folder in folders.EnumerateArray())
            {
                if (Text(folder, "path") is not { } path) continue;
                bool exists = folder.TryGetProperty("exists", out JsonElement there) && there.ValueKind == JsonValueKind.True;
                notes.Add(new(path, exists
                    ? "searched for Windows VST3 and CLAP plugins that run on OpenXLR's Windows plugin host"
                    : "searched for Windows plugins, but the daemon cannot see this folder. It looks in the Wine prefix it was started with (WINEPREFIX, else ~/.wine); add the folder your plugins are in under Windows in the plugin manager"));
            }
        if (discovery.TryGetProperty("scans", out JsonElement scans) && scans.ValueKind == JsonValueKind.Array)
            foreach (JsonElement scan in scans.EnumerateArray())
            {
                if (Text(scan, "kind") is not { } kind || !kind.StartsWith("windows-", StringComparison.Ordinal)
                    || !scan.TryGetProperty("entries", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement entry in entries.EnumerateArray())
                    if (Text(entry, "path") is { } path && LeftOut(entry) is { } reason)
                        notes.Add(new(path, reason));
            }
        return notes.Count <= WindowsPluginNoteLimit ? notes
            : [.. notes.Take(WindowsPluginNoteLimit - 1), new("windows plugins", $"{notes.Count - WindowsPluginNoteLimit + 1} more notes; openxlr doctor --json and the diagnostics archive have every scan entry")];
    }

    /// <summary>Why a bundle the Windows plugin folders' scan found is not in the catalogue as its own entry, or null when it is.</summary>
    private static string? LeftOut(JsonElement entry)
    {
        string? outcome = Text(entry, "outcome");
        int plugins = Whole(entry, "plugins"), duplicates = Whole(entry, "duplicates");
        string detail = Text(entry, "detail") is { Length: > 0 } said ? " " + said.Split('\n')[0] : "";
        return outcome switch
        {
            "ok" when plugins > 0 && duplicates == plugins =>
                "listed through its yabridge wrapper, which runs on either host; the plugin's controls window moves it to OpenXLR's Windows plugin host",
            "ok" or "directory" or "directory-missing" or "no-plugins" => null,
            "windows-not-64-bit" => "not listed: OpenXLR's Windows plugin host runs only 64-bit Windows plugins." + detail,
            "skipped-failed" => "not listed: an earlier scan failed and it is not read again until it changes; press Rescan all plugins in the plugin manager",
            "timeout" => "not listed: the scan did not finish in time; press Rescan all plugins in the plugin manager",
            _ => $"not listed: the scan ended with {outcome ?? "no outcome"}." + detail,
        };
    }

    /// <summary>The items of an array property, none when it is absent or not an array.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object) : [];

    /// <summary>An insert as a note names it: its chain, then its plugin.</summary>
    private static string InsertName(JsonElement insert)
        => $"{Text(insert, "chainName") ?? Text(insert, "chain") ?? "?"} {Text(insert, "name") ?? Text(insert, "plugin") ?? Text(insert, "id") ?? "?"}";

    private static string Count(int count, string one, string many) => count == 1 ? "1 " + one : $"{count} {many}";

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Whole(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;

    private static Task<string> ReadPluginDiagnostics(CancellationToken cancel) => Read("/api/v1/plugin-diagnostics", cancel);

    private static async Task<DaemonPresence> Probe(Func<CancellationToken, Task<DaemonPresence>> probe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { return await probe(timeout.Token); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException) { return DaemonPresence.Reachable; }
    }

    internal static string ModuleId(string assembly)
    {
        RegularFile.Require(assembly);
        using var stream = File.OpenRead(assembly);
        if (stream.Length > 128 * 1024 * 1024) throw new IOException("Daemon assembly exceeds 128 MiB.");
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D");
    }

    private static Task<string> ReadState(CancellationToken cancel) => Read("/api/v1/state", cancel);

    /// <summary>One read of the daemon's HTTP API over its control socket, at most 8 MiB.</summary>
    private static async Task<string> Read(string path, CancellationToken cancel)
    {
        using var handler = ControlConnection.HttpHandler();
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost" + path);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await body.ReadAsync(buffer, cancel)) != 0)
        {
            if (bytes.Length + count > 8 * 1024 * 1024) throw new IOException("the daemon's answer exceeds 8 MiB");
            bytes.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}

internal static class InstallationCommands
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("doctor" or "installation")) return false;
        Environment.ExitCode = Run(args, Console.Out).GetAwaiter().GetResult();
        return true;
    }

    internal static async Task<int> Run(string[] args, TextWriter output)
    {
        try
        {
            if (args.SequenceEqual(new[] { "installation", "remove-startup" }))
            {
                InstallationReceipt receipt = StartupReceipt.PrepareRemoval();
                foreach (string path in StartupReceipt.StartupPaths)
                    if (StartupReceipt.RemovalProblem(receipt, path) is { } problem) throw new IOException(problem);
                // Removal is exact and idempotent, with the same ownership checks
                // as Options. Keep the receipt and originals for later diagnosis.
                foreach (InstalledFile file in receipt.Files.Where(f => !f.Removed && StartupReceipt.Allowed(f.Path))) StartupReceipt.Remove(file.Path);
                output.WriteLine("Recorded startup files removed; original user files restored where recorded.");
                output.WriteLine("Run: systemctl --user daemon-reload");
                return 0;
            }
            if (args.SequenceEqual(new[] { "installation", "reset-startup-receipt" }))
            {
                output.WriteLine("Original receipt preserved at " + StartupReceipt.Recover());
                output.WriteLine("Run openxlr installation repair-startup to reapply enabled preferences.");
                return 0;
            }
            if (args.SequenceEqual(new[] { "installation", "repair-startup" }))
            {
                UiSettings settings = UiSettings.Load();
                if (!settings.StartDaemonAtLogin && !settings.OpenWindowAtLogin)
                {
                    output.WriteLine("No startup preferences are enabled; no files were changed.");
                    return 0;
                }
                bool daemon = !settings.StartDaemonAtLogin || StartupIntegration.SetDaemonAtLogin(true);
                bool window = !settings.OpenWindowAtLogin || StartupIntegration.SetWindowAtLogin(true);
                output.WriteLine(daemon && window ? "Enabled startup preferences reapplied; replaced files are backed up in the receipt." : "Startup repair failed. Run openxlr doctor and inspect foreign or linked startup paths.");
                return daemon && window ? 0 : 1;
            }
            if (args.SequenceEqual(new[] { "installation", "remember" }))
            {
                string[] receipts = [.. InstallationHistory.PackageReceipts(AppContext.BaseDirectory)];
                foreach (string receipt in receipts) InstallationHistory.Remember(receipt);
                output.WriteLine(receipts.Length > 0 ? "Installed package receipts saved for upgrade and removal checks." : "No installed package receipts were found; ownership is unknown.");
                return receipts.Length > 0 ? 0 : 1;
            }
            bool json = false, runtime = true, removed = false;
            string? root = null;
            var paths = new List<string>();
            if (args.Length == 0 || args[0] != "doctor") return Usage(output);
            for (int i = 1; i < args.Length; ++i)
            {
                if (args[i] == "--json") json = true;
                else if (args[i] == "--no-runtime") runtime = false;
                else if (args[i] == "--removed") removed = true;
                else if (args[i] == "--receipt" && i + 1 < args.Length && paths.Count < 32) paths.Add(System.IO.Path.GetFullPath(args[++i]));
                else if (args[i] == "--root" && i + 1 < args.Length) root = System.IO.Path.GetFullPath(args[++i]).TrimEnd('/');
                else return Usage(output);
            }
            if (root is not null)
            {
                if (root.Length == 0 || paths.Count == 0 || !Directory.Exists(root) || paths.Any(p => !p.StartsWith(root + '/', StringComparison.Ordinal))) return Usage(output);
                runtime = false;
            }
            if (removed)
            {
                if (paths.Count == 0 || root is not null) return Usage(output);
                runtime = false;
            }
            bool defaults = paths.Count == 0;
            if (defaults) paths.AddRange(InstallationDoctor.DefaultReceipts(AppContext.BaseDirectory));
            DoctorReport report = await InstallationDoctor.Inspect(paths, root, runtime, removed: removed);
            if (defaults)
            {
                HashSet<string> owned = [.. report.Receipts.Where(r => !r.Archived && r.Installation.Manager == "user")
                    .SelectMany(r => r.Installation.Files).Where(f => !f.Removed).Select(f => f.Path)];
                var findings = new List<DoctorFinding>();
                var notes = new List<DoctorNote>();
                foreach (string p in StartupReceipt.StartupPaths.Where(p => !owned.Contains(p) &&
                    (File.Exists(p) || Directory.Exists(p) || new FileInfo(p).LinkTarget is not null)))
                {
                    // The same read-only recognition the toggles apply: a file a
                    // release of this window generated, or the enable link
                    // systemctl leaves, is adopted later rather than moved aside.
                    if (Recognized(p)) notes.Add(new(p, "recognized startup path not yet recorded in a receipt; it is adopted by the next startup change in Options or by openxlr installation repair-startup"));
                    else findings.Add(new(p, "startup path is not owned by a receipt; it will not be removed",
                        "mv -T -i -- " + InstallationDoctor.Quote(p) + " " + InstallationDoctor.Quote(p + ".before-openxlr")));
                }
                report = report with { Findings = [.. report.Findings, .. findings], Notes = [.. report.Notes, .. notes] };
            }
            if (json) output.WriteLine(JsonSerializer.Serialize(report, InstallationReceipt.Json));
            else
            {
                foreach (DoctorReceipt receipt in report.Receipts)
                {
                    output.WriteLine($"{receipt.Installation.Package} {receipt.Installation.Version}, commit {receipt.Installation.Commit ?? "unknown"}: {receipt.Installation.Files.Count} recorded paths ({receipt.Path})");
                    if (receipt.Installation.Dirty) output.WriteLine("  Information: built from a modified source tree.");
                    if (receipt.Installation.Commit is null) output.WriteLine("  Information: source commit is unknown.");
                }
                foreach (DoctorNote note in report.Notes)
                    output.WriteLine($"{note.Path}: Information: {note.Information}");
                foreach (DoctorFinding finding in report.Findings)
                    output.WriteLine($"{finding.Path}: {finding.Problem}\n  Repair: {finding.Repair}");
                if (report.Daemon is { } daemon) output.WriteLine("Daemon: " + daemon.Status);
                if (report.Findings.Count == 0) output.WriteLine(removed ? "Removal checked; no previously owned paths remain." : "All recorded paths match. Unrecorded files were not inspected.");
            }
            return report.Findings.Count == 0 ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { output.WriteLine("OpenXLR: " + ex.Message); return 2; }
    }

    private static bool Recognized(string path)
    {
        try { return StartupReceipt.Recognized(path, StartupIntegration.PackagedUnit); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static int Usage(TextWriter output)
    {
        output.WriteLine("Usage: openxlr doctor [--json] [--no-runtime] [--receipt PATH] [--root STAGING | --removed]\n       openxlr installation {remember|repair-startup|remove-startup|reset-startup-receipt}");
        return 2;
    }
}
