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
    /// <summary>Names of the main window's tiles the user collapsed (INPUTS, HEADPHONES, ...).</summary>
    public IReadOnlyList<string> CollapsedSections { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ConfigDir
    {
        get
        {
            string baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
                ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(baseDir, "openxlr");
        }
    }

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
        try
        {
            Directory.CreateDirectory(ConfigDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
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

    public void Save()
    {
        Directory.CreateDirectory(UiSettings.ConfigDir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
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

    private static string ConfigHome =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x : Path.Combine(HomeDir, ".config");

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
    private static string UiBinary => FirstExisting(
        Path.Combine("..", "..", "bin", "openxlr"),
        Path.Combine("..", "..", "..", "bin", "openxlr"))
        ?? Path.Combine(AppContext.BaseDirectory, "OpenXLR.UI");

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
                string exe = line["ExecStart=".Length..].Trim().Split(' ')[0];
                return exe.Length > 0 && !File.Exists(exe);
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

    public static void SetDaemonAtLogin(bool enabled)
    {
        if (enabled)
        {
            if (PackagedUnit is not null)
            {
                // Any copy in ~/.config would shadow the packaged unit.
                try { File.Delete(UnitPath); } catch (IOException) { }
            }
            else
            {
                // No binary anywhere we know of: a unit would only loop on 203/EXEC.
                if (DaemonBinary is not { } daemon) return;
                Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
                File.WriteAllText(UnitPath, $"""
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
                    ExecStart={daemon}
                    Environment=OPENXLR_BUILD_MIXER=1
                    TimeoutStopSec=45
                    Restart=on-failure
                    RestartSec=3
                    NoNewPrivileges=true
                    PrivateTmp=true
                    ProtectSystem=strict
                    ProtectControlGroups=true
                    ProtectKernelTunables=true
                    RestrictSUIDSGID=true

                    [Install]
                    WantedBy=default.target
                    """);
            }
            Systemctl("daemon-reload");
            Systemctl("enable", UnitName);
        }
        else
        {
            Systemctl("disable", UnitName);
            try { File.Delete(UnitPath); } catch (IOException) { }
            Systemctl("daemon-reload");
        }
    }

    public static void SetWindowAtLogin(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartPath)!);
            File.WriteAllText(AutostartPath, $"""
                [Desktop Entry]
                Type=Application
                Name=OpenXLR
                Comment=OpenXLR mixer window
                Exec={UiBinary}
                Icon=openxlr
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """);
        }
        else
        {
            try { File.Delete(AutostartPath); } catch (IOException) { }
        }
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
            var psi = new ProcessStartInfo("systemctl") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--user");
            foreach (string a in args) psi.ArgumentList.Add(a);
            using Process? p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(15000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }
}
