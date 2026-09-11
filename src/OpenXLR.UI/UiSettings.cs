using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenXLR.UI;

/// <summary>
/// UI-side preferences (startup behaviour, tray), stored in
/// ~/.config/openxlr/ui.json. The mixer's own state lives in the daemon's
/// mixer.json; this file only holds what the window process needs.
/// </summary>
public sealed record UiSettings
{
    public bool StartDaemonAtLogin { get; init; }
    public bool OpenWindowAtLogin { get; init; }
    public bool MinimizeToTray { get; init; }
    public bool StartMinimized { get; init; }
    /// <summary>Opt-in only. False means the UI performs no startup network request.</summary>
    public bool CheckForUpdates { get; init; }
    /// <summary>Successful or failed automatic checks are limited to once per day.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; init; }
    /// <summary>Release tag whose banner the user dismissed.</summary>
    public string? DismissedUpdate { get; init; }
    /// <summary>
    /// The launch path the autostart entry was last written for. The startup
    /// repair recreates a missing entry only when this differs from the path
    /// the running copy resolves to, so an entry removed with a desktop tool
    /// stays removed while a moved installation is still fixed.
    /// </summary>
    public string? AutostartExecutable { get; init; }
    /// <summary>Names of the main window's tiles the user collapsed (INPUTS, HEADPHONES, ...).</summary>
    public IReadOnlyList<string> CollapsedSections { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ConfigDir => OpenXlrPaths.ConfigDir;

    private static string FilePath => Path.Combine(ConfigDir, "ui.json");

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), Json) ?? new UiSettings();
        }
        catch (Exception) { /* corrupt file must not stop the app */ }
        return new UiSettings();
    }

    public void Save()
    {
        try { OpenXlrPaths.WriteAtomicJson(FilePath, this, Json); }
        catch (Exception) { /* best effort */ }
    }
}

/// <summary>
/// The daemon's own preference file (~/.config/openxlr/daemon.json), mirrored
/// here so the window can write it without referencing the daemon's code.
/// Keep the shape in step with OpenXLR.Core.DaemonSettings. A null Submixer
/// means "not chosen": the daemon falls back to its unit's environment.
/// </summary>
public sealed record DaemonPrefs
{
    public bool? Submixer { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string FilePath => Path.Combine(UiSettings.ConfigDir, "daemon.json");

    public static DaemonPrefs Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<DaemonPrefs>(File.ReadAllText(FilePath), Json) ?? new DaemonPrefs();
        }
        catch (Exception) { /* corrupt file: behave as unset */ }
        return new DaemonPrefs();
    }

    public void Save() => OpenXlrPaths.WriteAtomicJson(FilePath, this, Json);
}

/// <summary>
/// Applies startup preferences to the system: a systemd user unit for the
/// daemon, an XDG autostart entry for the window.
///
/// Packaged installs (AUR, .deb, Nix) ship their own daemon unit in a system
/// unit directory. The UI must enable that one rather than write a copy into
/// ~/.config/systemd/user: that directory has the highest precedence, so a
/// copy there shadows the packaged unit, and its ExecStart goes stale as soon
/// as the package layout differs from the build tree. Only source builds,
/// which have no packaged unit, get one written here.
/// </summary>
public static class StartupIntegration
{
    private const string UnitName = "openxlr-daemon.service";

    private static string HomeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ConfigHome => OpenXlrPaths.ConfigHome;

    /// <summary>System unit directories, highest precedence first (systemd.unit(5)).</summary>
    private static readonly string[] SystemUnitDirs =
    [
        "/etc/systemd/user",
        "/run/systemd/user",
        "/usr/local/lib/systemd/user",
        "/usr/lib/systemd/user",
        "/lib/systemd/user",
    ];

    /// <summary>
    /// The daemon to run for installs without a packaged unit. The installed
    /// wrapper first (Nix: <prefix>/lib/openxlr next to <prefix>/bin; the
    /// distro packages: <prefix>/lib/openxlr/ui under <prefix>/bin), then an
    /// unpacked <prefix>/lib/openxlr/{ui,daemon} layout, then the source
    /// tree. Null when none exists, so no unit is ever written with a bad
    /// ExecStart.
    /// </summary>
    private static string? DaemonBinary => FirstExisting(
        Path.Combine("..", "..", "bin", "openxlr-daemon"),
        Path.Combine("..", "..", "..", "bin", "openxlr-daemon"),
        Path.Combine("..", "daemon", "OpenXLR.Daemon"),
        Path.Combine("..", "..", "..", "..", "OpenXLR.Daemon", "bin", "Release", "net10.0", "OpenXLR.Daemon"));

