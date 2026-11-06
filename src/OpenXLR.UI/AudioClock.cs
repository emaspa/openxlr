using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace OpenXLR.UI;

/// <summary>One of the mixer's devices that runs on the driver's clock, as the daemon reports it.</summary>
internal sealed record AudioClockFollower(string Name, string Description, string Role, IReadOnlyList<string> Channels,
    bool Microphone, bool Resampled);

/// <summary>A capture input's device on its own clock, carried into the mixer by a bridge.</summary>
internal sealed record AudioClockBridged(string Name, string Description, IReadOnlyList<string> Channels, bool Separate);

/// <summary>
/// The daemon's <c>mixer.clock</c>: the node that drives the mixer's graph,
/// the name to show for it, and the mixer's devices that follow it.
/// </summary>
internal sealed record AudioClock(string Driver, string DriverName, IReadOnlyList<AudioClockFollower> Followers)
{
    /// <summary>The devices on their own clock; empty from an older daemon.</summary>
    public IReadOnlyList<AudioClockBridged> Bridged { get; init; } = [];

    /// <summary>The bridged device that feeds a strip, if any.</summary>
    public AudioClockBridged? BridgedFor(string channel)
        => Bridged.FirstOrDefault(b => b.Channels.Contains(channel, StringComparer.Ordinal));

    /// <summary>A microphone's interface follows another device's clock and is resampled.</summary>
    public bool MicResampled => Followers.Any(f => f.Microphone && f.Resampled);

    /// <summary>The resampled follower that feeds a strip, if any.</summary>
    public AudioClockFollower? ResampledFor(string channel)
        => Followers.FirstOrDefault(f => f.Resampled && f.Channels.Contains(channel, StringComparer.Ordinal));

    /// <summary>The resampled follower that is a selected output, by its node name before any '#' marker.</summary>
    public AudioClockFollower? ResampledOutput(string output)
    {
        int marker = output.IndexOf('#');
        string node = marker < 0 ? output : output[..marker];
        return Followers.FirstOrDefault(f => f.Resampled && f.Channels.Count == 0 && f.Name == node);
    }

    public bool Equals(AudioClock? other)
        => other is not null && Driver == other.Driver && DriverName == other.DriverName
            && Followers.Count == other.Followers.Count
            && Followers.Zip(other.Followers).All(p => p.First.Name == p.Second.Name && p.First.Description == p.Second.Description
                && p.First.Role == p.Second.Role && p.First.Microphone == p.Second.Microphone && p.First.Resampled == p.Second.Resampled
                && p.First.Channels.SequenceEqual(p.Second.Channels))
            && Bridged.Count == other.Bridged.Count
            && Bridged.Zip(other.Bridged).All(p => p.First.Name == p.Second.Name && p.First.Description == p.Second.Description
                && p.First.Separate == p.Second.Separate && p.First.Channels.SequenceEqual(p.Second.Channels));

    public override int GetHashCode() => HashCode.Combine(Driver, Followers.Count);

    /// <summary>The clock in a mixer state, or null when it has none (an older daemon, or no driver yet).</summary>
    public static AudioClock? From(JsonNode? mixer)
    {
        if (mixer?["clock"] is not JsonObject clock || clock["driver"] is not JsonObject driver
            || Text(driver, "name") is not { } name) return null;
        var followers = new List<AudioClockFollower>();
        if (clock["followers"] is JsonArray list)
            foreach (JsonObject follower in list.OfType<JsonObject>())
            {
                if (Text(follower, "name") is not { } node) continue;
                followers.Add(new AudioClockFollower(node, Text(follower, "description") ?? node, Text(follower, "role") ?? "",
                    Strips(follower),
                    Flag(follower, "microphone"), Flag(follower, "resampled")));
            }
        var bridged = new List<AudioClockBridged>();
        if (clock["bridged"] is JsonArray devices)
            foreach (JsonObject device in devices.OfType<JsonObject>())
                if (Text(device, "name") is { } node)
                    bridged.Add(new AudioClockBridged(node, Text(device, "description") ?? node, Strips(device), Flag(device, "separate")));
        return new AudioClock(name, Text(driver, "description") ?? name, followers) { Bridged = bridged };
    }

    private static IReadOnlyList<string> Strips(JsonObject node)
        => node["channels"] is JsonArray strips ? [.. strips.Select(s => s is JsonValue v && v.TryGetValue(out string? id) ? id : null).OfType<string>()] : [];

    private static string? Text(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0 ? text : null;

    private static bool Flag(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue(out bool flag) && flag;
}
