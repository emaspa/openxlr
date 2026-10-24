using OpenXLR.UI.Localization;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// <summary>On by default in the Flatpak, whose UI owns the daemon: closing it would stop audio.</summary>
    public bool MinimizeToTray { get; init; } = FlatpakEnvironment.IsFlatpak;
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
    /// <summary>
    /// The order of the main window's five sections, by tile name. Missing
    /// sections follow in their usual order and unknown names are ignored.
    /// A window preference only: audio and profiles do not depend on it.
    /// </summary>
    public IReadOnlyList<string> SectionOrder { get; init; } = [];
    /// <summary>
    /// The arrangement the user made under a skin that sets its own layout,
    /// one entry per skin id and layout version: which section the user put
    /// in which of the skin's places, and which of the sections the skin
    /// hides the user brought back. A skin with a layout starts from its own
    /// placement and these moves go on top; they never touch
    /// <see cref="SectionOrder"/>, which stays the order for skins without
    /// one. At most <see cref="MaxSkinArrangements"/> are kept, the most
    /// recently arranged last.
    /// </summary>
    public IReadOnlyList<SkinArrangement> SkinArrangements { get; init; } = [];

    /// <summary>How many skins' arrangements <see cref="SkinArrangements"/> keeps.</summary>
    public const int MaxSkinArrangements = 32;

    /// <summary>
    /// The mixes the user picked for the banks of a skin whose submixer is
    /// drawn by mix, one entry per skin id and layout version, the first
    /// bank's mix first. A skin's <c>selectionDefaults</c> apply only while
    /// it has no entry here. Choosing a bank's mix is a window preference:
    /// it never changes the mixer's order or sends a command. At most
    /// <see cref="MaxSkinArrangements"/> are kept, the most recent last.
    /// </summary>
    public IReadOnlyList<SkinBankSelection> SkinBankSelections { get; init; } = [];

    /// <summary>
    /// The skin layouts, by skin id and layout version, whose collapsed
    /// sections have been folded once. A skin's <c>collapsed</c> is the
    /// start state the first time the window wears it; after that the
    /// sections stay as the user leaves them in <see cref="CollapsedSections"/>.
    /// At most <see cref="MaxSkinArrangements"/> are kept, the most recent last.
    /// </summary>
    public IReadOnlyList<SkinLayoutKey> SkinLayoutsSeen { get; init; } = [];

    /// <summary>
    /// Where the user last put the submixer: true after docking it, false
    /// after popping it out, null while the user has done neither. Only the
    /// user's own Pop out and Dock write it, never a skin, and while it holds
    /// a choice no skin's pop-out default applies.
    /// </summary>
    [JsonConverter(typeof(LocalMixerDockedConverter))]
    public bool? MixerDocked { get; init; }

    /// <summary>
    /// The skin layouts, by skin id and layout version, whose pop-out default
    /// already opened the submixer in its own window. A skin's
    /// <c>"detached"</c> default opens it once, so a reload or a colour change
    /// never opens it again. At most <see cref="MaxSkinArrangements"/> are
    /// kept, the most recent last.
    /// </summary>
    public IReadOnlyList<SkinLayoutKey> SkinPopOutsApplied { get; init; } = [];

    /// <summary>
    /// How the submixer is drawn: absent or <c>full</c> for every strip,
    /// <c>compact</c> for one chosen channel, <c>mini</c> for one channel's
    /// send into one chosen mix with the other sections out of the way.
    /// </summary>
    public string? MixerView { get; init; }
    /// <summary>The channel the compact and mini views show, by id.</summary>
    public string? CompactChannel { get; init; }
    /// <summary>The mix the mini view shows, by id.</summary>
    public string? CompactMix { get; init; }
    /// <summary>
    /// Show hidden in the submixer's header: true draws the channels and
    /// mixes hidden in the layout editor in their places, faded. A window
    /// preference only; the layout and the profiles know nothing about it.
    /// </summary>
    public bool ShowHiddenItems { get; init; }
    /// <summary>
    /// The appearance the window wears, by skin id; <c>default</c> is
    /// Material. Null or empty is no choice made, which wears Broadcast
    /// (<see cref="Skinning.SkinPackage.StartingId"/>) without writing it
    /// here. It lives here and nowhere else: the mixer
    /// layout, the daemon's preferences and the audio profiles know nothing
    /// about it, so changing appearance cannot disturb what is playing.
    /// </summary>
    public string? Skin { get; init; }

    /// <summary>
    /// Material's mode: <c>system</c> follows the desktop's light or dark
    /// preference, <c>light</c> and <c>dark</c> hold one. Other skins carry
    /// their own colours and ignore it. Like the skin, it lives here alone.
    /// </summary>
    [JsonConverter(typeof(LocalAppearanceModeConverter))]
    public string AppearanceMode { get; init; } = AppearanceModes.System;

    /// <summary>
    /// Touch sizing for the main mixer's controls; false is Standard. Like the
    /// skin, it lives here alone.
    /// </summary>
    [JsonConverter(typeof(LocalTouchControlsConverter))]
    public bool TouchControls { get; init; }

    /// <summary>
    /// The window's language, by catalogue tag such as <c>en</c>; null
    /// follows the desktop's message language. Read once at startup, so a
    /// change applies the next time the window starts. Like the skin, it
    /// lives here alone.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Keys this version does not know, such as a setting written by a newer
    /// window or by the terminal mixer. They are written back unchanged, so a
    /// save from here never erases them.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // A key given twice has no single meaning; the file is the user's to settle.
        AllowDuplicateProperties = false,
    };

    public static string ConfigDir => OpenXlrPaths.ConfigDir;

    private static string FilePath => Path.Combine(ConfigDir, "ui.json");

    /// <summary>The saved preferences, or the defaults when the file is missing or cannot be read.</summary>
    public static UiSettings Load() => Load(out _);

    /// <summary>
    /// The saved preferences, or the defaults with the reason the file could
    /// not be read. A file that cannot be read is never written over: see
    /// <see cref="Save"/>.
    /// </summary>
    public static UiSettings Load(out string? problem)
    {
        UiSettings? settings = PreferenceFile.Read<UiSettings>(FilePath, Json, out problem);
        if (settings is null) return new UiSettings();
        // "collapsedSections": null is valid JSON for a list the window
        // enumerates, so it reads as an empty list rather than failing later.
        return settings with
        {
            CollapsedSections = settings.CollapsedSections ?? [],
            SectionOrder = settings.SectionOrder ?? [],
            SkinArrangements = NormalizeSkinArrangements(settings.SkinArrangements),
            SkinLayoutsSeen = NormalizeSeen(settings.SkinLayoutsSeen),
            SkinBankSelections = NormalizeBankSelections(settings.SkinBankSelections),
            SkinPopOutsApplied = NormalizeSeen(settings.SkinPopOutsApplied),
            // The per-skin section orders of an unreleased build, which the
            // arrangements replaced. They are not read, and go at the next save.
            UnknownKeys = WithoutObsoleteKeys(settings.UnknownKeys),
        };
    }

    private static Dictionary<string, JsonElement>? WithoutObsoleteKeys(Dictionary<string, JsonElement>? keys)
    {
        if (keys is null || !keys.ContainsKey("skinSectionOrders")) return keys;
        var kept = new Dictionary<string, JsonElement>(keys);
        kept.Remove("skinSectionOrders");
        return kept.Count == 0 ? null : kept;
    }

    /// <summary>The user's arrangement for this skin and layout version, or null when there is none.</summary>
    public SkinArrangement? SkinArrangementFor(string skin, int version) =>
        SkinArrangements.LastOrDefault(e => e.Skin == skin && e.Version == version);

    /// <summary>
    /// These settings with the arrangement for one skin and layout version
    /// replaced, or removed when <paramref name="slots"/> and
    /// <paramref name="shown"/> are both null or empty. The entry moves to the
    /// end, and the oldest go once there are more than
    /// <see cref="MaxSkinArrangements"/>.
    /// </summary>
    public UiSettings WithSkinArrangement(string skin, int version,
        IReadOnlyDictionary<string, string>? slots, IReadOnlyList<string>? shown)
    {
        List<SkinArrangement> entries = [.. SkinArrangements.Where(e => e.Skin != skin || e.Version != version)];
        if (slots is { Count: > 0 } || shown is { Count: > 0 })
            entries.Add(new SkinArrangement
            {
                Skin = skin,
                Version = version,
                Slots = new Dictionary<string, string>(slots ?? new Dictionary<string, string>(), StringComparer.Ordinal),
                Shown = [.. shown ?? []],
            });
        return this with { SkinArrangements = NormalizeSkinArrangements(entries) };
    }

    /// <summary>The mixes the user picked for this skin's banks, or null when the user has picked none.</summary>
    public IReadOnlyList<string>? SkinBanksFor(string skin, int version) =>
        SkinBankSelections.LastOrDefault(e => e.Skin == skin && e.Version == version)?.Banks;

    /// <summary>
    /// These settings with the banks of one skin and layout version replaced,
    /// or forgotten when <paramref name="banks"/> is null or empty. The entry
    /// moves to the end, and the oldest go once there are more than
    /// <see cref="MaxSkinArrangements"/>.
    /// </summary>
    public UiSettings WithSkinBanks(string skin, int version, IReadOnlyList<string>? banks)
    {
        List<SkinBankSelection> entries = [.. SkinBankSelections.Where(e => e.Skin != skin || e.Version != version)];
        if (banks is { Count: > 0 }) entries.Add(new SkinBankSelection { Skin = skin, Version = version, Banks = [.. banks] });
        return this with { SkinBankSelections = NormalizeBankSelections(entries) };
    }

    /// <summary>
    /// The bank choices made safe to use: entries with no skin, repeats of
    /// one skin and version (the last wins), and ids that are not a mix id
    /// go; each entry keeps at most as many mixes as a skin may show banks.
    /// </summary>
    private static IReadOnlyList<SkinBankSelection> NormalizeBankSelections(IReadOnlyList<SkinBankSelection?>? entries)
    {
        if (entries is null) return [];
        var kept = new List<SkinBankSelection>();
        foreach (SkinBankSelection? entry in entries)
        {
            if (entry is not { Skin.Length: > 0 }) continue;
            kept.RemoveAll(e => e.Skin == entry.Skin && e.Version == entry.Version);
            string[] banks = [.. (entry.Banks ?? []).Where(Skinning.SkinLayoutReader.ValidMixId).Distinct(StringComparer.Ordinal)
                .Take(Skinning.SkinFormat.MaxVisibleBanks)];
            if (banks.Length > 0) kept.Add(entry with { Banks = banks });
        }
        return kept.Count <= MaxSkinArrangements ? kept : kept[^MaxSkinArrangements..];
    }

    /// <summary>True when the skin layout's collapsed sections were already folded once.</summary>
    public bool HasSeenSkinLayout(string skin, int version) =>
        SkinLayoutsSeen.Any(k => k.Skin == skin && k.Version == version);

    /// <summary>These settings with the skin layout recorded as seen, the most recent last.</summary>
    public UiSettings WithSkinLayoutSeen(string skin, int version) => this with
    {
        SkinLayoutsSeen = NormalizeSeen([.. SkinLayoutsSeen.Where(k => k.Skin != skin || k.Version != version),
            new SkinLayoutKey { Skin = skin, Version = version }]),
    };

    /// <summary>True when the skin layout's pop-out default already opened the submixer once.</summary>
    public bool HasAppliedSkinPopOut(string skin, int version) =>
        SkinPopOutsApplied.Any(k => k.Skin == skin && k.Version == version);

    /// <summary>These settings with the skin layout's pop-out default recorded as applied, the most recent last.</summary>
    public UiSettings WithSkinPopOutApplied(string skin, int version) => this with
    {
        SkinPopOutsApplied = NormalizeSeen([.. SkinPopOutsApplied.Where(k => k.Skin != skin || k.Version != version),
            new SkinLayoutKey { Skin = skin, Version = version }]),
    };

    /// <summary>
    /// These settings with what the user chose under one skin layout
    /// forgotten: the arrangement, the bank picks, the record that its
    /// pop-out default was applied, and the docking choice, which is the one
    /// thing that keeps any skin's pop-out default from applying. What the
    /// user chose under other skins stays.
    /// </summary>
    public UiSettings WithoutSkinChoices(string skin, int version) =>
        WithSkinArrangement(skin, version, null, null).WithSkinBanks(skin, version, null) with
        {
            SkinPopOutsApplied = [.. SkinPopOutsApplied.Where(k => k.Skin != skin || k.Version != version)],
            MixerDocked = null,
        };

    /// <summary>
    /// The per-skin arrangements made safe to use: null lists, entries and
    /// values, entries with no skin, and repeats of one skin and version (the
    /// last wins) go; the places and the shown sections are cut to the most a
    /// layout can have. Whether the places still exist in the skin's tree is
    /// for the window to decide, since only it has the tree.
    /// </summary>
    private static IReadOnlyList<SkinArrangement> NormalizeSkinArrangements(IReadOnlyList<SkinArrangement?>? entries)
    {
        if (entries is null) return [];
        var kept = new List<SkinArrangement>();
        foreach (SkinArrangement? entry in entries)
        {
            if (entry is not { Skin.Length: > 0 }) continue;
            kept.RemoveAll(e => e.Skin == entry.Skin && e.Version == entry.Version);
            var slots = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> slot in entry.Slots ?? new Dictionary<string, string>())
                if (slot.Value is not null && slots.Count < Skinning.SkinFormat.MaxLayoutNodes) slots[slot.Key] = slot.Value;
            kept.Add(entry with
            {
                Slots = slots,
                Shown = [.. (entry.Shown ?? []).OfType<string>().Distinct(StringComparer.Ordinal)
                    .Take(Skinning.SkinLayoutRegions.All.Count)],
            });
        }
        return kept.Count <= MaxSkinArrangements ? kept : kept[^MaxSkinArrangements..];
    }

    private static IReadOnlyList<SkinLayoutKey> NormalizeSeen(IReadOnlyList<SkinLayoutKey?>? keys)
    {
        if (keys is null) return [];
        var kept = new List<SkinLayoutKey>();
        foreach (SkinLayoutKey? key in keys)
        {
            if (key is not { Skin.Length: > 0 }) continue;
            kept.RemoveAll(k => k.Skin == key.Skin && k.Version == key.Version);
            kept.Add(key);
        }
        return kept.Count <= MaxSkinArrangements ? kept : kept[^MaxSkinArrangements..];
    }

    /// <summary>
    /// Write the preferences. Null when they were written; otherwise why not,
    /// in words for the window's status line. A ui.json that cannot be read is
    /// left as it is, so a hand edit with a typo can still be repaired.
    /// </summary>
    public string? Save() => PreferenceFile.Write(FilePath, this, Json);
}

