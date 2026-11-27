using System.Buffers.Binary;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The Windows plugin host built into OpenXLR, as the daemon decides about
/// it: whether this build has one, which inserts may run on it, and what the
/// native helper is started with when one does.
///
/// A Windows plugin reaches the catalogue in one of two ways. Through the
/// wrapper yabridge made for it, the insert runs on the default host unless
/// it is pinned to one; on this host, the Windows module is read back from
/// the wrapper, which links to it. Or straight from a Windows
/// plugin folder (<see cref="Directories"/>), described by the helper's
/// <c>wine</c> backend; such an entry is <see cref="PluginInfo.WindowsHostOnly"/>
/// and runs only here. Either way the helper is started with its <c>wine</c>
/// backend and the module. The class id and the parameter ids are the
/// plugin's own on both paths, so an insert keeps its values on either host
/// and finds its plugin through whichever path is there.
///
/// The catalogue holds a plugin once. Where a wrapper and a Windows plugin
/// folder both reach it, the wrapper's entry is kept, because it can run on
/// either host. The scan of the folders does not load a module a wrapper
/// of the catalogue links to.
///
/// An insert of a plugin with a wrapper either pins a host, with
/// <see cref="OpenXlr"/> or <see cref="Yabridge"/> saved as its
/// <c>windowsHost</c>, or follows the default, with nothing saved. The
/// default is <see cref="Default"/>: <c>windowsHostDefault</c> in
/// <c>daemon.json</c> when the user chose one, else <see cref="BuiltInDefault"/>.
/// A plugin on <see cref="Held"/> runs through yabridge while its insert
/// follows the default.
///
/// Nothing here starts a process or loads plugin code. Every answer comes
/// from names, links and the first bytes of the module.
/// </summary>
internal static class WindowsPluginHost
{
    /// <summary>An insert's <c>windowsHost</c> when it is pinned to yabridge, and the default's value for yabridge.</summary>
    internal const string Yabridge = "yabridge";

    /// <summary>An insert's <c>windowsHost</c> when it is pinned to this host, and the default's value for it.</summary>
    internal const string OpenXlr = "openxlr";

    /// <summary>The <c>setInsertWindowsHost</c> value that clears a pin, so the insert follows the default again. Never saved.</summary>
    internal const string FollowDefault = "default";

    /// <summary>
    /// The host an insert that follows the default runs on when
    /// <c>daemon.json</c> names none. Moving every such insert to this host
    /// is a change of this line alone.
    /// </summary>
    internal const string BuiltInDefault = OpenXlr;

    /// <summary>Whether a value names one of the two hosts.</summary>
    internal static bool IsHost(string? value) => value is Yabridge or OpenXlr;

    /// <summary>The default a saved <c>windowsHostDefault</c> gives: the saved host, or <see cref="BuiltInDefault"/> for none or one this build does not know.</summary>
    internal static string DefaultFrom(string? saved) => IsHost(saved) ? saved! : BuiltInDefault;

    private static volatile string _default = BuiltInDefault;

    /// <summary>
    /// The host an insert that follows the default runs on in this daemon,
    /// set from <c>daemon.json</c> when the daemon starts and by
    /// <c>setWindowsHostDefault</c>. A build without this host runs every
    /// such insert through yabridge, whatever it says.
    /// </summary>
    internal static string Default
    {
        get => _default;
        set => _default = DefaultFrom(value);
    }

    /// <summary>A plugin that runs through yabridge while its insert follows the default, and why, in a sentence.</summary>
    internal sealed record HeldPlugin(string Kind, string Plugin, string Name, string Reason);

    /// <summary>
    /// The plugins this release holds on yabridge while their inserts follow
    /// the default, by format and id, the way
    /// <see cref="NativeEditorPolicy.Curated"/> ships editor rules. A plugin
    /// that regresses on this host is answered by a release that adds its id
    /// here. An insert that pins this host still runs on it.
    /// </summary>
    internal static IReadOnlyList<HeldPlugin> Held { get; } = [];

    /// <summary>The entry that holds an insert's plugin on yabridge, or null.</summary>
    /// <param name="held">The list, for a test; <see cref="Held"/> when null.</param>
    internal static HeldPlugin? HeldEntry(InsertDefinition insert, IReadOnlyList<HeldPlugin>? held = null)
        => (held ?? Held).FirstOrDefault(h => h.Kind == insert.Kind && h.Plugin == insert.Plugin);

    /// <summary>
    /// Whether the chain builder tries an insert on this host, as far as
    /// names and settings decide: it is pinned here, or it follows a
    /// default that means this host and its plugin is a Windows plugin the
    /// catalogue reaches through a yabridge wrapper and the list does not
    /// hold. Whether the module can run here is decided when the chain is
    /// built. This is what a change of the default rebuilds a chain for.
    /// </summary>
    /// <param name="defaultHost">The default, for a test or a change of it; <see cref="Default"/> when null.</param>
    /// <param name="held">The list, for a test; <see cref="Held"/> when null.</param>
    internal static bool Wanted(InsertDefinition insert, PluginInfo? plugin, string? defaultHost = null, IReadOnlyList<HeldPlugin>? held = null)
        => insert.OnWindowsHost
            || (insert.WindowsHost is null && insert.Kind is "clap" or "vst3"
                && plugin is { WindowsHostOnly: false } && WineSession.Bridged(plugin.Path)
                && (defaultHost ?? Default) == OpenXlr && HeldEntry(insert, held) is null);

