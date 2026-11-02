using OpenXLR.Core;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// One profile of a card as PipeWire describes it. <see cref="Capture"/> is
/// whether the profile gives the card a capture node (a class
/// <c>Audio/Source</c> with at least one device), null when the profile
/// carries no classes. <see cref="Outputs"/> are the card's devices the
/// profile plays through, in order, empty for a profile that plays nothing,
/// null when the classes do not list them.
/// </summary>
public sealed record CardProfileOption(string Name, int Index, int Priority, bool Available, bool? Capture, IReadOnlyList<int>? Outputs);

/// <summary>
/// A card found in one pw-dump: its registry id, its device.name, the active
/// profile and every profile it offers. <see cref="Capture"/> is whether the
/// active profile has a capture node: from its classes, or, where they are
/// missing, from a source node of this card in the same dump.
/// </summary>
public sealed record CardView(uint Id, string Name, CardProfileOption? Active, IReadOnlyList<CardProfileOption> Profiles, bool Capture)
{
    public string ActiveName => Active?.Name ?? "";
}

/// <summary>
/// The card profile of a driven unit.
///
/// UCM coexistence. A card that ships a UCM profile (the Wave XLR Pro's
/// splits the device into per-function nodes for daemon-less use) hides
/// the raw multichannel nodes this mixer links against while the split
/// "HiFi" profile is active. The daemon therefore switches such a card
/// to the pro-audio profile while it drives the device, and restores the
/// previous profile on graceful shutdown, so the split serves exactly
/// when OpenXLR is not running. Cards without a UCM profile never match
/// the gate (their active profile is not named "HiFi") and are left
/// alone by it.
///
/// Capture. A card on a profile that only plays (the original Wave XLR on
/// output:analog-stereo) has no capture node, and its microphone is
/// silent. <see cref="CaptureProfileFor"/> picks the profile that keeps
/// the same output and adds an input; <see cref="CardProfileWatch"/>
/// switches to it once per connect and reports what it cannot fix.
/// </summary>
public static class CardProfile
{
    /// <summary>
    /// If the one card whose device.name contains <paramref name="nameFragment"/>
    /// is in a UCM profile and offers pro-audio, switch it there. Returns
    /// the card's active profile name (null when the card is not in the
    /// PipeWire graph yet, which at boot can lag the USB device by seconds)
    /// and the previous profile name when a switch happened.
    /// </summary>
    public static (string? Active, string? Parked) EnsureProAudio(string nameFragment)
        => EnsureProAudio([nameFragment], out _);

    /// <summary>
    /// The same, trying each fragment in turn until one names a card.
    /// <paramref name="matched"/> is the fragment that found it, which the
    /// caller hands back to <see cref="SetProfile"/> on restore.
    /// </summary>
    public static (string? Active, string? Parked) EnsureProAudio(IReadOnlyList<string> fragments, out string? matched)
    {
        CardView? card = Read(fragments, out matched);
        if (card is null) return (null, null);
        return (card.ActiveName, ParkProAudio(card));
    }

    /// <summary>
    /// Switch a card in a UCM profile to pro-audio when it offers it. The
    /// previous profile's name when it switched, else null.
    /// </summary>
    public static string? ParkProAudio(CardView card)
    {
        string active = card.ActiveName;
        if (active is not ("HiFi" or "Direct")) return null;   // not UCM-split; leave alone
        if (card.Profiles.FirstOrDefault(p => p.Name == "pro-audio") is not { } pro) return null;
        Switch(card, pro);
        return active;
    }

    /// <summary>
    /// The profile a card on a profile without capture should take: one the
    /// card offers as available (or not known to be unavailable) that plays
    /// through exactly the devices the active profile plays through and
    /// also has a capture node, the highest priority first. Null when the
    /// active profile already has capture, plays nothing (off), or no such
    /// profile exists. Decided from the profiles' own classes, never from
    /// their names, so pro-audio, which plays through a device of its own,
    /// is never taken for an analog output.
    /// </summary>
    public static CardProfileOption? CaptureProfileFor(CardView card)
    {
        if (card.Capture || card.Active is not { Outputs: { Count: > 0 } outputs } active) return null;
        return card.Profiles
            .Where(p => p.Name != active.Name && p.Available && p.Capture == true && p.Outputs is { } o && o.SequenceEqual(outputs))
            .OrderByDescending(p => p.Priority).ThenBy(p => p.Index)
            .FirstOrDefault();
    }

