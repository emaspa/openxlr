using System.Collections.Concurrent;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// What PipeWire says about an application that can lead to its icon: the
/// icon name the application published, its desktop application id (the
/// Flatpak portal's, or the one the application set), its process binary and
/// the name it is listed under.
/// </summary>
public sealed record ApplicationIconHints(string? IconName, string? AppId, string? Binary, string? AppName)
{
    /// <summary>The hints a live stream or client carries.</summary>
    public static ApplicationIconHints From(AudioStream stream)
    {
        string? binary = stream.Binary is { Length: > 0 } b ? Path.GetFileName(b) : null;
        // Steam's audio comes from its browser helper; the identity is Steam's.
        if (string.Equals(binary, "steamwebhelper", StringComparison.OrdinalIgnoreCase)) binary = "steam";
        // Every WebKitGTK application plays from the same web process, which
        // names no one of them; its name leads to its entry instead.
        if (StreamMatcher.IsWebKitProcess(binary)) binary = null;
        // Every Wine and Proton game shares the runtime's binary, which
        // names the runtime and not the game.
        if (binary is not null && (binary.Contains("wine", StringComparison.OrdinalIgnoreCase) ||
                                   binary.Contains("proton", StringComparison.OrdinalIgnoreCase)))
            binary = null;
        return new(stream.IconName, stream.AppId, binary, stream.Label);
    }

    /// <summary>The hints a remembered application has left: its identity and its label.</summary>
    public static ApplicationIconHints Remembered(string identity, string label)
        => new(null, null, identity.Contains('|') ? null : identity, label);
}

/// <summary>Where an icon came from, best first.</summary>
public enum IconSource
{
    /// <summary>The <c>application.icon_name</c> the application published.</summary>
    IconName,
    /// <summary>The desktop entry named by the application id.</summary>
    AppId,
    /// <summary>A desktop entry whose id, window class or program is the process binary.</summary>
    Binary,
    /// <summary>A desktop entry whose name, window class or id is the application's name.</summary>
    AppName,
    /// <summary>A Wine program with no desktop entry: the icon inside its executable, as a PNG path.</summary>
    Executable,
    /// <summary>No desktop entry: the binary itself as an icon name, which many packages install.</summary>
    BinaryName,
}

/// <summary>An icon found for an application, and how.</summary>
public sealed record IconMatch(string Icon, IconSource Source);

/// <summary>The fields of one desktop entry that can identify an application.</summary>
/// <param name="Id">The desktop file id: its path under <c>applications</c>, slashes as dashes, no extension.</param>
/// <param name="Program">The program the Exec line runs, without its directory.</param>
/// <param name="WindowClass">StartupWMClass.</param>
public sealed record DesktopEntry(string Id, string? Name, string Icon, string? Program, string? WindowClass);

/// <summary>
/// The desktop entries installed for the user, read from the XDG data
/// directories, the Flatpak exports and Snap's directory. An entry earlier in
/// the search order hides a later one with the same id, and a hidden entry
/// hides it without taking its place.
/// </summary>
public sealed class DesktopEntryIndex
{
    private readonly IReadOnlyList<DesktopEntry> _entries;
    private readonly Dictionary<string, DesktopEntry> _byId;

    private DesktopEntryIndex(IReadOnlyList<DesktopEntry> entries)
    {
        _entries = entries;
        _byId = new Dictionary<string, DesktopEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (DesktopEntry e in entries) _byId.TryAdd(e.Id, e);
    }

    public static DesktopEntryIndex Empty { get; } = new([]);

    public IReadOnlyList<DesktopEntry> Entries => _entries;

