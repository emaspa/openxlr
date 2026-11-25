using System.Buffers.Binary;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The Windows plugin host built into OpenXLR, as the daemon decides about
/// it: whether this build has one, which inserts may run on it, and what the
/// native helper is started with when one does.
///
/// A Windows plugin is in the catalogue once, through the wrapper yabridge
/// made for it, and yabridge runs it unless the insert says otherwise. This
/// host has no catalogue entry of its own. For an insert that chooses it, the
/// Windows module is read back from the wrapper, which links to it, and the
/// helper is started with its <c>wine</c> backend and that module. The class
/// id and the parameter ids are the ones the wrapper gave the catalogue, so
/// the insert keeps its values on either host.
///
/// Nothing here starts a process or loads plugin code. Every answer comes
/// from names, links and the first bytes of the module.
/// </summary>
internal static class WindowsPluginHost
{
    /// <summary>The <c>setInsertWindowsHost</c> value for yabridge. It is the default, so an insert never holds it.</summary>
    internal const string Yabridge = "yabridge";

    /// <summary>An insert's <c>windowsHost</c> when it runs on this host.</summary>
    internal const string OpenXlr = "openxlr";

    /// <summary>Names the Windows program when it is not beside the native helper. The helper reads the same variable.</summary>
    internal const string ProgramVariable = "OPENXLR_WINHOST";

    /// <summary>Names the Wine the helper starts, <c>wine</c> on PATH otherwise.</summary>
    internal const string WineVariable = "OPENXLR_WINE";

    /// <summary>What a client is told about the editor of an insert that runs here.</summary>
    internal const string NoEditor =
        "This insert runs on the Windows plugin host, which has no plugin editor yet. Move it back to yabridge to open the plugin's own editor.";

    /// <summary>What the helper's startup error says when Wine was still silent at the daemon's deadline.</summary>
    internal const string SilentStart =
        "Wine did not answer in time. It may be creating or updating its prefix; turn the insert off and on to try again";

    /// <summary>The Windows program the helper starts under Wine: where <see cref="ProgramVariable"/> says, or beside the helper.</summary>
    internal static string Executable
        => Environment.GetEnvironmentVariable(ProgramVariable) is { Length: > 0 } named
            ? named : Path.Combine(AppContext.BaseDirectory, "openxlr-winhost.exe");

    /// <summary>
    /// Whether this build has the host: the native helper and the Windows
    /// program it starts. The program exists only in a build made with
    /// <c>WINHOST=1</c>, which is also what gives the helper its
    /// <c>wine</c> backend.
    /// </summary>
    internal static bool Installed => NativePluginHost.HostInstalled && File.Exists(Executable);

    /// <summary>
    /// Where a plugin runs on this host: its format, the Windows module by
    /// its real Linux path, and the Wine prefix that module lives in, or null
    /// for a module outside every prefix.
    /// </summary>
    internal sealed record Route(string Kind, string Module, string? Prefix);

