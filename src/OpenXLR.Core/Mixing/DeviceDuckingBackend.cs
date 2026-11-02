using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>The three values, preset words or numbers, and the USB return pairs the primary unit should duck.</summary>
public sealed record ProDuckingTarget(bool Enabled, DuckingLevel Sensitivity, DuckingLevel Amount, DuckingSpeed Speed, IReadOnlySet<int> Pairs)
{
    public bool Equals(ProDuckingTarget? other)
        => other is not null && Enabled == other.Enabled && Sensitivity == other.Sensitivity
            && Amount == other.Amount && Speed == other.Speed && Pairs.SetEquals(other.Pairs);

    public override int GetHashCode() => HashCode.Combine(Enabled, Sensitivity, Amount, Speed, Pairs.Count);

    /// <summary>The unit's ducking switched off, which writes no preset.</summary>
    public static ProDuckingTarget Off { get; } = new(false, ProDucking.Medium, ProDucking.Medium, ProDucking.Medium, new HashSet<int>());

    /// <summary>For the journal: off, or the pairs and values.</summary>
    public override string ToString() => Enabled
        ? $"on, pairs {(Pairs.Count == 0 ? "none" : string.Join(" ", Pairs.Order()))}, sensitivity {Sensitivity}, amount {Amount}, speed {Speed}"
        : "off";
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
