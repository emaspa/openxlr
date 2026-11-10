using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The three values, preset words or numbers, and the USB return pairs the
/// primary unit should duck. <see cref="Sections"/>, in hardware mixing,
/// says in which of block 0x0001's sections each pair is ducked; without it
/// the pairs are ducked as whole outputs (see <see cref="ProDucking"/>).
/// <see cref="Keys"/> are the sources whose level ducks, and
/// <see cref="Inputs"/> the unit's own inputs ducked beside the pairs.
/// </summary>
public sealed record ProDuckingTarget(bool Enabled, DuckingLevel Sensitivity, DuckingLevel Amount, DuckingSpeed Speed, IReadOnlySet<int> Pairs)
{
    /// <summary>Section start to the pairs ducked in it, each a subset of <see cref="Pairs"/>; null for whole outputs.</summary>
    public IReadOnlyDictionary<int, IReadOnlySet<int>>? Sections { get; init; }

    /// <summary>
    /// The sources that key the ducking, bit M for source M in block
    /// 0x0008's numbering (0 XLR 1, 1 XLR 2, 2 USB Aux, 3 Line In, 4 + k pair
    /// k): the unit follows the loudest. XLR 1 alone unless set.
    /// </summary>
    public ushort Keys { get; init; } = ProDucking.KeyXlr1Mask;

    /// <summary>The unit's inputs ducked (0 XLR 1 to 3 Line In), in the sections <see cref="ProDucking.InputSections"/> names.</summary>
    public IReadOnlySet<int> Inputs { get; init; } = NoInputs;

    private static readonly IReadOnlySet<int> NoInputs = new HashSet<int>();

    public bool Equals(ProDuckingTarget? other)
        => other is not null && Enabled == other.Enabled && Sensitivity == other.Sensitivity
            && Amount == other.Amount && Speed == other.Speed && Pairs.SetEquals(other.Pairs) && SameSections(Sections, other.Sections)
            && Keys == other.Keys && Inputs.SetEquals(other.Inputs);

    private static bool SameSections(IReadOnlyDictionary<int, IReadOnlySet<int>>? a, IReadOnlyDictionary<int, IReadOnlySet<int>>? b)
        => a is null ? b is null
            : b is not null && a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out IReadOnlySet<int>? other) && p.Value.SetEquals(other));

    public override int GetHashCode() => HashCode.Combine(Enabled, Sensitivity, Amount, Speed, Pairs.Count, Sections?.Count, Keys, Inputs.Count);

    /// <summary>The unit's ducking switched off, which writes no preset.</summary>
    public static ProDuckingTarget Off { get; } = new(false, ProDucking.Medium, ProDucking.Medium, ProDucking.Medium, new HashSet<int>());

    /// <summary>For the journal: off, or the pairs (by section, when it has sections), the inputs, the keys and the values.</summary>
    public override string ToString() => Enabled
        ? $"on, pairs {PairsText()}{InputsText()}, keys {Sources(Keys)}, sensitivity {Sensitivity}, amount {Amount}, speed {Speed}"
        : "off";

    private string PairsText()
    {
        static string List(IEnumerable<int> pairs) => pairs.Any() ? string.Join(" ", pairs.Order()) : "none";
        if (Sections is not { Count: > 0 } sections) return List(Pairs);
        return string.Join("; ", sections.OrderBy(s => s.Key).Select(s => $"{List(s.Value)} in section {s.Key}"));
    }

    private string InputsText() => Inputs.Count == 0 ? "" : $", inputs {string.Join(" and ", Inputs.Order().Select(ProMixSections.SourceName))}";

    private static string Sources(int mask)
    {
        List<string> names = [.. Enumerable.Range(0, ProMixSections.SourceCount).Where(m => (mask & 1 << m) != 0).Select(ProMixSections.SourceName)];
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }
}

/// <summary>Where the last ducking post stands on the primary unit.</summary>
public enum DeviceDuckingPhase
{
    /// <summary>No post in this connection, or the unit is no longer driven.</summary>
    None,
    /// <summary>A post awaits a write or readback.</summary>
    Pending,
    /// <summary>The unit holds the last target posted.</summary>
    Verified,
    /// <summary>The unit did not keep the target after two writes.</summary>
    Failed,
}

/// <summary>The device side's cached status. Reading it does no USB transfer.</summary>
public sealed record DeviceDuckingStatus(DeviceDuckingPhase Phase, ProDuckingTarget? Value = null, string? Reason = null)
{
    public static DeviceDuckingStatus None { get; } = new(DeviceDuckingPhase.None);

    /// <summary>Verified off, or no longer driven. A fresh connection must first receive its off post.</summary>
    public bool IsOff => Phase == DeviceDuckingPhase.None || Phase == DeviceDuckingPhase.Verified && Value is { Enabled: false };

    /// <summary>The last decoded blocks, when the device side has them.</summary>
    public DuckingReadback? Readback { get; init; }
}

/// <summary>The daemon's device writer. Core posts targets and never performs USB transfers.</summary>
public interface IDeviceDuckingPort
{
    /// <summary>Replace a pending target. Owner names the key channel for the journal. Ignore a unit no longer driven.</summary>
    void Post(string unit, ProDuckingTarget target, string owner);

    /// <summary>The cached status of the last post to this unit.</summary>
    DeviceDuckingStatus Status(string unit);
}

/// <summary>
/// A primary unit with hardware ducking, including any device-side refusal.
/// <see cref="ReachesJacks"/> says whether the unit's ducking is heard on
/// the jacks in the routing OpenXLR drives.
/// </summary>
public sealed record UnitDuckingSupport(string Name, string? Firmware, bool Ready, string? Reason = null, string? Note = null,
    bool ReachesJacks = true);

/// <summary>Posts changed targets once, repeating them when the device side reports a new connection.</summary>
internal sealed class DeviceDuckingBackend(IDeviceDuckingPort port, string unit)
{
    private ProDuckingTarget? _posted;
    public string Unit { get; } = unit;
    public DeviceDuckingStatus Status => port.Status(Unit);

    public void Apply(ProDuckingTarget target, string owner)
    {
        if (target.Equals(_posted) && Status.Phase != DeviceDuckingPhase.None) return;
        port.Post(Unit, target, owner);
        _posted = target;
    }
}
