using System.Collections.Frozen;
using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// One microphone lowering selected channels in selected mixes. Processing
/// is machine wiring; the rest also travels in a profile's ducking scene.
/// </summary>
public sealed record DuckingSetup : DuckingScene
{
    /// <summary>The preferred backend, device or software. An unfit unit falls back to software.</summary>
    public string Processing { get; init; } = ProDucking.Device;

    /// <summary>Off, with every application channel in the default monitor and the Pro's medium presets.</summary>
    public static DuckingSetup Default(MixerConfig config) => new()
    {
        Channels = [.. config.Channels.Where(c => c.IsApplication).Select(c => c.Id)],
        Mixes = config.DefaultMonitor is { } monitor ? [monitor.Id] : [],
    };

    /// <summary>The first invalid field, or null. Microphones include capture inputs on one mono port.</summary>
    public static string? Validate(DuckingSetup setup, ILayoutInfo layout)
    {
        if (setup.Key is null || !layout.IsSoundCheckChannel(setup.Key)) return "key must be a microphone channel";
        if (setup.Channels is null || setup.Channels.Any(c => c is null || c == setup.Key || !layout.HasChannel(c))
            || setup.Channels.Distinct(StringComparer.Ordinal).Count() != setup.Channels.Count)
            return "channels must be distinct channel ids other than the key";
        if (setup.Mixes is null || setup.Mixes.Any(m => m is null || !layout.HasMix(m))
            || setup.Mixes.Distinct(StringComparer.Ordinal).Count() != setup.Mixes.Count)
            return "mixes must be distinct mix ids";
        return CheckPresets(setup) ?? (MicEqProcessing.IsValid(setup.Processing) ? null : "processing must be device or software");
    }

    internal static string? CheckPresets(DuckingScene setup) => ProDucking.CheckValues(setup.Sensitivity, setup.Amount, setup.Speed);

    /// <summary>
    /// Keep surviving targets without adding new ones. A missing microphone
    /// disables ducking and resets the key; mixes a file never named become
    /// the default monitor.
    /// </summary>
    public static DuckingSetup Prune(DuckingSetup setup, MixerConfig config) => Prune(setup, config, null);

    internal static DuckingSetup Prune(DuckingSetup setup, MixerConfig config, List<string>? notes)
    {
        bool keyPresent = config.Channels.Any(c => c.Id == setup.Key && c.Microphone);
        if (!keyPresent) notes?.Add("ducking.key: not a microphone channel; disabled and reset to xlr1");
        string key = keyPresent ? setup.Key : ProDucking.KeyXlr1;
        return setup with
        {
            Enabled = keyPresent && setup.Enabled,
            Key = key,
            Channels = Keep(setup.Channels, "channels", c => c != key && config.Channels.Any(ch => ch.Id == c)),
            // A file without mixes means the default monitor, which is not
            // Monitor A once that was deleted.
            Mixes = ReferenceEquals(setup.Mixes, UnsavedMixes) ? Default(config).Mixes
                : Keep(setup.Mixes, "mixes", m => config.Mixes.Any(mix => mix.Id == m)),
        };

        IReadOnlyList<string> Keep(IReadOnlyList<string>? ids, string field, Func<string, bool> valid)
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
    public DuckingSetup Copy() => this with { Channels = [.. Channels], Mixes = [.. Mixes] };

    /// <summary>The profile form, which has no processing property.</summary>
    public DuckingScene ToScene() => new()
    {
        Enabled = Enabled, Key = Key, Channels = [.. Channels], Mixes = [.. Mixes],
        Sensitivity = Sensitivity, Amount = Amount, Speed = Speed,
    };

    /// <summary>A recalled scene with this machine's processing choice.</summary>
    public static DuckingSetup FromScene(DuckingScene scene, string processing) => new()
    {
        Enabled = scene.Enabled, Key = scene.Key, Channels = scene.Channels, Mixes = scene.Mixes,
        Sensitivity = scene.Sensitivity, Amount = scene.Amount, Speed = scene.Speed, Processing = processing,
    };

    // D2 computes key_open from the live cells. The hardware codec holds the
    // single preset table; a number goes to the software stage as it is.
    internal DuckingPlan ToPlan()
    {
        if (ProDucking.CheckValues(Sensitivity, Amount, Speed) is { } problem) throw new ArgumentException(problem);
        (int attack, int release) = ProDucking.TimesOf(Speed)!.Value;
        return new(Key, Channels.Where(c => c != Key).SelectMany(c => Mixes.Select(m => $"{c}|{m}"))
            .ToFrozenSet(StringComparer.Ordinal), ProDucking.ThresholdOf(Sensitivity)!.Value, ProDucking.DepthOf(Amount)!.Value,
            attack, release, Enabled);
    }
}

/// <summary>
/// A <c>setDucking</c> value: each field present replaces the setup's, an
/// absent one keeps it. Where ducking runs is a separate command.
/// </summary>
public sealed record DuckingChange(bool? Enabled = null, string? Key = null, IReadOnlyList<string>? Channels = null,
    IReadOnlyList<string>? Mixes = null, DuckingLevel? Sensitivity = null, DuckingLevel? Amount = null, DuckingSpeed? Speed = null)
{
    public DuckingSetup ApplyTo(DuckingSetup setup) => setup with
    {
        Enabled = Enabled ?? setup.Enabled,
        Key = Key ?? setup.Key,
        Channels = Channels is null ? setup.Channels : [.. Channels],
        Mixes = Mixes is null ? setup.Mixes : [.. Mixes],
        Sensitivity = Sensitivity ?? setup.Sensitivity,
        Amount = Amount ?? setup.Amount,
        Speed = Speed ?? setup.Speed,
    };
}
