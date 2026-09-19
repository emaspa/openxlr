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
/// One plugin as the picker lists it: what it is called and whether it can
/// sit in a chain, without its controls, which only an insert needs.
/// </summary>
internal sealed record PluginChoice
{
    public string Kind { get; init; } = "lv2";
    public string Plugin { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool Supported { get; init; } = true;
    public int AudioIns { get; init; }
    public int AudioOuts { get; init; }
    public List<int>? Widths { get; init; }

    /// <summary>
    /// Whether the plugin can sit in a chain of this width, as the window
    /// decides it: by <c>widths</c> when the daemon asked the plugin, else by
    /// its ports, one each way for a mono input and two or more for a mix.
    /// </summary>
    public bool Fits(int channels) => Widths is not null ? Widths.Contains(channels)
        : channels == 1 ? AudioIns == 1 && AudioOuts == 1 : AudioIns >= 2 && AudioOuts >= 2;
}

/// <summary>What one plugins message held: the used plugins in full, and every plugin as a choice.</summary>
internal sealed record PluginCatalogue(
    Dictionary<(string Kind, string Plugin), PluginEntry> Entries,
    List<PluginChoice> Choices);

/// <summary>
/// The full catalogue can be several megabytes. Filter before deserializing
/// controls, and release the JSON document as soon as the message is read.
/// </summary>
internal static class PluginCatalog
{
    public static PluginCatalogue Read(string json, HashSet<(string Kind, string Plugin)> used)
    {
        Dictionary<(string, string), PluginEntry> entries = [];
        List<PluginChoice> choices = [];
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("plugins", out JsonElement plugins) ||
            plugins.ValueKind != JsonValueKind.Array) return new(entries, choices);
        foreach (JsonElement item in plugins.EnumerateArray())
        {
            string kind = item.GetProperty("kind").GetString() ?? "lv2";
            string plugin = item.GetProperty("plugin").GetString() ?? string.Empty;
            if (item.Deserialize<PluginChoice>(Snapshot.Options) is { } choice)
                choices.Add(choice.Name.Length > 0 ? choice : choice with { Name = plugin });
            if (!used.Contains((kind, plugin))) continue;
            if (item.Deserialize<PluginEntry>(Snapshot.Options) is { } entry)
                entries[(kind, plugin)] = entry;
        }
        choices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new(entries, choices);
    }
}
