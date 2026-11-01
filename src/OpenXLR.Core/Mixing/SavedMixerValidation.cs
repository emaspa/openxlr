using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// Validate saved data before it can partially change a live mixer or device.
/// JSON can contain null despite a non-nullable C# property, and a number such
/// as 1e999 can deserialize to infinity. Missing legacy fields keep their defaults;
/// plugin availability and layout migration remain the existing loaders' job.
///
/// The two stores differ on purpose. A profile is applied as a whole, so one
/// bad field refuses the whole file: applying half a scene is worse than
/// applying none. The settings file is the daemon's own memory of every
/// decision the user made, so a hand-edited stray entry is dropped and the
/// rest is kept; refusing the file would discard the layout, the app registry
/// and the insert chains, and the next save would write defaults over them.
/// </summary>
internal static class SavedMixerValidation
{
    /// <summary>
    /// The settings with malformed entries removed: null list entries, null
    /// dictionary values, non-finite levels, user mixes of an unknown kind,
    /// apps or inserts missing a required field, and a block size PipeWire
    /// is not offered.
    /// <paramref name="dropped"/> names each removal.
    /// </summary>
    internal static MixerSettings Sanitize(MixerSettings settings, out IReadOnlyList<string> dropped)
    {
        var notes = new List<string>();
        dropped = notes;
        return settings with
        {
            InputBindings = InputBindings(settings, notes),
            PrimaryInput = null,
            UserChannels = settings.UserChannels is null ? null : Entries(settings.UserChannels, "userChannels", notes),
            Mixes = settings.Mixes is null ? null : UserMixes(settings.Mixes, notes, "mixes"),
            UserMixes = settings.UserMixes is null ? null : UserMixes(settings.UserMixes, notes),
            ExclusiveGroups = Groups(settings.ExclusiveGroups, notes),
            MixVolumes = Levels(settings.MixVolumes, "mixVolumes", notes),
            MixMuted = Entries(settings.MixMuted, "mixMuted", notes),
            Levels = Levels(settings.Levels, "levels", notes),
            ChannelVolumes = Levels(settings.ChannelVolumes, "channelVolumes", notes),
            ChannelMuted = Entries(settings.ChannelMuted, "channelMuted", notes),
            MonitorOutputs = Entries(settings.MonitorOutputs, "monitorOutputs", notes),
            MonitorOutputMutes = MonitorOutputMutes(settings, notes),
            MonitorFeeds = Mapping(settings.MonitorFeeds, "monitorFeeds", notes),
            MonitorOutputLabels = Mapping(settings.MonitorOutputLabels, "monitorOutputLabels", notes),
            AppOverrides = Mapping(settings.AppOverrides, "appOverrides", notes),
            BrowserSiteChannels = BrowserSites(settings.BrowserSiteChannels, notes),
            KnownApps = Apps(settings.KnownApps, notes),
            Inserts = Inserts(settings, notes),
            Eq = Eq(settings.Eq, notes),
            EqProcessing = EqProcessing(settings.EqProcessing, notes),
            DynamicsProcessing = Processing(settings.DynamicsProcessing, "dynamicsProcessing", notes),
            NoiseRemoval = NoiseRemoval(settings.NoiseRemoval, notes),
            VoiceChanger = VoiceChanger(settings.VoiceChanger, notes),
            Compressor = Dynamics(settings.Compressor, "compressor", MicCompressor.Check, MicCompressor.Snap, notes),
            Expander = Dynamics(settings.Expander, "expander", MicExpander.Check, MicExpander.Snap, notes),
            Ducking = Ducking(settings, notes),
            Appearance = Appearance(settings.Appearance, notes),
            BlockSize = BlockSize(settings.BlockSize, notes),
        };
    }

    private static int? BlockSize(int? frames, List<string> notes)
    {
        if (frames is not int f || Mixer.BlockSizes.Contains(f)) return frames;
        notes.Add($"blockSize {f} is not one of {string.Join(", ", Mixer.BlockSizes)}");
        return null;
    }

