namespace OpenXLR.Core.Devices;

/// <summary>
/// A controllable audio interface. OpenXLR is not tied to one vendor: the Wave
/// XLR Pro is the first implementation, and other brands (GoXLR, Focusrite,
/// future Elgato revisions) can be added as further IAudioDevice types behind
/// the same daemon/UI/plugin surface. The state model is a superset snapshot;
/// <see cref="Capabilities"/> tells clients which fields a given device honours,
/// so setters for unsupported controls are simply no-ops.
/// </summary>
public interface IAudioDevice : IDisposable
{
    /// <summary>Stable identity of the device model (for UI labels, logs, plugin routing).</summary>
    DeviceInfo Info { get; }

    /// <summary>Which controls this device actually exposes.</summary>
    DeviceCapabilities Capabilities { get; }

    bool Connected { get; }

    /// <summary>
    /// What a connect found worth telling the operator once: a control the
    /// unit lacks and how it is reached instead. Null when nothing is
    /// unusual. Read after <see cref="Connect"/>.
    /// </summary>
    string? ConnectionNote => null;

    /// <summary>The unit's firmware version as it reports it, or null where it is not read. Read after <see cref="Connect"/>.</summary>
    string? Firmware => null;

    /// <summary>
    /// Whether this unit runs the microphone EQ on its own DSP, and why not
    /// when its model can but this unit does not. Null on a model without a
    /// hardware EQ. Read after <see cref="Connect"/>;
    /// <see cref="DeviceCapabilities.HardwareEq"/> is its <c>Ready</c>.
    /// </summary>
    DeviceEqSupport? EqSupport => null;

    void Connect();
    void Disconnect();

    /// <summary>
    /// Reset the unit on the USB bus (libusb_reset_device through its open
    /// handle): 0 or a negative libusb error, -5 when it re-enumerated, which
    /// a unit whose audio the kernel drives does. Its card and its PipeWire
    /// nodes go away and come back, and the daemon reconnects it as after a
    /// replug. Only a model with <see cref="DeviceCapabilities.CaptureRecovery"/>
    /// is reset.
    /// </summary>
    /// <exception cref="InvalidOperationException">The model is never reset, or the unit is not open.</exception>
    int ResetUsb() => throw new InvalidOperationException("this unit is not reset on USB");

    /// <summary>Read every supported control field into one snapshot.</summary>
    DeviceState ReadState();

    void SetGainDb(int db);
    void SetMute(bool on);
    void SetLowCut(bool on);
    void SetExpander(bool on);
    void SetVoiceTune(bool on);
    void SetVoiceTuneStrength(int value);
    void SetHpVolumeDb(double db);
    void SetLowImpedance(bool on);
    void SetCrossfade(int value);
    void SetPhantom(bool on);
    void SetClipGuard(bool on);
    void SetCompressor(bool on);

    // Second XLR input. Default no-ops so single-input devices need no code;
    // a device with XlrInputs > 1 overrides them.
    void SetHp2VolumeDb(double db) { }
    void SetGain2Db(int db) { }
    void SetMute2(bool on) { }
    void SetLowCut2(bool on) { }
    void SetExpander2(bool on) { }
    void SetVoiceTune2(bool on) { }
    void SetVoiceTuneStrength2(int value) { }
    void SetPhantom2(bool on) { }
    void SetClipGuard2(bool on) { }
    void SetCompressor2(bool on) { }

    // The microphone EQ on the unit's own DSP. Default no-ops, like the
    // second-input setters; a unit whose capabilities report HardwareEq
    // overrides them.
    /// <summary>
    /// Write one input's EQ (1 or 2) through a read of the settings block, an
    /// edit of the fields the EQ owns and a write of the whole block. A value
    /// with no bands writes the switch alone. False when the unit already
    /// held it and nothing was written.
    /// </summary>
    /// <exception cref="InvalidOperationException">The unit does not run the EQ, or a transfer failed.</exception>
    bool SetEq(int input, DeviceEq eq) => throw new InvalidOperationException("this unit does not run the EQ");

    /// <summary>
    /// One input's EQ as the last poll read it, raw, without a transfer; null
    /// where the unit does not run the EQ or nothing has been read yet.
    /// </summary>
    DeviceEq? ReadEq(int input) => null;

    // Ducking on the unit's own DSP (the Wave XLR Pro). A unit whose
    // capabilities report Ducking overrides both.
    /// <summary>The unit's ducking as the last poll read it, without a transfer; null where it has none or nothing was read.</summary>
    DuckingReadback? ReadDucking() => null;

    /// <summary>
    /// Bring the unit's ducking to a target through a read of each block it
    /// lives in, an edit of the fields ducking owns and a write of each block
    /// that changed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The unit has no hardware ducking.</exception>
    DuckingWrite SetDucking(OpenXLR.Core.Mixing.ProDuckingTarget target) => throw new InvalidOperationException("this unit has no hardware ducking");

