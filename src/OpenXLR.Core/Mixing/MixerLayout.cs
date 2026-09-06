namespace OpenXLR.Core.Mixing;

public sealed partial record MixerConfig
{
    // Bound graph growth even for hand-edited settings files.
    public const int MaxApplicationChannels = 32;
    public const int MaxVirtualMixes = 16;

    /// <summary>Reorder editable nodes only; every existing ID must occur exactly once.</summary>
    public MixerConfig WithOrder(IReadOnlyList<string> channels, IReadOnlyList<string> mixes)
    {
        var apps = Channels.Where(c => c.InputPair is null).ToDictionary(c => c.Id);
        var virtualMics = Mixes.Where(m => m.Kind == MixKind.VirtualMic).ToDictionary(m => m.Id);
        Check(channels, apps.Keys, MaxApplicationChannels, "channels");
        Check(mixes, virtualMics.Keys, MaxVirtualMixes, "mixes");
        return this with
        {
            Channels = [.. Channels.Where(c => c.InputPair is not null), .. channels.Select(id => apps[id])],
            Mixes = [.. Mixes.Where(m => m.Kind == MixKind.Monitor), .. mixes.Select(id => virtualMics[id]),
                .. Mixes.Where(m => m.Kind == MixKind.AuxPort)],
        };

        static void Check(IReadOnlyList<string> order, IEnumerable<string> existing, int limit, string kind)
        {
            ArgumentNullException.ThrowIfNull(order);
            var expected = existing.ToHashSet(StringComparer.Ordinal);
            if (order.Count > limit || order.Count != expected.Count || order.Any(id => id is null || !expected.Remove(id)))
                throw new InvalidOperationException($"{kind}: provide every editable ID exactly once; structural IDs cannot be reordered");
        }
    }

    /// <summary>Keep obsolete app rules out of hardware inputs after a layout change.</summary>
    public string ResolveApplicationChannel(string requested)
        => requested == StreamMatcher.Ignore ? requested
            : (Channels.FirstOrDefault(c => c.InputPair is null && c.Id == requested)
                ?? Channels.FirstOrDefault(c => c.InputPair is null))?.Id ?? StreamMatcher.Ignore;

    /// <summary>
    /// Restore ordered user nodes without replacing hardware or monitor buses.
    /// Old files retain the default layout. Invalid entries are ignored, and
    /// an empty application list heals to System as a safe routing destination.
    /// </summary>
    public static MixerConfig FromSettings(MixerSettings? settings)
    {
        MixerConfig defaults = Default();
        var structuralMixes = defaults.Mixes.Where(m => m.Kind != MixKind.VirtualMic).ToList();
        var hardware = defaults.Channels.Where(c => c.InputPair is not null).ToList();
        var mixEntries = settings?.UserMixes is null
            ? defaults.Mixes.Where(m => m.Kind == MixKind.VirtualMic).Select(m => ((string?)m.Id, (string?)m.Name))
            : settings.UserMixes.Select(m => (m?.Id, m?.Name));
        var channelEntries = settings?.UserChannels is null
            ? defaults.Channels.Where(c => c.InputPair is null).Select(c => ((string?)c.Id, (string?)c.Name))
            : settings.UserChannels.Select(c => (c?.Id, c?.Name));

        var mixes = structuralMixes.Where(m => m.Kind == MixKind.Monitor).ToList();
        mixes.AddRange(ValidEntries(mixEntries, structuralMixes.Select(m => m.Id), MaxVirtualMixes)
            .Select(m => new MixDefinition(m.Id, m.Name, MixKind.VirtualMic)));
        mixes.AddRange(structuralMixes.Where(m => m.Kind == MixKind.AuxPort));

        var apps = ValidEntries(channelEntries, hardware.Select(c => c.Id).Append(StreamMatcher.Ignore), MaxApplicationChannels).ToList();
        if (apps.Count == 0) apps.Add(("system", "System"));
        var channels = hardware.Select(Normalize).ToList();
        channels.AddRange(apps.Select(c => Normalize(
            (defaults.Channels.FirstOrDefault(d => d.Id == c.Id) ?? new ChannelDefinition(c.Id, c.Name))
            with { Name = c.Name })));
        return new MixerConfig { Mixes = mixes, Channels = channels };

        ChannelDefinition Normalize(ChannelDefinition channel)
        {
            var levels = new Dictionary<string, double>();
            var muted = new HashSet<string>();
            foreach (MixDefinition mix in mixes)
            {
                bool feedback = channel.Id == "aux" && mix.Kind == MixKind.AuxPort;
                levels[mix.Id] = feedback ? 0 : channel.Levels.GetValueOrDefault(mix.Id, 1);
                if (feedback || channel.MutedIn.Contains(mix.Id)) muted.Add(mix.Id);
            }
            return channel with { Levels = levels, MutedIn = muted };
        }
    }

    private static IEnumerable<(string Id, string Name)> ValidEntries(
        IEnumerable<(string? Id, string? Name)> entries, IEnumerable<string> reserved, int limit)
    {
        var used = new HashSet<string>(reserved, StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (var (id, label) in entries)
        {
            string? name = label?.Trim();
            if (id is not { Length: > 0 and <= 36 } || id[0] is < 'a' or > 'z' ||
                !id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_') ||
                name is not { Length: > 0 and <= 60 } || name.Any(char.IsControl) || !used.Add(id)) continue;
            yield return (id, name);
            if (++count == limit) yield break;
        }
    }
}
