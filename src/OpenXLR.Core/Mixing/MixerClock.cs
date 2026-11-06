using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The node that drives the graph the mixer runs in: its node.name, a name
/// for people, its priority.driver, which of the mixer's devices it is
/// (<see cref="ClockFollower.Role"/>; null when it is none of them) and the
/// attached supported unit whose card publishes it, if any.
/// </summary>
public sealed record ClockDriver(string Name, string Description, int? PriorityDriver)
{
    public string? Role { get; init; }
    public string? DrivenUnit { get; init; }
}

/// <summary>
/// One of the mixer's hardware devices that runs on another node's clock.
/// </summary>
/// <param name="Role">
/// <c>unitInput</c> (the driven unit's capture), <c>capture</c> (a capture
/// input or a strip bound to another source), <c>unitOutput</c> (a selected
/// output on the driven unit's card) or <c>output</c> (any other selected output).
/// </param>
/// <param name="Channels">The strips the device feeds, by channel id; empty for an output.</param>
/// <param name="Microphone">A microphone strip (one that carries the EQ) takes its audio from the device.</param>
/// <param name="Resampled">
/// The device has a clock of its own, not the driver's, so PipeWire
/// resamples it to the driver's rate as it drifts.
/// </param>
public sealed record ClockFollower(string Name, string Description, string Role, IReadOnlyList<string> Channels,
    bool Microphone, int? PriorityDriver, bool Resampled)
{
    public string? DrivenUnit { get; init; }
}

/// <summary>
/// A capture input's device on its own clock: it is not one of the
/// followers, since a clock bridge, not PipeWire, carries its audio into the
/// mixer's graph (see <see cref="Mixer.SetCaptureOwnClock"/>).
/// </summary>
/// <param name="Name">The device's node.name.</param>
/// <param name="Description">A name for people.</param>
/// <param name="Channels">The capture inputs that take it through a bridge, by channel id.</param>
/// <param name="Driver">The node that drives the device's graph when it is another node, or null when the device drives it itself or is idle.</param>
/// <param name="Separate">The device is outside the mixer's graph, as it should be. False when something else links it into the mixer's graph, which puts it back on the mixer's clock.</param>
public sealed record ClockBridged(string Name, string Description, IReadOnlyList<string> Channels, string? Driver, bool Separate)
{
    /// <summary>The <see cref="ClockCandidate.Role"/> of a device a bridge takes.</summary>
    public const string Role = "bridged";

    public bool Equals(ClockBridged? other)
        => other is not null && Name == other.Name && Description == other.Description && Driver == other.Driver
            && Separate == other.Separate && Channels.SequenceEqual(other.Channels, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Name, Driver, Separate);
}

/// <summary>
/// Which node drives the mixer's graph and which of the mixer's devices
/// follow it, read from the registry's node properties (see
/// <see cref="Read"/>). Null in the state until the graph shows a driver.
/// </summary>
public sealed record MixerClock(ClockDriver? Driver, IReadOnlyList<ClockFollower> Followers)
{
    /// <summary>The devices on their own clock, carried into the mixer's graph by a bridge.</summary>
    public IReadOnlyList<ClockBridged> Bridged { get; init; } = [];

    /// <summary>A microphone's interface follows another device's clock and is resampled.</summary>
    public bool MicResampled => Followers.Any(f => f.Microphone && f.Resampled);

    public bool Equals(MixerClock? other)
        => other is not null && Driver == other.Driver && Followers.Count == other.Followers.Count
            && Followers.Zip(other.Followers).All(pair => pair.First with { Channels = NoChannels } == pair.Second with { Channels = NoChannels }
                && pair.First.Channels.SequenceEqual(pair.Second.Channels, StringComparer.Ordinal))
            && Bridged.SequenceEqual(other.Bridged);

    private static readonly string[] NoChannels = [];

    public override int GetHashCode() => HashCode.Combine(Driver, Followers.Count);

    /// <summary>
    /// The clock of the mixer's graph in one registry snapshot.
    ///
    /// PipeWire writes <c>node.driver-id</c> into every node that runs in a
    /// graph: the registry id of the driver it follows. A running driver
    /// carries no id or its own. The mixer's graph is the one its own mix
    /// sinks (<see cref="MixDefinition.SinkPrefix"/>) follow, or, before they
    /// run, the one its devices follow. A follower is resampled when it is a
    /// hardware node (<c>device.api</c>) of another device than the driver:
    /// another <c>device.id</c>, or another card in its name where there is
    /// no device id, and no shared <c>clock.name</c>. Nodes of one card, such
    /// as a unit's capture and playback, share its clock. This is read from
    /// the properties, not from the resampler: a follower at the driver's
    /// rate from another device is still rate matched, since two crystals
    /// drift apart, and a device that stops (suspended, unlinked) leaves the
    /// list.
    /// </summary>
    /// <param name="objects">The registry, as one pw-dump writes it; a subscription's copy of a node does not carry a changed driver id.</param>
    /// <param name="devices">The mixer's devices, by node name, and what each one is to the mixer.</param>
    public static MixerClock? Read(IReadOnlyList<JsonElement> objects, IReadOnlyList<ClockCandidate> devices)
    {
        var nodes = new Dictionary<uint, ClockNode>();
        var byName = new Dictionary<string, ClockNode>(StringComparer.Ordinal);
        foreach (JsonElement item in objects)
            if (ClockNode.From(item) is { } node)
            {
                nodes[node.Id] = node;
                byName.TryAdd(node.Name, node);
            }

        // The mixer's own sinks follow the graph's driver while anything plays.
        uint? driverId = nodes.Values
            .Where(n => n.Name.StartsWith(MixDefinition.SinkPrefix, StringComparison.Ordinal) && n.DriverId is not null)
            .GroupBy(n => n.DriverId!.Value).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => (uint?)g.Key).FirstOrDefault();
        driverId ??= devices.Where(d => d.Role != ClockBridged.Role)
            .Select(d => byName.GetValueOrDefault(d.Node)?.DriverId).FirstOrDefault(id => id is not null);
        if (driverId is not uint id || !nodes.TryGetValue(id, out ClockNode? driver)) return null;