    // Physical output routing and aux input. Default no-ops; devices with
    // OutputRouting / AuxInput capabilities override them.
    /// <summary>
    /// Raw vendor state for diagnostics: block name to hex payload. Default
    /// empty for devices without a block protocol.
    /// </summary>
    IReadOnlyDictionary<string, string> DumpBlocks() => new Dictionary<string, string>();

    void SetOutHp1(bool on) { }
    /// <summary>Wave XLR Pro: whether USB return pair 2/3 (the Monitor stream) is summed into the headphone mix.</summary>
    void SetHpMixMonitorReturn(bool on) { }
    /// <summary>Wave XLR Pro: the mic's zero-latency hardware path into the headphone mix.</summary>
    void SetHpMixMicDirect(bool on) { }
    /// <summary>Wave XLR Pro: that path's level in the headphone mix, in dB.</summary>
    void SetHpMixMicLevelDb(double db) { }
    /// <summary>Wave XLR Pro: XLR 2's zero-latency hardware path into the headphone mix.</summary>
    void SetHpMixMic2Direct(bool on) { }
    /// <summary>Wave XLR Pro: that path's level in the headphone mix, in dB.</summary>
    void SetHpMixMic2LevelDb(double db) { }
    void SetOutHp2(bool on) { }
    void SetOutUsbAux(bool on) { }
    void SetOutLineOut(bool on) { }
    void SetAuxLevelDb(double db) { }
    void SetAuxLevelLock(bool on) { }

    /// <summary>
    /// The Wave XLR Pro's analog Line In gain in dB, held to 0 to the
    /// model's <see cref="DeviceCapabilities.LineInGainMaxDb"/>; returns the value
    /// written. A device with <see cref="DeviceCapabilities.LineInput"/>
    /// overrides it.
    /// </summary>
    int SetLineInGainDb(int db) => throw new InvalidOperationException("this unit has no Line In gain");

    /// <summary>
    /// The Wave XLR Pro's Line In level lock: while on, the unit ignores Line
    /// In gain writes through its ALSA control. A device with
    /// <see cref="DeviceCapabilities.LineInput"/> overrides it.
    /// </summary>
    void SetLineInLevelLock(bool on) => throw new InvalidOperationException("this unit has no Line In level lock");
}

/// <summary>Stable identity of a device model.</summary>
public sealed record DeviceInfo(string Vendor, string Model, ushort VendorId, ushort ProductId)
{
    /// <summary>Where this unit sits on the bus; null for a backend built without one.</summary>
    public UsbLocation? Location { get; init; }

    /// <summary>
    /// One physical unit: the model id plus a hash of its serial, or of its
    /// USB port when it has no unique serial. Without a location it is the
    /// model id alone.
    /// </summary>
    public string InstanceId => $"{VendorId:x4}:{ProductId:x4}" + (Location is { } location ? "@" + location.Key : "");

    public string DisplayName => $"{Vendor} {Model}";

    /// <summary>
    /// The model as it appears inside the card's PipeWire node names, which
    /// udev builds from the USB product string with each space and colon
    /// turned into an underscore: "Wave XLR" is Wave_XLR, and "Wave:3" is
    /// Wave_3 (the source in a Wave:3 owner's dump, LukasParke/wave3-research
    /// pipewire/pactl-source-wave3.txt, is named
    /// alsa_input.usb-Elgato_Systems_Elgato_Wave_3_...). The daemon finds the
    /// capture node and the card by this fragment.
    /// </summary>
    public string ModelNameFragment => Model.Replace(' ', '_').Replace(':', '_');

    /// <summary>
    /// The model fragment with this unit's serial and the separator ALSA
    /// writes after it, so a serial that extends another unit's serial cannot
    /// match. The model fragment alone when the unit has no usable serial.
    /// </summary>
    public string NodeNameFragment => ModelNameFragment
        + (Location?.Serial is { Length: > 0 } serial && serial.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? "_" + serial + "-" : "");

    /// <summary>
    /// The fragments to look for, most specific first. udev builds the serial
    /// part of a node name from the descriptor itself, sysfs gives it to us
    /// separately, and a model whose two spellings differ would leave the
    /// serial fragment matching nothing, so the model fragment comes second.
    /// </summary>
    public IReadOnlyList<string> NodeNameFragments => NodeNameFragment == ModelNameFragment
        ? [ModelNameFragment] : [NodeNameFragment, ModelNameFragment];

    /// <summary>
    /// <see cref="NodeNameFragments"/> while these units are attached. The
    /// model fragment is left out while another unit's model would match it
    /// too ("Wave_XLR" is inside "Wave_XLR_Pro"): with this unit's node gone
    /// for a moment, it would find the other unit's node instead.
    /// </summary>
    public IReadOnlyList<string> FragmentsAmong(IEnumerable<DeviceInfo> attached)
        => attached.Any(other => other.InstanceId != InstanceId
                && other.ModelNameFragment.Contains(ModelNameFragment, StringComparison.OrdinalIgnoreCase))
            ? [NodeNameFragment] : NodeNameFragments;
}