/// <summary>
/// One skin's arrangement as the user made it in the window: the skin's id,
/// the layout version it was made under, the places the user moved a section
/// into, as place id to region id (<c>"main:0": "submixer"</c>, a stack's or
/// a dock's place by container id and index, a grid's by container id, row
/// and column as <c>"desktop:1,0"</c>), and the sections the skin hides that
/// the user showed. An entry naming a place or a region the skin's tree no
/// longer has is dropped whole.
/// </summary>
public sealed record SkinArrangement
{
    public string Skin { get; init; } = "";
    public int Version { get; init; }
    public IReadOnlyDictionary<string, string> Slots { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<string> Shown { get; init; } = [];
}

/// <summary>
/// The mixes the user picked for one skin's banks: the skin's id, the layout
/// version, and the mix ids in bank order. An id whose mix is gone is
/// skipped when the banks are filled, so the bank moves on to the next mix.
/// </summary>
public sealed record SkinBankSelection
{
    public string Skin { get; init; } = "";
    public int Version { get; init; }
    public IReadOnlyList<string> Banks { get; init; } = [];
}

/// <summary>A skin's layout by skin id and layout version.</summary>
public sealed record SkinLayoutKey
{
    public string Skin { get; init; } = "";
    public int Version { get; init; }
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

    /// <summary>Keys this version does not know, written back unchanged.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // A key given twice has no single meaning; the file is the user's to settle.
        AllowDuplicateProperties = false,
    };