    internal static void Validate(MixerScene scene)
    {
        Levels(scene.MixVolumes, "mixVolumes");
        Levels(scene.Levels, "levels");
        if (scene.ChannelVolumes is not null) Levels(scene.ChannelVolumes, "channelVolumes");
        Names(scene.MixMuted, "mixMuted");
        Names(scene.ChannelMuted, "channelMuted");
        if (scene.MonitorOutputs is not null) Names(scene.MonitorOutputs, "monitorOutputs");
        if (scene.MonitorFeeds is not null) Mapping(scene.MonitorFeeds, "monitorFeeds");
        if (scene.Inserts is not null) Inserts(scene.Inserts);
        if (scene.Eq is not null) Eq(scene.Eq);
        if (scene.NoiseRemoval is not null) NoiseRemoval(scene.NoiseRemoval);
        if (scene.VoiceChanger is not null) VoiceChanger(scene.VoiceChanger);
        if (scene.Compressor is not null) Dynamics(scene.Compressor, "compressor", MicCompressor.Check);
        if (scene.Expander is not null) Dynamics(scene.Expander, "expander", MicExpander.Check);
        if (scene.Ducking is { } ducking)
        {
            Require(!string.IsNullOrWhiteSpace(ducking.Key), "key must name a microphone channel", "ducking.key");
            Names(ducking.Channels, "ducking.channels");
            Names(ducking.Mixes, "ducking.mixes");
            Require(ducking.Channels.All(c => !string.IsNullOrWhiteSpace(c) && c != ducking.Key)
                && ducking.Channels.Distinct(StringComparer.Ordinal).Count() == ducking.Channels.Count,
                "channels must be distinct channel ids other than the key", "ducking.channels");
            Require(ducking.Mixes.All(m => !string.IsNullOrWhiteSpace(m))
                && ducking.Mixes.Distinct(StringComparer.Ordinal).Count() == ducking.Mixes.Count,
                "mixes must be distinct mix ids", "ducking.mixes");
            if (DuckingSetup.CheckPresets(ducking) is { } problem) throw new JsonException($"Invalid saved mixer field 'ducking': {problem}.");
        }
        Require(scene.OutputVolume is null || double.IsFinite(scene.OutputVolume.Value), NonFinite, "outputVolume");
    }

    private const string NullEntry = "null entry";
    private const string NonFinite = "non-finite number";
    private const string TooMany = "too many entries";

    // Lenient pass for the settings file.

    private static DuckingSetup? Ducking(MixerSettings settings, List<string> notes)
    {
        if (settings.Ducking is not { } setup) return null;
        setup = DuckingSetup.Prune(setup, MixerConfig.FromSettings(settings), notes);
        return setup with
        {
            Sensitivity = Preset(setup.Sensitivity, Devices.ProDucking.Sensitivities, "sensitivity"),
            Amount = Preset(setup.Amount, Devices.ProDucking.Amounts, "amount"),
            Speed = Preset(setup.Speed, Devices.ProDucking.Speeds, "speed"),
            Processing = Processing(),
        };

        string Preset(string value, IReadOnlyList<string> choices, string field)
        {
            if (choices.Contains(value)) return value;
            notes.Add($"ducking.{field}: invalid preset; reset to medium");
            return Devices.ProDucking.Medium;
        }

        string Processing()
        {
            if (MicEqProcessing.IsValid(setup.Processing)) return setup.Processing;
            notes.Add("ducking.processing: must be device or software; reset to device");
            return Devices.ProDucking.Device;
        }
    }

    /// <summary>
    /// The strips bound to a source. A file written before the per-strip
    /// bindings carries XLR 1's source as <c>primaryInput</c>; it is read
    /// as XLR 1's binding when <c>inputBindings</c> is absent, and dropped
    /// otherwise, so the next save writes only the new field.
    /// </summary>
    private static Dictionary<string, SourceBinding>? InputBindings(MixerSettings settings, List<string> notes)
    {
        if (settings.InputBindings is null)
        {
            if (settings.PrimaryInput is not { } primary) return null;
            if (primary.IsValid) return new() { ["xlr1"] = new SourceBinding(primary.Source, 0, primary.Channel) };
            notes.Add("primaryInput: invalid binding");
            return null;
        }
        var kept = new Dictionary<string, SourceBinding>();
        foreach ((string strip, SourceBinding? binding) in settings.InputBindings)
        {
            if (!Mixer.IsBindableStrip(strip)) notes.Add($"inputBindings: {strip} takes no binding");
            else if (binding is null || !binding.IsValid) notes.Add($"inputBindings: invalid binding for {strip}");
            else kept[strip] = binding;
        }
        return kept.Count == 0 ? null : kept;
    }

