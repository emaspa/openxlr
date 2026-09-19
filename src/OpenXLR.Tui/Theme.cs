using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenXLR.Tui;

/// <summary>One skin as the picker shows it, and where its file is.</summary>
internal sealed record SkinEntry(string Id, string Name, string? Description, string? Author, Func<string?> Read);

/// <summary>
/// The colours the terminal mixer draws with, read from the same skin files
/// the window reads. A terminal has no gradients, no images and no corner
/// radii, so a gradient is taken at its first stop and everything that is not
/// a colour is ignored; the palette is what carries a skin into the terminal.
/// </summary>
internal sealed class Theme
{
    /// <summary>The shipped appearance, which is the window's own defaults (SkinTokens.cs).</summary>
    public static Theme Material { get; } = new();

    public string Id { get; private set; } = "default";

    public string Name { get; private set; } = "Material";

    public Rgb Window { get; private set; } = Rgb.Parse("#16181d");
    public Rgb Card { get; private set; } = Rgb.Parse("#1d2027");
    public Rgb Tile { get; private set; } = Rgb.Parse("#23262f");
    public Rgb Dialog { get; private set; } = Rgb.Parse("#1d2027");
    public Rgb Divider { get; private set; } = Rgb.Parse("#2d313c");
    public Rgb Badge { get; private set; } = Rgb.Parse("#2a2e38");
    public Rgb DangerBack { get; private set; } = Rgb.Parse("#a03434");
    public Rgb DangerFore { get; private set; } = Rgb.Parse("#f6dede");

    public Rgb TextPrimary { get; private set; } = Rgb.Parse("#e6e9f0");
    public Rgb TextSecondary { get; private set; } = Rgb.Parse("#8b93a7");
    public Rgb TextMuted { get; private set; } = Rgb.Parse("#6b7285");
    public Rgb TextDetail { get; private set; } = Rgb.Parse("#b9bfcc");
    public Rgb TextWarning { get; private set; } = Rgb.Parse("#e0a05a");
    public Rgb TextAlert { get; private set; } = Rgb.Parse("#e0a030");
    public Rgb TextInfo { get; private set; } = Rgb.Parse("#5ba8f5");
    public Rgb Link { get; private set; } = Rgb.Parse("#5ba8f5");

    public Rgb LedOn { get; private set; } = Rgb.Parse("#3ecf7a");
    public Rgb LedOff { get; private set; } = Rgb.Parse("#4a4f5c");
    public Rgb LedAlert { get; private set; } = Rgb.Parse("#ff3c4e");

    public Rgb MeterFill { get; private set; } = Rgb.Parse("#3ecf7a");
    public Rgb MeterWarning { get; private set; } = Rgb.Parse("#3ecf7a");
    public Rgb MeterHot { get; private set; } = Rgb.Parse("#3ecf7a");
    public Rgb MeterTrack { get; private set; } = Rgb.Parse("#14161b");
    public double MeterWarningLevel { get; private set; } = 0.7;
    public double MeterHotLevel { get; private set; } = 0.9;

    public Rgb MuteBack { get; private set; } = Rgb.Parse("#1e5c3a");
    public Rgb MuteFore { get; private set; } = Rgb.Parse("#d9efe2");
    public Rgb MuteBackChecked { get; private set; } = Rgb.Parse("#a03434");
    public Rgb MuteForeChecked { get; private set; } = Rgb.Parse("#f6dede");

    public Rgb ControlBack { get; private set; } = Rgb.Parse("#2a2e38");
    public Rgb ControlFore { get; private set; } = Rgb.Parse("#e6e9f0");
    public Rgb ControlBackChecked { get; private set; } = Rgb.Parse("#2f6f52");
    public Rgb ControlForeChecked { get; private set; } = Rgb.Parse("#e6e9f0");
    public Rgb ControlDisabled { get; private set; } = Rgb.Parse("#6b7285");

    public Rgb FaderTrack { get; private set; } = Rgb.Parse("#14161b");
    public Rgb FaderFill { get; private set; } = Rgb.Parse("#5ba8f5");
    public Rgb FaderThumb { get; private set; } = Rgb.Parse("#c9d1e2");
    public Rgb Accent { get; private set; } = Rgb.Parse("#5ba8f5");

    /// <summary>True when the skin paints on a light ground, which flips a few shades.</summary>
    public bool Light => Window.Luminance() > 0.4;

    /// <summary>The row a selection sits on: the accent, quietened into the surface.</summary>
    public Rgb Selection => Card.Mix(Accent, Light ? 0.22 : 0.30);

    /// <summary>
    /// A skin file read into a palette. Anything unreadable leaves the shipped
    /// value in place, one token at a time, which is how the window reads a
    /// skin too: a partial skin is an overlay, not a half painted window.
    /// </summary>
    public static Theme FromJson(string json, string id, string fallbackName)
    {
        Theme theme = new() { Id = id, Name = fallbackName };
        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return theme; }
        if (root is null) return theme;

        if (root["name"]?.GetValue<string>() is { Length: > 0 } name) theme.Name = name;
        if (root["tokens"] is not JsonObject tokens) return theme;

