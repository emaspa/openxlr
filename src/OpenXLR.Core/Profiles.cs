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
    /// <summary>
    /// Channel id to its channel volume. Null in profiles saved before channel
    /// volumes, which recall every channel they describe at full volume; a
    /// channel it describes but does not name, as XLR 1, XLR 2 and Aux In in
    /// a profile saved before they took a volume, is recalled at full volume
    /// too.
    /// </summary>
    public Dictionary<string, double>? ChannelVolumes { get; init; }
    public List<string> ChannelMuted { get; init; } = [];
    /// <summary>
    /// Null in profiles written before monitor routing was stored; an explicit
    /// empty list means disconnect every monitor output.
    /// </summary>
    public List<string>? MonitorOutputs { get; init; }
    /// <summary>Output name to the monitor mix feeding it; null in profiles saved before Monitor B.</summary>
    public Dictionary<string, string>? MonitorFeeds { get; init; }

    public bool AuxPortEnabled { get; init; }
    public double? OutputVolume { get; init; }
    /// <summary>Software low cut (0, 80, or 120 Hz); absent in older profiles.</summary>
    public int? LowCutHz { get; init; }
    /// <summary>Software ClipGuard; absent in older profiles.</summary>
    public bool? SoftClipGuard { get; init; }
    /// <summary>Plugin insert chains by channel; absent in older profiles.</summary>
    public Dictionary<string, List<InsertDefinition>>? Inserts { get; init; }
    /// <summary>
    /// Microphone EQs by channel; absent in older profiles, which leave every
    /// EQ as it is. A recall replaces the EQ of each channel named here and
    /// leaves the others.
    /// </summary>
    public Dictionary<string, MicEq>? Eq { get; init; }

    /// <summary>
    /// Noise removal by channel; absent in older profiles, which leave it as
    /// it is. A recall replaces the setting of each channel named here.
    /// </summary>
    public Dictionary<string, MicNoise>? NoiseRemoval { get; init; }

    /// <summary>
    /// The voice changer by channel; absent in older profiles, which leave it
    /// as it is. A recall replaces the setting of each channel named here.
    /// </summary>
    public Dictionary<string, MicVoice>? VoiceChanger { get; init; }

    /// <summary>
    /// The compressor by channel; absent in older profiles, which leave it as
    /// it is. A recall replaces the setting of each channel named here.
    /// </summary>
    public Dictionary<string, MicCompressor>? Compressor { get; init; }

    /// <summary>
    /// The expander by channel; absent in older profiles, which leave it as it
    /// is. A recall replaces the setting of each channel named here.
    /// </summary>
    public Dictionary<string, MicExpander>? Expander { get; init; }

    /// <summary>
    /// Whether each monitor mix hears the microphones dry, by mix id, saved
    /// as <c>dryMonitors</c>; absent in older profiles, which leave it as it
    /// is. A recall sets each monitor mix named here. An older profile's
    /// per-microphone <c>monitorDry</c> map is not read.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("dryMonitors")]
    public Dictionary<string, bool>? MonitorDry { get; init; }

    /// <summary>
    /// The gain and source mute of each capture input that has a gain of its
    /// own, by channel; absent in older profiles, which leave both as they
    /// are. A recall writes each channel named here once its source is
    /// connected.
    /// </summary>
    public Dictionary<string, CaptureGainScene>? CaptureGains { get; init; }

    /// <summary>The ducking setup without machine processing. Absent in older profiles, which leave it alone.</summary>
    public DuckingScene? Ducking { get; init; }
}

/// <summary>A profile's ducking choices. Where they run belongs only to mixer settings.</summary>
public record DuckingScene
{
    public bool Enabled { get; init; }
    public string Key { get; init; } = ProDucking.KeyXlr1;
    public IReadOnlyList<string> Channels { get; init; } = [];
    /// <summary>
    /// The mixes to lower in. A file without this field keeps the
    /// <see cref="UnsavedMixes"/> instance, which pruning against a layout
    /// replaces with that layout's default monitor.
    /// </summary>
    public IReadOnlyList<string> Mixes { get; init; } = UnsavedMixes;