        Dictionary<string, ClockCandidate> roles = new(StringComparer.Ordinal);
        foreach (ClockCandidate device in devices.Where(d => d.Role != ClockBridged.Role)) roles.TryAdd(device.Node, device);

        var followers = new List<ClockFollower>();
        foreach (ClockCandidate device in roles.Values)
        {
            if (!byName.TryGetValue(device.Node, out ClockNode? node) || node.Id == driver.Id
                || node.DriverId != driver.Id || !node.Hardware) continue;
            followers.Add(new ClockFollower(node.Name, node.Description, device.Role, device.Channels,
                device.Microphone, node.PriorityDriver, !node.SharesClockWith(driver)));
        }
        // A bridged device should run in a graph of its own: driving it, or
        // following a node other than the mixer's driver.
        var bridged = new List<ClockBridged>();
        foreach (ClockCandidate device in devices.Where(d => d.Role == ClockBridged.Role).DistinctBy(d => d.Node))
        {
            if (!byName.TryGetValue(device.Node, out ClockNode? node)) continue;
            string? own = node.DriverId is uint other && nodes.TryGetValue(other, out ClockNode? leader) ? leader.Name : null;
            bridged.Add(new ClockBridged(node.Name, node.Description, device.Channels, own,
                node.Id != driver.Id && node.DriverId != driver.Id));
        }
        return new MixerClock(
            new ClockDriver(driver.Name, driver.Description, driver.PriorityDriver) { Role = roles.GetValueOrDefault(driver.Name)?.Role },
            [.. followers.OrderBy(f => RoleOrder(f.Role)).ThenBy(f => f.Name, StringComparer.Ordinal)])
        {
            Bridged = [.. bridged.OrderBy(b => b.Name, StringComparer.Ordinal)],
        };
    }

    private static int RoleOrder(string role) => role switch
    {
        "unitInput" => 0, "capture" => 1, "unitOutput" => 2, _ => 3,
    };

    /// <summary>One sentence for the user while a microphone's interface is resampled, or null.</summary>
    public string? Warning()
    {
        if (Driver is null) return null;
        ClockFollower[] microphones = [.. Followers.Where(f => f.Microphone && f.Resampled)];
        if (microphones.Length == 0) return null;
        string names = string.Join(" and ", microphones.Select(f => f.Description));
        return $"{names} {(microphones.Length == 1 ? "follows" : "follow")} the audio clock of {Driver.Description}, so PipeWire resamples the microphone; if it sounds garbled or robotic, see the manual.";
    }

    private sealed record ClockNode(uint Id, string Name, string Description, uint? DriverId, int? PriorityDriver,
        bool Hardware, string? DeviceId, string? ClockName)
    {
        public bool SharesClockWith(ClockNode driver)
        {
            if (ClockName is not null && ClockName == driver.ClockName) return true;
            if (DeviceId is not null && driver.DeviceId is not null) return DeviceId == driver.DeviceId;
            return driver.Hardware && Name.StartsWith("alsa_", StringComparison.Ordinal) && driver.Name.StartsWith("alsa_", StringComparison.Ordinal)
                && InterfaceNodes.CardKey(Name) == InterfaceNodes.CardKey(driver.Name);
        }

        public static ClockNode? From(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String
                || !type.ValueEquals("PipeWire:Interface:Node")
                || !item.TryGetProperty("id", out JsonElement key) || key.ValueKind != JsonValueKind.Number
                || !key.TryGetUInt32(out uint id)
                || !item.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("props", out JsonElement props) || props.ValueKind != JsonValueKind.Object
                || Text(props, "node.name") is not { } name) return null;
            uint? driver = Number(props, "node.driver-id") is long d and >= 0 and <= uint.MaxValue ? (uint)d : null;
            // A driver writes its own id, or none at all.
            if (driver == id) driver = null;
            long? priority = Number(props, "priority.driver");
            return new ClockNode(id, name, Text(props, "node.description") ?? Text(props, "node.nick") ?? name, driver,
                priority is >= int.MinValue and <= int.MaxValue ? (int)priority : null,
                Text(props, "device.api") is not null, Number(props, "device.id")?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ?? Text(props, "device.id"), Text(props, "clock.name"));
        }

        private static string? Text(JsonElement props, string key)
            => props.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text ? text : null;

        private static long? Number(JsonElement props, string key)
        {
            if (!props.TryGetProperty(key, out JsonElement value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number;
            return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long parsed) ? parsed : null;
        }
    }
}

/// <summary>
/// One of the mixer's devices for <see cref="MixerClock.Read"/>: its node.name
/// and what it is to the mixer, a <see cref="ClockFollower.Role"/> or
/// <see cref="ClockBridged.Role"/> for a device a clock bridge takes.
/// </summary>
public sealed record ClockCandidate(string Node, string Role, IReadOnlyList<string> Channels, bool Microphone);
