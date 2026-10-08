namespace OpenXLR.Core.Mixing;

/// <summary>Presentation only. Stable routing IDs and audio connections do not depend on these values.</summary>
public sealed record LayoutAppearance(string Icon = "", string? Colour = null, bool Hidden = false)
{
    public static LayoutAppearance Default { get; } = new();
    public const int MaxEntries = MixerConfig.MaxApplicationChannels + MixerConfig.MaxVirtualMixes + 6;
    public static bool IsValid(LayoutAppearance? value) => value is not null &&
        value.Icon is "" or "●" or "♪" or "♫" or "✦" or "◆" or "▶" or "◉" &&
        (value.Colour is null || value.Colour.Length == 7 && value.Colour[0] == '#' &&
            value.Colour.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0);
}

public sealed partial class Mixer
{
    private Dictionary<string, LayoutAppearance> _appearance = [];

    private Dictionary<string, LayoutAppearance> ExportAppearanceLocked() => _appearance
        .Where(p => AppearanceTargetExists(p.Key)).ToDictionary();

    private bool AppearanceTargetExists(string key) =>
        key.StartsWith("channel:", StringComparison.Ordinal) && _config.Channels.Any(c => c.Id == key[8..]) ||
        key.StartsWith("mix:", StringComparison.Ordinal) && _config.Mixes.Any(m => m.Id == key[4..]);

    private LayoutAppearance Appearance(string key) => _appearance.GetValueOrDefault(key) ?? LayoutAppearance.Default;

    public void SetLayoutAppearance(string key, LayoutAppearance value, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!_built || !AppearanceTargetExists(key)) throw new InvalidOperationException("unknown layout item");
            if (!LayoutAppearance.IsValid(value) || key.StartsWith("mix:", StringComparison.Ordinal) && value.Hidden) throw new InvalidOperationException("invalid layout appearance");
            var previous = _appearance;
            _appearance = new(previous) { [key] = value };
            try { PersistLocked(persist); }
            catch { _appearance = previous; throw; }
        }
    }
}
