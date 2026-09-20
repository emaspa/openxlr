namespace OpenXLR.Core.Devices;

/// <summary>
/// A full snapshot of the Wave XLR Pro's controllable state, as read from the
/// vendor blocks. Gain is expressed directly in dB (0..80), matching the
/// hardware byte and the ALSA capture-volume control.
/// </summary>
public sealed record DeviceState
{
    public int GainDb { get; init; }            // block 0x0004 off0, 0..80 dB
    public bool Mute { get; init; }             // block 0x0004 off1 bit0
    public bool LowCut { get; init; }           // block 0x0004 off1 bit4
    public bool Expander { get; init; }         // block 0x0004 off1 bit5
    public bool VoiceTune { get; init; }        // block 0x0004 off1 bit6
    public int VoiceTuneStrength { get; init; } // block 0x0004 off10, 0..100

    public double HpVolumeDb { get; init; }     // block 0x0005 off0, dB = -byte/4
    public double Hp2VolumeDb { get; init; }    // block 0x0005 off2 (second headphone out)
    public bool LowImpedance { get; init; }     // block 0x0005 off1 bit1

    /// <summary>
    /// Wave:3 only: the headphone mute the firmware keeps in its config block
    /// (byte 9) and, per public research not yet run here, asserts itself
    /// when the headphone level reaches its floor. Read-only; a headphone
    /// level written above the floor releases it. False on every other model.
    /// </summary>
    public bool HpMute { get; init; }

    public int Crossfade { get; init; }         // block 0x0001 off0, 0..200 (100 = centre)

    // Mic DSP, confirmed on the Pro against the logged capture of 2026-08-25:
    // phantom = off1 bit1, compressor = off1 bit7, ClipGuard = the struct's
    // offset-2 byte with 0x04 meaning DISABLED (stored here un-inverted).
    // Phantom is also live on the XLR Dock (config block byte 6, MK.1 dialect).
    public bool Phantom { get; init; }
    public bool ClipGuard { get; init; }
    public bool Compressor { get; init; }

    // Second XLR input (the Pro has XLR 1 and XLR 2; block 0x0004 holds one
    // 38-byte structure per input, XLR 2 at offset 38, same field layout).
    public int Gain2Db { get; init; }
    public bool Mute2 { get; init; }
    public bool LowCut2 { get; init; }
    public bool Expander2 { get; init; }
    public bool VoiceTune2 { get; init; }
    public int VoiceTuneStrength2 { get; init; }
    public bool Phantom2 { get; init; }
    public bool ClipGuard2 { get; init; }
    public bool Compressor2 { get; init; }

    // Physical output routing (block 0x0001 off90..93): whether each output
    // jack carries the hardware monitor bus. Confirmed by ear on both
    // headphone jacks; requires the commit block to take effect.
    public bool OutHp1 { get; init; }
    public bool OutHp2 { get; init; }
    public bool OutUsbAux { get; init; }
    public bool OutLineOut { get; init; }

    // The Pro's headphone ("Personal", selector 0x1e) mix membership, block
    // 0x0001 bytes 12 and 13, decoded by ear on hardware (issue #8): bit 5
    // of byte 12 sums USB return pair 2/3 (where the Monitor mix streams)
    // into the jacks; bit 1 of byte 13 is the mic's direct, zero-latency
    // path into them. Wave Link on Windows may leave either state behind.
    public bool HpMixMonitorReturn { get; init; }
    public bool HpMixMicDirect { get; init; }

    /// <summary>
    /// Whether the aux mix's Music-return matrix cell is open (level 0 dB +
    /// membership bit). Required, with the aux selector, for USB playback to
    /// reach the aux port; the receiving side must (re)open its input stream
    /// after this is set, because the aux stream latches its routing at open.
    /// </summary>
    public bool AuxReturnEnabled { get; init; }

    // USB Aux input stage (block 0x0004 tail): level -60..0 dB and level lock.
    public double AuxLevelDb { get; init; }
    public bool AuxLevelLock { get; init; }

    /// <summary>
    /// Daemon-enforced software gain lock (Wave Link's Gain Lock for devices
    /// that keep it app-side). Not a hardware field: the daemon stamps it
    /// onto every snapshot and rejects gain writes while it is set.
    /// </summary>
    public bool GainLocked { get; init; }

    /// <summary>
    /// Daemon-stamped, per XLR input: a 48V change was written within the
    /// last 15 s. The Pro's firmware mutes the input for ~13 s around every
    /// phantom transition (anti-thump) and unmutes it itself, ignoring host
    /// unmutes meanwhile; clients use this to present that hold instead of a
    /// stuck mute button.
    /// </summary>
    public bool PhantomSettling { get; init; }
    public bool PhantomSettling2 { get; init; }

    /// <summary>Whole seconds left in the settling hold (0 when not settling),
    /// so clients can show a countdown on the held mute.</summary>
    public int PhantomSettleSeconds { get; init; }
    public int PhantomSettleSeconds2 { get; init; }
}