    private static List<T> Entries<T>(List<T>? entries, string field, List<string> notes) where T : class
    {
        if (entries is null) { notes.Add($"{field}: {NullEntry}"); return []; }
        var kept = entries.Where(entry => entry is not null).ToList();
        if (kept.Count != entries.Count) notes.Add($"{field}: {NullEntry}");
        return kept;
    }

    private static Dictionary<string, LayoutAppearance> Appearance(Dictionary<string, LayoutAppearance>? values, List<string> notes)
    {
        if (values is null) { notes.Add($"appearance: {NullEntry}"); return []; }
        var kept = values.Where(p => LayoutAppearance.IsValidEntry(p.Key, p.Value))
            .Take(LayoutAppearance.MaxEntries).ToDictionary();
        if (kept.Count != values.Count) notes.Add("appearance: invalid entry");
        return kept;
    }

    private static List<UserMixDefinition> UserMixes(List<UserMixDefinition> entries, List<string> notes, string field = "userMixes")
    {
        var kept = Entries(entries, field, notes);
        int count = kept.Count;
        kept.RemoveAll(mix => !mix.HasUserKind);
        if (kept.Count != count) notes.Add($"{field}: unknown kind");
        return kept;
    }

    private static List<ExclusiveGroupDefinition> Groups(List<ExclusiveGroupDefinition>? groups, List<string> notes)
    {
        if (groups is null) { notes.Add($"exclusiveGroups: {NullEntry}"); return []; }
        var kept = new List<ExclusiveGroupDefinition>();
        foreach (ExclusiveGroupDefinition group in groups)
        {
            if (ExclusiveGroupsModel.Validate(group) is { } error) notes.Add($"exclusiveGroups: {error}");
            else kept.Add(group);
        }
        return kept;
    }

    private static Dictionary<string, double> Levels(Dictionary<string, double>? levels, string field, List<string> notes)
    {
        if (levels is null) { notes.Add($"{field}: {NullEntry}"); return []; }
        var kept = levels.Where(pair => double.IsFinite(pair.Value)).ToDictionary();
        if (kept.Count != levels.Count) notes.Add($"{field}: {NonFinite}");
        return kept;
    }

    private static Dictionary<string, bool> MonitorOutputMutes(MixerSettings settings, List<string> notes)
    {
        if (settings.MonitorOutputMutes is null) { notes.Add("monitorOutputMutes: null entry"); return []; }
        // An output outside the selection keeps its entry: the mixer still
        // owes that device its original flag when it loads these settings.
        var kept = settings.MonitorOutputMutes.Where(pair => Mixer.IsExactOutputName(pair.Key)
            && !pair.Key.StartsWith("OpenXLR_", StringComparison.Ordinal)).ToDictionary();
        if (kept.Count != settings.MonitorOutputMutes.Count) notes.Add("monitorOutputMutes: not a physical output name");
        return kept;
    }

    private static Dictionary<string, string> Mapping(Dictionary<string, string>? entries, string field, List<string> notes)
    {
        if (entries is null) { notes.Add($"{field}: {NullEntry}"); return []; }
        var kept = entries.Where(pair => pair.Value is not null).ToDictionary();
        if (kept.Count != entries.Count) notes.Add($"{field}: {NullEntry}");
        return kept;
    }

    private static Dictionary<string, string> BrowserSites(Dictionary<string, string>? entries, List<string> notes)
    {
        Dictionary<string, string> kept = BrowserTabRouting.SiteChannels(entries);
        if (entries is null || kept.Count != entries.Count) notes.Add("browserSiteChannels: invalid or excessive site preference");
        return kept;
    }

    private static List<SavedApp> Apps(List<SavedApp>? apps, List<string> notes)
    {
        if (apps is null) { notes.Add($"knownApps: {NullEntry}"); return []; }
        var kept = apps.Where(app => app is { Identity: not null, Label: not null, ChannelId: not null }).ToList();
        if (kept.Count != apps.Count) notes.Add($"knownApps: {NullEntry}");
        return kept;
    }

