using System.Text;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class AppIdentityTests
{
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
}
