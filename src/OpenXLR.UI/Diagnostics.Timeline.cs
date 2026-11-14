using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

public static partial class Diagnostics
{
    /// <summary>
    /// Write <c>timeline.txt</c>: the daemon's event timeline, each glitch
    /// marker with the entries of the window before it, then every entry the
    /// daemon still holds. The text goes through the archive's redaction and
    /// its masking of hex-encoded serials, as the journal does.
    /// </summary>
    internal static async Task WriteTimelineAsync(DaemonClient client, string work, Func<string, string> clean,
        IEnumerable<string> secrets, TimeSpan timeout)
    {
        JsonNode? reply = await client.RequestTimelineAsync(timeout);
        await File.WriteAllTextAsync(Path.Combine(work, "timeline.txt"),
            TimelineArchive(reply, DateTimeOffset.Now, TimeZoneInfo.Local, clean, secrets));
    }

    /// <summary><c>timeline.txt</c> as written: <see cref="TimelineText"/> after both redaction passes.</summary>
    internal static string TimelineArchive(JsonNode? reply, DateTimeOffset collectedAt, TimeZoneInfo zone,
        Func<string, string> clean, IEnumerable<string> secrets)
        => RedactHex(clean(TimelineText(reply, collectedAt, zone)), secrets);

    /// <summary>
    /// The timeline as the archive holds it, times in <paramref name="zone"/>
    /// to the millisecond, so they can be found in <c>journal.txt</c> and
    /// <c>audio-journal.txt</c>, which use the computer's local time.
    /// </summary>
    internal static string TimelineText(JsonNode? reply, DateTimeOffset collectedAt, TimeZoneInfo zone)
    {
        var text = new StringBuilder();
        text.AppendLine("OpenXLR event timeline");
        text.AppendLine($"collected at: {Stamp(collectedAt, zone)}");
        if (reply is not JsonObject timeline || timeline["events"] is not JsonArray events)
        {
            text.AppendLine();
            text.AppendLine("No timeline: the daemon did not answer getTimeline. It was disconnected, timed out, or is older than the glitch marker.");
            return text.ToString();
        }
        int span = Number(timeline, "spanSeconds") ?? 600;
        int capacity = Number(timeline, "capacity") ?? 500;
        int window = Number(timeline, "windowSeconds") ?? 30;
        long overflowed = (long?)Number(timeline, "overflowed") ?? 0;
        if (When(timeline["since"]) is { } since) text.AppendLine($"daemon started: {Stamp(since, zone)}");
        if (When(timeline["now"]) is { } now) text.AppendLine($"daemon time: {Stamp(now, zone)}");
        text.AppendLine();
        text.AppendLine($"The daemon keeps the last {span} s of events, at most {capacity}, in memory only; "
            + (overflowed > 0 ? $"{overflowed} older ones were dropped because the record was full. " : "")
            + $"Each glitch marker keeps the {window} s before it, copied when it was marked.");
        text.AppendLine("PipeWire reports no per-node xruns here: a marker records the moment the glitch was heard and what "
            + "changed before it. Find the same time in journal.txt, audio-journal.txt and kernel-log.txt.");

        var markers = timeline["markers"] as JsonArray ?? [];
        text.AppendLine();
        Heading(text, $"Glitch markers ({markers.Count})");
        if (markers.Count == 0) text.AppendLine("No glitch was marked while the daemon ran.");
        int index = 0;
        foreach (JsonNode? node in markers)
        {
            index++;
            if (node is not JsonObject marker || When(marker["at"]) is not { } at) continue;
            string? label = Value(marker["label"]);
            text.AppendLine($"#{index} {Stamp(at, zone)}{(label is null ? "" : $", marked from {label}")}");
            var before = (marker["before"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (before.Count == 0) text.AppendLine($"   nothing recorded in the {window} s before it");
            foreach (JsonObject entry in before)
            {
                if (When(entry["at"]) is not { } t) continue;
                double ago = (t - at).TotalSeconds;
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"   {ago,8:0.000} s  {TimeOfDay(t, zone)}  {Kind(entry)}  {Value(entry["text"])}"));
            }
            text.AppendLine();
        }

        text.AppendLine();
        Heading(text, $"Events ({events.Count})");
        foreach (JsonObject entry in events.OfType<JsonObject>())
        {
            if (When(entry["at"]) is not { } t) continue;
            string kind = Kind(entry);
            text.AppendLine($"{Stamp(t, zone)}  {kind}  {(kind.TrimEnd() == "marker" ? ">>> " : "")}{Value(entry["text"])}");
        }
        return text.ToString();

        static void Heading(StringBuilder text, string title) { text.AppendLine(title); text.AppendLine(new string('-', title.Length)); }
        static string Kind(JsonObject entry) => (Value(entry["kind"]) ?? "?").PadRight(7);
    }

    private static string Stamp(DateTimeOffset at, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    private static string TimeOfDay(DateTimeOffset at, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static DateTimeOffset? When(JsonNode? node)
        => Value(node) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at)
            ? at : null;

    private static string? Value(JsonNode? node)
        => node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static int? Number(JsonObject node, string name)
        => node[name] is JsonValue value && value.TryGetValue(out long number) ? (int)Math.Clamp(number, int.MinValue, int.MaxValue) : null;
}
