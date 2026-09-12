using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// One plugin whose own editor window OpenXLR does not open. Kind and plugin
/// are the plugin's identity, the same pair inserts and the catalogue use, so
/// a rule follows the plugin wherever its files are installed.
/// </summary>
public sealed record NativeEditorRule(string Kind, string Plugin, string Name, string Reason);

/// <summary>
/// One rule as the Options list shows it: what this release ships, what the
/// user chose, and what the two add up to. Override is null while the user
/// has made no choice, so the entry follows the release default.
/// </summary>
public sealed record NativeEditorRuleState(string Kind, string Plugin, string Name, string Reason,
    bool DefaultBlocked, bool? Override, bool Blocked);

/// <summary>
/// Which plugins run without their own editor. A few plugins bring an editor
/// that hangs or crashes under the bridge while their audio side is fine, so
/// the insert keeps processing and the user gets the OpenXLR generated
/// controls instead of the plugin's window.
///
/// The list of known cases ships with the release. The file under the
/// configuration directory holds only what the user decided, never a copy of
/// the release list, so an OpenXLR that adds or drops a case changes what the
/// user did not touch and leaves every choice they made alone.
///
/// Reads take no lock and touch no disk: the overrides are an immutable map
/// replaced whole, since IsBlocked is asked once per editor request and from
/// several threads. A write goes to disk first and is only adopted when the
/// file is on disk, so a failed save leaves the running policy as it was.
///
/// This decides editor windows only. Nothing here changes whether a plugin is
/// supported, whether it runs in the native host, or how it is processed.
/// </summary>
public sealed class NativeEditorPolicy
{
    /// <summary>Why an entry the user added by hand is blocked.</summary>
    public const string ChosenReason = "You turned this editor off in Options.";

    /// <summary>
    /// What a blocked entry says when its own rule carries no usable text, so
    /// a caller asking why an editor stayed closed always has something to
    /// show the user.
    /// </summary>
    public const string UnstatedReason = "This editor is known not to work here; the OpenXLR controls do.";

    private const int CurrentVersion = 1;
    private const int MaxPluginLength = 512;
    private const int MaxNameLength = 200;
    private const int MaxReasonLength = 400;
    private const int MaxOverrides = 1024;
    private const long MaxFileBytes = 1024 * 1024;

    /// <summary>
    /// The known cases this release ships. Every entry is blocked unless the
    /// user says otherwise.
    /// </summary>
    public static IReadOnlyList<NativeEditorRule> Curated { get; } =
    [
        new("vst3", "ABCDEF019182FAEB4D616E75466C7665", "Elgato De-Esser",
            "Its native editor freezes under Wine; the OpenXLR controls work."),
    ];

    /// <summary>~/.config/openxlr/native-editors.json, honouring XDG_CONFIG_HOME.</summary>
    public static string DefaultPath => OpenXlrPaths.ConfigFile("native-editors.json");

    private readonly string _path;
    private readonly IReadOnlyList<NativeEditorRule> _defaults;
    private readonly Dictionary<(string Kind, string Plugin), NativeEditorRule> _byIdentity;
    private readonly object _gate = new();
    private readonly string? _error;
    private volatile IReadOnlyDictionary<(string Kind, string Plugin), Choice> _overrides;

    /// <summary>
    /// Read the user's choices for this release's list. A missing file is the
    /// normal first run; a file that cannot be read or was written by another
    /// version leaves Error set and the release list in force.
    /// </summary>
    public NativeEditorPolicy(string? path = null, IReadOnlyList<NativeEditorRule>? defaults = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        var normalized = new List<NativeEditorRule>();
        _byIdentity = new();
        foreach (NativeEditorRule rule in defaults ?? Curated)
        {
            if (Normalize(rule.Kind, rule.Plugin, rule.Name, out string kind, out string plugin, out string? name) is string problem)
                throw new ArgumentException($"Built-in native editor rule is not usable: {problem}", nameof(defaults));
            // A blocked entry must always be able to say why, so a rule with
            // no usable text of its own borrows the general one.
            string reason = (rule.Reason ?? string.Empty).Trim();
            if (reason.Length == 0 || reason.Length > MaxReasonLength || reason.Any(char.IsControl)) reason = UnstatedReason;
            var clean = rule with { Kind = kind, Plugin = plugin, Name = name ?? plugin, Reason = reason };
            if (!_byIdentity.TryAdd((kind, plugin), clean))
                throw new ArgumentException($"Built-in native editor rules name {kind} {plugin} twice.", nameof(defaults));
            normalized.Add(clean);
        }
        _defaults = normalized;
        (_overrides, _error) = Load(_path);
    }

