using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenXLR.UI;

/// <summary>
/// The figures Mic setup's room measurement works to, in dBFS and dB. It
/// listens to the room for <see cref="MicSetupLevels.MeasureTime"/> while
/// the user stays quiet and suggests an expander threshold a little above
/// the room's noise.
/// </summary>
/// <remarks>
/// The expander judges each sample's peak (native/dynamics.c), so the room is
/// judged on peak readings, not on the 300 ms RMS Suggest gain uses for a
/// voice: steady noise peaks about 10 dB over its RMS, and a threshold a few
/// dB over the RMS would sit under the room's own peaks, which would hold the
/// expander open. The readings are the expander's own input meter where the
/// daemon sends it, else the held peak of the meter before the chain; both
/// are a sample peak taken over the meter's 66 ms.
/// </remarks>
public static class MicSetupRoomLevels
{
    /// <summary>
    /// The room's noise is the level the peak readings stay under 95 percent
    /// of the time, the same share Suggest gain takes for a voice: the loud
    /// end of the room, without a lone click deciding it.
    /// </summary>
    public const double NoisePercentile = 0.95;

    /// <summary>The quiet end of the room, which the spread is measured from.</summary>
    public const double QuietPercentile = 0.10;

    /// <summary>
    /// How far over the room's noise the threshold goes. The noise figure is
    /// already the loud end of the peaks, and a steady room's peaks move
    /// about 1 to 2 dB from one reading to the next, so 4 dB keeps the room
    /// from opening the expander and leaves a little for a fan that speeds
    /// up, while staying as low as it can under soft speech. Sluice, another
    /// mixer, uses the same 4 dB.
    /// </summary>
    public const double MarginDb = 4;

    /// <summary>
    /// A spread wider than this between the loud and the quiet end is not a
    /// room left alone: someone talked or something loud happened. A steady
    /// room spreads by a few dB; speech, a cough or a door, and the meter's
    /// fall after them, spread by 15 dB and more.
    /// </summary>
    public const double SpreadDb = 10;

    /// <summary>
    /// The threshold stays at least this far under the voice's level from
    /// Suggest gain, the RMS of its loud stretches. Quieter words run 10 to
    /// 20 dB under those stretches and their peaks some 10 dB over their own
    /// RMS, so they still open an expander set this low.
    /// </summary>
    public const double SpeechGapDb = 15;

    /// <summary>
    /// Noise whose peaks stay under this is a very quiet room: the expander
    /// has little to take away. It is the bottom of the meter before the
    /// chain, and also the level under which a spread no longer tells
    /// speech: noise removal's leftovers come and go by more than 10 dB down
    /// there, and no voice is that quiet.
    /// </summary>
    public const double QuietRoomDb = -60;

    /// <summary>Peaks at or under this are digital silence: nothing reaches the expander.</summary>
    public const double SilenceDb = -100;
}

/// <summary>What a room measurement found.</summary>
public enum RoomOutcome
{
    /// <summary>The meter sent too few readings to judge.</summary>
    TooFewReadings,
    /// <summary>Nothing reached the meter, or nothing above the floor of a meter that cannot tell a quiet room from none.</summary>
    NotHeard,
    /// <summary>The readings spread too far for a room left alone: talk or a loud sound.</summary>
    Talking,
    /// <summary>A threshold just over the room's noise and well under the voice.</summary>
    Set,
    /// <summary>The room is so quiet the expander has little to do; a threshold is still offered.</summary>
    QuietRoom,
    /// <summary>The noise is close to the voice: the highest safe threshold, which leaves some noise through.</summary>
    LoudRoom,
    /// <summary>The voice is so quiet that even the expander's lowest threshold could cut it.</summary>
    VoiceTooQuiet,
}

/// <summary>
/// A room measurement and the expander threshold it suggests.
/// <see cref="NoiseDb"/> is the 95th percentile of the peak readings, the
/// loud end of the room, <see cref="SpreadDb"/> how far it is over the 10th,
/// <see cref="SpeechDb"/> the voice it was checked against (null when no
/// voice was measured, in which case the level Suggest gain aims for stands
/// in), and <see cref="ThresholdDb"/> the threshold offered, in whole dB
/// inside the expander's range, or null when none is.
/// </summary>
public sealed record RoomSuggestion(RoomOutcome Outcome, int Readings, double NoiseDb, double SpreadDb, bool AtFloor,
    double? SpeechDb, int? ThresholdDb)
{
    /// <summary>A threshold can be applied.</summary>
    public bool HasThreshold => ThresholdDb is not null;

    /// <summary>
    /// Judge the peak readings of a quiet room. <paramref name="floorDb"/> is
    /// the lowest level the meter tells apart: a reading there means at that
    /// level or under it. <paramref name="speechDb"/> is the voice's level
    /// from Suggest gain at the gain the room was measured at, or null.
    /// <paramref name="minDb"/> and <paramref name="maxDb"/> are the range of
    /// the expander's threshold where it runs now.
    /// </summary>
    public static RoomSuggestion From(IReadOnlyList<double> peaksDb, double floorDb, double? speechDb, double minDb, double maxDb)
    {
        if (peaksDb.Count < MicSetupLevels.MinReadings)
            return new(RoomOutcome.TooFewReadings, peaksDb.Count, floorDb, 0, false, speechDb, null);
        double[] peaks = [.. peaksDb.Select(p => double.IsFinite(p) ? Math.Max(p, floorDb) : floorDb).Order()];
        double noise = Rank(peaks, MicSetupRoomLevels.NoisePercentile);
        double spread = noise - Rank(peaks, MicSetupRoomLevels.QuietPercentile);
        // Nearly every reading at the floor of a meter that stops above
        // digital silence: quiet, or not heard at all.
        bool atFloor = noise <= floorDb + 0.5 && floorDb > MicSetupRoomLevels.SilenceDb;
        RoomSuggestion Result(RoomOutcome outcome, int? threshold = null)
            => new(outcome, peaks.Length, noise, spread, atFloor, speechDb, threshold);

        // Digital silence, or a floor that a working microphone in a very
        // quiet room reads too: only a voice measured before tells them apart.
        if (noise <= MicSetupRoomLevels.SilenceDb || atFloor && speechDb is null) return Result(RoomOutcome.NotHeard);
        if (spread > MicSetupRoomLevels.SpreadDb && noise > MicSetupRoomLevels.QuietRoomDb) return Result(RoomOutcome.Talking);

        // The highest threshold that keeps the quieter words: under the voice
        // measured, or under the level Suggest gain aims a voice at.
        double ceiling = Math.Min(Math.Floor((speechDb ?? MicSetupLevels.TargetSpeechDb) - MicSetupRoomLevels.SpeechGapDb), maxDb);
        if (ceiling < Math.Ceiling(minDb)) return Result(RoomOutcome.VoiceTooQuiet);
        int wanted = (int)Math.Ceiling(noise + MicSetupRoomLevels.MarginDb - 1e-9);
        if (wanted > ceiling) return Result(RoomOutcome.LoudRoom, (int)ceiling);
        int threshold = (int)Math.Clamp(wanted, Math.Ceiling(minDb), ceiling);
        return Result(atFloor || noise < MicSetupRoomLevels.QuietRoomDb ? RoomOutcome.QuietRoom : RoomOutcome.Set, threshold);
    }

    private static double Rank(double[] sorted, double share)
        => sorted[Math.Clamp((int)Math.Ceiling(share * sorted.Length) - 1, 0, sorted.Length - 1)];
}