    private static string FilePath => Path.Combine(UiSettings.ConfigDir, "daemon.json");

    /// <summary>The saved preferences, or unset ones when the file is missing or cannot be read.</summary>
    public static DaemonPrefs Load() => Load(out _);

    /// <summary>The saved preferences, or unset ones with the reason the file could not be read.</summary>
    public static DaemonPrefs Load(out string? problem)
        => PreferenceFile.Read<DaemonPrefs>(FilePath, Json, out problem) ?? new DaemonPrefs();

    /// <summary>Write the preferences; null when written, otherwise why not. An unreadable file is left as it is.</summary>
    public string? Save() => PreferenceFile.Write(FilePath, this, Json);
}

/// <summary>
/// Reading and writing the window's preference files. Every save is a load,
/// a change and a write, so a write after a failed load would put the
/// defaults over whatever the file held. The write therefore checks the file
/// first and leaves one it cannot read alone.
/// </summary>
internal static class PreferenceFile
{
    /// <summary>The file's contents; null with no problem when it is missing, null with the reason when it cannot be read.</summary>
    internal static T? Read<T>(string path, JsonSerializerOptions json, out string? problem) where T : class
    {
        problem = null;
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(RegularFile.ReadText(path, OpenXlrPaths.MaxPreferenceBytes), json)
                ?? throw new JsonException(Localizer.Text("PreferenceFileHoldsNull"));
        }
        catch (Exception ex)
        {
            // Whatever the reason, a file that cannot be read must not stop the window.
            problem = Localizer.Format("PreferenceFileUnreadable", Path.GetFileName(path), ex.Message);
            return null;
        }
    }

    /// <summary>Write the value unless the file on disk cannot be read; null when written, otherwise why not.</summary>
    internal static string? Write<T>(string path, T value, JsonSerializerOptions json) where T : class
    {
        Read<T>(path, json, out string? problem);
        if (problem is not null) return problem;
        try
        {
            OpenXlrPaths.WriteAtomicJson(path, value, json, OpenXlrPaths.MaxPreferenceBytes);
            return null;
        }
        catch (Exception ex)
        {
            return Localizer.Format("PreferenceFileNotSaved", Path.GetFileName(path), ex.Message);
        }
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
    /// Repair startup integration when daemon-at-login is on. Start only
    /// after a successful repair, leaving a refused foreign unit untouched.
    /// </summary>
    public static void RepairDaemonUnit()
    {
        try
        {
            if (!HasStaleUserUnit()) return;
            if (SetDaemonAtLogin(true)) Systemctl("start", UnitName);
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

    private static bool IsSymbolicLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>The autostart entry this window writes for a given executable.</summary>
    internal static string AutostartEntry(string executable) => AutostartEntry("Exec=" + DesktopExec(executable), finalNewline: true);

    /// <summary>
    /// The entries earlier releases wrote for the executable, oldest first:
    /// the bare path until v0.1.22, the quoted path until v0.1.32, both
    /// without the final newline v0.1.33 added.
    /// </summary>
    internal static IEnumerable<string> LegacyAutostartEntries(string executable) =>
        [AutostartEntry("Exec=" + executable, finalNewline: false), AutostartEntry("Exec=" + DesktopExec(executable), finalNewline: false)];

    private static string AutostartEntry(string execLine, bool finalNewline) => $"""
        [Desktop Entry]
        Type=Application
        Name=OpenXLR
        Comment=OpenXLR mixer window
        {execLine}
        Icon=openxlr
        Terminal=false
        X-GNOME-Autostart-enabled=true
        """ + (finalNewline ? "\n" : "");

    /// <summary>
    /// Apply the daemon-at-login preference. False when nothing could be
    /// applied, so Options reports it instead of saving a preference the
    /// system does not have.
    /// </summary>
    public static bool SetDaemonAtLogin(bool enabled) => !FlatpakEnvironment.IsFlatpak && SetDaemonAtLogin(enabled, PackagedUnit);

    internal static bool SetDaemonAtLogin(bool enabled, string? packagedUnit)
    {
        try { return ApplyDaemonAtLogin(enabled, packagedUnit); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool ApplyDaemonAtLogin(bool enabled, string? packagedUnit)
    {
        string unit = StartupReceipt.StartupPaths[1];
        string wants = StartupReceipt.StartupPaths[2];
        InstallationReceipt receipt = StartupReceipt.PrepareRemoval(packagedUnit, includeWindow: false);
        if (!enabled)
        {
            // Check both paths first: a refused unit then leaves its link in
            // place too, instead of half of the integration.
            foreach (string path in new[] { wants, unit })
                if (StartupReceipt.RemovalProblem(receipt, path) is { } problem) throw new IOException(problem);
            StartupReceipt.Remove(wants, packagedUnit);
            StartupReceipt.Remove(unit);
            Systemctl("daemon-reload");
            return true;
        }
        if (packagedUnit is not null)
        {
            // Any copy in ~/.config would shadow the packaged unit.
            StartupReceipt.Remove(unit);
            // Removing our copy can restore an older user unit. systemd
            // would still load it ahead of the packaged one, even if the
            // enable link points at the package. Ask the user to move it.
            if (EntryExists(unit))
            {
                StartupReceipt.Remove(wants, unit);
                Systemctl("daemon-reload");
                return false;
            }
            // systemctl enable left the link pointing at the copy just
            // removed. Take it down so the packaged unit gets its own.
            if (StartupReceipt.LinksTo(wants, unit)) StartupReceipt.Remove(wants, unit);
        }
        else
        {
            // No binary anywhere we know of: a unit would only loop on 203/EXEC.
            if (DaemonBinary is not { } daemon) return false;
            // A foreign link is refused before the unit is written, so the
            // refusal leaves nothing behind.
            if (!StartupReceipt.CanEnableLink(wants, unit)) return false;
            StartupReceipt.Write(unit, SourceUnit(daemon));
        }
        StartupReceipt.EnableLink(wants, packagedUnit ?? unit);
        return Systemctl("daemon-reload");
    }

    /// <summary>
    /// Apply the window-at-login preference and record which executable the
    /// entry now names, so a later repair can tell a moved installation from
    /// an entry the user removed. False when nothing was applied.
    /// </summary>
    public static bool SetWindowAtLogin(bool enabled)
    {
        if (FlatpakEnvironment.IsFlatpak) return false;
        if (!SetWindowAtLogin(enabled, UiBinary)) return false;
        RememberAutostartExecutable(enabled ? UiBinary : null);
        return true;
    }

    internal static bool SetWindowAtLogin(bool enabled, string executable)
    {
        try
        {
            if (!enabled)
            {
                StartupReceipt.Remove(AutostartPath);
                // A restored foreign entry can still start the window. Do not
                // claim it was disabled or erase the saved choice in that case.
                return !EntryExists(AutostartPath);
            }
            if (!File.Exists(executable)) return false;
            StartupReceipt.Write(AutostartPath, AutostartEntry(executable));
            return true;
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
            StartupReceipt.Write(AutostartPath, string.Join("\n", lines) + "\n");
            return AutostartRepair.Repaired;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return AutostartRepair.Failed; }

        static AutostartRepair Written(bool ok) => ok ? AutostartRepair.Repaired : AutostartRepair.Failed;
    }

    /// <summary>
    /// Whether a startup file is one some release of this window generated:
    /// the program is read from the file's own Exec or ExecStart line and the
    /// file must equal a known template for it, byte for byte.
    /// </summary>
    internal static bool IsGeneratedStartupFile(string path, string text)
    {
        if (path == AutostartPath)
        {
            string[] lines = text.Split('\n').Where(l => l.StartsWith("Exec=", StringComparison.Ordinal)).ToArray();
            if (lines.Length != 1 || DesktopExecBinary(lines[0][5..]) is not { } executable) return false;
            return text == AutostartEntry(executable) || LegacyAutostartEntries(executable).Contains(text, StringComparer.Ordinal);
        }
        if (path != UnitPath) return false;
        string[] starts = text.Split('\n').Where(l => l.StartsWith("ExecStart=", StringComparison.Ordinal)).ToArray();
        if (starts.Length != 1) return false;
        string value = starts[0][10..];
        if (ExecStartBinary(value) is { } daemon && (text == SourceUnit(daemon) || LegacySourceUnits(daemon).Contains(text, StringComparer.Ordinal))) return true;
        // The documented manual source install copies this exact reference
        // unit and changes only ExecStart. Preserve every other byte. A copy
        // of an earlier release's reference unit is recognised the same way.
        if (value.Length == 0 || value.Any(char.IsControl)) return false;
        using var stream = typeof(StartupIntegration).Assembly.GetManifestResourceStream("OpenXLR.StartupUnit");
        using var reader = stream is null ? null : new StreamReader(stream);
        string[] templates = reader is null ? LegacyReferenceUnits : [reader.ReadToEnd(), .. LegacyReferenceUnits];
        return templates.Any(template =>
        {
            string original = template.Split('\n').Single(l => l.StartsWith("ExecStart=", StringComparison.Ordinal));
            return text == template.Replace(original, starts[0], StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Earlier bodies of packaging/openxlr-daemon.service, the reference unit
    /// the source install guide has users copy by hand, each byte for byte as
    /// releases shipped it. The one from v0.1.37 to v0.1.70 asks the user
    /// manager to drop capabilities, which fails with 218/CAPABILITIES where
    /// it cannot. Recognising it lets Start audio at login, turned off and on,
    /// replace such a copy with the current unit.
    /// </summary>
    internal static readonly string[] LegacyReferenceUnits =
    [
        """
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
        # %h is your home directory; adjust the path to where you cloned the repo.
        ExecStart=%h/openxlr/src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon
        Environment=OPENXLR_BUILD_MIXER=1
        TimeoutStopSec=45
        # Bridged Windows plugins lock their shared audio buffers into memory, and
        # they inherit this allowance from the daemon. The 8 MiB most sessions
        # default to is below what yabridge asks for, which costs a warning on every
        # plugin load and dropouts while one runs.
        LimitMEMLOCK=infinity
        # SIGTERM goes to the daemon alone, so it can end the Wine session a bridged
        # plugin leaves behind before the rest of the group is killed. Sending it to
        # the whole group would take wineserver down first and leave winedevice.exe
        # to the timeout above.
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
        # Verified on hardware with the device, the submix graph and both clients:
        # none of these touches USB, PipeWire or the .NET runtime.
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
        """ + "\n",
    ];

    internal static string SourceUnit(string daemon) => $"""
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
                    LimitMEMLOCK=infinity
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
                    ProtectHostname=true
                    LockPersonality=true
                    RestrictNamespaces=true

                    [Install]
                    WantedBy=default.target
                    """;

    /// <summary>
    /// The unit bodies earlier releases wrote, oldest first, so an upgrade
    /// from any of them adopts the file its own window generated. Generation
    /// 0 is v0.1.0; 1 adds TimeoutStopSec (v0.1.9); 2 the first sandbox lines
    /// (v0.1.13); 3 the notify type, watchdog and start limit (v0.1.22); 4 the
    /// comment line, the quoted path and UMask (v0.1.23); 5 the second sandbox
    /// block (v0.1.24); 6 KillMode (v0.1.33). Generation 7 adds LimitMEMLOCK
    /// (v0.1.37). These older bodies still carry capability restrictions that
    /// the current user unit omits, so upgrades can adopt their owned files.
    /// </summary>
    internal static IEnumerable<string> LegacySourceUnits(string daemon) => Enumerable.Range(0, 8).Select(g => SourceUnit(daemon, g));

    internal static string SourceUnit(string daemon, int generation)
    {
        var lines = new List<string>();
        if (generation >= 4) lines.Add("# Written by the OpenXLR window for a source build (Options, Start at login).");
        lines.AddRange(["[Unit]", "Description=OpenXLR audio daemon", "After=pipewire-pulse.service wireplumber.service"]);
        if (generation >= 3) lines.AddRange(["StartLimitIntervalSec=300", "StartLimitBurst=3"]);
        lines.AddRange(["", "[Service]"]);
        if (generation >= 3) lines.AddRange(["Type=notify", "NotifyAccess=main", "WatchdogSec=60", "WatchdogSignal=SIGTERM", "TimeoutStartSec=120"]);
        lines.AddRange(["ExecStart=" + (generation >= 4 ? SystemdQuote(daemon) : daemon), "Environment=OPENXLR_BUILD_MIXER=1"]);
        if (generation >= 1) lines.Add("TimeoutStopSec=45");
        if (generation >= 6) lines.Add("KillMode=mixed");
        if (generation >= 7) lines.Add("LimitMEMLOCK=infinity");
        lines.AddRange(["Restart=on-failure", "RestartSec=3"]);
        if (generation >= 2) lines.AddRange(["NoNewPrivileges=true", "PrivateTmp=true", "ProtectSystem=strict", "ProtectControlGroups=true", "ProtectKernelTunables=true", "RestrictSUIDSGID=true"]);
        if (generation >= 4) lines.Add("UMask=0077");
        if (generation >= 5) lines.AddRange(["KeyringMode=private", "ProtectClock=true", "ProtectHostname=true", "ProtectKernelLogs=true",
            "ProtectKernelModules=true", "LockPersonality=true", "RestrictNamespaces=true", "CapabilityBoundingSet="]);
        lines.AddRange(["", "[Install]", "WantedBy=default.target"]);
        return string.Join("\n", lines);
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
    /// An explicit request clears this unit's start limit before retrying;
    /// automatic crash recovery still observes the service's start limit.
    /// </summary>
    public static bool RestartDaemon() => FlatpakEnvironment.IsFlatpak
        ? FlatpakSession.Current is { Attached: true } ? AttachedRestart?.Invoke() == true : FlatpakSession.Current?.Restart() == true
        : Systemctl("reset-failed", "openxlr-daemon.service")
            && Systemctl("restart", "openxlr-daemon.service");

    /// <summary>
    /// How the Flatpak UI restarts a daemon it did not start: the window
    /// sends restartDaemon and the user service brings the daemon back.
    /// </summary>
    internal static Func<bool>? AttachedRestart { get; set; }

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
