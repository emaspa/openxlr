using OpenXLR.Core;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// UCM coexistence. A card that ships a UCM profile (the Wave XLR Pro's
/// splits the device into per-function nodes for daemon-less use) hides
/// the raw multichannel nodes this mixer links against while the split
/// "HiFi" profile is active. The daemon therefore switches such a card
/// to the pro-audio profile while it drives the device, and restores the
/// previous profile on graceful shutdown, so the split serves exactly
/// when OpenXLR is not running. Cards without a UCM profile never match
/// the gate (their active profile is not named "HiFi") and are left
/// alone.
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
        var card = FindCard(fragments, out matched);
        if (card is null) return (null, null);
        (uint id, string active, Dictionary<string, int> profiles) = card.Value;
        if (active is not ("HiFi" or "Direct")) return (active, null);   // not UCM-split; leave alone
        if (!profiles.TryGetValue("pro-audio", out int index)) return (active, null);
        Run("wpctl", "set-profile", id.ToString(), index.ToString());
        return (active, active);
    }

    /// <summary>Set the card back to a named profile (best effort).</summary>
    public static void SetProfile(string nameFragment, string profileName)
    {
        var card = FindCard([nameFragment], out _);
        if (card is null) return;
        (uint id, _, Dictionary<string, int> profiles) = card.Value;
        if (profiles.TryGetValue(profileName, out int index))
            Run("wpctl", "set-profile", id.ToString(), index.ToString());
    }

    /// <summary>
    /// The card the first fragment that names any card names. Two cards
    /// under one fragment are two units of one model that cannot be told
    /// apart, and neither is touched; a broader fragment would only match
    /// more of them.
    /// </summary>
    private static (uint Id, string Active, Dictionary<string, int> Profiles)? FindCard(IReadOnlyList<string> fragments, out string? matched)
    {
        matched = null;
        byte[] dump;
        try { dump = Run("pw-dump"); }
        catch (Exception) { return null; }
        using JsonDocument doc = PipeWireSnapshot.Parse(dump);
        var cards = new List<(string Name, JsonElement Card, JsonElement Info)>();
        foreach (JsonElement o in doc.RootElement.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("type", out JsonElement type)
                || type.GetString() != "PipeWire:Interface:Device") continue;
            if (!o.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("props", out JsonElement props) || props.ValueKind != JsonValueKind.Object) continue;
            string name = props.TryGetProperty("device.name", out JsonElement n)
                ? n.GetString() ?? "" : "";
            cards.Add((name, o, info));
        }
        foreach (string fragment in fragments)
        {
            if (fragment.Length == 0) continue;
            var found = cards.Where(c => c.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (found.Count == 0) continue;
            if (found.Count > 1) return null;
            matched = fragment;
            (_, JsonElement card, JsonElement info) = found[0];
            if (!info.TryGetProperty("params", out JsonElement pars)) return null;

            var profiles = new Dictionary<string, int>();
            if (pars.TryGetProperty("EnumProfile", out JsonElement list))
                foreach (JsonElement p in list.EnumerateArray())
                    if (p.TryGetProperty("name", out JsonElement pn) && p.TryGetProperty("index", out JsonElement pi))
                        profiles[pn.GetString() ?? ""] = pi.GetInt32();

            string active = "";
            if (pars.TryGetProperty("Profile", out JsonElement act))
                foreach (JsonElement p in act.EnumerateArray())
                    if (p.TryGetProperty("name", out JsonElement an))
                        active = an.GetString() ?? "";

            return (card.GetProperty("id").GetUInt32(), active, profiles);
        }
        return null;
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
