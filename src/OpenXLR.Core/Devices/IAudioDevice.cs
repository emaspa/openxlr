namespace OpenXLR.Core.Devices;

/// <summary>
/// A controllable audio interface. OpenXLR is not tied to one vendor: the Wave
/// XLR Pro is the first implementation, and other brands (GoXLR, Focusrite,
/// future Elgato revisions) can be added as further IAudioDevice types behind
/// the same daemon/UI/plugin surface. The state model is a superset snapshot;
/// <see cref="Capabilities"/> tells clients which fields a given device honours,
/// so setters for unsupported controls are simply no-ops.
/// </summary>
public interface IAudioDevice
{
    /// <summary>Stable identity of the device model (for UI labels, logs, plugin routing).</summary>
    DeviceInfo Info { get; }

    /// <summary>Which controls this device actually exposes.</summary>
    DeviceCapabilities Capabilities { get; }

    bool Connected { get; }

    void Connect();
    void Disconnect();

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
    void SetOutHp2(bool on) { }
    void SetOutUsbAux(bool on) { }
    void SetOutLineOut(bool on) { }
    void SetAuxLevelDb(double db) { }
    void SetAuxLevelLock(bool on) { }
}

/// <summary>Stable identity of a device model.</summary>
public sealed record DeviceInfo(string Vendor, string Model, ushort VendorId, ushort ProductId)
{
    public string DisplayName => $"{Vendor} {Model}";
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

    /// <summary>Per-jack routing of the hardware monitor bus (HP1/HP2/USB Aux out/Line out).</summary>
    public bool OutputRouting { get; init; }

    /// <summary>A controllable auxiliary input stage (level + lock).</summary>
    public bool AuxInput { get; init; }

    /// <summary>Number of XLR inputs the device has (the Pro has two).</summary>
    public int XlrInputs { get; init; } = 1;

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
    /// Wave XLR Pro does; the Wave XLR and the XLR Dock modules have no
    /// memory and come up with firmware defaults, so the daemon restores
    /// the last settings it saw (or the recall-on-connect profile) whenever
    /// one of them connects fresh, and offers a reset to those defaults.
    /// </summary>
    public bool RetainsSettings { get; init; } = true;
}
