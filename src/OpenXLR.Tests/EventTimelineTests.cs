using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// The daemon's event timeline and the glitch marker: the ring keeps the
/// newest entries within its count and its age, a marker keeps the 30
/// seconds before it whatever the ring does afterwards, the commands are
/// checked and answered, and the archive's timeline.txt lists the markers
/// with their windows and masks what the rest of the archive masks.
/// </summary>
public sealed class EventTimelineTests
{
    [Fact]
    public void TheRingKeepsItsNewestEntriesAndCountsThoseItDropped()
    {
        var time = new ManualTime();
        var timeline = new EventTimeline(time);
        for (int i = 0; i < EventTimeline.Capacity + 25; i++)
        {
            timeline.Record(TimelineKind.Mixer, $"entry {i}");
            time.Advance(TimeSpan.FromMilliseconds(10));
        }
        TimelineMessage snapshot = timeline.Snapshot();
        Assert.Equal(EventTimeline.Capacity, snapshot.Events.Count);
        Assert.Equal(25, snapshot.Overflowed);
        Assert.Equal("entry 25", snapshot.Events[0].Text);
        Assert.Equal($"entry {EventTimeline.Capacity + 24}", snapshot.Events[^1].Text);
    }

    [Fact]
    public void TheRingForgetsEntriesOlderThanItsSpan()
    {
        var time = new ManualTime();
        var timeline = new EventTimeline(time);
        timeline.Record(TimelineKind.Device, "connected Elgato Wave XLR");
        time.Advance(EventTimeline.Span);
        timeline.Record(TimelineKind.Mixer, "the mixer's graph was built (5 mixes, 9 channels)");
        // Exactly the span old is still held; a moment later it is gone.
        Assert.Equal(2, timeline.Snapshot().Events.Count);
        time.Advance(TimeSpan.FromMilliseconds(1));
        TimelineEvent left = Assert.Single(timeline.Snapshot().Events);
        Assert.Equal(TimelineKind.Mixer, left.Kind);
        Assert.Equal(0, timeline.Snapshot().Overflowed);
    }

    [Fact]
    public void AMarkerKeepsTheThirtySecondsBeforeItEvenAfterTheRingMovesOn()
    {
        var time = new ManualTime();
        var timeline = new EventTimeline(time);
        DateTimeOffset start = time.GetUtcNow();
        // Entries at 0, 1, 2 and 31.5 seconds; the marker at 32 s.
        timeline.Record(TimelineKind.Device, "too early");
        time.Advance(TimeSpan.FromSeconds(1));
        timeline.Record(TimelineKind.Usb, "a USB transfer to Elgato Wave XLR never returned");
        time.Advance(TimeSpan.FromSeconds(1));
        timeline.Record(TimelineKind.Device, "on the edge");
        time.Advance(TimeSpan.FromSeconds(29.5));
        timeline.Record(TimelineKind.Stream, "firefox started playing on Browser");
        time.Advance(TimeSpan.FromSeconds(0.5));
        GlitchMarker marker = timeline.Mark("UI");
        Assert.Equal(start + TimeSpan.FromSeconds(32), marker.At);
        Assert.Equal("UI", marker.Label);
        Assert.Equal(["on the edge", "firefox started playing on Browser"], marker.Before.Select(e => e.Text));

        // The entry one second later is after the marker, not in its window.
        time.Advance(TimeSpan.FromSeconds(1));
        timeline.Record(TimelineKind.Plugin, "xlr1 stopped on its own; it is started again");
        GlitchMarker second = timeline.Mark(null);
        Assert.Null(second.Label);
        // A second marker's window holds the entries, never the first marker.
        Assert.DoesNotContain(second.Before, e => e.Kind == TimelineKind.Marker);
        Assert.Contains(second.Before, e => e.Text == "xlr1 stopped on its own; it is started again");

        TimelineMessage now = timeline.Snapshot();
        Assert.Equal(["the user marked a glitch (UI)", "the user marked a glitch"],
            now.Events.Where(e => e.Kind == TimelineKind.Marker).Select(e => e.Text));

        // Long after, the ring has let go of everything; the markers have not.
        time.Advance(EventTimeline.Span + TimeSpan.FromMinutes(5));
        TimelineMessage later = timeline.Snapshot();
        Assert.Empty(later.Events);
        Assert.Equal(2, later.Markers.Count);
        Assert.Equal(2, later.Markers[0].Before.Count);
    }