/// <summary>Flags for which controls a device exposes, so the UI/plugin adapt per model.</summary>
public sealed record DeviceCapabilities
{
    public bool Gain { get; init; }
    public bool Mute { get; init; }
    public bool LowCut { get; init; }
    public bool Expander { get; init; }
    public bool VoiceTune { get; init; }
    public bool HpVolume { get; init; }
    public bool LowImpedance { get; init; }
    public bool Crossfade { get; init; }
    public bool Phantom { get; init; }
    public bool ClipGuard { get; init; }
    public bool Compressor { get; init; }

    /// <summary>
    /// The unit's DSP runs the microphone EQ and the driver can write it:
    /// the Wave XLR MK.2 and the XLR Dock MK.2 from firmware 1.0.15 and the
    /// Wave:3 MK.2 from its own firmware 0.2.3, whose settings block holds
    /// four bands and a switch they apply (measured on all three), and the Wave XLR Pro from firmware 1.0.14 on each of its two
    /// inputs (captured from Wave Link; not yet measured by ear on Linux).
    /// False on every other model and on older firmware, where the
    /// microphone's EQ runs in software.
    /// </summary>
    public bool HardwareEq { get; init; }

    /// <summary>
    /// The unit ducks its own outputs while the key input is loud and the
    /// driver can set it: the Wave XLR Pro, keyed by XLR 1, on the USB
    /// playback pairs OpenXLR streams to. False on every other model.
    /// </summary>
    public bool Ducking { get; init; }

    /// <summary>Per-jack routing of the hardware monitor bus (HP1/HP2/USB Aux out/Line out).</summary>
    public bool OutputRouting { get; init; }

    /// <summary>A controllable auxiliary input stage (level + lock).</summary>
    public bool AuxInput { get; init; }

    /// <summary>
    /// An analog Line In with a gain of its own, separate from the USB Aux
    /// input: the Wave XLR Pro, whose capture source carries it on AUX6 and
    /// AUX7. False on every other model.
    /// </summary>
    public bool LineInput { get; init; }

    /// <summary>
    /// The highest Line In gain the driver writes, in dB; the lowest is 0.
    /// 40 on the Wave XLR Pro, whose unit takes 80 but hisses above about
    /// 20 dB; 0 on a model without <see cref="LineInput"/>. A unit set
    /// higher elsewhere still reads back its own value.
    /// </summary>
    public int LineInGainMaxDb { get; init; }

    /// <summary>
    /// The highest gain the device takes, in dB; the lowest is 0. Each input
    /// has the same range. Clients size their gain controls to it.
    /// </summary>
    public int GainMaxDb { get; init; } = 80;

    /// <summary>Number of XLR inputs the device has (the Pro has two).</summary>
    public int XlrInputs { get; init; } = 1;

    /// <summary>
    /// Whether the inputs are XLR jacks. False on a USB microphone (Wave:3,
    /// Wave:1), whose one input is its built-in capsule: its strip is named
    /// "Mic 1", not "XLR 1", and wears the microphone icon.
    /// </summary>
    public bool XlrJacks { get; init; } = true;

    /// <summary>
    /// Whether the device has physical controls (dials, buttons) that write
    /// hardware state directly. A daemon-side gain lock cannot stop those,
    /// so the lock only shows for devices without them.
    /// </summary>
    public bool PhysicalControls { get; init; }

    /// <summary>Number of headphone outputs (the Pro has two).</summary>
    public int HpOutputs { get; init; } = 1;

    /// <summary>
    /// Whether the hardware keeps its settings across a power cycle. The
    /// Wave XLR Pro and the MK.2 family do; the Wave XLR and the first XLR
    /// Dock have no memory and come up with firmware defaults, so the daemon restores
    /// the last settings it saw (or the recall-on-connect profile) whenever
    /// one of them connects fresh, and offers a reset to those defaults.
    /// </summary>
    public bool RetainsSettings { get; init; } = true;

    /// <summary>
    /// Whether OpenXLR ships a baseline for this model (see
    /// <see cref="DeviceDefaults"/>), so a device that keeps its settings can
    /// still be reset to a known state.
    /// </summary>
    public bool BuiltInDefaults { get; init; }

    /// <summary>
    /// Whether the daemon brings the unit's microphone back by resetting the
    /// unit on USB when its capture stalls under its own playback, and
    /// offers the same as the recoverCapture command. True on the Wave:3
    /// MK.2 only: its capture stopped in the kernel, or delivered a steady
    /// buzz, while its headphones played, and a USB reset with capture
    /// started before playback cleared it every time.
    /// </summary>
    public bool CaptureRecovery { get; init; }
}
