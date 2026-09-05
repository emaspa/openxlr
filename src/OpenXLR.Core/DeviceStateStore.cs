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
/// would otherwise restore the last settings for ever. One folder per
/// device under the XDG config directory (devices/&lt;vid-pid&gt;/).
/// </summary>
public static class DeviceStateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Root
    {
        get
        {
            string root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "openxlr", "devices");
        }
    }

    private static string Dir(string deviceId) => Path.Combine(Root, deviceId.Replace(':', '-'));
    private static string LastPath(string deviceId) => Path.Combine(Dir(deviceId), "last-state.json");
    private static string DefaultsPath(string deviceId) => Path.Combine(Dir(deviceId), "defaults.json");

    /// <summary>The settings the daemon last saw on this device, or null.</summary>
    public static DeviceState? LoadLast(string deviceId) => Load(LastPath(deviceId));

    /// <summary>The firmware defaults recorded after a power cycle, or null.</summary>
    public static DeviceState? LoadDefaults(string deviceId) => Load(DefaultsPath(deviceId));

    public static void SaveLast(string deviceId, DeviceState state) => Save(deviceId, LastPath(deviceId), state);

    public static void SaveDefaults(string deviceId, DeviceState state) => Save(deviceId, DefaultsPath(deviceId), state);

    public static void ClearLast(string deviceId)
    {
        string path = LastPath(deviceId);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// A snapshot without the daemon's own stamps (gain lock, phantom
    /// settling), which describe the daemon at the time, not the hardware.
    /// </summary>
    public static DeviceState Hardware(DeviceState s) => s with
    {
        GainLocked = false,
        PhantomSettling = false,
        PhantomSettling2 = false,
        PhantomSettleSeconds = 0,
        PhantomSettleSeconds2 = 0,
    };

    private static DeviceState? Load(string path)
    {
        try { return JsonSerializer.Deserialize<DeviceState>(File.ReadAllText(path), Json); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return null; }
    }

    private static void Save(string deviceId, string path, DeviceState state)
    {
        Directory.CreateDirectory(Dir(deviceId));
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Hardware(state), Json));
        File.Move(tmp, path, overwrite: true);
    }
}
