using System.Globalization;
using System.Text;

namespace OpenXLR.Core.Mixing;

/// <summary>An insert as the plugin diagnostics name it: the chain it is in, by key and by name, and the insert.</summary>
public sealed record ChainInsert(string Chain, string ChainName, InsertDefinition Insert);

/// <summary>
/// An insert whose plugin is in the catalogue only through a yabridge
/// wrapper, so it would lose its plugin with the companion. <c>Reason</c> is
/// <c>outside-windows-folders</c> (a 64-bit module the Windows plugin host
/// does not search), <c>not-64-bit</c> or <c>module-missing</c>.
/// </summary>
public sealed record BridgeOnlyInsert(string Chain, string ChainName, string Id, string Kind, string Plugin, string Name,
    string Wrapper, string? Module, string Reason);

/// <summary>An insert with a Windows plugin, named for a note that Wine is missing.</summary>
public sealed record WindowsInsert(string Chain, string ChainName, string Id, string Name);

/// <summary>
/// What moving from the yabridge companion to the Windows plugin host built
/// into OpenXLR needs from the daemon: the one-time copy of the companion's
/// folders into the Windows plugin folders, and the inserts that still need
/// a wrapper or Wine. Nothing here starts a process or loads plugin code.
/// </summary>
public static class YabridgeMigration
{
    /// <summary>The most bytes of the registry read; yabridgectl's own file is a few hundred.</summary>
    internal const int MaxRegistryBytes = 1024 * 1024;

    /// <summary>The most inserts a list in the plugin diagnostics names.</summary>
    internal const int MaxListed = 64;

    /// <summary>The companion's folder registry, which its yabridgectl keeps under OpenXLR's own configuration.</summary>
    internal static string RegistryPath => Path.Combine(OpenXlrPaths.ConfigFile("bridge"), "yabridgectl", "config.toml");

    /// <summary>
    /// Copy the companion's registered folders into the Windows plugin
    /// folders, once, in a build with the Windows plugin host, when the
    /// companion is installed or its registry is still on disk. The registry
    /// outlives the package, so a user who removes the companion before the
    /// first start of this build keeps the folders as well. Only
    /// <c>daemon.json</c> is written; the registry is read and left as it is.
    /// Null when there was nothing to do: no host, neither the companion nor
    /// its registry, the copy already ran, or <c>daemon.json</c> cannot be
    /// read. A registry that is missing counts as one with no folders, and
    /// one that cannot be read is named in the record, so the copy is not
    /// tried again either way.
    /// </summary>
    /// <param name="windowsHost">The host's presence, for a test; the build's own when null.</param>
    /// <param name="bridge">The companion's presence, for a test; found as the daemon finds it when null.</param>
    public static BridgeFolderCopy? CopyFoldersOnce(bool? windowsHost = null, bool? bridge = null)
    {
        if (!(windowsHost ?? WindowsPluginHost.Installed)) return null;
        string registry = RegistryPath;
        if (!File.Exists(registry) && !(bridge ?? ManagedYabridge.Discover() is not null)) return null;
        if (DaemonSettings.Read(out _) is not { YabridgeFolderCopy: null }) return null;
        IReadOnlyList<string> folders = ReadRegistry(registry, out string? problem);
        return PluginSearchPaths.CopyWindowsFolders(folders, problem is null ? [] : [new(registry, problem)]);
    }

    /// <summary>The folders a yabridgectl registry lists in <c>plugin_dirs</c>; empty with no problem when the file is missing.</summary>
    internal static IReadOnlyList<string> ReadRegistry(string path, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(path)) return [];
            return PluginDirs(RegularFile.ReadText(path, MaxRegistryBytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            problem = "The yabridge folder registry cannot be read: " + ex.Message;
            return [];
        }
    }