    /// <summary>The mixes of a scene that never named any; Monitor A until a layout resolves it.</summary>
    internal static readonly IReadOnlyList<string> UnsavedMixes = ["monitor"];

    /// <summary>A preset word or the key threshold in whole dBFS.</summary>
    public DuckingLevel Sensitivity { get; init; } = ProDucking.Medium;
    /// <summary>A preset word or the depth in whole dB.</summary>
    public DuckingLevel Amount { get; init; } = ProDucking.Medium;
    /// <summary>A preset word or the attack and release in whole ms.</summary>
    public DuckingSpeed Speed { get; init; } = ProDucking.Medium;
}

/// <summary>
/// A named snapshot of the whole rig: the hardware DSP state and the mixer
/// scene. Either half may be absent (saved without a device connected, or
/// without the mixer built) and loading applies whatever is present.
/// </summary>
public sealed record Profile
{
    public DeviceState? Device { get; init; }
    /// <summary>Hardware states of additional interfaces by instance id; recall never enables a unit.</summary>
    public Dictionary<string, DeviceState>? AdditionalDevices { get; init; }
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
    internal const int MaxProfileBytes = 8 * 1024 * 1024;
    private const int MaxRecallBytes = 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Root => Path.Combine(OpenXlrPaths.ConfigDir, "profiles");

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
                try
                {
                    OpenXlrPaths.EnsurePrivateDir(proDir);
                    File.Move(f, Path.Combine(proDir, Path.GetFileName(f)), overwrite: false);
                }
                catch (IOException) { /* keep this original and migrate the remaining profiles */ }
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
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(profile, Json);
        if (bytes.Length > MaxProfileBytes) throw new IOException("Profiles are limited to 8 MiB.");
        OpenXlrPaths.WriteAtomic(Path.Combine(Dir(deviceId), name + ".json"), bytes);
    }

    public static Profile? Load(string deviceId, string name)
    {
        MigrateOnce();
        string path = Path.Combine(Dir(deviceId), name + ".json");
        try
        {
            Profile? profile = JsonSerializer.Deserialize<Profile>(RegularFile.ReadText(path, MaxProfileBytes), Json);
            if (profile?.Mixer is { } scene) SavedMixerValidation.Validate(scene);
            if (profile?.Device is { } device) DeviceStateStore.Validate(device);
            if (profile?.AdditionalDevices is { } additional)
            {
                if (additional.Count > 4 || additional.Any(pair => !UsbLocation.IsInstanceId(pair.Key) || pair.Value is null))
                    throw new JsonException("Invalid additional device snapshots.");
                foreach (DeviceState state in additional.Values) DeviceStateStore.Validate(state);
            }
            return profile;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static bool Delete(string deviceId, string name)
    {
        MigrateOnce();
        string path = Path.Combine(Dir(deviceId), name + ".json");
        if (!File.Exists(path)) return false;
        string? recall = RecallOnConnect(deviceId);
        File.Delete(path);
        if (recall == name) SetRecallOnConnect(deviceId, null);
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
            string name = RegularFile.ReadText(RecallPath(deviceId), MaxRecallBytes).Trim();
            if (name.Length == 0) return null;
            if (SanitizeName(name) != name) throw new IOException("The recall marker contains an invalid profile name.");
            return name;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>Set (or with null clear) the profile recalled on connect.</summary>
    public static void SetRecallOnConnect(string deviceId, string? name)
    {
        if (name is not null && SanitizeName(name) != name)
            throw new ArgumentException("Invalid profile name.", nameof(name));
        string path = RecallPath(deviceId);
        if (name is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        OpenXlrPaths.WriteAtomic(path, name);
    }
}
