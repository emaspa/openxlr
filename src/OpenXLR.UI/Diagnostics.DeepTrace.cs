using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenXLR.UI;

public static partial class Diagnostics
{
    private const int MaxProcessReads = 4096;
    private const int MaxProgramNames = 512;
    private const int MaxAudioProcesses = 32;

    private static readonly string[] SystemMemoryFields =
        ["MemTotal", "MemAvailable", "SwapTotal", "SwapFree", "Shmem"];

    internal sealed record ProgramCount(string Name, int Instances);
    internal sealed record ProcessMemory(string Name, char? State, int? Threads, Dictionary<string, long> MemoryKiB);
    internal sealed record ProcessContext(bool Available, bool Truncated, int Unreadable,
        Dictionary<string, long> SystemMemoryKiB, ProcessMemory? Window, ProcessMemory[] AudioProcesses,
        ProgramCount[] Programs);

    private sealed record InstalledBuild(string Package, string Manager, string Version, string? Commit, bool Dirty);
    private sealed record BuildHistory(InstalledBuild[] Builds, int Unreadable, bool Truncated, bool Available);

    private static Task<string> CollectDeepTraceAsync(DaemonClient client)
    {
        DateTimeOffset collectedAt = DateTimeOffset.UtcNow;
        var daemon = client.RequestRuntimeDiagnosticsAsync(TimeSpan.FromSeconds(5));
        return Task.Run(async () =>
        {
            RuntimeSnapshot window = RuntimeSnapshot.Capture();
            ProcessContext? processes = OperatingSystem.IsLinux()
                ? ReadProcessContext("/proc", UnixPeerCredentials.User, Environment.ProcessId) : null;
            DaemonPrefs saved = DaemonPrefs.Load(out string? problem);
            var installedBuilds = ReadInstalledBuilds();
            var installationHistory = ReadBuildHistory();
            return JsonSerializer.Serialize(new
            {
                collectedAt,
                processView = FlatpakEnvironment.IsFlatpak ? "sandbox" : "local",
                logicalProcessors = Environment.ProcessorCount,
                processes,
                windowRuntime = window,
                daemonRuntime = await daemon.ConfigureAwait(false),
                savedDaemonPreferences = new { readError = problem is not null, saved.Submixer },
                installedBuilds,
                installationHistory,
            }, ReportJson);
        });
    }

    internal static ProcessContext ReadProcessContext(string proc, uint user, int windowPid)
    {
        var programs = new Dictionary<string, int>(StringComparer.Ordinal);
        var audio = new List<ProcessMemory>();
        var system = RuntimeSnapshot.ReadKiB(RuntimeSnapshot.ReadProcFields(Path.Combine(proc, "meminfo")), SystemMemoryFields);
        string windowId = windowPid.ToString(CultureInfo.InvariantCulture);
        var windowStatus = RuntimeSnapshot.ReadProcFields(Path.Combine(proc, windowId, "status"));
        var window = OwnedProcessName(windowStatus, user) is { } name ? ReadProcessMemory(windowStatus, name) : null;
        bool available = false, truncated = false;
        int reads = 0, unreadable = 0;
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(proc))
            {
                string id = Path.GetFileName(dir);
                if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0) continue;
                available = true;
                if (++reads > MaxProcessReads) { truncated = true; break; }
                var status = pid == windowPid ? windowStatus : RuntimeSnapshot.ReadProcFields(Path.Combine(dir, "status"));
                if (status.Count == 0) { unreadable++; continue; }
                if (OwnedProcessName(status, user) is not { } program) continue;
                if (programs.TryGetValue(program, out int count)) programs[program] = count + 1;
                else if (programs.Count < MaxProgramNames) programs.Add(program, 1);
                else truncated = true;

                if (pid == windowPid || !IsAudioProcess(program)) continue;
                if (audio.Count < MaxAudioProcesses) audio.Add(ReadProcessMemory(status, program));
                else truncated = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (available) truncated = true;
        }
        return new(available, truncated, unreadable, system, window, [.. audio.OrderBy(p => p.Name, StringComparer.Ordinal)],
            [.. programs.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new ProgramCount(p.Key, p.Value))]);
    }

    // Linux truncates the native host's executable name to this task name.
    private static bool IsAudioProcess(string name) => name is "OpenXLR.Daemon" or "openxlr-daemon"
        or "openxlr-lv2-hos" or "pipewire" or "pipewire-pulse" or "wireplumber" or "pulseaudio";

    private static string? OwnedProcessName(Dictionary<string, string> status, uint user)
    {
        if (!status.TryGetValue("Uid", out string? uids) || !status.TryGetValue("Name", out string? name)
            || name.Length is 0 or > 128 || name.Any(c => c is '/' or '\\' || char.IsControl(c))) return null;
        string[] ids = uids.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return ids.Length == 4
            && uint.TryParse(ids[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint real) && real == user
            && uint.TryParse(ids[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint effective) && effective == user
            ? name : null;
    }

    private static ProcessMemory ReadProcessMemory(Dictionary<string, string> status, string name)
    {
        string? state = status.GetValueOrDefault("State");
        int? threads = int.TryParse(status.GetValueOrDefault("Threads"), NumberStyles.None, CultureInfo.InvariantCulture,
            out int count) ? count : null;
        return new(name, state is { Length: > 0 } && char.IsAsciiLetter(state[0]) ? state[0] : null, threads,
            RuntimeSnapshot.ReadKiB(status, RuntimeSnapshot.StatusMemoryFields));
    }

    private static BuildHistory ReadBuildHistory()
    {
        const long budget = 16 * 1024 * 1024;
        var builds = new HashSet<InstalledBuild>();
        bool truncated = false;
        int unreadable = 0;
        long bytes = 0;
        try
        {
            foreach (string path in InstallationHistory.ArchivedReceipts())
            {
                try
                {
                    long size = new FileInfo(path).Length;
                    if (size > InstallationReceipt.MaxBytes) { unreadable++; continue; }
                    if ((bytes += size) > budget) { truncated = true; break; }
                    builds.Add(ReadBuild(path, archived: true));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { unreadable++; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new([], 0, false, false); }
        return new(OrderBuilds(builds), unreadable, truncated, true);
    }

    private static InstalledBuild[] ReadInstalledBuilds()
    {
        var builds = new HashSet<InstalledBuild>();
        foreach (string path in InstallationHistory.PackageReceipts(AppContext.BaseDirectory).Distinct(StringComparer.Ordinal).Take(8))
        {
            try { builds.Add(ReadBuild(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { /* A missing or malformed receipt cannot identify an installed build. */ }
        }
        return OrderBuilds(builds);
    }

    private static InstalledBuild ReadBuild(string path, bool archived = false)
    {
        InstallationReceipt receipt = InstallationReceipt.Read(path, archived: archived);
        return new(receipt.Package, receipt.Manager, receipt.Version, receipt.Commit, receipt.Dirty);
    }

    private static InstalledBuild[] OrderBuilds(HashSet<InstalledBuild> builds)
        => [.. builds.OrderBy(b => b.Package, StringComparer.Ordinal).ThenBy(b => b.Version, StringComparer.Ordinal)
            .ThenBy(b => b.Manager, StringComparer.Ordinal).ThenBy(b => b.Commit, StringComparer.Ordinal).ThenBy(b => b.Dirty)];
}
