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
    /// dictionary values, non-finite levels, and apps or inserts missing a
    /// required field. <paramref name="dropped"/> names each removal.
    /// </summary>
    internal static MixerSettings Sanitize(MixerSettings settings, out IReadOnlyList<string> dropped)
    {
        var notes = new List<string>();
        dropped = notes;
        return settings with
        {
            UserChannels = settings.UserChannels is null ? null : Entries(settings.UserChannels, "userChannels", notes),
            UserMixes = settings.UserMixes is null ? null : Entries(settings.UserMixes, "userMixes", notes),
            MixVolumes = Levels(settings.MixVolumes, "mixVolumes", notes),
            MixMuted = Entries(settings.MixMuted, "mixMuted", notes),
            Levels = Levels(settings.Levels, "levels", notes),
            ChannelMuted = Entries(settings.ChannelMuted, "channelMuted", notes),
            MonitorOutputs = Entries(settings.MonitorOutputs, "monitorOutputs", notes),
            MonitorFeeds = Mapping(settings.MonitorFeeds, "monitorFeeds", notes),
            AppOverrides = Mapping(settings.AppOverrides, "appOverrides", notes),
            KnownApps = Apps(settings.KnownApps, notes),
            Inserts = Inserts(settings.Inserts, notes),
        };
    }

    internal static void Validate(MixerScene scene)
    {
        Levels(scene.MixVolumes, "mixVolumes");
        Levels(scene.Levels, "levels");
        Names(scene.MixMuted, "mixMuted");
        Names(scene.ChannelMuted, "channelMuted");
        if (scene.MonitorOutputs is not null) Names(scene.MonitorOutputs, "monitorOutputs");
        if (scene.MonitorFeeds is not null) Mapping(scene.MonitorFeeds, "monitorFeeds");
        if (scene.Inserts is not null) Inserts(scene.Inserts);
        Require(scene.OutputVolume is null || double.IsFinite(scene.OutputVolume.Value), NonFinite, "outputVolume");
    }

    private const string NullEntry = "null entry";
    private const string NonFinite = "non-finite number";
    private const string TooMany = "too many entries";

    // Lenient pass for the settings file.

    private static List<T> Entries<T>(List<T>? entries, string field, List<string> notes) where T : class
    {
        if (entries is null) { notes.Add($"{field}: {NullEntry}"); return []; }
        var kept = entries.Where(entry => entry is not null).ToList();
        if (kept.Count != entries.Count) notes.Add($"{field}: {NullEntry}");
        return kept;
    }

    private static Dictionary<string, double> Levels(Dictionary<string, double>? levels, string field, List<string> notes)
    {
        if (levels is null) { notes.Add($"{field}: {NullEntry}"); return []; }
        var kept = levels.Where(pair => double.IsFinite(pair.Value)).ToDictionary();
        if (kept.Count != levels.Count) notes.Add($"{field}: {NonFinite}");
        return kept;
    }

    private static Dictionary<string, string> Mapping(Dictionary<string, string>? entries, string field, List<string> notes)
    {
        if (entries is null) { notes.Add($"{field}: {NullEntry}"); return []; }
        var kept = entries.Where(pair => pair.Value is not null).ToDictionary();
        if (kept.Count != entries.Count) notes.Add($"{field}: {NullEntry}");
        return kept;
    }

    private static List<SavedApp> Apps(List<SavedApp>? apps, List<string> notes)
    {
        if (apps is null) { notes.Add($"knownApps: {NullEntry}"); return []; }
        var kept = apps.Where(app => app is { Identity: not null, Label: not null, ChannelId: not null }).ToList();
        if (kept.Count != apps.Count) notes.Add($"knownApps: {NullEntry}");
        return kept;
    }

    private static Dictionary<string, List<InsertDefinition>> Inserts(Dictionary<string, List<InsertDefinition>>? chains, List<string> notes)
    {
        if (chains is null) { notes.Add($"inserts: {NullEntry}"); return []; }
        var kept = new Dictionary<string, List<InsertDefinition>>();
        foreach (var (channel, chain) in chains)
        {
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

    private static void Require([DoesNotReturnIf(false)] bool valid, string problem, string field)
    {
        if (!valid) throw new JsonException($"Invalid saved mixer field '{field}': {problem}.");
    }
}
