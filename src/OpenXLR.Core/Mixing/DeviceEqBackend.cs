using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>Where a device-run EQ stands on its unit's input.</summary>
public enum DeviceEqPhase
{
    /// <summary>Nothing posted to this input in the unit's current connection, or the unit is not driven.</summary>
    None,
    /// <summary>A value waits to be written or for the poll to read it back.</summary>
    Pending,
    /// <summary>The unit holds the last value posted; <see cref="DeviceEqStatus.Value"/> is it.</summary>
    Verified,
    /// <summary>The unit did not keep a value written to it twice; <see cref="DeviceEqStatus.Reason"/> says so.</summary>
    Failed,
}

/// <summary>One input's EQ on its unit, as the device side reports it.</summary>
public sealed record DeviceEqStatus(DeviceEqPhase Phase, DeviceEq? Value = null, string? Reason = null)
{
    public static DeviceEqStatus None { get; } = new(DeviceEqPhase.None);

    /// <summary>Whether the unit holds its EQ switched off: verified off, or no longer driven at all.</summary>
    public bool IsOff => Phase == DeviceEqPhase.Verified && Value is { Enabled: false } || Phase == DeviceEqPhase.None;
}

/// <summary>
/// What the mixer hands a unit's EQ to. The mixer never does USB: the
/// daemon implements this over the device manager of the primary unit and
/// of each additional one, which write at their own pace.
/// </summary>
public interface IDeviceEqPort
{
    /// <summary>
    /// The EQ this input should hold, replacing any value not yet written; a
    /// value with no bands names the switch alone. <paramref name="owner"/>
    /// names the channel for the journal. Dropped when the unit is not driven.
    /// </summary>
    void Post(string unit, int input, DeviceEq eq, string owner);

    /// <summary>Where the last post to this input stands.</summary>
    DeviceEqStatus Status(string unit, int input);

    /// <summary>
    /// The compressor and expander this input should hold, as <see cref="Post"/>
    /// for the EQ; a value with no settings names the two switches alone.
    /// </summary>
    void PostDynamics(string unit, int input, DeviceDynamics dynamics, string owner) { }

    /// <summary>Where the last compressor and expander post to this input stands.</summary>
    DeviceDynamicsStatus DynamicsStatus(string unit, int input) => DeviceDynamicsStatus.None;

    /// <summary>The compressor and expander the input held at the last read, raw; null when not known.</summary>
    DeviceDynamics? HeldDynamics(string unit, int input) => null;

    /// <summary>
    /// No microphone holds this input's compressor and expander any more:
    /// what was posted to it is forgotten, nothing is written back over a
    /// change made elsewhere, and the unit's own switches are its controls
    /// again once no input of it is held.
    /// </summary>
    void ForgetDynamics(string unit, int input) { }

    /// <summary>
    /// The unit's own meters, for the levels of the compressor and expander
    /// it runs on this input. Asking keeps them read for a second; null
    /// while none is read yet, after a read that failed, on a unit that does
    /// not report them, or when this unit is not driven. Never waits for USB.
    /// </summary>
    Mk2MeterReading? DynamicsMeters(string unit, int input) => null;
}

/// <summary>
/// A unit that can run the microphone EQ, as the mixer sees it: its name,
/// its firmware, and whether it runs it now and why not.
/// </summary>
public sealed record UnitEqSupport(string Name, string? Firmware, bool Ready, string? Reason = null, string? Note = null)
{
    /// <summary>
    /// The microphone inputs the unit has. An input past them is no EQ
    /// carrier: its channel's EQ runs in software, with nothing failed.
    /// </summary>
    public int Inputs { get; init; } = 1;

    /// <summary>The first firmware the model runs the EQ on, as major.minor.patch.</summary>
    public string MinimumFirmware { get; init; } = WaveFxEq.Format(WaveFxEq.Mk2EqFirmware);

    /// <summary>The widest band gain, in dB either way, the unit's EQ is written.</summary>
    public double MaxGain { get; init; } = WaveFxEq.DefaultMaxGainDb;

    /// <summary>
    /// Whether the unit can hold <paramref name="eq"/>: no more bands than it
    /// has, and every band's gain within <see cref="MaxGain"/>. A gain is held
    /// to it whatever the band's type, since the unit stores every band's gain.
    /// </summary>
    public bool Holds(MicEq eq)
        => eq.Bands.Count is >= MicEq.MinimumBands and <= WaveFxEq.Bands && eq.Bands.All(b => Math.Abs(b.Gain) <= MaxGain);

    /// <summary>
    /// Whether the unit runs the microphone compressor and expander now, and
    /// why not; null on a model without them, whose microphones run them in
    /// software.
    /// </summary>
    public DeviceEqSupport? Dynamics { get; init; }

    /// <summary>
    /// Whether the unit reports the levels of the compressor and expander it
    /// runs (<see cref="DeviceCapabilities.DynamicsMeters"/>), which
    /// <see cref="IDeviceEqPort.DynamicsMeters"/> then gives.
    /// </summary>
    public bool DynamicsLevels { get; init; }

