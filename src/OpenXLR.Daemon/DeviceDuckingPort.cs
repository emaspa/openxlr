using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// The daemon's side of the mixer's ducking port: posts and statuses for the
/// primary unit, which unit that is and whether it has hardware ducking, and
/// a signal when a status settles. Tests replace it with a fake.
/// </summary>
internal interface IDaemonDuckingPort : IDeviceDuckingPort
{
    /// <summary>The primary driven unit with hardware ducking and its support, or null.</summary>
    (string Unit, UnitDuckingSupport Support)? Primary();

    /// <summary>A ducking status settled or the unit's readback changed; may be raised under a device lock, so handlers only hand off.</summary>
    event Action? StatusChanged;
}

/// <summary>
/// The mixer's way to the primary unit's hardware ducking, as
/// <see cref="DeviceEqPort"/> is for the EQ. Only the primary unit runs the
/// setup; additional interfaces never take a post. A post for a unit the
/// device manager does not drive is dropped, and its status is none.
/// </summary>
internal sealed class DeviceDuckingPort : IDaemonDuckingPort
{
    private readonly DeviceManager _primary;

    public DeviceDuckingPort(DeviceManager primary)
    {
        _primary = primary;
        _primary.DuckingStatusChanged += () => StatusChanged?.Invoke();
    }

    public event Action? StatusChanged;

    public void Post(string unit, ProDuckingTarget target, string owner) => _primary.PostDucking(unit, target, owner);

    public DeviceDuckingStatus Status(string unit) => _primary.DuckingStatus(unit);

    public double? Reduction(string unit) => _primary.UnitDuckReduction(unit);

    public ProMeterReading? Meters(string unit) => _primary.UnitMeters(unit);

    public (string Unit, UnitDuckingSupport Support)? Primary() => _primary.PublishedDuckingSupport;
}
