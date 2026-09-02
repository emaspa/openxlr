namespace OpenXLR.Core.Mixing;

/// <summary>
/// One plugin in a channel's insert chain. Inserts sit in the channel path
/// after the software low cut and ClipGuard, before the fan-out to the
/// mixes, so every mix hears the processed signal. Stage 1 hosts LV2 (and
/// LADSPA) natively in the PipeWire filter-chain; a bypassed insert is
/// simply left out of the chain until re-enabled.
/// </summary>
public sealed record InsertDefinition
{
    /// <summary>Stable id for this slot (survives reorders and restarts).</summary>
    public required string Id { get; init; }

    /// <summary>"lv2" or "ladspa".</summary>
    public required string Kind { get; init; }

    /// <summary>LV2: the plugin URI. LADSPA: "library:label".</summary>
    public required string Plugin { get; init; }

    /// <summary>Display name, captured from the catalog when added.</summary>
    public string? Label { get; init; }

    public bool Bypass { get; init; }

    /// <summary>Control values by port symbol; ports not listed keep defaults.</summary>
    public Dictionary<string, double> Params { get; init; } = [];
}

/// <summary>An insert as pushed to clients: its definition plus live status.</summary>
public sealed record InsertStatus(InsertDefinition Insert, string? Error);

/// <summary>
/// A plugin control port as declared by LV2. Clients use this metadata to
/// choose a switch, an enumeration selector, or a correctly scaled slider
/// without requiring the plugin's toolkit-specific native UI.
/// </summary>
public sealed record PluginParam(
    string Symbol, string Name,
    double Min, double Max, double Default,
    bool Toggled, bool Integer, bool Logarithmic, bool Enumeration,
    IReadOnlyList<ScalePoint> ScalePoints,
    string? UnitSymbol = null)
{
    /// <summary>
    /// Clamp and quantise an external value according to the port contract.
    /// This is enforced in the daemon as well as represented in the UI, so a
    /// hand-written WebSocket client cannot send invalid plugin state.
    /// </summary>
    public double Normalize(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "LV2 control values must be finite");

        double low = Math.Min(Min, Max);
        double high = Math.Max(Min, Max);
        if (double.IsFinite(low) && double.IsFinite(high)) value = Math.Clamp(value, low, high);

        if (Toggled) return value >= 0.5 ? 1.0 : 0.0;
        if (Enumeration && ScalePoints.Count > 0)
            return ScalePoints.MinBy(p => Math.Abs(p.Value - value))!.Value;
        if (Integer) value = Math.Round(value);
        return double.IsFinite(low) && double.IsFinite(high) ? Math.Clamp(value, low, high) : value;
    }
}

public sealed record ScalePoint(string Label, double Value);

/// <summary>A plugin the catalog offers for insertion.</summary>
public sealed record PluginInfo(
    string Kind, string Plugin, string Name, string Category,
    int AudioIns, int AudioOuts,
    /// <summary>Symbols of the first audio input and output ports (filter-chain link endpoints).</summary>
    string InputSymbol, string OutputSymbol,
    IReadOnlyList<PluginParam> Params,
    IReadOnlyList<string> RequiredFeatures,
    /// <summary>All audio input and output port symbols, in port order (stereo chains link both).</summary>
    IReadOnlyList<string> InputSymbols,
    IReadOnlyList<string> OutputSymbols);
