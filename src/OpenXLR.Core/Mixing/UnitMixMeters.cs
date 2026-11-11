using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The <c>mix:&lt;id&gt;</c> meters of the mixes a Wave XLR Pro builds in
/// hardware mixing, read from the unit's own meters (block 0x0002) instead
/// of this computer's copy of each mix.
///
/// A mix reads the level of the section that builds it (channels 30 to 34),
/// not the jack it plays on (bytes 142 to 149). The section level comes
/// after the faders, the channel volumes and the mix volume, which the unit
/// holds in its cells or, for a mix built alone in sections 18 to 54, in the
/// section's own level and mute (desk, 2026-11-11), and before the jack's headphone volume or Line Out
/// level, which the output volume moves in the mode. The computer's copy is
/// metered on its mix sink, after the sends and before any output, so the
/// output volume moves neither meter. A jack's own level would also follow
/// a headphone knob that the computer's meter never saw.
///
/// Only a section that builds one mix alone stands for that mix: a jack
/// feed of one mix, and the Aux mix in section 36 while the USB Aux port is
/// on. A summed feed's section carries the sum of its mixes, so each of
/// them keeps the computer's copy unless a feed of its own plays it.
/// </summary>
public static class UnitMixMeters
{
    private static readonly IReadOnlyDictionary<string, int> None = new Dictionary<string, int>();

    /// <summary>
    /// Each mix the unit builds alone in a section, to that section's level
    /// channel in block 0x0002; empty while the plan does not run.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Channels(HardwareMixingPlan plan)
    {
        if (!plan.Active || plan.Sections.Count == 0) return None;
        var channels = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string feed, int section) in plan.Sections)
            if (MonitorFeed.Parts(feed) is [string mix] && ProMixSections.Starts.Contains(section))
                channels[mix] = ProMeters.SectionChannel(section);
        return channels;
    }

    /// <summary>
    /// Put the unit's level of each mix in <paramref name="channels"/> under
    /// its <c>mix:&lt;id&gt;</c> key as <c>[left, right]</c> on the meters'
    /// scale (<see cref="MeterReader.DisplayOfDb"/>). With no reading (a
    /// read that failed, or none fresh) every key keeps the computer's copy.
    /// </summary>
    public static void Apply(IDictionary<string, double[]> meters, IReadOnlyDictionary<string, int> channels, ProMeterReading? reading)
    {
        if (reading is null) return;
        foreach ((string mix, int channel) in channels)
        {
            if (channel < 0 || channel >= reading.Levels.Count) continue;
            ProStereoMeter level = reading.Levels[channel];
            meters[MixKey(mix)] = [MeterReader.DisplayOfDb(level.LeftDb), MeterReader.DisplayOfDb(level.RightDb)];
        }
    }

    /// <summary>The meters key of a mix.</summary>
    public static string MixKey(string mix) => "mix:" + mix;

    /// <summary>The meters key of a mix's maximizer on the unit: its reduction in dB.</summary>
    public static string MaximizerKey(string mix) => "max:" + mix;

    /// <summary>
    /// Put each running maximizer's reduction now, in dB, under its
    /// <c>max:&lt;id&gt;</c> key: <paramref name="sections"/> maps each mix
    /// to the section that builds it, whose maximizer is block 0x0002's
    /// maximizer of the same index. With no reading the keys stay out, as a
    /// duck stage's do while it is not read.
    /// </summary>
    public static void ApplyMaximizers(IDictionary<string, double[]> meters, IReadOnlyDictionary<string, int> sections, ProMeterReading? reading)
    {
        if (reading is null) return;
        foreach ((string mix, int section) in sections)
        {
            int m = ProMixSections.MaximizerOf(section);
            if (m < reading.Maximizers.Count) meters[MaximizerKey(mix)] = [reading.Maximizers[m].ReductionDb];
        }
    }
}
