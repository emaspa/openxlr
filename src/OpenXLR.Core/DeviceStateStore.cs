using System.Text.Json;
using System.Text.Json.Serialization;
using OpenXLR.Core.Devices;

namespace OpenXLR.Core;

/// <summary>
/// Per-device hardware snapshots the daemon keeps for interfaces without
/// settings memory (<see cref="DeviceCapabilities.RetainsSettings"/> false):
/// the last settings it saw, restored on every fresh connect so the device
/// comes back as it was left, and the firmware defaults read after a power
/// cycle, so "reset to defaults" has something to write once the daemon
/// would otherwise restore the last settings for ever. A unit the desktop
/// writes to when its card appears (<see cref="IAudioDevice.DesktopRestoresControls"/>)
/// keeps its last settings here too, for its mute and headphone level. Each
/// also records the boot and USB address it was last opened at
/// (<see cref="Sighting"/>), so a daemon start can tell a unit that stayed on
/// the bus from one that was replugged or rebooted with the system. One folder per
/// device under the XDG config directory (devices/&lt;vid-pid&gt;/).
/// </summary>
public static class DeviceStateStore
{
    private const int MaxStateBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Root => Path.Combine(OpenXlrPaths.ConfigDir, "devices");

    private static string Dir(string deviceId) => Path.Combine(Root, deviceId.Replace(':', '-'));
    private static string LastPath(string deviceId) => Path.Combine(Dir(deviceId), "last-state.json");
    private static string DefaultsPath(string deviceId) => Path.Combine(Dir(deviceId), "defaults.json");
    private static string SightingPath(string deviceId) => Path.Combine(Dir(deviceId), "seen.json");
    private const string BootIdPath = "/proc/sys/kernel/random/boot_id";

    /// <summary>
    /// Where the daemon last opened a device: the kernel's id for that boot
    /// of the system and the device's USB bus and address, which change when
    /// it is replugged. Null bus and address for a device without a location.
    /// </summary>
    public sealed record Sighting(string Boot, byte? Bus, byte? Address);

    /// <summary>The settings the daemon last saw on this device, or null.</summary>
    public static DeviceState? LoadLast(string deviceId) => Load(LastPath(deviceId));

    /// <summary>The firmware defaults recorded after a power cycle, or null.</summary>
    public static DeviceState? LoadDefaults(string deviceId) => Load(DefaultsPath(deviceId));

    public static void SaveLast(string deviceId, DeviceState state) => Save(deviceId, LastPath(deviceId), state);

    public static void SaveDefaults(string deviceId, DeviceState state) => Save(deviceId, DefaultsPath(deviceId), state);

    /// <summary>Where the daemon last opened this device, or null.</summary>
    public static Sighting? LoadSighting(string deviceId)
    {
        try
        {
            Sighting? seen = JsonSerializer.Deserialize<Sighting>(RegularFile.ReadText(SightingPath(deviceId), MaxStateBytes), Json);
            return string.IsNullOrEmpty(seen?.Boot) ? null : seen;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return null; }
    }

    public static void SaveSighting(string deviceId, Sighting seen)
        => OpenXlrPaths.WriteAtomicJson(SightingPath(deviceId), seen, Json);

    /// <summary>The kernel's id for this boot of the system, or null where it cannot be read.</summary>
    public static string? CurrentBoot()
    {
        try
        {
            string id = File.ReadAllText(BootIdPath).Trim();
            return id.Length == 0 ? null : id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void ClearLast(string deviceId)
    {
        string path = LastPath(deviceId);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// A snapshot without the daemon's own stamps (gain lock, phantom
    /// settling), which describe the daemon at the time, not the hardware,
    /// without the unit's own gain lock, which follows the daemon's,
    /// without what is plugged into the unit, which is read, never set,
    /// without its LEDs, which the daemon keeps per unit in a file of their own,
    /// and without raw capture, which the daemon holds by its own setting.
    /// </summary>
    public static DeviceState Hardware(DeviceState s) => s with
    {
        GainLocked = false,
        UnitGainLock = null,
        MicConnected = null,
        HeadphonesConnected = null,
        Led = null,
        RawCapture = null,
        RawCapture2 = null,
        LineInRawCapture = null,
        PhantomSettling = false,
        PhantomSettling2 = false,
        PhantomSettleSeconds = 0,
        PhantomSettleSeconds2 = 0,
    };

    private static DeviceState? Load(string path)
    {
        try
        {
            DeviceState? state = JsonSerializer.Deserialize<DeviceState>(RegularFile.ReadText(path, MaxStateBytes), Json);
            if (state is not null) Validate(state);
            return state;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return null; }
    }

    internal static void Validate(DeviceState state)
    {
        if (!double.IsFinite(state.HpVolumeDb) || !double.IsFinite(state.Hp2VolumeDb) || !double.IsFinite(state.AuxLevelDb))
            throw new JsonException("Saved device levels must be finite numbers.");
    }

    private static void Save(string deviceId, string path, DeviceState state)
        => OpenXlrPaths.WriteAtomicJson(path, Hardware(state), Json);
}
