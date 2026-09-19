using System.Text.Json;

namespace OpenXLR.Tui;

/// <summary>Only the catalogue fields needed to edit an insert already in a chain.</summary>
internal sealed record PluginEntry
{
    public string Kind { get; init; } = "lv2";
    public string Plugin { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool NativeUiBlocked { get; init; }
    public string? NativeUiBlockReason { get; init; }
    public List<PluginControl> Params { get; init; } = [];
}

internal sealed record PluginControl
{
    public string Symbol { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public double Min { get; init; }
    public double Max { get; init; } = 1;
    public double Default { get; init; }
    public bool Toggled { get; init; }
    public bool Integer { get; init; }
    public bool Logarithmic { get; init; }
    public bool Enumeration { get; init; }
    public List<PluginScalePoint> ScalePoints { get; init; } = [];

    // The generated window uses the same precision and switch threshold.
    public string Format(double value) => Integer ? Math.Round(value).ToString("0")
        : Math.Abs(value) >= 100 ? value.ToString("0")
        : Math.Abs(value) >= 10 ? value.ToString("0.0") : value.ToString("0.000");
}

internal sealed record PluginScalePoint(string Label, double Value);

/// <summary>
/// The full catalogue can be several megabytes. Filter before deserializing
/// controls, and release the JSON document as soon as the message is read.
/// </summary>
internal static class PluginCatalog
{
    public static Dictionary<(string Kind, string Plugin), PluginEntry> Read(
        string json, HashSet<(string Kind, string Plugin)> used)
    {
        Dictionary<(string, string), PluginEntry> result = [];
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("plugins", out JsonElement plugins) ||
            plugins.ValueKind != JsonValueKind.Array) return result;
        foreach (JsonElement item in plugins.EnumerateArray())
        {
            string kind = item.GetProperty("kind").GetString() ?? "lv2";
            string plugin = item.GetProperty("plugin").GetString() ?? string.Empty;
            if (!used.Contains((kind, plugin))) continue;
            if (item.Deserialize<PluginEntry>(Snapshot.Options) is { } entry)
                result[(kind, plugin)] = entry;
        }
        return result;
    }
}