    /// <summary>
    /// The inputs whose capture the unit sends raw, before its processing,
    /// bit 0 for input 1 and bit 1 for input 2. The computer does not hear
    /// the unit's EQ, compressor or expander on such an input, so its
    /// microphone runs them in software.
    /// </summary>
    public int RawCaptureInputs { get; init; }

    /// <summary>Whether the unit sends input <paramref name="input"/> (1 or 2) raw.</summary>
    public bool RawCaptures(int input) => input is >= 1 and <= 31 && (RawCaptureInputs & 1 << input - 1) != 0;
}

/// <summary>One input of one unit, by the unit's instance id and the input's number (1 or 2).</summary>
internal readonly record struct EqUnitInput(string Unit, int Input)
{
    /// <summary>The key a failure is remembered under.</summary>
    public string Key => $"eqdev:{Unit}:{Input}";
}

/// <summary>
/// The microphone EQ in a unit's raw form and back. The two grids agree:
/// frequency, gain, type and the switches map one to one, and Q, stepped in
/// log10 by 0.016 here and by 1/64 on the unit, maps each of the 126 grid
/// points to its own byte, round(-64 + 1.024 k), and each byte back to the
/// nearest point, round((b + 64) / 1.024). Bytes -43, 0 and 43 are reached by
/// no point; 0, the one exact tie, goes to k 63.
/// </summary>
public static class DeviceEqMapping
{
    /// <summary>The unit's Q byte for a grid step k (0 to 125).</summary>
    public static int QByte(int k) => (int)Math.Round((1024.0 * k - 64000) / 1000, MidpointRounding.AwayFromZero);

    /// <summary>The grid step nearest a unit's Q byte (-64 to 64).</summary>
    public static int QStep(int qByte) => (int)Math.Round((qByte + 64) * 1000 / 1024.0, MidpointRounding.AwayFromZero);

    private static int StepOf(double q) => (int)Math.Round((MicEq.QLog10(q) + 1) / MicEq.QLog10Step, MidpointRounding.AwayFromZero);

    /// <summary>A microphone's EQ as its unit holds it.</summary>
    public static DeviceEq ToDevice(MicEq eq)
    {
        if (MicEq.Check(eq) is not null || eq.Bands.Count > WaveFxEq.Bands)
            throw new ArgumentException("The device EQ supports one to four bands.", nameof(eq));
        return new(eq.Enabled, [.. eq.Bands.Select(ToDevice),
            .. Enumerable.Repeat(ToDevice(new EqBand()), WaveFxEq.Bands - eq.Bands.Count)]);
    }

    /// <summary>One band as its unit holds it.</summary>
    public static DeviceEqBand ToDevice(EqBand band)
        => new(band.Enabled, MicEq.TypeIndex(band.Type), (int)Math.Round(band.Gain * 4, MidpointRounding.AwayFromZero),
            (int)Math.Round(band.Frequency, MidpointRounding.AwayFromZero), QByte(StepOf(band.Q)));

    /// <summary>A unit's EQ as the mixer holds it, or null when a band is outside the EQ's ranges.</summary>
    public static MicEq? FromDevice(DeviceEq eq)
    {
        if (!WaveFxEq.Decodes(eq)) return null;
        return new MicEq
        {
            Enabled = eq.Enabled,
            Bands = [.. eq.Bands!.Select(b => new EqBand
            {
                Enabled = b.Enabled,
                Type = MicEq.Types[b.Type],
                Frequency = b.Frequency,
                Gain = b.Gain / 4.0,
                Q = Math.Pow(10, -1 + MicEq.QLog10Step * Math.Clamp(QStep(b.Q), 0, 125)),
            })],
        };
    }
}

/// <summary>
/// The microphone EQ on a unit's own DSP: values go to the device side
/// through <see cref="IDeviceEqPort"/>, which writes them at most ten times a
/// second, the last always. Nothing starts and nothing is linked; a value
/// equal to the one posted last is not posted again. Releasing writes the
/// switch off and leaves the bands, which come back with the next hand-over.
/// </summary>
internal sealed class DeviceEqBackend(IDeviceEqPort port, EqUnitInput target, string owner) : IMicEqBackend
{
    private MicEq? _applied;
    private DeviceEq? _posted;

    public string Kind => "device";
    public EqUnitInput Target { get; } = target;

    public DeviceEqStatus Status => port.Status(Target.Unit, Target.Input);

    public void Apply(MicEq eq)
    {
        _applied = eq;
        DeviceEq raw = DeviceEqMapping.ToDevice(eq);
        // A unit that connected again has forgotten nothing it holds, but
        // its device side has forgotten the post: say it again.
        if (raw.Equals(_posted) && Status.Phase != DeviceEqPhase.None) return;
        _posted = raw;
        port.Post(Target.Unit, Target.Input, raw, owner);
    }

    public void ApplyBand(int band, EqBand value) => Apply((_applied ?? MicEq.Default).WithBand(band, value));

    public void SetEnabled(bool on) => Apply((_applied ?? MicEq.Default) with { Enabled = on });

    public void Release()
    {
        _posted = DeviceEq.Off;
        port.Post(Target.Unit, Target.Input, DeviceEq.Off, owner);
    }
}
