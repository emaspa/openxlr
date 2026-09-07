namespace OpenXLR.Core.Devices;

/// <summary>
/// OpenXLR's baseline for an interface that keeps its settings in its own
/// memory. Such a device never boots to a clean state, so there is nothing
/// to record after a power cycle; instead a known, safe set is written on
/// request: a moderate gain, every processing stage and phantom power off,
/// every level at half, the headphone crossfade fully on PC. Output routing
/// and the monitor mix membership are
/// left as they are, since the daemon drives those from the mixer.
/// </summary>
public static class DeviceDefaults
{
    /// <summary>Whether a baseline exists for this model.</summary>
    public static bool HasBaseline(DeviceInfo info) => info.ProductId == WaveXlrProDevice.ProductId;

    /// <summary>The baseline for <paramref name="info"/> over the current state, or null for a model without one.</summary>
    public static DeviceState? Baseline(DeviceInfo info, DeviceState current)
    {
        if (!HasBaseline(info)) return null;
        return current with
        {
            GainDb = 30, Gain2Db = 30,
            Mute = false, Mute2 = false,
            LowCut = false, LowCut2 = false,
            Expander = false, Expander2 = false,
            VoiceTune = false, VoiceTune2 = false,
            VoiceTuneStrength = 50, VoiceTuneStrength2 = 50,
            Phantom = false, Phantom2 = false,
            ClipGuard = false, ClipGuard2 = false,
            Compressor = false, Compressor2 = false,
            HpVolumeDb = -30, Hp2VolumeDb = -30,   // half of the 60 dB attenuator range
            LowImpedance = false,
            Crossfade = 200,                       // all the way to PC: the mixer's monitor mix, not the direct mic
            AuxLevelDb = -30,                      // half of the -60..0 dB range
            AuxLevelLock = false,
        };
    }
}