    /// <summary>
    /// The strings of the top-level <c>plugin_dirs</c> array of a TOML
    /// document, as yabridgectl writes its registry. Only as much TOML is
    /// read as that file holds: keys and values up to the first table, with
    /// strings, arrays, inline tables and bare values. A multi-line string
    /// is refused rather than guessed at.
    /// </summary>
    internal static IReadOnlyList<string> PluginDirs(string toml)
    {
        int i = 0;
        while (true)
        {
            SkipBlank(toml, ref i);
            if (i >= toml.Length || toml[i] == '[') return [];
            int start = i;
            while (i < toml.Length && toml[i] is not ('=' or '\n')) i++;
            if (i >= toml.Length || toml[i] != '=') throw new FormatException("a line holds no key and value");
            string key = toml[start..i].Trim();
            i++;
            object value = Value(toml, ref i);
            SkipSpaces(toml, ref i);
            if (i < toml.Length && toml[i] == '#') while (i < toml.Length && toml[i] != '\n') i++;
            if (i < toml.Length && toml[i] is not ('\n' or '\r')) throw new FormatException($"more follows the value of {key}");
            if (key is not ("plugin_dirs" or "\"plugin_dirs\"" or "'plugin_dirs'")) continue;
            if (value is not List<object> list) throw new FormatException("plugin_dirs is not an array");
            return [.. list.Select(item => item as string ?? throw new FormatException("plugin_dirs holds something other than a string"))];
        }
    }

    /// <summary>Any value but a string or an array: kept apart from both, since plugin_dirs holds strings in an array.</summary>
    private sealed record Bare(string Text);

    private static void SkipSpaces(string s, ref int i)
    {
        while (i < s.Length && s[i] is ' ' or '\t') i++;
    }

    /// <summary>Spaces, line ends and comments.</summary>
    private static void SkipBlank(string s, ref int i)
    {
        while (i < s.Length)
        {
            if (s[i] is ' ' or '\t' or '\r' or '\n') i++;
            else if (s[i] == '#') while (i < s.Length && s[i] != '\n') i++;
            else return;
        }
    }

    private static object Value(string s, ref int i)
    {
        SkipSpaces(s, ref i);
        if (i >= s.Length) throw new FormatException("a value is missing");
        switch (s[i])
        {
            case '"' or '\'':
                return Text(s, ref i);
            case '[':
            {
                i++;
                var list = new List<object>();
                while (true)
                {
                    SkipBlank(s, ref i);
                    if (i >= s.Length) throw new FormatException("an array is not closed");
                    if (s[i] == ']') { i++; return list; }
                    list.Add(Value(s, ref i));
                    SkipBlank(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return list; }
                    throw new FormatException("an array item is not followed by a comma");
                }
            }
            case '{':
            {
                i++;
                while (true)
                {
                    SkipSpaces(s, ref i);
                    if (i >= s.Length || s[i] == '\n') throw new FormatException("an inline table is not closed");
                    if (s[i] == '}') { i++; return new Bare("{}"); }
                    if (s[i] is '"' or '\'') Text(s, ref i);
                    while (i < s.Length && s[i] is not ('=' or '\n')) i++;
                    if (i >= s.Length || s[i] != '=') throw new FormatException("an inline table holds no key and value");
                    i++;
                    Value(s, ref i);
                    SkipSpaces(s, ref i);
                    if (i < s.Length && s[i] == ',') i++;
                }
            }
            default:
            {
                int start = i;
                while (i < s.Length && s[i] is not (' ' or '\t' or '\r' or '\n' or ',' or ']' or '}' or '#')) i++;
                if (i == start) throw new FormatException("a value is missing");
                return new Bare(s[start..i]);
            }
        }
    }