    /// <summary>
    /// The <c>applications</c> directories in the order the desktop searches
    /// them: <c>XDG_DATA_HOME</c>, then <c>XDG_DATA_DIRS</c>, then the Flatpak
    /// exports and Snap's entries when the session did not list them.
    /// </summary>
    public static IReadOnlyList<string> ApplicationDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome) || !Path.IsPathRooted(dataHome)) dataHome = Path.Combine(home, ".local/share");
        string? dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrEmpty(dataDirs)) dataDirs = "/usr/local/share:/usr/share";
        var roots = new List<string> { dataHome };
        roots.AddRange(dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathRooted));
        roots.Add(Path.Combine(home, ".local/share/flatpak/exports/share"));
        roots.Add("/var/lib/flatpak/exports/share");
        roots.Add("/var/lib/snapd/desktop");
        return [.. roots.Select(r => Path.Combine(r.TrimEnd('/'), "applications")).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Read every entry under these directories; unreadable files are skipped.</summary>
    public static DesktopEntryIndex Scan(IEnumerable<string> directories)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<DesktopEntry>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
        foreach (string dir in directories)
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.desktop", options).Order(StringComparer.Ordinal).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string file in files)
            {
                string id = Path.GetRelativePath(dir, file)[..^".desktop".Length].Replace('/', '-');
                if (!seen.Add(id)) continue;   // an earlier directory's entry of this id wins
                if (Parse(id, file) is { } entry) entries.Add(entry);
            }
        }
        return new DesktopEntryIndex(entries);
    }

    /// <summary>One entry's identifying fields, or null when it is no application, is hidden or has no icon.</summary>
    internal static DesktopEntry? Parse(string id, string path)
    {
        string? name = null, icon = null, exec = null, wmClass = null;
        bool inEntry = false, application = false, hidden = false;
        try
        {
            using var reader = new StreamReader(path);
            string? raw;
            for (int lines = 0; lines < 2000 && (raw = reader.ReadLine()) is not null; lines++)
            {
                string line = raw.Trim();
                if (line.StartsWith('['))
                {
                    if (inEntry) break;   // only the main group describes the application
                    inEntry = line == "[Desktop Entry]";
                    continue;
                }
                if (!inEntry) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].TrimEnd(), value = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "Name": name ??= value; break;
                    case "Icon": icon ??= value; break;
                    case "Exec": exec ??= value; break;
                    case "StartupWMClass": wmClass ??= value; break;
                    case "Type": application = value == "Application"; break;
                    case "Hidden": hidden = value == "true"; break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (!application || hidden || string.IsNullOrEmpty(icon)) return null;
        return new DesktopEntry(id, name, icon, exec is null ? null : ProgramOf(exec), string.IsNullOrEmpty(wmClass) ? null : wmClass);
    }

    /// <summary>
    /// The program an Exec line starts: past <c>env</c> and its assignments,
    /// and for <c>flatpak run</c> the <c>--command</c> it names.
    /// </summary>
    internal static string? ProgramOf(string exec)
    {
        List<string> tokens = Tokens(exec);
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (token.StartsWith('%') || token.Length == 0) continue;
            string program = Path.GetFileName(token);
            if (program == "env" || program.StartsWith('-') || program.Contains('=')) continue;   // env, its options, its assignments
            if (program == "flatpak")
                return tokens.Skip(i + 1).FirstOrDefault(t => t.StartsWith("--command=", StringComparison.Ordinal))?["--command=".Length..]
                    is { Length: > 0 } command ? Path.GetFileName(command) : null;
            return program.Length > 0 ? program : null;
        }
        return null;
    }

    // The Exec key's quoting: an argument in double quotes may hold spaces,
    // and a backslash inside the quotes escapes the next character.
    private static List<string> Tokens(string exec)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < exec.Length; i++)
        {
            char c = exec[i];
            if (quoted && c == '\\' && i + 1 < exec.Length) { current.Append(exec[++i]); continue; }
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any || current.Length > 0) tokens.Add(current.ToString());
                current.Clear();
                any = false;
                continue;
            }
            current.Append(c);
        }
        if (any || current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>The entry with this desktop file id, or null.</summary>
    public DesktopEntry? ById(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The first entry, in search order, that satisfies one test, trying the tests in order.</summary>
    public DesktopEntry? First(params Func<DesktopEntry, bool>[] tests)
    {
        foreach (Func<DesktopEntry, bool> test in tests)
            foreach (DesktopEntry e in _entries)
                if (test(e)) return e;
        return null;
    }
}

/// <summary>
/// Finds the icon for an application: the icon name it published, or its
/// desktop entry's icon by application id, by process binary and by name,
/// or the binary itself as a name. The result is a freedesktop icon name or
/// an absolute image path, as a desktop entry's Icon key holds it; a client
/// finds the file through its icon theme. Lookups are kept, and the entries
/// are read again only when one of the directories changed.
/// </summary>
public sealed class ApplicationIconResolver
{
    private readonly Func<IReadOnlyList<string>> _directories;
    private readonly TimeSpan _checkInterval;
    private readonly Lock _refresh = new();
    private readonly ConcurrentDictionary<ApplicationIconHints, IconMatch?> _cache = new();
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);
    private DesktopEntryIndex _index = DesktopEntryIndex.Empty;
    private string? _stamp;
    private long _checkedAt;
    private volatile bool _read;

    public ApplicationIconResolver(Func<IReadOnlyList<string>>? directories = null, TimeSpan? checkInterval = null)
    {
        _directories = directories ?? DesktopEntryIndex.ApplicationDirectories;
        _checkInterval = checkInterval ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>The entries the last refresh read.</summary>
    public DesktopEntryIndex Index => Volatile.Read(ref _index);

    /// <summary>
    /// Read the entries again when a directory changed since the last read.
    /// Touches the disk, so it is called outside any lock; within the check
    /// interval it returns at once.
    /// </summary>
    public void Refresh()
    {
        long interval = (long)_checkInterval.TotalMilliseconds;
        if (_read && Environment.TickCount64 - Interlocked.Read(ref _checkedAt) < interval) return;
        lock (_refresh)
        {
            if (_read && Environment.TickCount64 - _checkedAt < interval) return;
            IReadOnlyList<string> dirs = _directories();
            string stamp = string.Join('\n', dirs.Select(d =>
            {
                try { return Directory.Exists(d) ? $"{d}={Directory.GetLastWriteTimeUtc(d).Ticks}" : d; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return d; }
            }));
            if (stamp != _stamp)
            {
                Volatile.Write(ref _index, DesktopEntryIndex.Scan(dirs));
                _cache.Clear();
                _names.Clear();
                _stamp = stamp;
            }
            Interlocked.Exchange(ref _checkedAt, Environment.TickCount64);
            _read = true;
        }
    }

    /// <summary>The icon for these hints from the entries already read, or null. Never touches the disk.</summary>
    public IconMatch? Resolve(ApplicationIconHints hints) => _cache.GetOrAdd(hints, h => Resolve(Index, h));

    /// <summary>
    /// The name as the application's desktop entry spells it, when an entry's
    /// name is this one but for letter case: a WebKitGTK client that names
    /// itself "whatrust" is listed as its entry's "whatRust". Any other name
    /// comes back as it is. Never touches the disk.
    /// </summary>
    public string DisplayName(string label) => _names.GetOrAdd(label, l => DisplayName(Index, l));

    internal static string DisplayName(DesktopEntryIndex index, string label)
        => index.First(e => e.Name is { Length: > 0 } name && string.Equals(name, label, StringComparison.OrdinalIgnoreCase))?.Name ?? label;

    internal static IconMatch? Resolve(DesktopEntryIndex index, ApplicationIconHints hints)
    {
        static bool Same(string? a, string? b) => a is { Length: > 0 } && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        static string LastSegment(string id) => id[(id.LastIndexOf('.') + 1)..];

        // A published icon is a theme name; a path there is not one, and the
        // client that set it is not trusted to point at files.
        if (Clean(hints.IconName) is { } published && !published.Contains('/')) return new(published, IconSource.IconName);
        if (Clean(hints.AppId) is { } appId && index.ById(appId) is { } byId) return new(byId.Icon, IconSource.AppId);
        string? binary = Clean(hints.Binary);
        if (binary is not null && index.First(
                e => Same(e.Id, binary),
                e => Same(e.WindowClass, binary),
                e => e.Id.Contains('.') && Same(LastSegment(e.Id), binary),
                e => Same(e.Program, binary)) is { } byBinary)
            return new(byBinary.Icon, IconSource.Binary);
        string? name = Clean(hints.AppName);
        // A game is listed as "Cyberpunk2077" and installed as "Cyberpunk 2077":
        // last, names compare the way game identities do, letters and digits.
        string? game = name is null ? null : StreamMatcher.GameIdentity(name);
        if (name is not null && index.First(
                e => Same(e.Name, name),
                e => Same(e.WindowClass, name),
                e => Same(e.Id, name),
                e => e.Name is { Length: > 0 } n && Same(StreamMatcher.GameIdentity(n), game)) is { } byName)
            return new(byName.Icon, IconSource.AppName);
        return binary is not null ? new(binary.ToLowerInvariant(), IconSource.BinaryName) : null;
    }

    // Names and paths only: nothing that could reach outside an icon lookup.
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.EndsWith(" (deleted)", StringComparison.Ordinal)) value = value[..^10];
        return value.Length > 256 || value.Any(char.IsControl) ? null : value;
    }
}
