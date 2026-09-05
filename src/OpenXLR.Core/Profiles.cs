using System.Text.Json;
using System.Text.Json.Serialization;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Core;

/// <summary>
/// The mixer half of a profile: a scene, not a machine configuration. App
/// routing, the registry, and enforced system defaults deliberately stay
/// global; a profile recalls what a session sounds like, not how the
/// machine is wired into the desktop.
/// </summary>
public sealed record MixerScene
{
    public Dictionary<string, double> MixVolumes { get; init; } = [];
    public List<string> MixMuted { get; init; } = [];
    /// <summary>"channel|mix" to level.</summary>
    public Dictionary<string, double> Levels { get; init; } = [];
    public List<string> ChannelMuted { get; init; } = [];
    /// <summary>
    /// Null in profiles written before monitor routing was stored; an explicit
    /// empty list means disconnect every monitor output.
    /// </summary>
    public List<string>? MonitorOutputs { get; init; }
    /// <summary>Output name to the monitor mix feeding it; null in profiles saved before Monitor 2.</summary>
    public Dictionary<string, string>? MonitorFeeds { get; init; }
    public bool AuxPortEnabled { get; init; }
    public double? OutputVolume { get; init; }
    /// <summary>Software low cut (0, 80, or 120 Hz); absent in older profiles.</summary>
    public int? LowCutHz { get; init; }
    /// <summary>Software ClipGuard; absent in older profiles.</summary>
    public bool? SoftClipGuard { get; init; }
    /// <summary>Plugin insert chains by channel; absent in older profiles.</summary>
    public Dictionary<string, List<InsertDefinition>>? Inserts { get; init; }
}

/// <summary>
/// A named snapshot of the whole rig: the hardware DSP state and the mixer
/// scene. Either half may be absent (saved without a device connected, or
/// without the mixer built) and loading applies whatever is present.
/// </summary>
public sealed record Profile
{
    public DeviceState? Device { get; init; }
    public MixerScene? Mixer { get; init; }
}

/// <summary>
/// Named profiles as single JSON files under the XDG config directory,
/// scoped per device model (profiles/&lt;usbId&gt;/&lt;name&gt;.json): a scene
/// saved from one interface only makes sense recalled on the same model.
/// The file name is the profile name (sanitized), so the store needs no
/// index and survives hand-editing.
/// </summary>
public static class ProfileStore
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
            return Path.Combine(root, "openxlr", "profiles");
        }
    }

    private static string Dir(string deviceId) => Path.Combine(Root, deviceId.Replace(':', '-'));

    private static bool _migrated;

    /// <summary>
    /// Profiles saved before per-device scoping lived directly in the root;
    /// they were all Wave XLR Pro scenes (the only supported device then), so
    /// they move into its folder once.
    /// </summary>
    private static void MigrateOnce()
    {
        if (_migrated) return;
        _migrated = true;
        try
        {
            if (!Directory.Exists(Root)) return;
            string proDir = Dir("0fd9:00b4");
            foreach (string f in Directory.EnumerateFiles(Root, "*.json"))
            {
                Directory.CreateDirectory(proDir);
                File.Move(f, Path.Combine(proDir, Path.GetFileName(f)), overwrite: false);
            }
        }
        catch (IOException) { /* leave stragglers for the next run */ }
    }

    /// <summary>
    /// A profile name reduced to something that is safe as a file name and
    /// stable across save/load. Returns null when nothing usable remains.
    /// </summary>
    public static string? SanitizeName(string? name)
    {
        if (name is null) return null;
        var kept = new string(name.Trim()
            .Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').ToArray()).Trim();
        return kept.Length is 0 or > 60 ? null : kept;
    }

    public static IReadOnlyList<string> List(string deviceId)
    {
        MigrateOnce();
        try
        {
            return [.. Directory.EnumerateFiles(Dir(deviceId), "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null)
                .Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    public static void Save(string deviceId, string name, Profile profile)
    {
        MigrateOnce();
        Directory.CreateDirectory(Dir(deviceId));
        string path = Path.Combine(Dir(deviceId), name + ".json");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(profile, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static Profile? Load(string deviceId, string name)
    {
        MigrateOnce();
        string path = Path.Combine(Dir(deviceId), name + ".json");
        try { return JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Json); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static bool Delete(string deviceId, string name)
    {
        MigrateOnce();
        string path = Path.Combine(Dir(deviceId), name + ".json");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        if (RecallOnConnect(deviceId) == name) SetRecallOnConnect(deviceId, null);
        return true;
    }

    // The profile the daemon recalls when this device connects (daemon
    // start, a replug or a power cycle, or a switch to it), kept as a
    // marker file in the device's folder. No .json extension, so List()
    // never shows it and a profile named "recall-on-connect" cannot
    // collide with it.
    private static string RecallPath(string deviceId) => Path.Combine(Dir(deviceId), "recall-on-connect");

    /// <summary>The profile to recall when the device connects, or null.</summary>
    public static string? RecallOnConnect(string deviceId)
    {
        try
        {
            string name = File.ReadAllText(RecallPath(deviceId)).Trim();
            return name.Length == 0 ? null : name;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>Set (or with null clear) the profile recalled on connect.</summary>
    public static void SetRecallOnConnect(string deviceId, string? name)
    {
        string path = RecallPath(deviceId);
        if (name is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Dir(deviceId));
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, name);
        File.Move(tmp, path, overwrite: true);
    }
}