    /// <summary>
    /// Whether yabridge is there to run a wrapper: the companion package
    /// OpenXLR finds, or a system yabridge whose Windows program is on PATH.
    /// </summary>
    internal static bool YabridgeInstalled()
        => ManagedYabridge.Discover() is not null || PluginInstaller.OnPath("yabridge-host.exe") is not null;

    /// <summary>Names the Windows program when it is not beside the native helper. The helper reads the same variable.</summary>
    internal const string ProgramVariable = "OPENXLR_WINHOST";

    /// <summary>Names the Wine the helper starts, <c>wine</c> on PATH otherwise.</summary>
    internal const string WineVariable = "OPENXLR_WINE";

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
    /// the plugin is neither a Windows plugin bridged through yabridge nor one
    /// found in a Windows plugin folder, or its module is gone or not 64-bit.
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
            if (plugin.WindowsHostOnly) return Direct(plugin, out reason);
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

    /// <summary>The route of a plugin found in a Windows plugin folder: its module, straight from the bundle the scan named.</summary>
    private static Route? Direct(PluginInfo plugin, out string? reason)
    {
        reason = null;
        if (plugin.Path is not { Length: > 0 } bundle || Module(plugin.Kind, bundle) is not { } found)
        {
            reason = plugin.Kind == "vst3" && plugin.Path is { Length: > 0 } narrow && Only32Bit(narrow)
                ? $"'{plugin.Name}' has no 64-bit Windows module, the only kind the Windows plugin host runs"
                : $"the Windows plugin '{plugin.Name}' is missing: {plugin.Path}";
            return null;
        }
        string module = WindowsPluginWrappers.Canonical(found);
        if (!IsWindows64(module))
        {
            reason = $"'{plugin.Name}' is not a 64-bit Windows module, the only kind the Windows plugin host runs";
            return null;
        }
        return new(plugin.Kind, module, PrefixOf(module));
    }

    /// <summary>
    /// The Windows module of a bundle in a Windows plugin folder, or null
    /// when there is none. A CLAP file is its own module, and so is a VST3
    /// file. A VST3 directory holds its 64-bit module under
    /// <c>Contents/x86_64-win</c>: the one named after the bundle, or else
    /// the first <c>.vst3</c> there, which is the one the helper loads.
    /// </summary>
    internal static string? Module(string kind, string bundle)
    {
        if (kind is not ("clap" or "vst3")) return null;
        string home = Path.TrimEndingDirectorySeparator(bundle);
        if (File.Exists(home)) return home;
        if (kind != "vst3" || !Directory.Exists(home)) return null;
        string architecture = Path.Combine(home, "Contents", "x86_64-win");
        string named = Path.Combine(architecture, Path.GetFileNameWithoutExtension(home) + ".vst3");
        if (File.Exists(named)) return named;
        if (!Directory.Exists(architecture)) return null;
        return Directory.EnumerateFiles(architecture).Order(StringComparer.Ordinal)
            .FirstOrDefault(f => f.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a VST3 directory in a Windows plugin folder holds a 32-bit module and no 64-bit one.</summary>
    internal static bool Only32Bit(string bundle)
    {
        string home = Path.TrimEndingDirectorySeparator(bundle);
        return Directory.Exists(home) && Module("vst3", home) is null
            && new[] { "x86-win", "i386-win" }.Any(architecture => Directory.Exists(Path.Combine(home, "Contents", architecture))
                && Directory.EnumerateFiles(Path.Combine(home, "Contents", architecture))
                    .Any(f => f.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Where "Install file" puts a Windows plugin: a folder per format and
    /// plugin under <c>$XDG_DATA_HOME/openxlr/windows-plugins</c>, beside
    /// the companion's wrappers.
    /// </summary>
    internal static string ImportDirectory => Path.Combine(Path.GetDirectoryName(ManagedYabridge.PluginHome)!, "windows-plugins");

    /// <summary>
    /// Where a Windows installer puts a 64-bit VST3 and CLAP plugin, inside
    /// the Wine prefix the daemon sees: <c>WINEPREFIX</c>, or <c>~/.wine</c>
    /// as Wine itself picks. Then <see cref="ImportDirectory"/>, so a plugin
    /// installed from a file reaches the catalogue with no yabridge wrapper.
    /// The 32-bit <c>Program Files (x86)</c> folders are left out, since
    /// this host runs only 64-bit modules.
    /// </summary>
    /// <param name="prefix">The prefix, for a test; the daemon's own when null.</param>
    internal static IReadOnlyList<string> StandardDirectories(string? prefix = null)
    {
        string common = Path.Combine(prefix ?? PluginInstaller.DefaultWinePrefix(), "drive_c", "Program Files", "Common Files");
        return [Path.Combine(common, "VST3"), Path.Combine(common, "CLAP"), ImportDirectory];
    }

    /// <summary>
    /// Every folder whose Windows plugins are scanned for this host: the
    /// standard ones, then those added with <c>addPluginSearchPath</c> under
    /// the kind <c>windows</c>. Each is searched for both formats.
    /// </summary>
    internal static IReadOnlyList<string> Directories(string? prefix = null)
        => PluginSearchPaths.Include("windows", StandardDirectories(prefix));

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
    internal static bool Has32BitModule(string bundle)
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