        Rgb? Colour(string token)
        {
            JsonNode? value = tokens[token];
            return value switch
            {
                JsonValue text when text.TryGetValue(out string? hex) => Rgb.TryParse(hex),
                // A gradient is taken at its first stop, and a solid object at
                // its colour. A cell cannot hold a ramp.
                JsonObject shape => Rgb.TryParse(
                    shape["color"]?.GetValue<string>()
                    ?? (shape["stops"] as JsonArray)?.FirstOrDefault()?["color"]?.GetValue<string>()),
                _ => null,
            };
        }

        double? Number(string token)
        {
            if (tokens[token] is JsonValue value && value.TryGetValue(out double number) && double.IsFinite(number))
                return number;
            return null;
        }

        theme.Window = Colour("Ox.Window.Background") ?? theme.Window;
        theme.Card = Colour("Ox.Card.Background") ?? theme.Card;
        theme.Tile = Colour("Ox.Tile.Background") ?? theme.Tile;
        theme.Dialog = Colour("Ox.Dialog.Background") ?? theme.Card;
        theme.Divider = Colour("Ox.Divider") ?? theme.Divider;
        theme.Badge = Colour("Ox.Badge.Background") ?? theme.Badge;
        theme.DangerBack = Colour("Ox.Danger.Background") ?? theme.DangerBack;
        theme.DangerFore = Colour("Ox.Danger.Foreground") ?? theme.DangerFore;

        theme.TextPrimary = Colour("Ox.Text.Primary") ?? theme.TextPrimary;
        theme.TextSecondary = Colour("Ox.Text.Secondary") ?? theme.TextSecondary;
        theme.TextMuted = Colour("Ox.Text.Muted") ?? theme.TextMuted;
        theme.TextDetail = Colour("Ox.Text.Detail") ?? theme.TextDetail;
        theme.TextWarning = Colour("Ox.Text.Warning") ?? theme.TextWarning;
        theme.TextAlert = Colour("Ox.Text.Alert") ?? theme.TextAlert;
        theme.TextInfo = Colour("Ox.Text.Info") ?? theme.TextInfo;
        theme.Link = Colour("Ox.Link.Foreground") ?? theme.TextInfo;

        theme.LedOn = Colour("Ox.Led.On") ?? theme.LedOn;
        theme.LedOff = Colour("Ox.Led.Off") ?? theme.LedOff;
        theme.LedAlert = Colour("Ox.Led.Alert") ?? theme.LedAlert;

        theme.MeterFill = Colour("Ox.Meter.Fill") ?? theme.MeterFill;
        theme.MeterWarning = Colour("Ox.Meter.Warning") ?? theme.MeterFill;
        theme.MeterHot = Colour("Ox.Meter.Hot") ?? theme.MeterFill;
        theme.MeterTrack = Colour("Ox.Meter.Track") ?? theme.MeterTrack;
        theme.MeterWarningLevel = Math.Clamp(Number("Ox.Meter.WarningLevel") ?? theme.MeterWarningLevel, 0, 1);
        theme.MeterHotLevel = Math.Clamp(Number("Ox.Meter.HotLevel") ?? theme.MeterHotLevel, 0, 1);
        // The zones can never cross, which is the rule the window follows.
        if (theme.MeterHotLevel < theme.MeterWarningLevel) theme.MeterHotLevel = theme.MeterWarningLevel;

        theme.MuteBack = Colour("Ox.Mute.Background") ?? theme.MuteBack;
        theme.MuteFore = Colour("Ox.Mute.Foreground") ?? theme.MuteFore;
        theme.MuteBackChecked = Colour("Ox.Mute.BackgroundChecked") ?? theme.MuteBackChecked;
        theme.MuteForeChecked = Colour("Ox.Mute.ForegroundChecked") ?? theme.MuteForeChecked;

        theme.ControlBack = Colour("Ox.Control.Background") ?? theme.Tile;
        theme.ControlFore = Colour("Ox.Control.Foreground") ?? theme.TextPrimary;
        theme.ControlBackChecked = Colour("Ox.Control.BackgroundChecked") ?? theme.ControlBack;
        theme.ControlForeChecked = Colour("Ox.Toggle.ForegroundChecked")
            ?? Colour("Ox.Control.ForegroundChecked") ?? theme.LedOn;
        theme.ControlDisabled = Colour("Ox.Control.ForegroundDisabled") ?? theme.TextMuted;

        theme.FaderTrack = Colour("Ox.Fader.Track") ?? theme.MeterTrack;
        theme.FaderFill = Colour("Ox.Fader.Fill") ?? theme.Accent;
        theme.FaderThumb = Colour("Ox.Fader.Thumb.Background") ?? theme.TextPrimary;
        theme.Accent = Colour("Ox.Accent") ?? theme.FaderFill;

        return theme;
    }

    /// <summary>The colour a meter cell takes at that place on the scale.</summary>
    public Rgb MeterColour(double position) =>
        position >= MeterHotLevel ? MeterHot : position >= MeterWarningLevel ? MeterWarning : MeterFill;
}

