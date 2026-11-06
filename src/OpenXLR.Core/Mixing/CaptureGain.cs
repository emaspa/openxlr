using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// A capture input's gain, as the state reports it: the source's level in dB,
/// the range a client offers, and whether the level is the device's own
/// hardware gain. With <see cref="Hardware"/> the numbers read as the
/// device's capture control does in alsamixer; without it they are the
/// software level PipeWire applies to the source, 0 dB being unity.
/// <see cref="Muted"/> is the source's own mute, which silences it for every
/// program that records it.
/// </summary>
public sealed record CaptureGainStatus(double GainDb, double MinDb, double MaxDb, bool Hardware, bool Muted = false);

/// <summary>A capture input's gain in dB and its source's mute, as a profile keeps them.</summary>
public sealed record CaptureGainScene(double GainDb, bool Muted);

/// <summary>One PipeWire source's volume as pipewire-pulse reports it.</summary>
/// <param name="Name">The source's node name.</param>
/// <param name="Volume">The loudest channel's volume, on Pulse's scale (65536 is 0 dB).</param>
/// <param name="BaseVolume">The volume at which the hardware control sits at 0 dB (65536 without one).</param>
/// <param name="HardwareVolume">The source sets its volume on the device's own capture control.</param>
/// <param name="AlsaCard">The ALSA card number behind the source, or null.</param>
/// <param name="Muted">The source's own mute.</param>
public sealed record SourceVolume(string Name, long Volume, long BaseVolume, bool HardwareVolume, string? AlsaCard, bool Muted = false);

/// <summary>
/// How a capture input's source volume maps to a gain in dB, and which
/// sources OpenXLR never writes.
///
/// Pulse volumes are cubic: a raw volume v is 60 log10(v / 65536) dB. A
/// source whose device has a hardware capture control reports a base volume
/// below 0 dB: the volume at which that control reads 0 dB. PipeWire puts
/// the source's volume on the hardware control first and the remainder in
/// software, so the device's gain is the source volume in dB minus the base
/// volume in dB, and its highest gain is minus the base volume. A Logitech
/// BRIO reports a base volume of -54 dB and a 'Mic Capture Volume' of +18 to
/// +54 dB: at 0 dB its control reads +54 dB, at -18 dB it reads +36 dB, and
/// below -36 dB the control stays at +18 dB and the rest is attenuated in
/// software. The lowest hardware gain is read from the ALSA control whose
/// highest value matches; without one, the range reaches
/// <see cref="FallbackSpanDb"/> below the highest gain.
/// </summary>
public static class CaptureGain
{
    /// <summary>The range of a source without a hardware control: its software level, 0 dB being unity.</summary>
    public const double SoftwareMinDb = -60, SoftwareMaxDb = 10;

    /// <summary>How far below a hardware source's highest gain the range reaches when the device's lowest gain is unknown.</summary>
    public const double FallbackSpanDb = 60;

    /// <summary>Pulse's 0 dB volume.</summary>
    public const long Norm = 65536;

    /// <summary>The loudest volume OpenXLR writes, +30 dB on Pulse's scale, well past any range offered here.</summary>
    private const long MaxRaw = 65536 * 4;

    /// <summary>A Pulse volume in dB; negative infinity for silence.</summary>
    public static double VolumeDb(long raw) => raw <= 0 ? double.NegativeInfinity : 60 * Math.Log10(raw / (double)Norm);

    /// <summary>The Pulse volume of a level in dB, within what OpenXLR writes.</summary>
    public static long RawVolume(double db)
        => double.IsNegativeInfinity(db) ? 0 : (long)Math.Clamp(Math.Round(Norm * Math.Pow(10, db / 60)), 0, MaxRaw);

    /// <summary>The source has a hardware gain: its device's capture control, with a base volume below 0 dB.</summary>
    public static bool IsHardware(SourceVolume source) => source.HardwareVolume && source.BaseVolume is > 0 and < Norm;

    /// <summary>
    /// The dB a source's gain reads at its base volume: 0 for a software
    /// level. Rounded to the hundredth pactl prints, since the raw base volume
    /// is itself a rounded -54.00 dB and the device's top is a round number.
    /// </summary>
    private static double Offset(SourceVolume source) => IsHardware(source) ? Round(-VolumeDb(source.BaseVolume)) : 0;

    /// <summary>The source's gain, its range and its kind, given the device's lowest hardware gain when it is known.</summary>
    public static CaptureGainStatus Status(SourceVolume source, double? hardwareMinDb)
    {
        if (!IsHardware(source))
            return new(Round(Math.Max(VolumeDb(source.Volume), -120)), SoftwareMinDb, SoftwareMaxDb, false, source.Muted);
        double offset = Offset(source);
        double max = offset;
        double min = hardwareMinDb is double low && low < max ? Round(low) : max - FallbackSpanDb;
        return new(Round(Math.Max(VolumeDb(source.Volume), -120) + offset), min, max, true, source.Muted);
    }

    /// <summary>The Pulse volume that puts the source's gain at <paramref name="gainDb"/>, clamped to the range.</summary>
    public static long RawFor(SourceVolume source, CaptureGainStatus status, double gainDb)
        => RawVolume(Math.Clamp(gainDb, status.MinDb, status.MaxDb) - Offset(source));

    private static double Round(double db) => Math.Round(db, 2);

