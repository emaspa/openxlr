using System.Collections.Frozen;
using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// One to <see cref="MaxKeys"/> key channels lowering selected channels in
/// selected mixes. Processing is machine wiring; the rest also travels in a
/// profile's ducking scene.
/// </summary>
public sealed record DuckingSetup : DuckingScene
{
    /// <summary>The most keys a setup takes: the software stage's key inputs (native/duck.c).</summary>
    public const int MaxKeys = 8;

    /// <summary>The preferred backend, device or software. An unfit unit falls back to software.</summary>
    public string Processing { get; init; } = ProDucking.Device;

    public const string KeyProblem = "key must be a microphone, Aux In, Line In or application channel";
    public const string KeysProblem = "keys must be one to 8 distinct microphone, Aux In, Line In or application channels";
    public const string ChannelsProblem = "channels must be distinct channel ids other than the keys";
    public const string MixesProblem = "mixes must be distinct mix ids";

    /// <summary>Off, keyed by XLR 1, with every application channel in the default monitor and the Pro's medium presets.</summary>
    public static DuckingSetup Default(MixerConfig config) => new()
    {
        Keys = [ProDucking.KeyXlr1],
        Channels = [.. config.Channels.Where(c => c.IsApplication).Select(c => c.Id)],
        Mixes = config.DefaultMonitor is { } monitor ? [monitor.Id] : [],
    };

    /// <summary>
    /// A channel that can key ducking: a microphone (XLR 1, XLR 2 or a
    /// capture input on one mono port), another of a unit's inputs (Aux In,
    /// Line In) or an application channel. A capture input on a stereo pair
    /// cannot.
    /// </summary>
    public static bool CanKey(ChannelDefinition channel) => channel.Microphone || channel.InputPair is not null || channel.IsApplication;

