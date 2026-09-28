namespace OpenXLR.Core.Mixing;

public sealed partial record MixerConfig
{
    // Bound graph growth even for hand-edited settings files.
    public const int MaxApplicationChannels = 32;
    public const int MaxUserMixes = 16;

    /// <summary>Reorder editable nodes only; every existing ID must occur exactly once.</summary>
    public MixerConfig WithOrder(IReadOnlyList<string> channels, IReadOnlyList<string> mixes)
    {
        var apps = Channels.Where(c => c.InputPair is null).ToDictionary(c => c.Id);
        var editable = Mixes.Where(m => m.IsEditable).ToDictionary(m => m.Id);
        Check(channels, apps.Keys, MaxApplicationChannels, "channels");
        Check(mixes, editable.Keys, MaxUserMixes, "mixes");
        return this with
        {
            Channels = [.. Channels.Where(c => c.InputPair is not null), .. channels.Select(id => apps[id])],
            Mixes = [.. Mixes.Where(m => m.Kind == MixKind.Monitor && !m.IsEditable), .. mixes.Select(id => editable[id]),
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

    /// <summary>The application channel with one display name changed; everything else identical.</summary>
    public MixerConfig WithChannelName(string id, string name)
    {
        ChannelDefinition channel = Channels.FirstOrDefault(c => c.Id == id && c.InputPair is null)
            ?? throw new InvalidOperationException($"'{id}' is not an application channel");
        return this with { Channels = [.. Channels.Select(c => ReferenceEquals(c, channel) ? c with { Name = name } : c)] };
    }

    /// <summary>The user mix with one display name changed; everything else identical.</summary>
    public MixerConfig WithMixName(string id, string name)
    {
        MixDefinition mix = Mixes.FirstOrDefault(m => m.Id == id && m.IsEditable)
            ?? throw new InvalidOperationException($"'{id}' is not an editable mix");
        return this with { Mixes = [.. Mixes.Select(m => ReferenceEquals(m, mix) ? m with { Name = name } : m)] };
    }

    /// <summary>Without one application channel. The last application channel stays: streams need a destination.</summary>
    public MixerConfig WithoutChannel(string id)
    {
        if (!Channels.Any(c => c.Id == id && c.InputPair is null))
            throw new InvalidOperationException($"'{id}' is not an application channel");
        if (Channels.First(c => c.Id == id).IsApplication && Channels.Count(c => c.IsApplication) == 1)
            throw new InvalidOperationException("the last application channel cannot be deleted");
        return this with { Channels = [.. Channels.Where(c => c.Id != id)] };
    }

    /// <summary>Without one user mix; the per-channel sends into it go with it.</summary>
    public MixerConfig WithoutMix(string id)
    {
        if (!Mixes.Any(m => m.Id == id && m.IsEditable))
            throw new InvalidOperationException($"'{id}' is not an editable mix");
        return this with
        {
            Mixes = [.. Mixes.Where(m => m.Id != id)],
            Channels = [.. Channels.Select(c => c with
            {
                Levels = c.Levels.Where(l => l.Key != id).ToDictionary(),
                MutedIn = c.MutedIn.Where(m => m != id).ToHashSet(),
            })],
        };
    }

    /// <summary>
    /// With a new user mix after the existing ones, ahead of Aux.
    /// Every channel gets a muted full-level send into it, so nothing reaches
    /// the new mix until the user opens a send.
    /// </summary>
    public MixerConfig WithMix(MixDefinition mix)
    {
        if (!mix.IsEditable) throw new InvalidOperationException("only user monitor mixes and virtual microphones can be added");
        if (Mixes.Count(m => m.IsEditable) >= MaxUserMixes)
            throw new InvalidOperationException("user mix limit reached");
        if (Mixes.Any(m => m.Id.Equals(mix.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"mix '{mix.Id}' already exists");
        return this with
        {
            Mixes = [.. Mixes.Where(m => m.Kind != MixKind.AuxPort), mix, .. Mixes.Where(m => m.Kind == MixKind.AuxPort)],
            Channels = [.. Channels.Select(c => c with
            {
                Levels = c.Levels.Append(new(mix.Id, 1.0)).ToDictionary(),
                MutedIn = c.MutedIn.Append(mix.Id).ToHashSet(),
            })],
        };
    }

    /// <summary>
    /// A stable id from a display name: lowercase ASCII letters and digits with
    /// hyphens, starting with a letter, at most 28 characters, unique among
    /// <paramref name="existing"/> (case-insensitive) with a numeric suffix.
    /// </summary>
    public static string NewId(string name, string fallback, IEnumerable<string> existing)
    {
        string slug = new(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0) slug = fallback;
        if (!char.IsAsciiLetter(slug[0])) slug = $"{fallback}-{slug}";
        if (slug.Length > 28) slug = slug[..28].TrimEnd('-');
        var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        string id = slug;
        for (int n = 2; used.Contains(id); n++) id = $"{slug}-{n}";
        return id;
    }

    /// <summary>Keep obsolete app rules out of hardware inputs after a layout change.</summary>
    public string ResolveApplicationChannel(string requested)
        => requested == StreamMatcher.Ignore ? requested
            : (Channels.FirstOrDefault(c => c.IsApplication && c.Id == requested)
                ?? Channels.FirstOrDefault(c => c.IsApplication))?.Id ?? StreamMatcher.Ignore;

    /// <summary>
    /// Restore ordered user nodes without replacing hardware or monitor buses.
    /// Old files retain the default layout. Invalid entries are ignored, and
    /// an empty application list heals to System as a safe routing destination.
    /// </summary>
    public static MixerConfig FromSettings(MixerSettings? settings)
    {
        MixerConfig defaults = Default();
        var structuralMixes = defaults.Mixes.Where(m => !m.IsEditable).ToList();
        var hardware = defaults.Channels.Where(c => c.InputPair is not null).ToList();
        var userMixes = (settings?.UserMixes ?? defaults.Mixes.Where(m => m.IsEditable)
            .Select(m => new UserMixDefinition(m.Id, m.Name)).ToList())
            .Where(m => m is not null && m.Kind is "monitor" or "virtualMic").ToList();
        var mixEntries = userMixes.Select(m => ((string?)m.Id, (string?)m.Name));
        var userChannels = (settings?.UserChannels ?? defaults.Channels.Where(c => c.IsApplication)
            .Select(c => new UserChannelDefinition(c.Id, c.Name)).ToList())
            .Where(c => c is not null && (c.CaptureSource is null ? c.CapturePair == 0
                : CaptureBinding.IsValid(c.CaptureSource, c.CapturePair))).ToList();
        var channelEntries = userChannels.Select(c => ((string?)c.Id, (string?)c.Name));

        var mixes = structuralMixes.Where(m => m.Kind == MixKind.Monitor).ToList();
        mixes.AddRange(ValidEntries(mixEntries, structuralMixes.Select(m => m.Id), MaxUserMixes)
            .Select(m => new MixDefinition(m.Id, m.Name,
                userMixes.First(saved => saved.Id == m.Id && saved.Name?.Trim() == m.Name).Kind == "monitor"
                    ? MixKind.Monitor : MixKind.VirtualMic)));
        mixes.AddRange(structuralMixes.Where(m => m.Kind == MixKind.AuxPort));

        var apps = ValidEntries(channelEntries, hardware.Select(c => c.Id).Append(StreamMatcher.Ignore), MaxApplicationChannels).ToList();
        string? fallbackId = null;
        UserChannelDefinition? SavedChannel((string Id, string Name) entry)
            => entry.Id == fallbackId ? null : userChannels.FirstOrDefault(c => c.Id == entry.Id && c.Name?.Trim() == entry.Name);
        if (!apps.Any(c => SavedChannel(c)?.CaptureSource is null))
        {
            if (apps.Count == MaxApplicationChannels) apps.RemoveAt(apps.Count - 1);
            // The synthesized destination is always an application channel.
            // A truncated capture with the same id and label cannot supply
            // its binding when the accepted entries are materialized below.
            fallbackId = NewId("system", "system", apps.Select(c => c.Id));
            apps.Add((fallbackId, "System"));
        }
        var channels = hardware.Select(Normalize).ToList();
        channels.AddRange(apps.Select(c =>
        {
            UserChannelDefinition? saved = SavedChannel(c);
            return Normalize(saved?.CaptureSource is { } source
                ? new ChannelDefinition(c.Id, c.Name) { CaptureSource = source, CapturePair = saved.CapturePair,
                    MutedIn = mixes.Select(m => m.Id).ToHashSet() }
                : (defaults.Channels.FirstOrDefault(d => d.Id == c.Id) ?? new ChannelDefinition(c.Id, c.Name))
                    with { Name = c.Name });
        }));
        return new MixerConfig { Mixes = mixes, Channels = channels };

        ChannelDefinition Normalize(ChannelDefinition channel)
        {
            var levels = new Dictionary<string, double>();
            var muted = new HashSet<string>();
            foreach (MixDefinition mix in mixes)
            {
                bool feedback = channel.Id == "aux" && mix.Kind == MixKind.AuxPort;
                levels[mix.Id] = feedback ? 0 : channel.Levels.GetValueOrDefault(mix.Id, 1);
                if (feedback || channel.MutedIn.Contains(mix.Id) ||
                    !defaults.Mixes.Any(m => m.Id == mix.Id) && !channel.Levels.ContainsKey(mix.Id)) muted.Add(mix.Id);
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
