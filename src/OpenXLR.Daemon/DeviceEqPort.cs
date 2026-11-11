using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// The mixer's way to a unit's EQ: a post or a status question goes, by the
/// unit's instance id, to the device manager driving it, the primary's or an
/// additional interface's. A unit no manager drives takes no post and
/// reports nothing.
/// </summary>
internal sealed class DeviceEqPort : IDeviceEqPort
{
    private readonly DeviceManager _primary;
    private readonly WaveInterfaces? _interfaces;

    public DeviceEqPort(DeviceManager primary, WaveInterfaces? interfaces)
    {
        _primary = primary;
        _interfaces = interfaces;
        _primary.EqStatusChanged += () => StatusChanged?.Invoke();
        if (_interfaces is not null) _interfaces.EqStatusChanged += () => StatusChanged?.Invoke();
    }

    /// <summary>An EQ status settled on some unit; raised under a device lock, so handlers only hand off.</summary>
    public event Action? StatusChanged;

    private DeviceManager? For(string unit)
        => _primary.PublishedInfo?.InstanceId == unit ? _primary : _interfaces?.Session(unit);

    public void Post(string unit, int input, DeviceEq eq, string owner) => For(unit)?.PostEq(input, eq, owner);

    public DeviceEqStatus Status(string unit, int input) => For(unit)?.EqStatus(input) ?? DeviceEqStatus.None;

    public void PostDynamics(string unit, int input, DeviceDynamics dynamics, string owner) => For(unit)?.PostDynamics(input, dynamics, owner);

    public DeviceDynamicsStatus DynamicsStatus(string unit, int input) => For(unit)?.DynamicsStatus(input) ?? DeviceDynamicsStatus.None;

    public DeviceDynamics? HeldDynamics(string unit, int input) => For(unit)?.HeldDynamics(input);

    public Mk2MeterReading? DynamicsMeters(string unit, int input) => For(unit)?.UnitDynamicsMeters(input);

    /// <summary>Every driven unit whose model has a hardware EQ, by instance id, for the mixer.</summary>
    public IReadOnlyDictionary<string, UnitEqSupport> Units()
    {
        var units = new Dictionary<string, UnitEqSupport>(StringComparer.Ordinal);
        if (_primary.PublishedInfo?.InstanceId is { } primary && _primary.PublishedEqSupport is { } support) units[primary] = support;
        if (_interfaces is not null)
            foreach ((string id, UnitEqSupport additional) in _interfaces.EqSupport()) units.TryAdd(id, additional);
        return units;
    }
}
