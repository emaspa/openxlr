using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

public sealed partial record MixStatus
{
    /// <summary>
    /// The mix's maximizer amount, 0 (off) to 100 (<c>setMixMaximizer</c>).
    /// It runs on the Wave XLR Pro while hardware mixing builds the mix alone
    /// in a section; everywhere else it is kept and does nothing.
    /// </summary>
    public int Maximizer { get; init; }
}

/// <summary>
/// The Wave XLR Pro's maximizer, one per mix section (block 0x0001 byte 14 of
/// the section, bypassed by membership bit 12; desk, 2026-11-11): make-up
/// gain of about 0.2 dB a step up to 20 dB at 100, against a ceiling near
/// -1 dBFS. Each mix keeps an amount, saved as <c>mixMaximizer</c> in
/// mixer.json; hardware mixing writes it to the section that builds the mix
/// alone, and bypasses every other section it builds.
/// </summary>
public sealed partial class Mixer
{
    // Mix id to amount, 1 to 100; a mix absent here is off.
    private readonly Dictionary<string, int> _maximizers = new(StringComparer.Ordinal);

    /// <summary>A mix's maximizer amount, 0 when off.</summary>
    public int MixMaximizer(string mix) { lock (_gate) return _maximizers.GetValueOrDefault(mix); }

    /// <summary>
    /// Set a mix's maximizer amount, 0 (off) to 100. The next sweep writes it
    /// while the unit builds the mix alone. The caller saves the settings.
    /// </summary>
    public void SetMixMaximizer(string mix, int amount)
    {
        if (amount is < 0 or > ProMixSections.MaximizerMaximum)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, $"a maximizer amount is 0 to {ProMixSections.MaximizerMaximum}");
        lock (_gate)
        {
            if (_config.Mixes.All(m => m.Id != mix)) throw new ArgumentException($"'{mix}' is not a mix", nameof(mix));
            if (amount == 0) _maximizers.Remove(mix);
            else _maximizers[mix] = amount;
        }
    }

    /// <summary>The amounts as the settings file keeps them: every mix of the layout with its maximizer on.</summary>
    private Dictionary<string, int> ExportMaximizersLocked()
        => _maximizers.Where(e => _config.Mixes.Any(m => m.Id == e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal)
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    /// <summary>Take the saved amounts; a mix of another layout, or an amount out of range, is dropped.</summary>
    private void RestoreMaximizersLocked(IReadOnlyDictionary<string, int>? saved)
    {
        _maximizers.Clear();
        foreach ((string mix, int amount) in saved ?? new Dictionary<string, int>())
            if (amount is > 0 and <= ProMixSections.MaximizerMaximum && _config.Mixes.Any(m => m.Id == mix)) _maximizers[mix] = amount;
    }
}
