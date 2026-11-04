using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class StreamLifecycleTests
{
    // Real helper exchanges with a controlled graph let consecutive sweeps
    // reproduce registry-id reuse without racing the desktop session manager.
    private sealed class Graph : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-streams-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        private readonly PipeWireAdapter _adapter = new();
        public Mixer Mixer { get; }
        public string[] Writes => File.ReadAllLines(Path.Combine(_directory, "writes"));

        public Graph()
        {
            ExecutableScript.Write(Path.Combine(_directory, "pw-dump"), """
                dir=${0%/*}
                /bin/cat "$dir/graph.json"
                """);
            ExecutableScript.Write(Path.Combine(_directory, "pactl"), """
                dir=${0%/*}
                echo "$*" >> "$dir/writes"
                case "$*" in
                    'list sinks short') printf '1\tOpenXLR_ch_system\n2\tOpenXLR_ch_music\n3\tdesktop\n' ;;
                    'list sink-inputs short') /bin/cat "$dir/inputs" ;;
                    'get-default-sink') echo desktop ;;
                    move-sink-input*)
                        if [ -f "$dir/refuse-moves" ]; then exit 1; fi
                        case "$3" in
                            OpenXLR_ch_system) sink=1 ;;
                            OpenXLR_ch_music) sink=2 ;;
                            desktop) sink=3 ;;
                            *) exit 1 ;;
                        esac
                        printf '%s\t%s\n' "$2" "$sink" > "$dir/inputs"
                        ;;
                esac
                """);
            Environment.SetEnvironmentVariable("PATH", _directory);
            Mixer = new(_adapter);
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, true);
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer,
                new MixerConfig { Channels = [new("system", "System"), new("music", "Music")], Mixes = [] });
        }

        public void Set(string binary, int serial = 100, bool playing = true, string clientProps = "")
        {
            string name = JsonSerializer.Serialize(binary);
            string client = $$$$"""
                {"id":20,"type":"PipeWire:Interface:Client","info":{"props":{
                    {{{{clientProps}}}}"application.process.binary":{{{{name}}}},"application.name":{{{{name}}}}
                }}}
                """;
            string stream = $$$$"""
                {"id":10,"type":"PipeWire:Interface:Node","info":{"props":{
                    "client.id":20,"object.serial":{{{{serial}}}},"media.class":"Stream/Output/Audio"
                }}}
                """;
            File.WriteAllText(Path.Combine(_directory, "graph.json"), "[" + client + (playing ? "," + stream : "") + "]");
            File.WriteAllText(Path.Combine(_directory, "inputs"), $"{serial}\t1\n");
            // A normal adapter write retires its short-lived graph cache.
            _adapter.SetSinkMuted("OpenXLR_ch_system", false);
            File.WriteAllText(Path.Combine(_directory, "writes"), "");
        }

        public void RefuseMoves() => File.WriteAllText(Path.Combine(_directory, "refuse-moves"), "");

        public string Folder => _directory;

        /// <summary>Retire the adapter's short-lived graph cache after writing the graph by hand.</summary>
        public void Retire() => _adapter.SetSinkMuted("OpenXLR_ch_system", false);

        public void Dispose()
        {
            try { Mixer.Dispose(); }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", _path);
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("player", 101)]
    [InlineData("other-player", 100)]
    public void ReusedNodeOrChangedIdentityIsRoutedAgain(string identity, int serial)
    {
        using var graph = new Graph();
        graph.Mixer.AssignApp("player", "music");
        graph.Mixer.AssignApp("other-player", "music");
        graph.Set("player");
        Assert.True(graph.Mixer.SyncStreams());
        Assert.False(graph.Mixer.SyncStreams());

        graph.Set(identity, serial);
        Assert.True(graph.Mixer.SyncStreams());

        StreamAssignment stream = Assert.Single(graph.Mixer.Streams);
        Assert.Equal((serial, identity, "music"), (stream.Serial, stream.Identity, stream.ChannelId));
        Assert.Contains($"move-sink-input {serial} OpenXLR_ch_music", graph.Writes);
        Assert.False(graph.Mixer.SyncStreams());
    }

    [Theory]
    [InlineData("Steam", "Steam Voice Settings")]
    [InlineData("Steam Voice Settings", "Steam")]
    public void AnAppWithSeveralClientsSettlesOnOneLabel(string first, string second)
    {
        // Steam registers "Steam" and "Steam Voice Settings" under one
        // identity. Neither order may swap the label on every sweep: each
        // swap was a state change, and a full state went to every client
        // once a second.
        using var graph = new Graph();
        string Client(int id, string name) => $$$$"""
            {"id":{{{{id}}}},"type":"PipeWire:Interface:Client","info":{"props":{
                "application.process.binary":"steam","application.name":{{{{JsonSerializer.Serialize(name)}}}}
            }}}
            """;
        File.WriteAllText(Path.Combine(graph.Folder, "graph.json"), "[" + Client(20, first) + "," + Client(21, second) + "]");
        graph.Retire();

        Assert.True(graph.Mixer.SyncStreams());
        Assert.Equal("Steam", Assert.Single(graph.Mixer.Snapshot().Streams).Label);
        Assert.False(graph.Mixer.SyncStreams());
        Assert.False(graph.Mixer.SyncStreams());
    }

    [Fact]
    public void AnAppCarriesTheIconItsDesktopEntryNamesAndKeepsItOnceGone()
    {
        string data = Directory.CreateTempSubdirectory("openxlr-app-icon-data-").FullName;
        string?[] saved = [Environment.GetEnvironmentVariable("XDG_DATA_HOME"), Environment.GetEnvironmentVariable("XDG_DATA_DIRS")];
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(data, "none"));
            Directory.CreateDirectory(Path.Combine(data, "applications"));
            File.WriteAllText(Path.Combine(data, "applications", "org.example.OpenXlrTestPlayer.desktop"),
                "[Desktop Entry]\nType=Application\nName=Player\nExec=player\nIcon=org.example.OpenXlrTestPlayer\n");
            using var graph = new Graph();
            graph.Set("player", clientProps: "\"pipewire.access.portal.app_id\":\"org.example.OpenXlrTestPlayer\",");
            Assert.True(graph.Mixer.SyncStreams());
            Assert.Equal("org.example.OpenXlrTestPlayer", Assert.Single(graph.Mixer.Snapshot().Streams).Icon);
            Assert.False(graph.Mixer.SyncStreams());

            // A published icon name wins over the entry.
            graph.Set("player", clientProps: "\"application.icon_name\":\"player-published\",");
            Assert.True(graph.Mixer.SyncStreams());
            Assert.Equal("player-published", Assert.Single(graph.Mixer.Snapshot().Streams).Icon);

            // Gone, it keeps the icon it was last seen with.
            File.WriteAllText(Path.Combine(graph.Folder, "graph.json"), "[]");
            graph.Retire();
            Assert.True(graph.Mixer.SyncStreams());
            StreamAssignment gone = Assert.Single(graph.Mixer.Snapshot().Streams);
            Assert.False(gone.Running);
            Assert.Equal("player-published", gone.Icon);

            // A remembered app with no live hints is found by its identity.
            graph.Mixer.AssignApp("org.example.OpenXlrTestPlayer", "music", "Other");
            graph.Mixer.SyncStreams();
            Assert.Equal("org.example.OpenXlrTestPlayer",
                graph.Mixer.Snapshot().Streams.Single(a => a.Identity == "org.example.OpenXlrTestPlayer").Icon);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", saved[0]);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", saved[1]);
            Directory.Delete(data, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedIdentityCasingDoesNotHideLiveApps(bool playing)
    {
        using var graph = new Graph();
        graph.Mixer.AssignApp("PLAYER", "music");
        graph.Set("player", playing: playing);

        graph.Mixer.SyncStreams();

        StreamAssignment app = Assert.Single(graph.Mixer.Snapshot().Streams);
        Assert.Equal(playing, app.Active);
        Assert.True(app.Running);
        Assert.False(graph.Mixer.SyncStreams());
    }

    [Theory]
    [InlineData("music", "OpenXLR_ch_music")]
    [InlineData("ignore", "desktop")]
    public void CaseInsensitiveAssignmentMovesTheLiveStreamImmediately(string channel, string sink)
    {
        using var graph = new Graph();
        graph.Set("player");
        graph.Mixer.SyncStreams();

        graph.Mixer.AssignApp("PLAYER", channel);

        Assert.Contains($"move-sink-input 100 {sink}", graph.Writes);
        Assert.True(graph.Mixer.SyncStreams());
        Assert.Equal(channel, Assert.Single(graph.Mixer.Streams).ChannelId);
    }

    [Fact]
    public void ForgettingAnActiveAppRestoresAutomaticRoutingOnTheNextSweep()
    {
        using var graph = new Graph();
        graph.Mixer.AssignApp("player", "music");
        graph.Set("player");
        graph.Mixer.SyncStreams();

        graph.Mixer.ForgetApp("PLAYER");
        Assert.Empty(graph.Mixer.Snapshot().Streams);
        Assert.True(graph.Mixer.SyncStreams());

        Assert.Contains("move-sink-input 100 OpenXLR_ch_system", graph.Writes);
        Assert.Equal("system", Assert.Single(graph.Mixer.Streams).ChannelId);
        Assert.Equal("system", Assert.Single(graph.Mixer.Snapshot().Streams).ChannelId);
        Assert.Empty(graph.Mixer.Matcher.Overrides);
    }

    [Theory]
    [InlineData("music")]
    [InlineData("ignore")]
    public void UnknownNodeIdCannotAddressAnotherStreamsPulseSerial(string channel)
    {
        using var graph = new Graph();
        graph.Set("player");
        graph.Mixer.SyncStreams();
        Assert.Equal((10, 100), (Assert.Single(graph.Mixer.Streams).Id, Assert.Single(graph.Mixer.Streams).Serial));

        Assert.Throws<InvalidOperationException>(() => graph.Mixer.AssignStream(100, channel));

        Assert.DoesNotContain(graph.Writes, command => command.StartsWith("move-sink-input", StringComparison.Ordinal));
        Assert.Empty(graph.Mixer.Matcher.Overrides);
        Assert.Equal("system", Assert.Single(graph.Mixer.Streams).ChannelId);
    }

    [Theory]
    [InlineData("music")]
    [InlineData("ignore")]
    public void RefusedStreamMoveDoesNotRememberAnUnappliedAssignment(string channel)
    {
        using var graph = new Graph();
        graph.Set("player");
        graph.Mixer.SyncStreams();
        graph.RefuseMoves();

        Assert.Throws<InvalidOperationException>(() => graph.Mixer.AssignStream(10, channel));

        Assert.Empty(graph.Mixer.Matcher.Overrides);
        Assert.Equal("system", Assert.Single(graph.Mixer.Streams).ChannelId);
    }

    /// <summary>
    /// Reduced from a live desk: Discord registers a client named after its
    /// headset mute watcher beside its voice engine, Firefox plays one stream
    /// and Chrome two. Each application is one entry under its own name.
    /// </summary>
    private const string ApplicationsGraph = """
        [
          {"id":30,"type":"PipeWire:Interface:Client","info":{"props":{
            "application.name":"discord_hardware_mute","application.process.binary":"Discord","application.process.id":561674}}},
          {"id":31,"type":"PipeWire:Interface:Client","info":{"props":{
            "application.name":"WEBRTC VoiceEngine","application.process.binary":"Discord","application.process.id":561674}}},
          {"id":32,"type":"PipeWire:Interface:Client","info":{"props":{
            "application.name":"Firefox","application.process.binary":"firefox"}}},
          {"id":33,"type":"PipeWire:Interface:Client","info":{"props":{
            "application.name":"Google Chrome","application.process.binary":"chrome"}}},
          {"id":40,"type":"PipeWire:Interface:Node","info":{"props":{
            "client.id":30,"object.serial":140,"media.class":"Stream/Output/Audio","media.name":"playback"}}},
          {"id":41,"type":"PipeWire:Interface:Node","info":{"props":{
            "client.id":32,"object.serial":141,"media.class":"Stream/Output/Audio","media.name":"AudioStream"}}},
          {"id":42,"type":"PipeWire:Interface:Node","info":{"props":{
            "client.id":33,"object.serial":142,"media.class":"Stream/Output/Audio","media.name":"Playback"}}},
          {"id":43,"type":"PipeWire:Interface:Node","info":{"props":{
            "client.id":33,"object.serial":143,"media.class":"Stream/Output/Audio","media.name":"Playback"}}}
        ]
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EachApplicationIsOneEntryUnderItsOwnName(bool playing)
    {
        using var graph = new Graph();
        string json = ApplicationsGraph;
        if (!playing) json = "[" + string.Join(",", JsonDocument.Parse(json).RootElement.EnumerateArray()
            .Where(o => o.GetProperty("type").GetString()!.EndsWith("Client", StringComparison.Ordinal)).Select(o => o.GetRawText())) + "]";
        File.WriteAllText(Path.Combine(graph.Folder, "graph.json"), json);
        File.WriteAllText(Path.Combine(graph.Folder, "inputs"), "140\t3\n141\t3\n142\t3\n143\t3\n");
        graph.Retire();
        graph.Mixer.SyncStreams();

        StreamAssignment[] apps = [.. graph.Mixer.Snapshot().Streams.OrderBy(a => a.Identity, StringComparer.Ordinal)];
        Assert.Equal(["Discord", "chrome", "firefox"], apps.Select(a => a.Identity));
        Assert.Equal(["Discord", "Google Chrome", "Firefox"], apps.Select(a => a.Label));
        Assert.All(apps, app => Assert.Equal(playing, app.Active));
        Assert.All(apps, app => Assert.True(app.Running));
        if (playing)
        {
            // Chrome's two streams and Discord's mute watcher are each routed, under their application's entry.
            Assert.Equal([140, 141, 142, 143], graph.Mixer.Streams.Select(s => s.Serial).Order());
            Assert.Equal(["Discord", "Firefox", "Google Chrome", "Google Chrome"], graph.Mixer.Streams.Select(s => s.Label).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void ALabelSavedFromAHelperStreamReadsAsTheApplication()
    {
        using var graph = new Graph();
        graph.Mixer.ApplySettings(new MixerSettings
        {
            KnownApps = [new("Discord", "discord_hardware_mute", "system"), new("firefox", "Firefox", "music")],
        });
        StreamAssignment[] apps = [.. graph.Mixer.Snapshot().Streams.OrderBy(a => a.Identity, StringComparer.Ordinal)];
        Assert.Equal(["Discord", "Firefox"], apps.Select(a => a.Label));
        Assert.All(apps, app => Assert.False(app.Running));
    }

    [Fact]
    public void AnAlsaClientKeepsItsSavedChannelUnderItsProgramsName()
    {
        // Saved before ALSA clients were named after their program: the
        // entry's label is pipewire-alsa's wrapper name.
        using var graph = new Graph();
        graph.Mixer.ApplySettings(new MixerSettings
        {
            AppOverrides = new() { ["kded6"] = "music" },
            KnownApps = [new("kded6", "PipeWire ALSA [kded6]", "music")],
        });
        Assert.Equal("KDE", Assert.Single(graph.Mixer.Snapshot().Streams).Label);

        File.WriteAllText(Path.Combine(graph.Folder, "graph.json"), """
            [
              {"id":20,"type":"PipeWire:Interface:Client","info":{"props":{
                "application.name":"PipeWire ALSA [kded6]","application.process.binary":"kded6"}}},
              {"id":10,"type":"PipeWire:Interface:Node","info":{"props":{
                "application.name":"PipeWire ALSA [kded6]","client.id":20,"object.serial":100,
                "media.class":"Stream/Output/Audio","media.name":"ALSA Playback"}}}
            ]
            """);
        File.WriteAllText(Path.Combine(graph.Folder, "inputs"), "100\t3\n");
        graph.Retire();
        File.WriteAllText(Path.Combine(graph.Folder, "writes"), "");
        Assert.True(graph.Mixer.SyncStreams());

        Assert.Contains("move-sink-input 100 OpenXLR_ch_music", graph.Writes);
        StreamAssignment app = Assert.Single(graph.Mixer.Snapshot().Streams);
        Assert.Equal(("kded6", "KDE", "music", true), (app.Identity, app.Label, app.ChannelId, app.Active));
        Assert.False(graph.Mixer.SyncStreams());
    }
}
