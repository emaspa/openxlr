using System.Text;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class AppIdentityTests
{
    [Theory]
    [InlineData("music")]
    [InlineData("ignore")]
    public void AFullRegistryStillAllowsExistingAppsToChange(string channel)
    {
        using var mixer = new Mixer();
        mixer.Matcher.SetOverride("balatro", "game");
        for (int i = 1; i < CommandValidation.MaxOverrides; i++) mixer.Matcher.SetOverride($"app{i}", "system");
        var command = new Command { Cmd = "assignApp", Identity = "BALATRO.exe", Channel = channel };
        Assert.Null(CommandValidation.Check(command, mixer, _ => null));
        mixer.AssignApp(command.Identity, channel);
        Assert.Equal(channel, mixer.Matcher.Overrides["balatro"]);
        Assert.Equal(CommandValidation.MaxOverrides, mixer.OverrideCount);
        Assert.Throws<InvalidOperationException>(() => mixer.AssignApp("new-app", channel));
        Assert.False(mixer.Matcher.Overrides.ContainsKey("new-app"));
        Assert.DoesNotContain(mixer.ExportSettings().KnownApps, app => app.Identity == "new-app");
    }

    [Fact]
    public void ConcurrentAssignmentsCannotOverfillTheRegistry()
    {
        using var mixer = new Mixer();
        for (int i = 1; i < CommandValidation.MaxOverrides; i++) mixer.Matcher.SetOverride($"app{i}", "system");
        int accepted = 0;
        Parallel.For(0, 32, i =>
        {
            try { mixer.AssignApp($"new{i}", "music"); Interlocked.Increment(ref accepted); }
            catch (InvalidOperationException) { }
        });
        Assert.Equal(1, accepted);
        Assert.Equal(CommandValidation.MaxOverrides, mixer.OverrideCount);
    }

    [Fact]
    public void LegacyAssignmentsAboveTheLimitRemainEditable()
    {
        using var mixer = new Mixer();
        for (int i = 0; i <= CommandValidation.MaxOverrides; i++) mixer.Matcher.SetOverride($"app{i}", "system");
        Assert.Null(CommandValidation.Check(new Command { Cmd = "assignApp", Identity = "APP0", Channel = "music" }, mixer, _ => null));
        mixer.AssignApp("APP0", "music");
        Assert.Equal("music", mixer.Matcher.Overrides["app0"]);
        Assert.Equal(CommandValidation.MaxOverrides + 1, mixer.OverrideCount);
    }

    // Reduced from the live Balatro graph: the process binary belongs to
    // the client, while the playback node only carries the Windows app name.
    private static byte[] BalatroGraph => Encoding.UTF8.GetBytes("""
        [
          {"id":649,"type":"PipeWire:Interface:Node","info":{"props":{
            "client.id":650,"application.name":"Balatro.exe",
            "media.class":"Stream/Output/Audio","media.name":"Balatro.exe",
            "object.serial":43258
          }}},
          {"id":650,"type":"PipeWire:Interface:Client","info":{"props":{
            "application.name":"Balatro.exe","application.process.binary":"wine64-preloader"
          }}}
        ]
        """);

    [Fact]
    public void OneApplicationSnapshotKeepsClientFallbackAndLaterUpdates()
    {
        byte[] json = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(BalatroGraph) + """
            [{"id":650,"type":"PipeWire:Interface:Client","info":{"props":{
                "application.name":"Player","application.process.binary":"mpv"
            }}}]
            """);
        var (streams, clients) = PipeWireAdapter.ListApplications(json);

        AudioStream stream = Assert.Single(streams);
        AudioStream client = Assert.Single(clients);
        Assert.Equal("mpv", stream.Identity);
        Assert.Equal("mpv", client.Identity);
        Assert.Equal("Balatro.exe", stream.AppName); // explicit node name wins over the client
        Assert.Equal("Player", client.AppName);
        Assert.Equal(43258, stream.Serial);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[] {}")]
    [InlineData("[{\"id\":1")]
    public void InvalidGraphCannotProduceHalfAnApplicationSnapshot(string json)
    {
        var (streams, clients) = PipeWireAdapter.ListApplications(Encoding.UTF8.GetBytes(json));
        Assert.Empty(streams);
        Assert.Empty(clients);
    }

    [Fact]
    public void PlaybackAndClientShareOneIdentityAndSavedRoute()
    {
        AudioStream client = Assert.Single(PipeWireAdapter.ListClients(BalatroGraph));
        AudioStream stream = Assert.Single(PipeWireAdapter.ListStreams(BalatroGraph));
        var matcher = new StreamMatcher();
        matcher.SetOverride(client.Identity, "music");

        Assert.Equal("balatro", client.Identity);
        Assert.Equal(client.Identity, stream.Identity);
        Assert.Equal("music", matcher.Match(stream));
        Assert.Equal("game", new StreamMatcher().Match(stream));
        Assert.Equal(43258, stream.Serial);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Balatro.exe")]
    [InlineData("wine64-preloader")]
    public void WindowsIdentityMatchesItsPersistedKey(string? binary)
    {
        var stream = new AudioStream(649, "Balatro.exe", binary, "playback");
        Assert.Equal("balatro", stream.Identity);
        Assert.Equal(stream.Identity, StreamMatcher.MigrateIdentity(stream.Identity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalOverrideWinsOverStaleAliasRegardlessOfFileOrder(bool reverse)
    {
        KeyValuePair<string, string>[] entries =
        [new("balatro", "game"), new("Balatro.exe", "system")];
        var saved = (reverse ? entries.Reverse() : entries).ToDictionary(pair => pair.Key, pair => pair.Value);
        IReadOnlyDictionary<string, string> migrated = StreamMatcher.MigrateOverrides(saved);

        Assert.Equal("game", Assert.Single(migrated).Value);
        Assert.Equal("balatro", Assert.Single(migrated).Key);
        var matcher = new StreamMatcher();
        foreach ((string identity, string channel) in migrated) matcher.SetOverride(identity, channel);
        Assert.Equal("game", matcher.Match(Assert.Single(PipeWireAdapter.ListStreams(BalatroGraph))));
    }

    [Fact]
    public void LegacyOnlyOverridesAndNotManagedChoicesSurviveMigration()
    {
        var saved = new Dictionary<string, string>
        {
            ["Balatro.exe"] = StreamMatcher.Ignore,
            ["spotify (deleted)"] = "music",
        };
        var migrated = StreamMatcher.MigrateOverrides(saved);
        Assert.Equal(StreamMatcher.Ignore, migrated["balatro"]);
        Assert.Equal("music", migrated["spotify"]);
        Assert.Equal(2, migrated.Count);
    }

    [Fact]
    public void ExplicitNodeMetadataWinsOverClientMetadata()
    {
        byte[] json = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(BalatroGraph)
            .Replace("\"client.id\":650,", "\"client.id\":650,\"application.process.binary\":\"spotify\","));
        AudioStream stream = Assert.Single(PipeWireAdapter.ListStreams(json));
        Assert.Equal("spotify", stream.Identity);
        Assert.Equal("music", new StreamMatcher().Match(stream));
    }

    [Fact]
    public void MissingClientStillUsesTheWindowsGameKey()
    {
        byte[] json = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(BalatroGraph).Replace("\"client.id\":650", "\"client.id\":999"));
        AudioStream stream = Assert.Single(PipeWireAdapter.ListStreams(json));
        Assert.Equal("balatro", stream.Identity);
        Assert.Equal("game", new StreamMatcher().Match(stream));
    }

    [Fact]
    public void SharedAppLabelsDoNotMergeDifferentNativeBinaries()
    {
        byte[] json = Encoding.UTF8.GetBytes("""
            [
              {"id":1,"type":"PipeWire:Interface:Client","info":{"props":{
                "application.name":"Chromium","application.process.binary":"vesktop (deleted)"
              }}},
              {"id":2,"type":"PipeWire:Interface:Client","info":{"props":{
                "application.name":"Chromium","application.process.binary":"chromium"
              }}},
              {"id":3,"type":"PipeWire:Interface:Node","info":{"props":{
                "client.id":1,"media.class":"Stream/Output/Audio"
              }}},
              {"id":4,"type":"PipeWire:Interface:Node","info":{"props":{
                "client.id":2,"media.class":"Stream/Output/Audio"
              }}}
            ]
            """);
        IReadOnlyList<AudioStream> streams = PipeWireAdapter.ListStreams(json);
        Assert.Equal(["vesktop", "chromium"], streams.Select(stream => stream.Identity));
        Assert.Equal(["voicechat", "browser"], streams.Select(new StreamMatcher().Match));
        Assert.Equal("Vesktop", streams[0].Label);
    }

    [Fact]
    public void WindowsGamesRemainDistinctUnderTheSameRuntime()
    {
        Assert.NotEqual(new AudioStream(1, "Balatro.exe", "wine64-preloader", null).Identity,
            new AudioStream(2, "Other Game.exe", "wine64-preloader", null).Identity);
    }

    [Fact]
    public void AssigningAndForgettingAnExecutableAliasUseTheCanonicalApp()
    {
        using var mixer = new Mixer();
        mixer.AssignApp("balatro", "game", "Balatro");
        mixer.AssignApp("Balatro.exe", "music", "Balatro");

        MixerSettings settings = mixer.ExportSettings();
        Assert.Equal("balatro", Assert.Single(settings.KnownApps).Identity);
        Assert.Equal("music", settings.AppOverrides["balatro"]);
        Assert.Single(settings.AppOverrides);

        mixer.ForgetApp("Balatro.exe");
        Assert.Empty(mixer.ExportSettings().KnownApps);
        Assert.Empty(mixer.Matcher.Overrides);
    }

    /// <summary>
    /// Every WebKitGTK application plays from the same web process binary,
    /// so the application name tells them apart: whatRust and OpenDeck keep
    /// their own channels, and a WhatsApp client lands in Voice Chat.
    /// </summary>
    [Fact]
    public void WebKitApplicationsAreToldApartByName()
    {
        var whatrust = new AudioStream(1, "whatrust", "WebKitWebProcess", "Playback Stream");
        var opendeck = new AudioStream(2, "opendeck", "WebKitWebProcess", "Playback Stream");
        var unnamed = new AudioStream(3, null, "WebKitWebProcess", "Playback Stream");
        Assert.Equal("whatrust", whatrust.Identity);
        Assert.Equal("opendeck", opendeck.Identity);
        Assert.Equal("WebKitWebProcess", unnamed.Identity);
        Assert.Equal("WPEWebProcess", new AudioStream(4, "WPEWebProcess", "WPEWebProcess", null).Identity);
        var matcher = new StreamMatcher();
        Assert.Equal("voicechat", matcher.Match(whatrust));
        matcher.SetOverride(whatrust.Identity, "music");
        Assert.Equal("music", matcher.Match(whatrust));
        Assert.Equal("system", matcher.Match(opendeck));
        Assert.Null(ApplicationIconHints.From(whatrust).Binary);
    }

    /// <summary>
    /// The name an entry shows. A runtime's name or an identifier one word
    /// long with underscores gives way to the application's own binary; a
    /// real name, a Windows executable's and anything without a binary of its
    /// own stay as they are.
    /// </summary>
    [Theory]
    [InlineData("discord_hardware_mute", "Discord", "Discord")]
    [InlineData("WEBRTC VoiceEngine", "Discord", "Discord")]
    [InlineData("Chromium", "Discord", "Discord")]
    [InlineData("Firefox", "firefox", "Firefox")]
    [InlineData("Google Chrome", "chrome", "Google Chrome")]
    [InlineData("Snake_Game.exe", "wine64-preloader", "Snake_Game")]
    [InlineData("snake_game", "wine64-preloader", "snake_game")]
    [InlineData("some_tool", null, "some_tool")]
    [InlineData("Two words_with underscore", "player", "Two words_with underscore")]
    public void AnEntryShowsTheApplicationsNameNotAStreamIdentifier(string appName, string? binary, string label)
    {
        Assert.Equal(label, new AudioStream(1, appName, binary, null).Label);
    }

    /// <summary>
    /// The program behind an ALSA plugin's wrapper name. Anything that is
    /// not a complete wrapper around a name stays as it was.
    /// </summary>
    [Theory]
    [InlineData("PipeWire ALSA [kded6]", "kded6")]
    [InlineData("  PipeWire ALSA [kded6]  ", "kded6")]
    [InlineData("PipeWire ALSA[kded6]", "kded6")]
    [InlineData("PipeWire ALSA [ speaker-test ]", "speaker-test")]
    [InlineData("PipeWire ALSA [foo [bar]]", "foo [bar]")]
    [InlineData("ALSA plug-in [aplay]", "aplay")]
    [InlineData("PipeWire ALSA []", "PipeWire ALSA []")]
    [InlineData("PipeWire ALSA [  ]", "PipeWire ALSA [  ]")]
    [InlineData("PipeWire ALSA [kded6", "PipeWire ALSA [kded6")]
    [InlineData("PipeWire ALSA [kded6] extra", "PipeWire ALSA [kded6] extra")]
    [InlineData("PipeWire ALSA", "PipeWire ALSA")]
    [InlineData("PipeWire ALSA kded6", "PipeWire ALSA kded6")]
    [InlineData("pipewire alsa [kded6]", "pipewire alsa [kded6]")]
    [InlineData("Firefox", "Firefox")]
    [InlineData("", "")]
    public void AnAlsaClientIsNamedAfterTheProgramInside(string name, string program)
    {
        Assert.Equal(program, AudioStream.ProgramName(name));
        Assert.Null(AudioStream.ProgramName(null));
    }

    // Reduced from the live desk: kded6 plays through pipewire-alsa. The
    // node carries only the wrapper name, the client the process binary.
    private static byte[] AlsaGraph(bool clientKnown = true) => Encoding.UTF8.GetBytes($$$$"""
        [
          {"id":629,"type":"PipeWire:Interface:Client","info":{"props":{
            "application.name":"PipeWire ALSA [kded6]","application.process.binary":"kded6",
            "media.name":"ALSA Playback","node.name":"alsa_playback.kded6"
          }}},
          {"id":630,"type":"PipeWire:Interface:Client","info":{"props":{
            "application.name":"PipeWire ALSA [recorder]","application.process.binary":"recorder",
            "media.name":"ALSA Capture","node.name":"alsa_capture.recorder"
          }}},
          {"id":791,"type":"PipeWire:Interface:Node","info":{"props":{
            "application.name":"PipeWire ALSA [kded6]","client.id":{{{{(clientKnown ? 629 : 999)}}}},
            "media.class":"Stream/Output/Audio","media.name":"ALSA Playback",
            "node.name":"alsa_playback.kded6","object.serial":5001
          }}}
        ]
        """);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnAlsaClientReadsAsItsProgramUnderOneIdentity(bool clientKnown)
    {
        var (streams, clients) = PipeWireAdapter.ListApplications(AlsaGraph(clientKnown));
        AudioStream stream = Assert.Single(streams);
        // KDE's service daemon reads as KDE; its identity stays the binary, so rules keep matching.
        Assert.Equal(("kded6", "kded6", "KDE"), (stream.AppName, stream.Identity, stream.Label));
        // kded6 is a desktop service, left out of the running list; the
        // recording program behind the capture side is listed by its name.
        AudioStream recorder = Assert.Single(clients);
        Assert.Equal(("recorder", "recorder"), (recorder.Identity, recorder.Label));
    }

    [Fact]
    public void ASavedAssignmentForAnAlsaClientStillApplies()
    {
        AudioStream stream = Assert.Single(PipeWireAdapter.ListStreams(AlsaGraph()));
        AudioStream bare = Assert.Single(PipeWireAdapter.ListStreams(AlsaGraph(clientKnown: false)));
        // Keyed by the process binary, as it always was.
        var matcher = new StreamMatcher();
        matcher.SetOverride("kded6", "music");
        Assert.Equal("music", matcher.Match(stream));
        Assert.Equal("music", matcher.Match(bare));
        // Keyed by the wrapper name, as a client without its binary was.
        var migrated = StreamMatcher.MigrateOverrides(new Dictionary<string, string> { ["PipeWire ALSA [kded6]"] = "music" });
        Assert.Equal("kded6", Assert.Single(migrated).Key);
        var legacy = new StreamMatcher();
        foreach ((string identity, string channel) in migrated) legacy.SetOverride(identity, channel);
        Assert.Equal("music", legacy.Match(stream));
        Assert.Equal("music", legacy.Match(bare));
    }

    [Fact]
    public void EveryClientOfDiscordShowsOneNameUnderOneIdentity()
    {
        byte[] json = Encoding.UTF8.GetBytes("""
            [
              {"id":1,"type":"PipeWire:Interface:Client","info":{"props":{
                "application.name":"discord_hardware_mute","application.process.binary":"Discord"}}},
              {"id":2,"type":"PipeWire:Interface:Client","info":{"props":{
                "application.name":"WEBRTC VoiceEngine","application.process.binary":"Discord"}}},
              {"id":3,"type":"PipeWire:Interface:Client","info":{"props":{
                "application.name":"Chromium input","application.process.binary":"Discord"}}},
              {"id":4,"type":"PipeWire:Interface:Node","info":{"props":{
                "client.id":1,"media.class":"Stream/Output/Audio","object.serial":7}}}
            ]
            """);
        var (streams, clients) = PipeWireAdapter.ListApplications(json);
        Assert.Equal(["Discord"], streams.Concat(clients).Select(s => s.Identity).Distinct());
        Assert.Equal(["Discord"], streams.Concat(clients).Select(s => s.Label).Distinct());
        // The name is for showing; the stream still matches as Discord does.
        Assert.Equal("voicechat", new StreamMatcher().Match(Assert.Single(streams)));
    }
}