    [Fact]
    public void MarkersAreBoundedOldestFirstOut()
    {
        var time = new ManualTime();
        var timeline = new EventTimeline(time);
        for (int i = 0; i < EventTimeline.MarkerCapacity + 3; i++)
        {
            timeline.Mark($"key {i}");
            time.Advance(TimeSpan.FromSeconds(1));
        }
        IReadOnlyList<GlitchMarker> markers = timeline.Snapshot().Markers;
        Assert.Equal(EventTimeline.MarkerCapacity, markers.Count);
        Assert.Equal("key 3", markers[0].Label);
    }

    [Fact]
    public void AnEntryIsOneShortLine()
    {
        var timeline = new EventTimeline(new ManualTime());
        timeline.Record(TimelineKind.Mixer, "Plugin host OpenXLR_ch_xlr1_stage_0:\ntrace: line two\r\n");
        timeline.Record(TimelineKind.Mixer, new string('x', EventTimeline.TextLimit * 10));
        timeline.Mark("a label far longer than the limit for one");
        var events = timeline.Snapshot().Events;
        Assert.Equal("Plugin host OpenXLR_ch_xlr1_stage_0: trace: line two", events[0].Text);
        Assert.Equal(EventTimeline.TextLimit, events[1].Text.Length);
        Assert.EndsWith("...", events[1].Text, StringComparison.Ordinal);
        Assert.True(timeline.Snapshot().Markers[0].Label!.Length <= EventTimeline.LabelLimit);
    }