    /// <summary>A basic or literal string on one line.</summary>
    private static string Text(string s, ref int i)
    {
        char quote = s[i];
        if (i + 2 < s.Length && s[i + 1] == quote && s[i + 2] == quote) throw new FormatException("multi-line strings are not read");
        i++;
        var text = new StringBuilder();
        while (true)
        {
            if (i >= s.Length || s[i] == '\n') throw new FormatException("a string is not closed");
            char c = s[i++];
            if (c == quote) return text.ToString();
            if (c != '\\' || quote == '\'') { text.Append(c); continue; }
            if (i >= s.Length) throw new FormatException("a string is not closed");
            char escape = s[i++];
            switch (escape)
            {
                case 'b': text.Append('\b'); break;
                case 't': text.Append('\t'); break;
                case 'n': text.Append('\n'); break;
                case 'f': text.Append('\f'); break;
                case 'r': text.Append('\r'); break;
                case 'e': text.Append('\u001b'); break;
                case '"': text.Append('"'); break;
                case '\\': text.Append('\\'); break;
                case 'u' or 'U':
                {
                    int digits = escape == 'u' ? 4 : 8;
                    if (i + digits > s.Length || !int.TryParse(s.AsSpan(i, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code)
                        || code is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
                        throw new FormatException("a string holds an invalid escape");
                    text.Append(char.ConvertFromUtf32(code));
                    i += digits;
                    break;
                }
                default: throw new FormatException("a string holds an invalid escape");
            }
        }
    }

    /// <summary>
    /// Every insert whose plugin is in the catalogue only through a yabridge
    /// wrapper: one whose Windows module is in none of the folders the
    /// Windows plugin host searches, is not a 64-bit module, or is gone.
    /// Such an insert loses its plugin when the wrapper goes. An insert
    /// whose plugin is not in the catalogue at all is not listed.
    /// </summary>
    /// <param name="find">The catalogue entry for a format and plugin id.</param>
    /// <param name="directories">The Windows plugin folders, resolved; the daemon's own when null.</param>
    public static IReadOnlyList<BridgeOnlyInsert> BridgeOnlyInserts(IEnumerable<ChainInsert> inserts,
        Func<string, string, PluginInfo?> find, IReadOnlyList<string>? directories = null)
    {
        directories ??= WindowsPluginHost.Directories();
        var found = new List<BridgeOnlyInsert>();
        foreach ((string chain, string chainName, InsertDefinition insert) in inserts)
        {
            if (insert.Kind is not ("clap" or "vst3") || find(insert.Kind, insert.Plugin) is not { WindowsHostOnly: false, Path: { Length: > 0 } bundle } plugin)
                continue;
            string? module = null, reason;
            try
            {
                if (WindowsPluginHost.ModuleLink(insert.Kind, bundle) is not { } link)
                {
                    if (insert.Kind != "vst3" || !WindowsPluginHost.Has32BitModule(bundle)) continue;
                    reason = "not-64-bit";
                }
                else
                {
                    string real = WindowsPluginWrappers.Canonical(link);
                    module = real;
                    reason = !File.Exists(real) ? "module-missing"
                        : !WindowsPluginHost.IsWindows64(real) ? "not-64-bit"
                        : directories.Any(folder => WindowsPluginWrappers.Under(real, folder)) ? null
                        : "outside-windows-folders";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                reason = "module-missing";
            }
            if (reason is not null)
                found.Add(new(chain, chainName, insert.Id, insert.Kind, insert.Plugin, insert.Label ?? plugin.Name, bundle, module, reason));
        }
        return found;
    }

    /// <summary>Every insert whose plugin is a Windows one, through a yabridge wrapper or a Windows plugin folder: those that need Wine to start.</summary>
    public static IReadOnlyList<WindowsInsert> WindowsInserts(IEnumerable<ChainInsert> inserts, Func<string, string, PluginInfo?> find)
    {
        var found = new List<WindowsInsert>();
        foreach ((string chain, string chainName, InsertDefinition insert) in inserts)
        {
            if (insert.Kind is not ("clap" or "vst3") || find(insert.Kind, insert.Plugin) is not { Path: { Length: > 0 } bundle } plugin)
                continue;
            bool windows;
            try
            {
                windows = plugin.WindowsHostOnly || WindowsPluginHost.ModuleLink(insert.Kind, bundle) is not null
                    || insert.Kind == "vst3" && WindowsPluginHost.Has32BitModule(bundle);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { windows = false; }
            if (windows) found.Add(new(chain, chainName, insert.Id, insert.Label ?? plugin.Name));
        }
        return found;
    }
}
