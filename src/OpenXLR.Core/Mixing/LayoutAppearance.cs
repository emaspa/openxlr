namespace OpenXLR.Core.Mixing;

/// <summary>
/// How a channel or a mix is drawn: an icon from a fixed set, an optional
/// colour, and for a channel whether its strip is left out of the full
/// mixer. Presentation only: stable ids, routing order and every audio
/// connection are the same whatever these hold.
/// </summary>
public sealed record LayoutAppearance(string Icon = "", string? Colour = null, bool Hidden = false)
{
    public static LayoutAppearance Default { get; } = new();

    /// <summary>The icons a client may draw; plain text glyphs, never markup.</summary>
    public static IReadOnlyList<string> Icons { get; } = ["●", "♪", "♫", "✦", "◆", "▶", "◉"];

    /// <summary>Every channel and mix the layout can hold: the editable ones and the six structural ones.</summary>
    public const int MaxEntries = MixerConfig.MaxApplicationChannels + MixerConfig.MaxUserMixes + 6;

    /// <summary>An empty or listed icon and a null or #RRGGBB colour.</summary>
    public static bool IsValid(LayoutAppearance? value) => value is not null &&
        (value.Icon == "" || Icons.Contains(value.Icon)) &&
        (value.Colour is null || value.Colour.Length == 7 && value.Colour[0] == '#' &&
            value.Colour.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0);

    /// <summary>
    /// A saved entry: a <c>channel:</c> or <c>mix:</c> key of bounded length
    /// and a valid value. Only a channel can be hidden; a mix master always
    /// shows, because its outputs and recorders depend on it.
    /// </summary>
    internal static bool IsValidEntry(string? key, LayoutAppearance? value) =>
        key is { Length: <= 44 } && IsValid(value) &&
        (key.StartsWith("channel:", StringComparison.Ordinal) && key.Length > 8 ||
         key.StartsWith("mix:", StringComparison.Ordinal) && key.Length > 4 && !value!.Hidden);
}

public sealed partial record ChannelStatus
{
    /// <summary>The channel's icon, colour and hidden flag.</summary>
    public LayoutAppearance Appearance { get; init; } = LayoutAppearance.Default;
}

public sealed partial record MixStatus
{
    /// <summary>The mix's icon and colour.</summary>
    public LayoutAppearance Appearance { get; init; } = LayoutAppearance.Default;
}

public sealed partial class Mixer
{
    private Dictionary<string, LayoutAppearance> _appearance = [];

    private Dictionary<string, LayoutAppearance> ExportAppearanceLocked() => _appearance
        .Where(p => AppearanceTargetExists(p.Key) && p.Value != LayoutAppearance.Default).ToDictionary();

    private void RestoreAppearanceLocked(Dictionary<string, LayoutAppearance>? saved) =>
        _appearance = (saved ?? []).Where(p => AppearanceTargetExists(p.Key) && LayoutAppearance.IsValidEntry(p.Key, p.Value))
            .Take(LayoutAppearance.MaxEntries).ToDictionary();

    private bool AppearanceTargetExists(string key) =>
        key.StartsWith("channel:", StringComparison.Ordinal) && _config.Channels.Any(c => c.Id == key[8..]) ||
        key.StartsWith("mix:", StringComparison.Ordinal) && _config.Mixes.Any(m => m.Id == key[4..]);

    private LayoutAppearance AppearanceLocked(string key) => _appearance.GetValueOrDefault(key) ?? LayoutAppearance.Default;

    /// <summary>The state with each channel and mix carrying its appearance.</summary>
    private MixerState WithAppearanceLocked(MixerState state) => state with
    {
        Channels = [.. state.Channels.Select(c => c with { Appearance = AppearanceLocked("channel:" + c.Id) })],
        Mixes = [.. state.Mixes.Select(m => m with { Appearance = AppearanceLocked("mix:" + m.Id) })],
    };

    /// <summary>
    /// Replace one channel's or mix's appearance and save it. No PipeWire
    /// node or link changes; a failed save restores the previous appearance.
    /// </summary>
    /// <param name="key"><c>channel:&lt;id&gt;</c> or <c>mix:&lt;id&gt;</c>.</param>
    public void SetLayoutAppearance(string key, LayoutAppearance value, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (!AppearanceTargetExists(key)) throw new InvalidOperationException("unknown channel or mix");
            if (!LayoutAppearance.IsValidEntry(key, value)) throw new InvalidOperationException("invalid icon, colour or hidden flag");
            var previous = _appearance;
            _appearance = new(previous) { [key] = value };
            try { PersistLocked(persist); }
            catch { _appearance = previous; throw; }
        }
    }
}
