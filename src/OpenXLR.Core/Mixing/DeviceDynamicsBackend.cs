using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>One input's compressor and expander on its unit, as the device side reports them; the phases are the EQ's.</summary>
public sealed record DeviceDynamicsStatus(DeviceEqPhase Phase, DeviceDynamics? Value = null, string? Reason = null)
{
    public static DeviceDynamicsStatus None { get; } = new(DeviceEqPhase.None);

    /// <summary>Whether the unit holds both switched off: verified off, or no longer driven at all.</summary>
    public bool IsOff => Phase == DeviceEqPhase.Verified && Value is { Compressor: false, Expander: false } || Phase == DeviceEqPhase.None;
}

/// <summary>
/// A microphone's compressor and expander in a unit's raw form and back.
/// The way to the unit rounds and clamps through <see cref="UnitDynamics"/>;
/// the way back reads the settings the unit holds into the software's
/// ranges, for a microphone that had none of its own.
/// </summary>
public static class DeviceDynamicsMapping
{
    /// <summary>A microphone's compressor and expander as its unit holds them.</summary>
    public static DeviceDynamics ToDevice(MicCompressor compressor, MicExpander expander)
    {
        MicCompressor c = UnitDynamics.Compressor(compressor);
        MicExpander e = UnitDynamics.Expander(expander);
        return new(c.Enabled, e.Enabled, new DeviceDynamicsValues(
            (int)c.Threshold, (int)c.Ratio, (int)c.Attack, (int)c.Release, (int)c.Makeup,
            (int)e.Threshold, (int)Math.Round(e.Ratio * WaveFxDynamics.ExpanderRatioScale, MidpointRounding.AwayFromZero),
            (int)e.Attack, (int)e.Release, (int)e.Range));
    }

    /// <summary>
    /// The settings a unit holds as the mixer takes them, snapped and inside
    /// the software's ranges, with the knee and the hold the software's
    /// defaults; null when the unit holds a value outside its own limits.
    /// </summary>
    public static (MicCompressor Compressor, MicExpander Expander)? FromDevice(DeviceDynamics held)
    {
        if (!WaveFxDynamics.Decodes(held)) return null;
        DeviceDynamicsValues v = held.Values!;
        static double In(DynamicsRange range, double value) => Math.Clamp(value, range.Minimum, range.Maximum);
        MicCompressor c = MicCompressor.Snap(MicCompressor.Default with
        {
            Enabled = held.Compressor,
            Threshold = In(MicCompressor.ThresholdRange, v.CompressorThreshold),
            Ratio = In(MicCompressor.RatioRange, v.CompressorRatio),
            Attack = In(MicCompressor.AttackRange, v.CompressorAttack),
            Release = In(MicCompressor.ReleaseRange, v.CompressorRelease),
            Makeup = In(MicCompressor.MakeupRange, v.CompressorMakeup),
        });
        MicExpander e = MicExpander.Snap(MicExpander.Default with
        {
            Enabled = held.Expander,
            Threshold = In(MicExpander.ThresholdRange, v.ExpanderThreshold),
            Ratio = In(MicExpander.RatioRange, (double)v.ExpanderRatio / WaveFxDynamics.ExpanderRatioScale),
            Range = In(MicExpander.RangeRange, v.ExpanderRange),
            Attack = In(MicExpander.AttackRange, v.ExpanderAttack),
            Release = In(MicExpander.ReleaseRange, v.ExpanderRelease),
        });
        return (c, e);
    }
}

/// <summary>
/// A microphone's compressor and expander on its unit's own DSP, as
/// <see cref="DeviceEqBackend"/> runs its EQ: values go to the device side
/// through <see cref="IDeviceEqPort.PostDynamics"/>, which writes them at
/// most ten times a second, the last always. A value equal to the one posted
/// last is not posted again. Releasing writes both switches off and leaves
/// the settings, which come back with the next hand-over.
/// </summary>
internal sealed class DeviceDynamicsBackend(IDeviceEqPort port, EqUnitInput target, string owner)
{
    private DeviceDynamics? _posted;

    public EqUnitInput Target { get; } = target;

    public DeviceDynamicsStatus Status => port.DynamicsStatus(Target.Unit, Target.Input);

    public void Apply(MicCompressor compressor, MicExpander expander)
    {
        DeviceDynamics raw = DeviceDynamicsMapping.ToDevice(compressor, expander);
        // A unit that connected again has forgotten nothing it holds, but
        // its device side has forgotten the post: say it again.
        if (raw.Equals(_posted) && Status.Phase != DeviceEqPhase.None) return;
        _posted = raw;
        port.PostDynamics(Target.Unit, Target.Input, raw, owner);
    }

    public void Release()
    {
        _posted = DeviceDynamics.Off;
        port.PostDynamics(Target.Unit, Target.Input, DeviceDynamics.Off, owner);
    }
}