    private static Dictionary<string, List<InsertDefinition>> Inserts(MixerSettings settings, List<string> notes)
    {
        Dictionary<string, List<InsertDefinition>>? chains = settings.Inserts;
        if (chains is null) { notes.Add($"inserts: {NullEntry}"); return []; }
        MixerConfig layout = MixerConfig.FromSettings(settings);
        HashSet<string> keys = [.. layout.Channels.Where(c => c.ChainWidth is not null).Select(c => c.Id),
            .. layout.Mixes.Select(m => "mix:" + m.Id)];
        var kept = new Dictionary<string, List<InsertDefinition>>();
        foreach (var (channel, chain) in chains)
        {
            if (!keys.Contains(channel)) { notes.Add($"inserts.{channel}: has no insert chain"); continue; }
            if (chain is null) { notes.Add($"inserts.{channel}: {NullEntry}"); continue; }
            var inserts = new List<InsertDefinition>();
            foreach (InsertDefinition insert in chain)
            {
                if (insert is not { Id: not null, Kind: not null, Plugin: not null })
                {
                    notes.Add($"inserts.{channel}: {NullEntry}");
                    continue;
                }
                inserts.Add(insert with { Params = Capped(Levels(insert.Params, $"inserts.{channel}.params", notes), $"inserts.{channel}.params", notes) });
            }
            kept[channel] = inserts;
        }
        return kept;
    }

    /// <summary>
    /// Microphone EQs, each checked and snapped onto the EQ's steps. An entry
    /// that cannot be read is dropped and named; the others, including one
    /// for a channel that is not a microphone today, are kept.
    /// </summary>
    private static Dictionary<string, MicEq> Eq(Dictionary<string, MicEq>? entries, List<string> notes)
    {
        if (entries is null) { notes.Add($"eq: {NullEntry}"); return []; }
        var kept = new Dictionary<string, MicEq>();
        foreach ((string channel, MicEq eq) in entries)
        {
            if (MicEq.Check(eq) is string problem) notes.Add($"eq.{channel}: {problem}");
            else kept[channel] = MicEq.Snap(eq);
        }
        return kept;
    }

    /// <summary>
    /// Noise removal settings, each checked and snapped onto the strength's
    /// steps. An entry that cannot be read is dropped and named, so that
    /// microphone has noise removal off; the others are kept.
    /// </summary>
    private static Dictionary<string, MicNoise> NoiseRemoval(Dictionary<string, MicNoise>? entries, List<string> notes)
    {
        if (entries is null) { notes.Add($"noiseRemoval: {NullEntry}"); return []; }
        var kept = new Dictionary<string, MicNoise>();
        foreach ((string channel, MicNoise noise) in entries)
        {
            if (MicNoise.Check(noise) is string problem) notes.Add($"noiseRemoval.{channel}: {problem}");
            else kept[channel] = MicNoise.Snap(noise);
        }
        return kept;
    }

    /// <summary>
    /// Compressor or expander settings, each checked and snapped onto its
    /// steps. An entry that cannot be read is dropped and named, so that
    /// microphone has the processor off; the others are kept.
    /// </summary>
    private static Dictionary<string, T> Dynamics<T>(Dictionary<string, T>? entries, string field, Func<T?, string?> check,
        Func<T, T> snap, List<string> notes) where T : class
    {
        if (entries is null) { notes.Add($"{field}: {NullEntry}"); return []; }
        var kept = new Dictionary<string, T>();
        foreach ((string channel, T value) in entries)
        {
            if (check(value) is string problem) notes.Add($"{field}.{channel}: {problem}");
            else kept[channel] = snap(value);
        }
        return kept;
    }

    private static void Dynamics<T>(IReadOnlyDictionary<string, T> entries, string field, Func<T?, string?> check) where T : class
    {
        foreach ((string channel, T value) in entries)
            if (check(value) is string problem)
                throw new JsonException($"Invalid saved mixer field '{field}.{channel}': {problem}.");
    }