/// <summary>
/// Where skins come from. The same three places the window reads, in the same
/// order, so a skin picked in one is the skin offered in the other: the user's
/// data directory, the system data directories, then the two the application
/// ships with.
/// </summary>
internal static class SkinCatalog
{
    private const int MaxSkins = 200;
    private const long MaxFileBytes = 256 * 1024;

    /// <summary>Every skin found, Material first and the rest by name.</summary>
    public static IReadOnlyList<SkinEntry> Scan()
    {
        Dictionary<string, SkinEntry> found = new(StringComparer.Ordinal);

        foreach (string root in Roots())
        {
            if (found.Count >= MaxSkins) break;
            DirectoryInfo directory = new(root);
            if (!directory.Exists) continue;
            IEnumerable<DirectoryInfo> folders;
            try { folders = directory.EnumerateDirectories(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (DirectoryInfo folder in folders)
            {
                if (found.Count >= MaxSkins) break;
                string id = folder.Name;
                if (!ValidId(id) || found.ContainsKey(id)) continue;
                string file = Path.Combine(folder.FullName, "skin.json");
                FileInfo info = new(file);
                if (!info.Exists || info.Length > MaxFileBytes) continue;
                found[id] = Describe(id, () => ReadFile(file));
            }
        }

        foreach ((string id, string resource) in Shipped())
        {
            if (found.ContainsKey(id) || found.Count >= MaxSkins) continue;
            found[id] = Describe(id, () => ReadResource(resource));
        }

        List<SkinEntry> ordered = found.Values.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ordered.Insert(0, new SkinEntry("default", "Material", "The appearance OpenXLR ships with", "OpenXLR", () => null));
        return ordered;
    }

    /// <summary>The theme for an id, or Material when it is not there or cannot be read.</summary>
    public static Theme Load(string? id)
    {
        if (string.IsNullOrEmpty(id) || id == "default") return Theme.Material;
        SkinEntry? entry = Scan().FirstOrDefault(skin => skin.Id == id);
        if (entry is null) return Theme.Material;
        string? json = entry.Read();
        return json is null ? Theme.Material : Theme.FromJson(json, entry.Id, entry.Name);
    }

    private static SkinEntry Describe(string id, Func<string?> read)
    {
        string name = id, description = string.Empty, author = string.Empty;
        try
        {
            if (read() is { } json && JsonNode.Parse(json) is JsonObject root)
            {
                name = root["name"]?.GetValue<string>() ?? id;
                description = root["description"]?.GetValue<string>() ?? string.Empty;
                author = root["author"]?.GetValue<string>() ?? string.Empty;
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return new SkinEntry(id, Cut(name), Cut(description), Cut(author), read);
    }

    private static string Cut(string text) =>
        new(text.Where(ch => !char.IsControl(ch)).Take(400).ToArray());

    private static string? ReadFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? ReadResource(string resource)
    {
        using Stream? stream = typeof(SkinCatalog).Assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static IEnumerable<(string Id, string Resource)> Shipped()
    {
        foreach (string resource in typeof(SkinCatalog).Assembly.GetManifestResourceNames())
        {
            const string prefix = "OpenXLR.Tui.Skins.";
            const string suffix = ".skin.json";
            if (!resource.StartsWith(prefix, StringComparison.Ordinal) ||
                !resource.EndsWith(suffix, StringComparison.Ordinal)) continue;
            string id = resource[prefix.Length..^suffix.Length];
            if (ValidId(id)) yield return (id, resource);
        }
    }

    internal static IEnumerable<string> Roots()
    {
        string home = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome
            ? dataHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        yield return Path.Combine(home, "openxlr", "skins");

        string dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dataDirs
            ? dataDirs
            : "/usr/local/share:/usr/share";
        foreach (string entry in dirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(entry, "openxlr", "skins");
    }

    /// <summary>Lower case letters, digits, dot, dash and underscore, starting with a letter or a digit.</summary>
    internal static bool ValidId(string id)
    {
        if (id.Length is 0 or > 64) return false;
        if (!char.IsAsciiLetterLower(id[0]) && !char.IsAsciiDigit(id[0])) return false;
        return id.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '.' or '-' or '_');
    }
}

/// <summary>
/// The one setting the terminal mixer keeps, which is the skin, held in the
/// same <c>ui.json</c> the window uses so choosing an appearance in either one
/// is the same choice. Every other setting in that file is left untouched.
/// </summary>
internal static class UiSettingsFile
{
    private static string Path => OpenXlrPaths.ConfigFile("ui.json");

    public static string? ReadSkin()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            return JsonNode.Parse(File.ReadAllText(Path)) is JsonObject root
                ? root["skin"]?.GetValue<string>()
                : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Writes the chosen skin back, keeping every other property the file holds.</summary>
    public static bool WriteSkin(string id)
    {
        try
        {
            JsonObject root = File.Exists(Path) && JsonNode.Parse(File.ReadAllText(Path)) is JsonObject existing
                ? existing
                : new JsonObject();
            root["skin"] = id;
            OpenXlrPaths.WriteAtomic(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
