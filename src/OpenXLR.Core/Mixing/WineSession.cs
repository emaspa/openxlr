using System.Text;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// Ends the Wine prefix OpenXLR's bridged plugins ran in.
///
/// A Windows plugin loaded through yabridge brings up Wine's per-prefix
/// service processes: a wineserver, services.exe, winedevice.exe and the
/// rest. None of them is a child of the helper that started them, they stay
/// after the helper exits, and they ignore SIGTERM, so they sit in the
/// daemon's control group until systemd kills them at the unit's stop
/// timeout. A stop that took one second took forty-five.
///
/// The cure is Wine's own: "wineserver -k" against the prefix, once the last
/// bridged helper has gone. Which prefix is not guessed. yabridge picks the
/// prefix from where the Windows plugin lives, not from the daemon's
/// environment, so the prefixes are read back from the wineservers that
/// share this process's control group. A wineserver can only be in it if one
/// of our helpers forked it, which makes the control group both the answer to
/// "which prefix" and the proof that OpenXLR started that session.
///
/// Everything else in the same prefix is left alone: another application's
/// Wine session, a DAW with its own bridged plugins, an installer the user is
/// running, all live in their own control groups, and ending one of those
/// would take that application's plugins with it.
///
/// The decision and the helper call are separate on purpose. A caller decides
/// under whatever lock it holds and makes the call outside it, so no command
/// or state push ever waits on Wine.
/// </summary>
internal sealed class WineSession
{
    /// <summary>
    /// "wineserver -k" signals the server and then walks a fixed schedule of
    /// growing waits, about two and a quarter seconds of them, before it gives
    /// up; it returns at once when there is no server to end. So this is that
    /// schedule with room on top, and still short enough that a stop which
    /// Wine ignores altogether costs seconds rather than the unit's timeout.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(6);

    /// <summary>A decided ending: which wineserver to run, against which prefix.</summary>
    public sealed record Ending(string Server, string Prefix);

    /// <summary>
    /// A Wine session of ours that is up: its prefix, and the program its
    /// wineserver runs from when that can be read.
    /// </summary>
    public sealed record Running(string Prefix, string? Server);

    private readonly Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, ProcessResult> _run;
    private readonly Func<string?> _findServer;
    private readonly Func<IReadOnlyList<Running>> _ours;
    private readonly Action<string>? _note;
    private readonly object _gate = new();
    private bool _bridgedRan;
    private bool _saidNoServer;

    public WineSession(Action<string>? note = null,
        Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, ProcessResult>? run = null,
        Func<string?>? findServer = null, Func<IReadOnlyList<Running>>? ours = null)
    {
        _note = note;
        // Helpers are started through ProcessRunner and so is this: arguments
        // as a list, a deadline, and the tree killed if it is reached.
        _run = run ?? ((server, arguments, environment)
            => ProcessRunner.Run(server, arguments, Deadline, environment: environment));
        _findServer = findServer ?? (() => PluginInstaller.OnPath("wineserver"));
        _ours = ours ?? (() => SessionsStartedHere());
    }

    /// <summary>A bridged helper has been started, so Wine is now this daemon's to end.</summary>
    public void HelperStarted()
    {
        lock (_gate) _bridgedRan = true;
    }

    /// <summary>
    /// What ending Wine would take now. Empty unless a bridged helper ran
    /// here, every one of them has gone, a Wine session of ours is actually
    /// up, and there is a wineserver to run against it.
    ///
    /// The wineserver run is the program the session's own server runs from.
    /// That is the one Wine build sure to speak the running server's protocol,
    /// and it is found where PATH has none: Debian and Ubuntu keep wineserver
    /// in /usr/lib/x86_64-linux-gnu/wine. The one on PATH is the fallback for
    /// a server whose program cannot be read, or was replaced by an upgrade
    /// while it ran. Asking again after a successful
    /// ending finds no session and answers empty, so the question can be put
    /// at every rewire and again at the stop; an ending that failed is simply
    /// offered again.
    /// </summary>
    public IReadOnlyList<Ending> Decide(bool anyBridgedHelperAlive)
    {
        lock (_gate)
        {
            if (anyBridgedHelperAlive || !_bridgedRan) return [];
            var endings = new List<Ending>();
            foreach (Running session in _ours())
            {
                string? server = session.Server ?? _findServer();
                if (server is not null)
                    endings.Add(new Ending(server, session.Prefix));
                else if (!_saidNoServer)
                {
                    _saidNoServer = true;
                    _note?.Invoke($"no wineserver found for the Wine prefix {session.Prefix}: systemd ends what is left of it when the daemon stops");
                }
            }
            return endings;
        }
    }