    /// <summary>
    /// Voice changer settings, each checked and snapped onto the half
    /// semitone. An entry that cannot be read is dropped and named, so that
    /// microphone has the voice changer off; the others are kept.
    /// </summary>
    private static Dictionary<string, MicVoice> VoiceChanger(Dictionary<string, MicVoice>? entries, List<string> notes)
    {
        if (entries is null) { notes.Add($"voiceChanger: {NullEntry}"); return []; }
        var kept = new Dictionary<string, MicVoice>();
        foreach ((string channel, MicVoice voice) in entries)
        {
            if (MicVoice.Check(voice) is string problem) notes.Add($"voiceChanger.{channel}: {problem}");
            else kept[channel] = MicVoice.Snap(voice);
        }
        return kept;
    }

    /// <summary>
    /// Where each microphone's EQ runs: an entry that is neither
    /// <c>device</c> nor <c>software</c> is dropped and named, and the channel
    /// takes the default.
    /// </summary>
    private static Dictionary<string, string> EqProcessing(Dictionary<string, string>? entries, List<string> notes)
        => Processing(entries, "eqProcessing", notes);

    /// <summary>Where each microphone's EQ, or compressor and expander, run, by the rule of <see cref="EqProcessing"/>.</summary>
    private static Dictionary<string, string> Processing(Dictionary<string, string>? entries, string name, List<string> notes)
    {
        if (entries is null) { notes.Add($"{name}: {NullEntry}"); return []; }
        var kept = new Dictionary<string, string>();
        foreach ((string channel, string value) in entries)
        {
            if (MicEqProcessing.IsValid(value)) kept[channel] = value;
            else notes.Add($"{name}.{channel}: must be 'device' or 'software'");
        }
        return kept;
    }

    // The live mixer refuses a control value past the cap, so a file holding
    // more was not written by this daemon; the first entries are kept.
    private static Dictionary<string, double> Capped(Dictionary<string, double> levels, string field, List<string> notes)
    {
        if (levels.Count <= InsertDefinition.MaxParams) return levels;
        notes.Add($"{field}: {TooMany}");
        return levels.Take(InsertDefinition.MaxParams).ToDictionary();
    }

    // Strict pass for profiles.

    private static void Levels(IReadOnlyDictionary<string, double>? levels, string field)
    {
        Require(levels is not null, NullEntry, field);
        Require(levels.Values.All(double.IsFinite), NonFinite, field);
    }

    private static void Names(IReadOnlyCollection<string>? names, string field)
        => Require(names is not null && names.All(name => name is not null), NullEntry, field);

    private static void Mapping(IReadOnlyDictionary<string, string>? entries, string field)
        => Require(entries is not null && entries.Values.All(value => value is not null), NullEntry, field);

    private static void Inserts(IReadOnlyDictionary<string, List<InsertDefinition>>? chains)
    {
        Require(chains is not null, NullEntry, "inserts");
        foreach (var (channel, chain) in chains)
        {
            Require(chain is not null, NullEntry, $"inserts.{channel}");
            foreach (InsertDefinition insert in chain)
            {
                Require(insert is { Id: not null, Kind: not null, Plugin: not null }, NullEntry, $"inserts.{channel}");
                Levels(insert.Params, $"inserts.{channel}.params");
                Require(insert.Params.Count <= InsertDefinition.MaxParams, TooMany, $"inserts.{channel}.params");
            }
        }
    }

    private static void Eq(IReadOnlyDictionary<string, MicEq> entries)
    {
        foreach ((string channel, MicEq eq) in entries)
            if (MicEq.Check(eq) is string problem) throw new JsonException($"Invalid saved mixer field 'eq.{channel}': {problem}.");
    }

    private static void NoiseRemoval(IReadOnlyDictionary<string, MicNoise> entries)
    {
        foreach ((string channel, MicNoise noise) in entries)
            if (MicNoise.Check(noise) is string problem)
                throw new JsonException($"Invalid saved mixer field 'noiseRemoval.{channel}': {problem}.");
    }

    private static void VoiceChanger(IReadOnlyDictionary<string, MicVoice> entries)
    {
        foreach ((string channel, MicVoice voice) in entries)
            if (MicVoice.Check(voice) is string problem)
                throw new JsonException($"Invalid saved mixer field 'voiceChanger.{channel}': {problem}.");
    }

    private static void Require([DoesNotReturnIf(false)] bool valid, string problem, string field)
    {
        if (!valid) throw new JsonException($"Invalid saved mixer field '{field}': {problem}.");
    }
}