    /// <summary>What is wrong with the stored file, or null when it read cleanly.</summary>
    public string? Error => _error;

    /// <summary>
    /// Every rule the Options list shows: this release's cases in their order,
    /// then the plugins the user named that the release does not know about.
    /// </summary>
    public IReadOnlyList<NativeEditorRuleState> Rules
    {
        get
        {
            IReadOnlyDictionary<(string Kind, string Plugin), Choice> overrides = _overrides;
            var rules = new List<NativeEditorRuleState>();
            foreach (NativeEditorRule rule in _defaults)
            {
                bool? choice = overrides.TryGetValue((rule.Kind, rule.Plugin), out Choice? entry) ? entry.Blocked : null;
                rules.Add(new(rule.Kind, rule.Plugin, rule.Name, rule.Reason, true, choice, choice ?? true));
            }
            foreach (KeyValuePair<(string Kind, string Plugin), Choice> pair in overrides
                .Where(p => !_byIdentity.ContainsKey(p.Key))
                .OrderBy(p => p.Key.Kind, StringComparer.Ordinal)
                .ThenBy(p => p.Key.Plugin, StringComparer.Ordinal))
                rules.Add(new(pair.Key.Kind, pair.Key.Plugin, pair.Value.Name ?? pair.Key.Plugin,
                    pair.Value.Blocked ? ChosenReason : "You allowed this native editor in Options.",
                    false, pair.Value.Blocked, pair.Value.Blocked));
            return rules;
        }
    }

    /// <summary>Whether this plugin's own editor stays closed. No disk access.</summary>
    public bool IsBlocked(string? kind, string? plugin) => Decide(kind, plugin).Blocked;

    /// <summary>Why the editor stays closed, or null when it may open.</summary>
    public string? BlockReason(string? kind, string? plugin)
    {
        (bool blocked, string? reason) = Decide(kind, plugin);
        return blocked ? reason : null;
    }

    /// <summary>
    /// Record the user's choice for one plugin: true blocks its editor, false
    /// allows it, null drops the choice so the plugin follows this release's
    /// list again. The file is written before the change takes effect, so a
    /// returned message means nothing changed, in memory or on disk.
    /// </summary>
    public string? Set(string kind, string plugin, string? name, bool? blocked)
    {
        if (Normalize(kind, plugin, name, out string cleanKind, out string cleanPlugin, out string? cleanName) is string problem)
            return problem;
        lock (_gate)
        {
            if (_error is not null) return _error;
            var next = new Dictionary<(string Kind, string Plugin), Choice>(_overrides);
            var identity = (cleanKind, cleanPlugin);
            if (blocked is bool choice)
            {
                next.TryGetValue(identity, out Choice? current);
                if (current is null && next.Count >= MaxOverrides) return $"At most {MaxOverrides} native-editor overrides can be stored.";
                var entry = new Choice(choice, cleanName ?? current?.Name ?? _byIdentity.GetValueOrDefault(identity)?.Name);
                if (entry == current) return null;
                next[identity] = entry;
            }
            else if (!next.Remove(identity)) return null;
            if (Write(next) is string failure) return failure;
            _overrides = next;
            return null;
        }
    }

    private (bool Blocked, string? Reason) Decide(string? kind, string? plugin)
    {
        if (Normalize(kind, plugin, null, out string cleanKind, out string cleanPlugin, out _) is not null) return (false, null);
        var identity = (cleanKind, cleanPlugin);
        bool known = _byIdentity.TryGetValue(identity, out NativeEditorRule? rule);
        if (_overrides.TryGetValue(identity, out Choice? entry))
            return (entry.Blocked, known ? rule!.Reason : ChosenReason);
        return known ? (true, rule!.Reason) : (false, null);
    }