    /// <summary>The window for the autostart entry: the installed wrapper, else this binary.</summary>
    private static string UiBinary => ResolveUiBinary(AppContext.BaseDirectory);

    internal static string ResolveUiBinary(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        var package = directory.Name == "ui" ? directory.Parent : directory;
        // Only packaged lib/openxlr[/ui] layouts have a sibling bin wrapper.
        // Walking upward from an arbitrary build can select another install.
        if (package?.Name == "openxlr" && package.Parent?.Name == "lib"
            && package.Parent.Parent is { } prefix)
        {
            string wrapper = Path.Combine(prefix.FullName, "bin", "openxlr");
            if (File.Exists(wrapper)) return wrapper;
        }
        return Path.Combine(baseDirectory, "OpenXLR.UI");
    }

    private static string? FirstExisting(params string[] relativeToBaseDir) =>
        relativeToBaseDir
            .Select(r => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, r)))
            .FirstOrDefault(File.Exists);

    private static string UnitPath => Path.Combine(ConfigHome, "systemd", "user", UnitName);

    private static string AutostartPath => Path.Combine(ConfigHome, "autostart", "openxlr.desktop");

    /// <summary>Path of the unit a package installed, or null on a source build.</summary>
    public static string? PackagedUnit =>
        SystemUnitDirs.Select(d => Path.Combine(d, UnitName)).FirstOrDefault(File.Exists);

    /// <summary>
    /// True when ~/.config holds a unit that must not be there: any copy on a
    /// packaged install (it shadows the packaged unit), or one whose ExecStart
    /// binary does not exist. Earlier OpenXLR versions wrote exactly that on
    /// packaged installs, leaving the daemon looping on 203/EXEC after every
    /// reboot.
    /// </summary>
    public static bool HasStaleUserUnit()
    {
        if (!File.Exists(UnitPath)) return false;
        if (PackagedUnit is not null) return true;
        try
        {
            foreach (string line in File.ReadLines(UnitPath))
            {
                if (!line.StartsWith("ExecStart=", StringComparison.Ordinal)) continue;
                string? exe = ExecStartBinary(line["ExecStart=".Length..]);
                return exe is { Length: > 0 } && !File.Exists(exe);
            }
        }
        catch (IOException) { /* unreadable: leave it alone */ }
        return false;
    }

    /// <summary>
    /// Run at startup when daemon-at-login is on: replaces a stale unit left
    /// by an earlier version and starts the daemon, without waiting for the
    /// user to toggle the option again.
    /// </summary>
    public static void RepairDaemonUnit()
    {
        try
        {
            if (!HasStaleUserUnit()) return;
            SetDaemonAtLogin(true);
            Systemctl("start", UnitName);
        }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// A path as one ExecStart argument (systemd.service(5)): double quoted,
    /// backslash and quote escaped, and '%' doubled since specifiers expand
    /// inside quotes too. A source tree under "My Projects" or a home with a
    /// percent sign would otherwise split or expand.
    /// </summary>
    internal static string SystemdQuote(string path)
        => "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%") + "\"";

    /// <summary>
    /// The first argument of an ExecStart value, unquoted: the plain word
    /// up to the first space, or a double or single quoted argument with
    /// backslash escapes and '%%' folded back.
    /// </summary>
    internal static string? ExecStartBinary(string value)
    {
        value = value.TrimStart();
        // Leading option characters (systemd's -, @, :, +, !, !!) are not part of the path.
        value = value.TrimStart('-', '@', ':', '+', '!').TrimStart();
        if (value.Length == 0) return null;
        var sb = new System.Text.StringBuilder();
        if (value[0] is '"' or '\'')
        {
            char quote = value[0];
            for (int i = 1; i < value.Length; i++)
            {
                char c = value[i];
                if (c == quote) break;
                if (c == '\\' && i + 1 < value.Length) { sb.Append(value[++i]); continue; }
                sb.Append(c);
            }
        }
        else
        {
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c)) break;
                sb.Append(c);
            }
        }
        return sb.ToString().Replace("%%", "%");
    }

    /// <summary>
    /// A path as the Exec value of a desktop entry: quoted, with the
    /// characters the spec reserves inside quotes escaped, the backslashes
    /// doubled once more for the file's own string escaping, and '%'
    /// doubled so it is not read as a field code.
    /// </summary>
    internal static string DesktopExec(string path)
    {
        string inner = path
            .Replace("\\", "\\\\")     // backslash for the quoting layer
            .Replace("\"", "\\\"")
            .Replace("`", "\\`")
            .Replace("$", "\\$")
            .Replace("%", "%%");
        return "\"" + inner.Replace("\\", "\\\\") + "\"";   // and once more for the string layer
    }

    /// <summary>
    /// The program an Exec value runs, as a path: the inverse of
    /// <see cref="DesktopExec"/> for entries this window wrote, and a best
    /// effort for entries a desktop or a user wrote by hand. Null when the
    /// value carries no program at all.
    /// </summary>
    internal static string? DesktopExecBinary(string value)
    {
        // The desktop entry file itself escapes the value (\\ for a
        // backslash, \s \n \t \r for whitespace); undo that first, then read
        // the first argument under the Exec quoting rules.
        var unescaped = new System.Text.StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length) { unescaped.Append(value[i]); continue; }
            char next = value[++i];
            unescaped.Append(next switch { '\\' => '\\', 's' => ' ', 'n' => '\n', 't' => '\t', 'r' => '\r', _ => next });
            // An unknown sequence keeps the escaped character, not the backslash.
        }

        string argument = unescaped.ToString().TrimStart();
        if (argument.Length == 0) return null;
        var program = new System.Text.StringBuilder();
        if (argument[0] == '"')
        {
            for (int i = 1; i < argument.Length; i++)
            {
                char c = argument[i];
                if (c == '"') break;
                // Inside quotes the spec reserves " ` $ \, each escaped with a backslash.
                if (c == '\\' && i + 1 < argument.Length) { program.Append(argument[++i]); continue; }
                program.Append(c);
            }
        }
        else
        {
            foreach (char c in argument)
            {
                if (char.IsWhiteSpace(c)) break;
                program.Append(c);
            }
        }
        string result = program.ToString().Replace("%%", "%");
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// True when the program an Exec value names can still be launched: an
    /// absolute or relative path that exists, or a bare command found on PATH.
    /// </summary>
    internal static bool DesktopExecTargetExists(string? program)
    {
        if (program is not { Length: > 0 }) return false;
        if (program.Contains(Path.DirectorySeparatorChar)) return File.Exists(program);
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => File.Exists(Path.Combine(d, program)));
    }

    /// <summary>
    /// Write one of the two startup files, both of which live in directories
    /// the desktop and systemd own rather than in OpenXLR's own tree:
    /// ~/.config/autostart and ~/.config/systemd/user.
    ///
    /// AGENTS.md requires <c>OpenXlrPaths.WriteAtomic</c> for files under
    /// ~/.config/openxlr. These two are outside that tree and that helper is
    /// wrong for them: it forces the directory to 0700 and the file to 0600,
    /// which is not OpenXLR's call to make for a shared directory, and its
    /// rename would turn an entry the user symlinked into a regular file.
    /// So the write is still a temporary file and a rename in the same
    /// directory, but the directory keeps its mode and the process umask
    /// decides the file's (0644 on a default umask).
    ///
    /// False when the target is a symbolic link: whatever it points at
    /// belongs to whoever made the link, so it is left untouched and the
    /// caller reports the refusal.
    /// </summary>
    private static bool WriteStartupFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (IsSymbolicLink(path)) return false;
        string tmp = path + ".openxlr-tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    /// <summary>Remove a startup file, treating one that is already gone as done.</summary>
    private static void RemoveStartupFile(string path)
    {
        // File.Delete is silent about a missing file but throws
        // DirectoryNotFoundException when the directory itself is absent,
        // which is the same "nothing to remove" state.
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { }
    }

    private static bool IsSymbolicLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>The autostart entry this window writes for a given executable.</summary>
    private static string AutostartEntry(string executable) => $"""
        [Desktop Entry]
        Type=Application
        Name=OpenXLR
        Comment=OpenXLR mixer window
        Exec={DesktopExec(executable)}
        Icon=openxlr
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """;

    /// <summary>
    /// Apply the daemon-at-login preference. False when nothing could be
    /// applied, so Options reports it instead of saving a preference the
    /// system does not have.
    /// </summary>
    public static bool SetDaemonAtLogin(bool enabled)
    {
        try { return ApplyDaemonAtLogin(enabled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool ApplyDaemonAtLogin(bool enabled)
    {
        if (enabled)
        {
            if (PackagedUnit is not null)
            {
                // Any copy in ~/.config would shadow the packaged unit.
                try { RemoveStartupFile(UnitPath); } catch (IOException) { }
            }
            else
            {
                // No binary anywhere we know of: a unit would only loop on 203/EXEC.
                if (DaemonBinary is not { } daemon) return false;
                if (!WriteStartupFile(UnitPath, $"""
                    # Written by the OpenXLR window for a source build (Options, Start at login).
                    [Unit]
                    Description=OpenXLR audio daemon
                    After=pipewire-pulse.service wireplumber.service
                    StartLimitIntervalSec=300
                    StartLimitBurst=3

                    [Service]
                    Type=notify
                    NotifyAccess=main
                    WatchdogSec=60
                    WatchdogSignal=SIGTERM
                    TimeoutStartSec=120
                    ExecStart={SystemdQuote(daemon)}
                    Environment=OPENXLR_BUILD_MIXER=1
                    TimeoutStopSec=45
                    KillMode=mixed
                    Restart=on-failure
                    RestartSec=3
                    NoNewPrivileges=true
                    PrivateTmp=true
                    ProtectSystem=strict
                    ProtectControlGroups=true
                    ProtectKernelTunables=true
                    RestrictSUIDSGID=true
                    UMask=0077
                    KeyringMode=private
                    ProtectClock=true
                    ProtectHostname=true
                    ProtectKernelLogs=true
                    ProtectKernelModules=true
                    LockPersonality=true
                    RestrictNamespaces=true
                    CapabilityBoundingSet=

                    [Install]
                    WantedBy=default.target
                    """)) return false;
            }
            Systemctl("daemon-reload");
            return Systemctl("enable", UnitName);
        }
        bool disabled = Systemctl("disable", UnitName);
        try { RemoveStartupFile(UnitPath); } catch (IOException) { }
        Systemctl("daemon-reload");
        // systemctl refuses to disable a unit that does not exist, but an
        // install with no unit at all already starts no daemon at login, so
        // turning the option off there has nothing to report.
        return disabled || (PackagedUnit is null && !File.Exists(UnitPath));
    }

    /// <summary>
    /// Apply the window-at-login preference and record which executable the
    /// entry now names, so a later repair can tell a moved installation from
    /// an entry the user removed. False when nothing was applied.
    /// </summary>
    public static bool SetWindowAtLogin(bool enabled)
    {
        if (!SetWindowAtLogin(enabled, UiBinary)) return false;
        RememberAutostartExecutable(enabled ? UiBinary : null);
        return true;
    }

    internal static bool SetWindowAtLogin(bool enabled, string executable)
    {
        try
        {
            if (!enabled) { RemoveStartupFile(AutostartPath); return true; }
            if (!File.Exists(executable)) return false;
            return WriteStartupFile(AutostartPath, AutostartEntry(executable));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void RememberAutostartExecutable(string? executable)
    {
        UiSettings saved = UiSettings.Load();
        if (saved.AutostartExecutable == executable) return;
        (saved with { AutostartExecutable = executable }).Save();
    }

    /// <summary>What a startup repair did, so Options can report it.</summary>
    public enum AutostartRepair
    {
        /// <summary>The preference is off, or the entry is already right.</summary>
        NotNeeded,
        /// <summary>The entry was created, or its launch path was corrected.</summary>
        Repaired,
        /// <summary>The entry is gone and was left gone: it was removed outside OpenXLR.</summary>
        RemovedOutside,
        /// <summary>The entry could not be written.</summary>
        Failed,
    }

    /// <summary>The outcome of the repair this launch ran, for Options to show.</summary>
    public static AutostartRepair LastRepair { get; private set; } = AutostartRepair.NotNeeded;

    /// <summary>
    /// Bring the autostart entry back in line with the installation, once per
    /// installation path change rather than on every launch:
    ///
    /// - an entry whose Exec names a program that still exists is never
    ///   rewritten, so a user who edited the command keeps it;
    /// - an entry whose Exec names a program that is gone gets this
    ///   installation's path, keeping every other key, including an external
    ///   Hidden=true disable;
    /// - a missing entry is recreated only when the recorded launch path
    ///   differs from this one. An entry removed with a desktop tool while the
    ///   path is unchanged stays removed, and Options says so.
    ///
    /// A saved choice to start only the daemon never enables the window.
    /// </summary>
    public static AutostartRepair RepairWindowAutostart()
    {
        string executable = UiBinary;
        AutostartRepair result = RepairWindowAutostart(UiSettings.Load(), executable);
        if (result == AutostartRepair.Repaired) RememberAutostartExecutable(executable);
        LastRepair = result;
        return result;
    }

    internal static AutostartRepair RepairWindowAutostart(UiSettings settings, string executable)
    {
        if (!settings.OpenWindowAtLogin) return AutostartRepair.NotNeeded;
        try
        {
            if (!File.Exists(executable)) return AutostartRepair.Failed;
            if (!EntryExists(AutostartPath))
                return settings.AutostartExecutable == executable
                    ? AutostartRepair.RemovedOutside
                    : Written(SetWindowAtLogin(true, executable));

            var lines = File.ReadAllLines(AutostartPath).ToList();
            int start = lines.FindIndex(l => l.Trim() == "[Desktop Entry]");
            if (start < 0) return Written(SetWindowAtLogin(true, executable));
            int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("[", StringComparison.Ordinal));
            if (end < 0) end = lines.Count;

            // The spec allows space around the separator, so "Exec = x" is the
            // same key as "Exec=x". Matching on the literal "Exec=" missed it
            // and appended a second Exec, which desktop-file-validate rejects.
            var execLines = Enumerable.Range(start + 1, end - start - 1)
                .Where(i => DesktopKey(lines[i]) == "Exec").ToList();
            string expected = "Exec=" + DesktopExec(executable);

            if (execLines.Count == 1)
            {
                string? program = DesktopExecBinary(DesktopValue(lines[execLines[0]]));
                if (DesktopExecTargetExists(program)) return AutostartRepair.NotNeeded;
                lines[execLines[0]] = expected;
            }
            else if (execLines.Count == 0) lines.Insert(end, expected);
            else
            {
                // More than one Exec in the group is already invalid. Keep the
                // first, pointing at this installation, and drop the rest.
                lines[execLines[0]] = expected;
                foreach (int i in execLines.Skip(1).OrderDescending()) lines.RemoveAt(i);
            }
            return Written(WriteStartupFile(AutostartPath, string.Join("\n", lines) + "\n"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return AutostartRepair.Failed; }

        static AutostartRepair Written(bool ok) => ok ? AutostartRepair.Repaired : AutostartRepair.Failed;
    }

    /// <summary>An entry is there when the file exists, or when a symlink stands in its place.</summary>
    private static bool EntryExists(string path) => File.Exists(path) || IsSymbolicLink(path);

    /// <summary>
    /// The key a desktop entry line declares, per the Desktop Entry
    /// specification: everything before the first '=', trimmed. Null for a
    /// comment, a blank line, a group header or a line with no separator.
    /// </summary>
    internal static string? DesktopKey(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is '#' or '[') return null;
        int separator = trimmed.IndexOf('=');
        return separator < 0 ? null : trimmed[..separator].TrimEnd();
    }

    /// <summary>The value of a desktop entry line: everything after the first '=', leading space dropped.</summary>
    internal static string DesktopValue(string line)
    {
        int separator = line.IndexOf('=');
        return separator < 0 ? "" : line[(separator + 1)..].TrimStart();
    }

    /// <summary>
    /// Restart the daemon's user service so a daemon-side setting takes
    /// effect. False when systemd does not manage it (source builds run by
    /// hand), so the caller can tell the user to restart it themselves.
    /// </summary>
    public static bool RestartDaemon() => Systemctl("restart", "openxlr-daemon.service");

    private static bool Systemctl(params string[] args)
    {
        try
        {
            // Bounded: a systemctl that hangs (a stuck user manager) is
            // killed after 15 s instead of being left behind.
            return ProcessRunner.Run("systemctl", ["--user", .. args], TimeSpan.FromSeconds(15),
                stdoutCap: 64 * 1024, stderrCap: 64 * 1024).Ok;
        }
        catch (Exception) { return false; }
    }
}