    /// <summary>
    /// Carry out decided endings. Never throws and never waits longer than
    /// <see cref="Deadline"/> each: a shutdown that cannot end Wine goes on and
    /// leaves it to systemd, which is where it was before.
    /// </summary>
    public void Run(IReadOnlyList<Ending> endings)
    {
        foreach (Ending ending in endings)
        {
            try
            {
                ProcessResult result = _run(ending.Server, ["-k"], RunEnvironment(ending.Prefix));
                _note?.Invoke(result.Ok
                    ? $"ended the Wine prefix {ending.Prefix}"
                    : $"could not end the Wine prefix {ending.Prefix}: wineserver exit {result.ExitCode}{(result.TimedOut ? ", timed out" : "")}");
            }
            catch (Exception ex)
            {
                _note?.Invoke($"could not end the Wine prefix {ending.Prefix}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The same environment a bridged helper gets: the bridge's own directory
    /// first on PATH, and the prefix to act on. wineserver picks the session it
    /// ends by WINEPREFIX, so this is what decides which one goes.
    /// </summary>
    internal static Dictionary<string, string> RunEnvironment(string prefix)
    {
        Dictionary<string, string> environment = ManagedYabridge.Discover()?.HostEnvironment() ?? new(StringComparer.Ordinal);
        environment["WINEPREFIX"] = prefix;
        return environment;
    }

    /// <summary>
    /// Whether a bundle is one of yabridge's wrappers, so loading it starts
    /// Wine. Both layouts are named after the bridge: the companion's private
    /// home, .../openxlr/yabridge/vst3/Plugin.vst3, and the system bridge's own
    /// directory under the format's home, ~/.vst3/yabridge/Plugin.vst3.
    /// </summary>
    internal static bool Bridged(string? bundle)
        => bundle is not null && bundle.Split('/').Contains("yabridge");

    /// <summary>
    /// The Wine sessions whose wineservers share this process's control
    /// group, each with the program its server runs from. Only a server one of our helpers forked is in it: a wineserver
    /// is reparented to the user manager the moment it daemonizes, but it
    /// keeps the control group it was forked in, and for a user service that
    /// group is the unit. Usually one prefix, more when a user keeps their
    /// Windows plugins in several.
    /// </summary>
    internal static IReadOnlyList<Running> SessionsStartedHere(string? procRoot = null, string? homePrefix = null)
    {
        procRoot ??= "/proc";
        homePrefix ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wine");
        var found = new List<Running>();
        try
        {
            string ours = File.ReadAllText(Path.Combine(procRoot, "self", "cgroup")).Trim();
            if (ours.Length == 0) return found;
            foreach (string directory in Directory.EnumerateDirectories(procRoot))
            {
                if (!Path.GetFileName(directory).All(char.IsAsciiDigit)) continue;
                try
                {
                    // comm first: it is one short read, and it rules out
                    // everything but the handful of Wine processes.
                    if (File.ReadAllText(Path.Combine(directory, "comm")).Trim() != "wineserver") continue;
                    if (File.ReadAllText(Path.Combine(directory, "cgroup")).Trim() != ours) continue;
                    string prefix = PrefixIn(File.ReadAllBytes(Path.Combine(directory, "environ")), homePrefix);
                    if (!found.Any(session => string.Equals(session.Prefix, prefix, StringComparison.Ordinal)))
                        found.Add(new Running(prefix, ServerProgram(directory)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // The process ended between the listing and the read, or it
                    // belongs to somebody else. Neither is ours.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return found;
    }

    /// <summary>
    /// The program a process runs from, or null when the link cannot be read
    /// or names a file an upgrade has since replaced.
    /// </summary>
    internal static string? ServerProgram(string processDirectory)
    {
        try
        {
            string? target = new FileInfo(Path.Combine(processDirectory, "exe")).LinkTarget;
            return target is null || target.EndsWith(" (deleted)", StringComparison.Ordinal) || !File.Exists(target) ? null : target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// WINEPREFIX out of a process's environment block, or the prefix Wine
    /// itself falls back to when the variable is not set.
    /// </summary>
    internal static string PrefixIn(byte[] environment, string homePrefix)
    {
        foreach (string entry in Encoding.UTF8.GetString(environment).Split('\0'))
            if (entry.StartsWith("WINEPREFIX=", StringComparison.Ordinal))
                return Normalize(entry["WINEPREFIX=".Length..]);
        return Normalize(homePrefix);
    }

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}
