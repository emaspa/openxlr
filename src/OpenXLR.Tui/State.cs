using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.Tui;

/// <summary>One mix master: a monitor mix, a virtual microphone or the Aux port.</summary>
internal sealed record MixEntry
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public double Volume { get; init; } = 1;
    public bool Muted { get; init; }
    public string Kind { get; init; } = "virtualMic";

    /// <summary>A monitor mix goes to 150%, everything else to 100%.</summary>
    public double Ceiling => Kind == "monitor" ? 1.5 : 1.0;
}

/// <summary>One channel strip and its send into every mix.</summary>
internal sealed record ChannelEntry
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, double> Levels { get; init; } = [];
    public List<string> MutedIn { get; init; } = [];
    public bool Hardware { get; init; }
    public string? CaptureSource { get; init; }
    public int CapturePair { get; init; }
    public bool CaptureConnected { get; init; }

    public double Level(string mix) => Levels.TryGetValue(mix, out double value) ? value : 0;

    public bool IsMuted(string mix) => MutedIn.Contains(mix);
}

/// <summary>A live playback stream the daemon has placed on a channel.</summary>
internal sealed record StreamEntry
{
    public long Id { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Identity { get; init; } = string.Empty;
    public string? ChannelId { get; init; }
    public bool Active { get; init; }
    public bool Running { get; init; }
}

/// <summary>One plugin in a chain, with the status the daemon reports for it.</summary>
internal sealed record InsertEntry
{
    public InsertBody Insert { get; init; } = new();
    public string? Error { get; init; }
    public bool NativeHostRunning { get; init; }
}

internal sealed record InsertBody
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Plugin { get; init; } = string.Empty;
    public string? Label { get; init; }
    public bool Bypass { get; init; }
    public bool NativeHost { get; init; }

    /// <summary>The plugin's control values, carried back untouched whenever the chain is rewritten.</summary>
    public Dictionary<string, double> Params { get; init; } = [];
}

/// <summary>A PipeWire sink or source the daemon can see.</summary>
internal sealed record DeviceEntry
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Kind { get; init; }
    public bool IsOwn { get; init; }
    public bool IsPhysical { get; init; }
    public double? Volume { get; init; }
    public bool? Muted { get; init; }

    /// <summary>Kind 0 is a sink in the daemon's own listing.</summary>
    public bool IsSink => Kind == 0;
}

/// <summary>An interface attached to the machine, whether or not it is the active one.</summary>
internal sealed record DetectedDevice
{
    public string UsbId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Active { get; init; }
}

internal sealed record DeviceDescriptor
{
    public string Vendor { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string UsbId { get; init; } = string.Empty;
    public string? Note { get; init; }
}

internal sealed record MixerSnapshot
{
    public List<MixEntry> Mixes { get; init; } = [];
    public List<ChannelEntry> Channels { get; init; } = [];
    public List<string> MonitorOutputs { get; init; } = [];
    public Dictionary<string, string> MonitorFeeds { get; init; } = [];
    public double? OutputVolume { get; init; }
    public bool AuxPortEnabled { get; init; }
    public int LowCutHz { get; init; }
    public bool SoftClipGuard { get; init; }
    public bool SoftClipGuardAvailable { get; init; }
    public string? EnforcedDefaultSink { get; init; }
    public string? EnforcedDefaultSource { get; init; }
    public List<StreamEntry> Streams { get; init; } = [];
    public Dictionary<string, List<InsertEntry>> Inserts { get; init; } = [];
    public string? LayoutWarning { get; init; }
}

/// <summary>
/// A state message, kept as one immutable snapshot the drawing code reads
/// without a lock. The hardware controls and the capabilities stay as raw JSON
/// values: they are a long list of flags that grows with every device, and the
/// terminal only ever asks for one by name.
/// </summary>
internal sealed record Snapshot
{
    public string DaemonVersion { get; init; } = string.Empty;
    public bool Connected { get; init; }
    public string? Warning { get; init; }
    public DeviceDescriptor? Device { get; init; }
    public Dictionary<string, JsonElement> Capabilities { get; init; } = [];
    public Dictionary<string, JsonElement> State { get; init; } = [];
    public MixerSnapshot Mixer { get; init; } = new();
    public List<DeviceEntry> Devices { get; init; } = [];
    public List<string> Profiles { get; init; } = [];
    public string? ActiveProfile { get; init; }
    public string? RecallOnConnect { get; init; }
    public List<DetectedDevice> Detected { get; init; } = [];

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static Snapshot? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<Snapshot>(json, Options); }
        catch (JsonException) { return null; }
    }

    public bool Can(string capability) =>
        Capabilities.TryGetValue(capability, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public int Count(string capability) =>
        Capabilities.TryGetValue(capability, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    public bool Flag(string control) =>
        State.TryGetValue(control, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public double Number(string control) =>
        State.TryGetValue(control, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;
}
