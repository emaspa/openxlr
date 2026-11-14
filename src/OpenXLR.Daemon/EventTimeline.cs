using System.Text.Json.Serialization;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>One entry of the event timeline: when, which kind (<see cref="TimelineKind"/>) and one short sentence.</summary>
public sealed record TimelineEvent(
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// A moment the user marked as a glitch, with where it was marked from and
/// the entries of the <see cref="EventTimeline.Window"/> before it, copied
/// when it was marked so the ring moving on cannot take them away.
/// </summary>
public sealed record GlitchMarker(
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("before")] IReadOnlyList<TimelineEvent> Before);

/// <summary>
/// The daemon's event timeline: a bounded record, in memory only, of what
/// changed around the audio, read against the moments the user marks as a
/// glitch. Entries come from places the daemon already watches (a device
/// connecting or dropping, the card profile, a capture stall, the graph,
/// a helper dying, the clock, an application stream on a channel); nothing
/// polls for them, so an idle daemon adds nothing. It cannot say where an
/// xrun happened: PipeWire's profiler drops a report as large as the
/// mixer's graph, so a marker records the user's moment and what changed
/// before it.
/// </summary>
public sealed class EventTimeline
{
    /// <summary>
    /// The most entries kept. A busy ten minutes with a flapping device and
    /// streams coming and going stays well inside it, and at the longest
    /// text that is about 150 KiB.
    /// </summary>
    public const int Capacity = 500;

    /// <summary>
    /// How far back the ring reaches: long enough that an archive collected
    /// a few minutes after a crackle still holds what led up to it.
    /// </summary>
    public static readonly TimeSpan Span = TimeSpan.FromMinutes(10);

    /// <summary>How much of the ring before a marker the marker keeps.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most markers kept. They outlive <see cref="Span"/> for the run,
    /// so a marker made long before the archive is still in it.
    /// </summary>
    public const int MarkerCapacity = 20;

    /// <summary>The longest entry text; a longer one is cut.</summary>
    public const int TextLimit = 300;

    /// <summary>The longest label a client can give a marker.</summary>
    public const int LabelLimit = 32;

    private readonly object _gate = new();
    private readonly Queue<TimelineEvent> _events = new();
    private readonly Queue<GlitchMarker> _markers = new();
    private readonly TimeProvider _time;
    private long _overflowed;

    public EventTimeline(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Started = _time.GetUtcNow();
    }

    /// <summary>When this timeline began, which is when the daemon started.</summary>
    public DateTimeOffset Started { get; }

    /// <summary>
    /// Add one entry now. A line break or control character becomes a space
    /// and a long text is cut, so one entry stays one line.
    /// </summary>
    public void Record(string kind, string text)
    {
        var entry = new TimelineEvent(_time.GetUtcNow(), kind, Clean(text, TextLimit));
        lock (_gate)
        {
            _events.Enqueue(entry);
            TrimLocked(entry.At);
        }
    }

    /// <summary>
    /// Mark a glitch now: a <see cref="TimelineKind.Marker"/> entry in the
    /// ring, and a marker holding the entries of the window before it.
    /// </summary>
    /// <param name="label">Where it was marked, such as the UI or a Deck key; null when the client gave none.</param>
    public GlitchMarker Mark(string? label)
    {
        DateTimeOffset at = _time.GetUtcNow();
        string? from = string.IsNullOrWhiteSpace(label) ? null : Clean(label, LabelLimit);
        lock (_gate)
        {
            TrimLocked(at);
            var marker = new GlitchMarker(at, from, Before(_events, at, Window));
            _events.Enqueue(new TimelineEvent(at, TimelineKind.Marker, from is null ? "the user marked a glitch" : $"the user marked a glitch ({from})"));
            TrimLocked(at);
            _markers.Enqueue(marker);
            while (_markers.Count > MarkerCapacity) _markers.Dequeue();
            return marker;
        }
    }

    /// <summary>What the ring and the markers hold now, as the <c>timeline</c> message carries them.</summary>
    public TimelineMessage Snapshot()
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            TrimLocked(now);
            return new TimelineMessage(now, Started, [.. _events], [.. _markers], _overflowed);
        }
    }

    /// <summary>
    /// The entries no older than <paramref name="window"/> before
    /// <paramref name="at"/> and not after it, oldest first. Markers are
    /// left out: each is read on its own.
    /// </summary>
    internal static IReadOnlyList<TimelineEvent> Before(IEnumerable<TimelineEvent> events, DateTimeOffset at, TimeSpan window)
        => [.. events.Where(e => e.Kind != TimelineKind.Marker && e.At <= at && at - e.At <= window)];

    private void TrimLocked(DateTimeOffset now)
    {
        while (_events.Count > Capacity) { _events.Dequeue(); _overflowed++; }
        while (_events.TryPeek(out TimelineEvent? oldest) && now - oldest.At > Span) _events.Dequeue();
    }

    internal static string Clean(string text, int limit)
    {
        // Only the head can be kept, so a huge text is not copied whole.
        string head = text.Length > limit * 4 ? text[..(limit * 4)] : text;
        string clean = string.Create(head.Length, head, static (line, source) =>
        {
            for (int i = 0; i < source.Length; i++) line[i] = char.IsControl(source[i]) ? ' ' : source[i];
        }).Trim();
        if (clean.Length <= limit) return clean;
        int cut = limit - 3;
        if (char.IsHighSurrogate(clean[cut - 1])) cut--;
        return clean[..cut].TrimEnd() + "...";
    }
}

/// <summary>Reply to <c>getTimeline</c>: the event timeline and the glitch markers (see <see cref="EventTimeline"/>).</summary>
public sealed record TimelineMessage(
    [property: JsonPropertyName("now")] DateTimeOffset Now,
    [property: JsonPropertyName("since")] DateTimeOffset Since,
    [property: JsonPropertyName("events")] IReadOnlyList<TimelineEvent> Events,
    [property: JsonPropertyName("markers")] IReadOnlyList<GlitchMarker> Markers,
    [property: JsonPropertyName("overflowed")] long Overflowed)
{
    [JsonPropertyName("type")] public string Type => "timeline";
    [JsonPropertyName("capacity")] public int Capacity => EventTimeline.Capacity;
    [JsonPropertyName("spanSeconds")] public int SpanSeconds => (int)EventTimeline.Span.TotalSeconds;
    [JsonPropertyName("windowSeconds")] public int WindowSeconds => (int)EventTimeline.Window.TotalSeconds;
}

/// <summary>
/// A logger that writes through to another and puts each line at
/// Information or above on the timeline as well, for a part of the daemon
/// whose every journal line is a timeline event (the capture rescue).
/// </summary>
internal sealed class TimelineLog(ILogger inner, EventTimeline timeline, string kind) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        inner.Log(logLevel, eventId, state, exception, formatter);
        if (logLevel is >= LogLevel.Information and < LogLevel.None) timeline.Record(kind, formatter(state, exception));
    }
}