    /// <summary>Make <paramref name="profile"/> the card's active profile through wpctl.</summary>
    public static void Switch(CardView card, CardProfileOption profile)
        => Run("wpctl", "set-profile", card.Id.ToString(), profile.Index.ToString());

    /// <summary>Set the card back to a named profile (best effort).</summary>
    public static void SetProfile(string nameFragment, string profileName)
    {
        CardView? card = Read([nameFragment], out _);
        if (card?.Profiles.FirstOrDefault(p => p.Name == profileName) is { } profile)
            Switch(card, profile);
    }

    /// <summary>
    /// The card the first fragment that names any card names, read from one
    /// pw-dump, or null when none does or pw-dump cannot run. Two cards
    /// under one fragment are two units of one model that cannot be told
    /// apart, and neither is returned; a broader fragment would only match
    /// more of them. A dump that does not parse throws, so a half-written
    /// update never leads to a write. With <paramref name="usbId"/>, a card
    /// that states another USB vendor or product id is never one of them, so
    /// a model name inside another model's ("Wave_XLR" in "Wave_XLR_Pro")
    /// cannot find the other unit's card.
    /// </summary>
    public static CardView? Read(IReadOnlyList<string> fragments, out string? matched, (ushort Vendor, ushort Product)? usbId = null)
    {
        matched = null;
        byte[] dump;
        try { dump = Run("pw-dump"); }
        catch (Exception) { return null; }
        using JsonDocument doc = PipeWireSnapshot.Parse(dump);
        var cards = new List<(string Name, JsonElement Card, JsonElement Info)>();
        var sourceCards = new HashSet<long>();
        foreach (JsonElement o in doc.RootElement.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("type", out JsonElement type)) continue;
            if (!o.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("props", out JsonElement props) || props.ValueKind != JsonValueKind.Object) continue;
            string? kind = type.GetString();
            if (kind == "PipeWire:Interface:Node")
            {
                if (props.TryGetProperty("media.class", out JsonElement mc) && mc.ValueKind == JsonValueKind.String
                    && mc.GetString() == "Audio/Source" && props.TryGetProperty("device.id", out JsonElement did)
                    && did.ValueKind == JsonValueKind.Number && did.TryGetInt64(out long deviceId))
                    sourceCards.Add(deviceId);
                continue;
            }
            if (kind != "PipeWire:Interface:Device") continue;
            string name = props.TryGetProperty("device.name", out JsonElement n)
                ? n.GetString() ?? "" : "";
            if (usbId is { } want && (Differs(props, "device.vendor.id", want.Vendor) || Differs(props, "device.product.id", want.Product))) continue;
            cards.Add((name, o, info));
        }
        foreach (string fragment in fragments)
        {
            if (fragment.Length == 0) continue;
            var found = cards.Where(c => c.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (found.Count == 0) continue;
            if (found.Count > 1) return null;
            matched = fragment;
            (string cardName, JsonElement card, JsonElement info) = found[0];
            if (!info.TryGetProperty("params", out JsonElement pars)) return null;

            // By name, the last entry winning, as wpctl is given one index per name.
            var profiles = new Dictionary<string, CardProfileOption>();
            if (pars.TryGetProperty("EnumProfile", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                foreach (JsonElement p in list.EnumerateArray())
                    if (p.TryGetProperty("name", out JsonElement pn) && p.TryGetProperty("index", out JsonElement pi))
                    {
                        string profileName = pn.GetString() ?? "";
                        profiles[profileName] = Option(p, profileName, pi.GetInt32());
                    }

            CardProfileOption? active = null;
            if (pars.TryGetProperty("Profile", out JsonElement act) && act.ValueKind == JsonValueKind.Array)
                foreach (JsonElement p in act.EnumerateArray())
                    if (p.TryGetProperty("name", out JsonElement an))
                    {
                        string activeName = an.GetString() ?? "";
                        int index = p.TryGetProperty("index", out JsonElement ai) && ai.ValueKind == JsonValueKind.Number ? ai.GetInt32()
                            : profiles.TryGetValue(activeName, out CardProfileOption? listed) ? listed.Index : -1;
                        active = Option(p, activeName, index);
                        // The active entry may come without classes; the listed one may have them.
                        if (active.Capture is null && profiles.TryGetValue(activeName, out CardProfileOption? same))
                            active = active with { Capture = same.Capture, Outputs = same.Outputs };
                    }

            uint id = card.GetProperty("id").GetUInt32();
            bool capture = active?.Capture ?? sourceCards.Contains(id);
            return new CardView(id, cardName, active, [.. profiles.Values], capture);
        }
        return null;
    }

    // A USB id property ("0x0fd9") the card states, and it is not this one.
    private static bool Differs(JsonElement props, string key, ushort want)
        => props.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ushort.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ushort id)
            && id != want;

    // One EnumProfile or Profile entry. "classes" is a count followed by one
    // struct per media class: [n, ["Audio/Source", count, "card.profile.devices", [ids]], ...].
    private static CardProfileOption Option(JsonElement p, string name, int index)
    {
        int priority = p.TryGetProperty("priority", out JsonElement pr) && pr.ValueKind == JsonValueKind.Number && pr.TryGetInt32(out int prio) ? prio : 0;
        bool available = !(p.TryGetProperty("available", out JsonElement av) && av.ValueKind == JsonValueKind.String && av.GetString() == "no");
        bool? capture = null;
        IReadOnlyList<int>? outputs = null;
        if (p.TryGetProperty("classes", out JsonElement classes) && classes.ValueKind == JsonValueKind.Array)
        {
            capture = false;
            List<int> sinks = [];
            bool sinksListed = true;
            foreach (JsonElement c in classes.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() < 2) continue;
                string? mediaClass = c[0].ValueKind == JsonValueKind.String ? c[0].GetString() : null;
                int count = c[1].ValueKind == JsonValueKind.Number && c[1].TryGetInt32(out int k) ? k : 0;
                if (count <= 0) continue;
                if (mediaClass == "Audio/Source") capture = true;
                else if (mediaClass == "Audio/Sink")
                {
                    JsonElement? devices = null;
                    for (int i = 2; i + 1 < c.GetArrayLength(); i += 2)
                        if (c[i].ValueKind == JsonValueKind.String && c[i].GetString() == "card.profile.devices" && c[i + 1].ValueKind == JsonValueKind.Array)
                            devices = c[i + 1];
                    if (devices is not { } ids) { sinksListed = false; continue; }
                    foreach (JsonElement d in ids.EnumerateArray())
                        if (d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out int device)) sinks.Add(device);
                }
            }
            sinks.Sort();
            outputs = sinksListed ? sinks : null;
        }
        return new CardProfileOption(name, index, priority, available, capture, outputs);
    }

    private static byte[] Run(string cmd, params string[] args)
    {
        ProcessResult r = ProcessRunner.Run(cmd, args, TimeSpan.FromSeconds(5), stdoutCap: 16 * 1024 * 1024, stderrCap: 64 * 1024);
        if (r.TimedOut) throw new TimeoutException($"{cmd} timed out after 5 seconds");
        if (r.Truncated) throw new InvalidOperationException($"{cmd}: output over the 16 MiB cap");
        if (r.Incomplete) throw new InvalidOperationException($"{cmd}: output ended before the helper closed it");
        if (r.ExitCode != 0) throw new InvalidOperationException($"{cmd}: {r.Stderr.Trim()}");
        return r.Stdout;
    }
}