    /// <summary>The first invalid field, or null. Microphones include capture inputs on one mono port.</summary>
    public static string? Validate(DuckingSetup setup, ILayoutInfo layout)
    {
        if (KeysOf(setup) is not { } keys || keys.Count is 0 or > MaxKeys || keys.Any(k => k is null || !layout.IsDuckingKeyChannel(k))
            || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
            return KeysProblem;
        if (setup.Channels is null || setup.Channels.Any(c => c is null || keys.Contains(c) || !layout.HasChannel(c))
            || setup.Channels.Distinct(StringComparer.Ordinal).Count() != setup.Channels.Count)
            return ChannelsProblem;
        if (setup.Mixes is null || setup.Mixes.Any(m => m is null || !layout.HasMix(m))
            || setup.Mixes.Distinct(StringComparer.Ordinal).Count() != setup.Mixes.Count)
            return MixesProblem;
        return CheckPresets(setup) ?? (MicEqProcessing.IsValid(setup.Processing) ? null : "processing must be device or software");
    }

    internal static string? CheckPresets(DuckingScene setup) => ProDucking.CheckValues(setup.Sensitivity, setup.Amount, setup.Speed);

    /// <summary>
    /// Keep surviving targets without adding new ones. A file's single key
    /// from before several keys becomes the keys; a key that is gone or
    /// cannot key is dropped, and with none left ducking is disabled and the
    /// keys reset to XLR 1. Mixes a file never named become the default
    /// monitor.
    /// </summary>
    public static DuckingSetup Prune(DuckingSetup setup, MixerConfig config) => Prune(setup, config, null);

    internal static DuckingSetup Prune(DuckingSetup setup, MixerConfig config, List<string>? notes)
    {
        List<string> keys = Keep(KeysOf(setup), "keys", k => config.Channels.Any(c => c.Id == k && CanKey(c)));
        if (keys.Count > MaxKeys)
        {
            foreach (string extra in keys[MaxKeys..]) notes?.Add($"ducking.keys: dropped '{extra}', past the {MaxKeys}th key");
            keys = keys[..MaxKeys];
        }
        bool keyPresent = keys.Count > 0;
        if (!keyPresent)
        {
            notes?.Add("ducking.keys: no channel that can key; disabled and reset to xlr1");
            keys = [ProDucking.KeyXlr1];
        }
        return setup with
        {
            Enabled = keyPresent && setup.Enabled,
            Keys = keys,
            LegacyKey = null,
            Channels = Keep(setup.Channels, "channels", c => !keys.Contains(c) && config.Channels.Any(ch => ch.Id == c)),
            // A file without mixes means the default monitor, which is not
            // Monitor A once that was deleted.
            Mixes = ReferenceEquals(setup.Mixes, UnsavedMixes) ? Default(config).Mixes
                : Keep(setup.Mixes, "mixes", m => config.Mixes.Any(mix => mix.Id == m)),
        };

        List<string> Keep(IReadOnlyList<string>? ids, string field, Func<string, bool> valid)
        {
            if (ids is null) { notes?.Add($"ducking.{field}: null list"); return []; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var kept = new List<string>();
            foreach (string id in ids)
                if (id is not null && valid(id) && seen.Add(id)) kept.Add(id);
                else notes?.Add($"ducking.{field}: dropped '{id ?? "null"}'");
            return kept;
        }
    }

    /// <summary>A detached copy for callers and saves.</summary>
    public DuckingSetup Copy() => this with { Keys = [.. KeysOf(this) ?? []], LegacyKey = null, Channels = [.. Channels], Mixes = [.. Mixes] };

    /// <summary>The profile form, which has no processing property.</summary>
    public DuckingScene ToScene() => new()
    {
        Enabled = Enabled, Keys = [.. KeysOf(this) ?? []], Channels = [.. Channels], Mixes = [.. Mixes],
        Sensitivity = Sensitivity, Amount = Amount, Speed = Speed,
    };

    /// <summary>A recalled scene with this machine's processing choice; a profile's single key from before several keys stays for <see cref="Prune(DuckingSetup, MixerConfig)"/> to read.</summary>
    public static DuckingSetup FromScene(DuckingScene scene, string processing) => new()
    {
        Enabled = scene.Enabled, Keys = scene.Keys, LegacyKey = scene.LegacyKey, Channels = scene.Channels, Mixes = scene.Mixes,
        Sensitivity = scene.Sensitivity, Amount = scene.Amount, Speed = scene.Speed, Processing = processing,
    };

    // D2 computes key_open from the live cells. The hardware codec holds the
    // single preset table; a number goes to the software stage as it is.
    internal DuckingPlan ToPlan()
    {
        if (ProDucking.CheckValues(Sensitivity, Amount, Speed) is { } problem) throw new ArgumentException(problem);
        (int attack, int release) = ProDucking.TimesOf(Speed)!.Value;
        IReadOnlyList<string> keys = KeysOf(this) ?? [];
        return new([.. keys], Channels.Where(c => !keys.Contains(c)).SelectMany(c => Mixes.Select(m => $"{c}|{m}"))
            .ToFrozenSet(StringComparer.Ordinal), ProDucking.ThresholdOf(Sensitivity)!.Value, ProDucking.DepthOf(Amount)!.Value,
            attack, release, Enabled);
    }
}

/// <summary>
/// A <c>setDucking</c> value: each field present replaces the setup's, an
/// absent one keeps it. Where ducking runs is a separate command.
/// </summary>
public sealed record DuckingChange(bool? Enabled = null, IReadOnlyList<string>? Keys = null, IReadOnlyList<string>? Channels = null,
    IReadOnlyList<string>? Mixes = null, DuckingLevel? Sensitivity = null, DuckingLevel? Amount = null, DuckingSpeed? Speed = null)
{
    public DuckingSetup ApplyTo(DuckingSetup setup) => setup with
    {
        Enabled = Enabled ?? setup.Enabled,
        Keys = Keys is null ? [.. DuckingScene.KeysOf(setup) ?? []] : [.. Keys],
        LegacyKey = null,
        Channels = Channels is null ? setup.Channels : [.. Channels],
        Mixes = Mixes is null ? setup.Mixes : [.. Mixes],
        Sensitivity = Sensitivity ?? setup.Sensitivity,
        Amount = Amount ?? setup.Amount,
        Speed = Speed ?? setup.Speed,
    };

    public bool Equals(DuckingChange? other)
        => other is not null && Enabled == other.Enabled && Same(Keys, other.Keys) && Same(Channels, other.Channels)
            && Same(Mixes, other.Mixes) && Sensitivity == other.Sensitivity && Amount == other.Amount && Speed == other.Speed;

    public override int GetHashCode() => HashCode.Combine(Enabled, Keys?.Count, Channels?.Count, Mixes?.Count, Sensitivity, Amount, Speed);

    private static bool Same(IReadOnlyList<string>? a, IReadOnlyList<string>? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);
}