    /// <summary>
    /// The route for a plugin of the catalogue, or null with the reason this
    /// host cannot run it, as the end of a sentence: the build has no host,
    /// the plugin is not a Windows plugin bridged through yabridge, or its
    /// module is gone or not 64-bit.
    /// </summary>
    /// <param name="installed">The host's presence, for a test; the build's own when null.</param>
    internal static Route? Resolve(PluginInfo? plugin, out string? reason, bool? installed = null)
    {
        reason = null;
        if (!(installed ?? Installed))
        {
            reason = "this build has no Windows plugin host, openxlr-winhost.exe is not beside the native plugin host";
            return null;
        }
        if (plugin is null)
        {
            reason = "the plugin is not installed";
            return null;
        }
        try
        {
            if (plugin.Path is not { Length: > 0 } bundle || ModuleLink(plugin.Kind, bundle) is not { } link)
            {
                reason = plugin.Kind == "vst3" && plugin.Path is { Length: > 0 } narrow && Has32BitModule(narrow)
                    ? $"'{plugin.Name}' has no 64-bit Windows module, the only kind the Windows plugin host runs"
                    : $"'{plugin.Name}' is not a Windows plugin bridged through yabridge";
                return null;
            }
            string module = WindowsPluginWrappers.Canonical(link);
            if (!File.Exists(module))
            {
                reason = $"the Windows plugin behind '{plugin.Name}' is missing: {module}";
                return null;
            }
            if (!IsWindows64(module))
            {
                reason = $"'{plugin.Name}' is not a 64-bit Windows module, the only kind the Windows plugin host runs";
                return null;
            }
            return new(plugin.Kind, module, PrefixOf(module));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            reason = $"the Windows plugin behind '{plugin.Name}' cannot be read: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// The entry of a yabridge wrapper that names the Windows module, or null
    /// when the bundle is not such a wrapper. Both layouts are yabridgectl's,
    /// whether the companion made the wrapper under OpenXLR's own data
    /// directory or a system yabridge made it under <c>~/.vst3/yabridge</c>
    /// or <c>~/.clap/yabridge</c>.
    ///
    /// VST3: the bundle is a directory, and
    /// <c>Contents/x86_64-win/NAME.vst3</c> in it is a link to the module.
    /// It has to be a link, since a native bundle built for several systems
    /// holds a real file there and runs without Wine.
    ///
    /// CLAP: the bundle is yabridge's own library, and <c>NAME.clap-win</c>
    /// beside it is the module or a link to it. Nothing else has that name.
    /// </summary>
    internal static string? ModuleLink(string kind, string bundle)
    {
        switch (kind)
        {
            case "vst3":
            {
                string home = Path.TrimEndingDirectorySeparator(bundle);
                string link = Path.Combine(home, "Contents", "x86_64-win", Path.GetFileNameWithoutExtension(home) + ".vst3");
                return new FileInfo(link).LinkTarget is not null ? link : null;
            }
            case "clap":
            {
                string link = Path.ChangeExtension(bundle, ".clap-win");
                return new FileInfo(link).LinkTarget is not null || File.Exists(link) ? link : null;
            }
            default:
                return null;
        }
    }

    /// <summary>Whether a VST3 wrapper links a 32-bit module, under either name the two code bases give that directory.</summary>
    private static bool Has32BitModule(string bundle)
    {
        string home = Path.TrimEndingDirectorySeparator(bundle);
        string module = Path.GetFileNameWithoutExtension(home) + ".vst3";
        return new[] { "x86-win", "i386-win" }.Any(architecture
            => new FileInfo(Path.Combine(home, "Contents", architecture, module)).LinkTarget is not null);
    }

    /// <summary>
    /// The Wine prefix a Windows module lives in, found the way yabridge
    /// finds it: the nearest directory above the module that holds a
    /// <c>dosdevices</c> directory. Null for a module outside every prefix,
    /// which Wine then runs in its default one.
    /// </summary>
    internal static string? PrefixOf(string module)
    {
        for (string? directory = Path.GetDirectoryName(module); directory is not null; directory = Path.GetDirectoryName(directory))
            if (Directory.Exists(Path.Combine(directory, "dosdevices"))) return directory;
        return null;
    }

    /// <summary>Whether a file is a Windows module for x86-64: a PE image whose machine field says so.</summary>
    internal static bool IsWindows64(string path)
    {
        using FileStream file = File.OpenRead(path);
        Span<byte> dos = stackalloc byte[64];
        if (file.ReadAtLeast(dos, dos.Length, throwOnEndOfStream: false) != dos.Length || dos[0] != 'M' || dos[1] != 'Z')
            return false;
        file.Position = BinaryPrimitives.ReadUInt32LittleEndian(dos[0x3c..]);
        Span<byte> pe = stackalloc byte[6];
        return file.ReadAtLeast(pe, pe.Length, throwOnEndOfStream: false) == pe.Length
            && pe[..4].SequenceEqual("PE\0\0"u8) && BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) == 0x8664;
    }

    /// <summary>The helper's arguments up to the node for a plugin on this host: the <c>wine</c> backend, the format, the module and the plugin's id.</summary>
    internal static IReadOnlyList<string> Arguments(Route route, string plugin) => ["wine", route.Kind, route.Module, plugin];

    /// <summary>
    /// What the helper's environment gets on top of what every plugin helper
    /// gets, which already keeps Wine's USB driver out of the prefix.
    ///
    /// The prefix follows yabridge's rule, so a plugin runs in the same one
    /// on both hosts: a <c>WINEPREFIX</c> the daemon was started with stands,
    /// else the prefix the module lives in, else nothing, and Wine uses its
    /// default. <c>WINELOADER</c>, which is how yabridge is told to start
    /// another Wine, is handed to the helper as <see cref="WineVariable"/>.
    /// </summary>
    /// <param name="inherited">A variable of the environment the helper starts from, or null.</param>
    internal static Dictionary<string, string> HelperEnvironment(Route route, Func<string, string?> inherited)
    {
        var overlay = new Dictionary<string, string>(StringComparer.Ordinal);
        if (route.Prefix is not null && string.IsNullOrEmpty(inherited("WINEPREFIX"))) overlay["WINEPREFIX"] = route.Prefix;
        if (inherited("WINELOADER") is { Length: > 0 } loader && string.IsNullOrEmpty(inherited(WineVariable)))
            overlay[WineVariable] = loader;
        return overlay;
    }
}