    [Fact]
    public void TheCaptureRescueLogAlsoWritesTheTimeline()
    {
        var timeline = new EventTimeline(new ManualTime());
        var inner = new RecordingLogger<MixerService>();
        ILogger log = new TimelineLog(inner, timeline, TimelineKind.Capture);
        log.LogWarning("{dev}: its capture is still stopped", "Elgato Wave:3 MK.2");
        log.LogDebug("a debug line stays in the journal only");
        Assert.Equal(2, inner.Entries.Count);
        TimelineEvent entry = Assert.Single(timeline.Snapshot().Events);
        Assert.Equal(TimelineKind.Capture, entry.Kind);
        Assert.Equal("Elgato Wave:3 MK.2: its capture is still stopped", entry.Text);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("UI", true)]
    [InlineData("terminal mixer", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Deck\nkey", false)]
    [InlineData("a label longer than thirty-two characters", false)]
    public void TheMarkerCommandsAreChecked(string? label, bool accepted)
    {
        using var mixer = new Mixer();
        string? error = CommandValidation.Check(new Command { Cmd = "markGlitch", Label = label }, mixer, _ => null);
        if (accepted) Assert.Null(error);
        else Assert.StartsWith("markGlitch: label", error, StringComparison.Ordinal);
        Assert.Null(CommandValidation.Check(new Command { Cmd = "getTimeline" }, mixer, _ => null));
    }

    [Fact]
    public void TheTimelineMessageCarriesItsBoundsAndCamelCaseFields()
    {
        var timeline = new EventTimeline(new ManualTime());
        timeline.Record(TimelineKind.Device, "connected Elgato Wave XLR");
        timeline.Mark("UI");
        JsonNode json = JsonNode.Parse(JsonSerializer.Serialize(timeline.Snapshot()))!;
        Assert.Equal("timeline", json["type"]!.GetValue<string>());
        Assert.Equal(EventTimeline.Capacity, json["capacity"]!.GetValue<int>());
        Assert.Equal(600, json["spanSeconds"]!.GetValue<int>());
        Assert.Equal(30, json["windowSeconds"]!.GetValue<int>());
        Assert.Equal("device", json["events"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("connected Elgato Wave XLR", json["markers"]![0]!["before"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("UI", json["markers"]![0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public void TheArchiveListsEachMarkerWithItsWindowAndMasksSerialsAndTheHome()
    {
        const string serial = "AAY3H5481241GS";
        string hex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(serial));
        var time = new ManualTime();
        var timeline = new EventTimeline(time);
        timeline.Record(TimelineKind.Daemon, "OpenXLR daemon 0.7.1 started");
        time.Advance(TimeSpan.FromSeconds(50));
        timeline.Record(TimelineKind.Clock, $"the audio clock rule now names alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_{serial}-00.mono-fallback");
        time.Advance(TimeSpan.FromSeconds(2));
        timeline.Record(TimelineKind.Plugin, "plugin host started for /home/alice/.vst3/Gate.vst3 on OpenXLR_ch_xlr1_stage_0");
        time.Advance(TimeSpan.FromSeconds(1));
        timeline.Record(TimelineKind.Usb, $"block {hex} read back");
        time.Advance(TimeSpan.FromSeconds(1.25));
        timeline.Mark("Deck");
        JsonNode reply = JsonNode.Parse(JsonSerializer.Serialize(timeline.Snapshot()))!;

        string text = Diagnostics.TimelineArchive(reply, time.GetUtcNow(), TimeZoneInfo.Utc,
            line => Diagnostics.Redact(line, ["/home/alice", serial], "alice"), [serial]);
        Assert.DoesNotContain(serial, text, StringComparison.Ordinal);
        Assert.DoesNotContain(hex, text, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", text, StringComparison.Ordinal);
        Assert.Contains("Glitch markers (1)", text, StringComparison.Ordinal);
        Assert.Contains("#1 2026-10-20 12:00:54.250 +00:00, marked from Deck", text, StringComparison.Ordinal);
        // The window, oldest first, with how long before the marker each came.
        string[] lines = text.Split('\n');
        int heading = Array.FindIndex(lines, l => l.StartsWith("#1 ", StringComparison.Ordinal));
        Assert.Equal("     -4.250 s  12:00:50.000  clock    the audio clock rule now names alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_<redacted>-00.mono-fallback",
            lines[heading + 1]);
        Assert.StartsWith("     -2.250 s  12:00:52.000  plugin   plugin host started for <redacted>/.vst3/Gate.vst3", lines[heading + 2], StringComparison.Ordinal);
        Assert.Contains("-1.250 s  12:00:53.000  usb      block 3F3F", lines[heading + 3], StringComparison.Ordinal);
        // The daemon's start was more than 30 seconds before: in the events, not the window.
        Assert.DoesNotContain(lines.Skip(heading).TakeWhile(l => l.Length > 0), l => l.Contains("started", StringComparison.Ordinal) && l.Contains("daemon", StringComparison.Ordinal));
        Assert.Contains("2026-10-20 12:00:00.000 +00:00  daemon   OpenXLR daemon 0.7.1 started", text, StringComparison.Ordinal);
        Assert.Contains("2026-10-20 12:00:54.250 +00:00  marker   >>> the user marked a glitch (Deck)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArchiveWithoutATimelineSaysWhy()
    {
        string text = Diagnostics.TimelineText(null, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc);
        Assert.Contains("No timeline: the daemon did not answer getTimeline", text, StringComparison.Ordinal);
        // A daemon from before the marker answers the query with something else.
        Assert.Contains("No timeline", Diagnostics.TimelineText(new JsonObject { ["type"] = "diagnostics" }, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc),
            StringComparison.Ordinal);
    }
}

/// <summary>
/// The timeline entries the device manager writes, by the units' names and
/// never by their serials or instance ids, which carry a hash of the serial.
/// </summary>
[Collection("xdg-config")]
public sealed class DeviceTimelineTests
{
    private sealed class Unit(string model, ushort productId, string serial, string port) : IAudioDevice
    {
        public DeviceInfo Info { get; } = new("Elgato", model, 0x0fd9, productId) { Location = new(1, 2, port, serial) };
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true };
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() => Connected = false;
        public DeviceState ReadState() => new() { GainDb = 30 };
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    [Fact]
    public void ConnectsSwitchesAndTheBusGoOnTheTimelineByName()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-timeline-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            Unit pro = new("Wave XLR Pro", 0x00b4, "AAY3H5481241GS", "1-2"), mk2 = new("Wave:3 MK.2", 0x00ca, "AD0JH55014KSN3", "1-4");
            IReadOnlyList<IAudioDevice> attached = [pro];
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => attached,
                time: new ManualTime(), preferredModel: 0x00b4, keepsChoice: true);
            manager.SweepOnce();
            attached = [pro, mk2];
            manager.SweepOnce();
            Assert.Null(manager.SetActiveDevice(mk2.Info.InstanceId));
            manager.SweepOnce();
            attached = [mk2];
            manager.SweepOnce();
            IReadOnlyList<TimelineEvent> events = manager.Timeline.Snapshot().Events;
            manager.SuspendSession();

            Assert.Equal([
                "connected Elgato Wave XLR Pro",
                "Elgato Wave:3 MK.2 came onto the USB bus",
                "switching to Elgato Wave:3 MK.2 from Elgato Wave XLR Pro",
                "Elgato Wave XLR Pro is no longer driven",
                "connected Elgato Wave:3 MK.2",
                "Elgato Wave XLR Pro left the USB bus",
            ], events.Where(e => e.Kind == TimelineKind.Device).Select(e => e.Text));
            foreach (TimelineEvent entry in events)
            {
                Assert.DoesNotContain("AAY3H5481241GS", entry.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("AD0JH55014KSN3", entry.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(pro.Info.InstanceId, entry.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(mk2.Info.InstanceId, entry.Text, StringComparison.Ordinal);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>
/// The commands through the daemon's own hub: <c>markGlitch</c> writes one
/// journal line and a marker, <c>getTimeline</c> returns it, a bad label is
/// refused and leaves nothing, and the UI's archive holds the marker.
/// </summary>
[Collection("xdg-config")]
public sealed class GlitchMarkerCommandTests
{
    private sealed class Journal : ILoggerProvider
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public ILogger CreateLogger(string categoryName) => new Writer(this);
        public void Dispose() { }

        private sealed class Writer(Journal journal) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => journal.Lines.Enqueue(formatter(state, exception));
        }
    }

    [Fact]
    public async Task MarkGlitchWritesOneJournalLineAndAMarkerThatGetTimelineReturns()
    {
        var journal = new Journal();
        await using var fixture = new PluginWineTraceFixture(journal);
        ApiCommandResult refused = await fixture.Hub.ExecuteForApiAsync("""{"cmd":"markGlitch","label":""}""");
        Assert.False(refused.Ok);

        ApiCommandResult marked = await fixture.Hub.ExecuteForApiAsync("""{"cmd":"markGlitch","label":"UI"}""");
        Assert.True(marked.Ok, string.Join(", ", marked.Messages));
        Assert.Single(journal.Lines, line => line == "the user marked a glitch (UI)");

        ApiCommandResult read = await fixture.Hub.ExecuteForApiAsync("""{"cmd":"getTimeline"}""");
        Assert.True(read.Ok);
        TimelineMessage timeline = Assert.IsType<TimelineMessage>(Assert.Single(read.Messages));
        GlitchMarker marker = Assert.Single(timeline.Markers);
        Assert.Equal("UI", marker.Label);
        Assert.Single(timeline.Events, e => e.Kind == TimelineKind.Marker && e.Text == "the user marked a glitch (UI)");
    }

    [Fact]
    public async Task TheArchiveHoldsAGlitchTheUiMarked()
    {
        await using var fixture = new PluginWineTraceFixture();
        await using var server = await fixture.StartServerAsync();
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await client.MarkGlitchAsync());

        string archive = await Diagnostics.CollectAsync(client, "nobody-here", fixture.Root);
        try
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
            using var tar = new System.Formats.Tar.TarReader(gzip);
            string? timeline = null;
            while (tar.GetNextEntry() is { } entry)
                if (entry.Name.TrimStart('.', '/') == "timeline.txt" && entry.DataStream is { } data)
                    timeline = new StreamReader(data).ReadToEnd();
            Assert.NotNull(timeline);
            Assert.Contains("Glitch markers (1)", timeline, StringComparison.Ordinal);
            Assert.Contains(", marked from UI", timeline, StringComparison.Ordinal);
            Assert.Contains(">>> the user marked a glitch (UI)", timeline, StringComparison.Ordinal);
        }
        finally { File.Delete(archive); }
    }
}