    /// <summary>
    /// A source OpenXLR never writes: its own nodes, and any node of an
    /// Elgato Wave interface, XLR Dock or Wave microphone, which OpenXLR
    /// either drives through the unit or holds at a fixed level. The Wave XLR
    /// Pro's raw source is one of them.
    /// </summary>
    public static bool IsProtectedSource(string source)
        => source.StartsWith("OpenXLR", StringComparison.Ordinal)
            || source.Contains("Elgato", StringComparison.OrdinalIgnoreCase)
                && (source.Contains("Wave", StringComparison.OrdinalIgnoreCase) || source.Contains("XLR", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The sources in <c>pactl --format=json list sources</c> output that
    /// <paramref name="names"/> lists. A source named twice is left out,
    /// since neither can be told to be the right one. A source without a
    /// mute flag reads as unmuted.
    /// </summary>
    public static IReadOnlyDictionary<string, SourceVolume> ParseSources(ReadOnlySpan<byte> json, IReadOnlySet<string> names)
    {
        var found = new Dictionary<string, SourceVolume>(StringComparer.Ordinal);
        var twice = new HashSet<string>(StringComparer.Ordinal);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return found;
        foreach (JsonElement source in document.RootElement.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("name", out JsonElement n)
                || n.ValueKind != JsonValueKind.String || n.GetString() is not { } name || !names.Contains(name)) continue;
            long? volume = Loudest(source.TryGetProperty("volume", out JsonElement v) ? v : default);
            if (volume is null) continue;
            long baseVolume = source.TryGetProperty("base_volume", out JsonElement b) && RawOf(b) is long raw ? raw : Norm;
            bool hardware = source.TryGetProperty("flags", out JsonElement flags) && flags.ValueKind == JsonValueKind.Array
                && flags.EnumerateArray().Any(f => f.ValueKind == JsonValueKind.String && f.GetString() == "HW_VOLUME_CTRL")
                && flags.EnumerateArray().Any(f => f.ValueKind == JsonValueKind.String && f.GetString() == "DECIBEL_VOLUME");
            string? card = source.TryGetProperty("properties", out JsonElement props) && props.ValueKind == JsonValueKind.Object
                && props.TryGetProperty("alsa.card", out JsonElement c) && c.ValueKind == JsonValueKind.String
                && int.TryParse(c.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                    ? number.ToString(CultureInfo.InvariantCulture) : null;
            bool muted = source.TryGetProperty("mute", out JsonElement mute) && mute.ValueKind == JsonValueKind.True;
            if (!found.TryAdd(name, new(name, volume.Value, baseVolume, hardware, card, muted))) twice.Add(name);
        }
        foreach (string name in twice) found.Remove(name);
        return found;

        static long? Loudest(JsonElement volume)
        {
            if (volume.ValueKind != JsonValueKind.Object) return null;
            long? loudest = null;
            foreach (JsonProperty channel in volume.EnumerateObject())
                if (RawOf(channel.Value) is long raw) loudest = Math.Max(loudest ?? 0, raw);
            return loudest;
        }

        static long? RawOf(JsonElement entry)
            => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("value", out JsonElement value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long raw) && raw >= 0 ? raw : null;
    }

    private static readonly Regex ControlName = new(@"^numid=\d+,iface=MIXER,name='(?<name>[^']*)'", RegexOptions.CultureInvariant);
    private static readonly Regex IntegerRange = new(@"type=INTEGER,.*?,min=(?<min>-?\d+),max=(?<max>-?\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex MinMax = new(@"dBminmax(?:-mute)?-min=(?<min>-?[\d.]+)dB,max=(?<max>-?[\d.]+)dB", RegexOptions.CultureInvariant);
    private static readonly Regex Scale = new(@"dBscale-min=(?<min>-?[\d.]+)dB,step=(?<step>-?[\d.]+)dB", RegexOptions.CultureInvariant);

    /// <summary>
    /// The lowest gain of the capture control in <c>amixer -c N contents</c>
    /// output whose highest gain is <paramref name="maxDb"/>, the one the
    /// source's base volume stands for; null when no capture volume control
    /// matches, or when two that match disagree.
    /// </summary>
    public static double? HardwareMinimum(string amixerContents, double maxDb)
    {
        double? found = null;
        string? name = null;
        long? low = null, high = null;
        foreach (string raw in amixerContents.Split('\n'))
        {
            string line = raw.Trim();
            if (ControlName.Match(line) is { Success: true } control)
            {
                name = control.Groups["name"].Value;
                low = high = null;
                continue;
            }
            if (name is null || !name.Contains("Capture Volume", StringComparison.Ordinal)) continue;
            if (IntegerRange.Match(line) is { Success: true } range)
            {
                low = long.Parse(range.Groups["min"].Value, CultureInfo.InvariantCulture);
                high = long.Parse(range.Groups["max"].Value, CultureInfo.InvariantCulture);
                continue;
            }
            (double Min, double Max)? db = null;
            if (MinMax.Match(line) is { Success: true } minmax)
                db = (Number(minmax.Groups["min"].Value), Number(minmax.Groups["max"].Value));
            else if (Scale.Match(line) is { Success: true } scale && low is long l && high is long h && h > l)
            {
                double min = Number(scale.Groups["min"].Value);
                db = (min, min + Number(scale.Groups["step"].Value) * (h - l));
            }
            if (db is not { } dB || Math.Abs(dB.Max - maxDb) > 0.05 || dB.Min >= dB.Max) continue;
            if (found is double other && Math.Abs(other - dB.Min) > 0.05) return null;
            found = dB.Min;
            name = null;
        }
        return found;

        static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