    private string? Write(IReadOnlyDictionary<(string Kind, string Plugin), Choice> overrides)
    {
        var file = new StoredFile(CurrentVersion,
        [
            .. overrides
                .OrderBy(p => p.Key.Kind, StringComparer.Ordinal)
                .ThenBy(p => p.Key.Plugin, StringComparer.Ordinal)
                .Select(p => new StoredChoice(p.Key.Kind, p.Key.Plugin, p.Value.Name, p.Value.Blocked))
        ]);
        try
        {
            string text = JsonSerializer.Serialize(file, Json);
            if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxFileBytes)
                return "The native-editor overrides file would be too large. Remove unused overrides first.";
            OpenXlrPaths.WriteAtomic(_path, text);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{_path}: {ex.Message}";
        }
    }

    private static (IReadOnlyDictionary<(string Kind, string Plugin), Choice> Overrides, string? Error) Load(string path)
    {
        var empty = new Dictionary<(string Kind, string Plugin), Choice>();
        string text;
        try
        {
            if (Directory.Exists(path)) return (empty, Damaged(path, "the path is a directory"));
            if (!File.Exists(path)) return (empty, null);
            if (new FileInfo(path).Length > MaxFileBytes) return (empty, Damaged(path, "the file is too large"));
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (empty, Damaged(path, ex.Message));
        }

        StoredFile? file;
        try { file = JsonSerializer.Deserialize<StoredFile>(text, Json); }
        catch (JsonException ex) { return (empty, Damaged(path, ex.Message)); }
        if (file is null) return (empty, Damaged(path, "it holds nothing"));
        if (file.Version != CurrentVersion)
            return (empty, Damaged(path, $"it was written by another version of OpenXLR (format {file.Version})"));

        if (file.Overrides is null || file.Overrides.Count > MaxOverrides)
            return (empty, Damaged(path, "the overrides list is missing or too large"));
        var overrides = new Dictionary<(string Kind, string Plugin), Choice>();
        foreach (StoredChoice entry in file.Overrides)
        {
            if (entry is null || entry.Blocked is null) return (empty, Damaged(path, "an override is missing its decision"));
            if (Normalize(entry.Kind, entry.Plugin, entry.Name, out string kind, out string plugin, out string? name) is string problem)
                return (empty, Damaged(path, problem));
            if (!overrides.TryAdd((kind, plugin), new Choice(entry.Blocked.Value, name)))
                return (empty, Damaged(path, $"it names {kind} {plugin} twice"));
        }
        return (overrides, null);
    }

    private static string Damaged(string path, string detail)
        => $"{path} could not be read ({detail}). It was left untouched; repair or remove it, then restart the daemon. The release defaults are in use.";

    /// <summary>
    /// Check one identity and put it in the form the policy keys on: a known
    /// kind, a VST3 class id as 32 upper case hex digits, an id and a name
    /// that are bounded plain text. Returns null when the input is usable.
    /// </summary>
    private static string? Normalize(string? kind, string? plugin, string? name,
        out string cleanKind, out string cleanPlugin, out string? cleanName)
    {
        cleanKind = (kind ?? string.Empty).Trim().ToLowerInvariant();
        cleanPlugin = (plugin ?? string.Empty).Trim();
        cleanName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        if (kind?.Any(char.IsControl) == true || plugin?.Any(char.IsControl) == true || name?.Any(char.IsControl) == true
            || (plugin?.Length ?? 0) > MaxPluginLength)
            return "Plugin identities and names must be bounded plain text without control characters.";
        if (cleanKind is not ("lv2" or "clap" or "vst3"))
            return $"\"{kind}\" is not a plugin kind OpenXLR knows (lv2, clap or vst3).";
        if (cleanName is not null && (cleanName.Length > MaxNameLength || cleanName.Any(char.IsControl)))
            return $"A plugin name must be plain text of at most {MaxNameLength} characters.";

        if (cleanKind == "vst3")
        {
            string id = string.Concat(cleanPlugin.Where(c => c is not ('-' or '{' or '}')));
            if (id.Length != 32 || !id.All(Uri.IsHexDigit))
                return "A VST3 plugin is named by its class id, 32 hexadecimal digits.";
            cleanPlugin = id.ToUpperInvariant();
            return null;
        }
        if (cleanPlugin.Length == 0 || cleanPlugin.Length > MaxPluginLength
            || cleanPlugin.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return $"A plugin id must be plain text without spaces, of at most {MaxPluginLength} characters.";
        return null;
    }

    /// <summary>One user decision: blocked or allowed, with a name to show it by.</summary>
    private sealed record Choice(bool Blocked, string? Name);

    private sealed record StoredChoice(string Kind, string Plugin, string? Name, bool? Blocked);

    private sealed record StoredFile(int Version, IReadOnlyList<StoredChoice>? Overrides);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
