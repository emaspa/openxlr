using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class BrowserTabRoutingTests
{
    private static BrowserTabRegistration Tab(int id, string site = "https://teams.microsoft.com", string title = "Call")
        => new() { Id = id, Site = site, Title = title };

    [Fact]
    public void IdenticalRegistrationsDoNotBroadcastState()
    {
        var config = new ConfigurationBuilder().Build();
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
        using var service = new MixerService(NullLogger<MixerService>.Instance, config, devices, time: new ManualTime());
        Guid owner = Guid.NewGuid();
        string session = service.OpenBrowserSession(owner);
        var command = new Command { Cmd = "syncBrowserTabs", Session = session, Tabs = [Tab(1), Tab(2)] };
        int changes = 0;
        service.Changed += () => changes++;
        Assert.Null(service.SyncBrowserTabs(owner, command));
        Assert.Equal(1, changes);
        Assert.Null(service.SyncBrowserTabs(owner, new Command
        {
            Cmd = "syncBrowserTabs", Session = session, Tabs = [Tab(2), Tab(1, "https://TEAMS.microsoft.com:443/")],
        }));
        Assert.Equal(1, changes);
        Assert.Null(service.SyncBrowserTabs(owner, command));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void TitleChurnPublishesTheLatestValueOncePerSecondAndRoutingChangesAreImmediate()
    {
        var config = new ConfigurationBuilder().Build();
        var time = new ManualTime();
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
        using var service = new MixerService(NullLogger<MixerService>.Instance, config, devices, time: time);
        Mixer mixer = (Mixer)typeof(MixerService).GetField("_mixer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        Guid owner = Guid.NewGuid();
        string session = service.OpenBrowserSession(owner);
        int changes = 0;
        service.Changed += () => changes++;
        void Sync(params BrowserTabRegistration[] tabs) => Assert.Null(service.SyncBrowserTabs(owner,
            new Command { Cmd = "syncBrowserTabs", Session = session, Tabs = [.. tabs] }));
        Sync(Tab(1) with { Capturing = true }, Tab(2));
        for (int n = 1; n <= 9; n++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            Sync(Tab(2, title: $"Private {n}"), Tab(1, title: $"Call {n}") with { Capturing = true });
            Assert.Equal(1, changes);
            Assert.Equal("Call", mixer.BrowserTabs.Single(t => t.Id == 1).Title);
        }
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, changes);
        Assert.Equal("Call 9", mixer.BrowserTabs.Single(t => t.Id == 1).Title);
        Assert.Equal("", mixer.BrowserTabs.Single(t => t.Id == 2).Title);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, changes);
        Sync(Tab(1, title: "Call 9") with { Capturing = true }, Tab(2, title: "Only a private title changed"));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, changes);
        Sync(Tab(1, title: "Call 10") with { Capturing = true }, Tab(2));
        Sync(Tab(1, title: "Call 11") with { Error = "Capture stopped" }, Tab(2));
        Assert.Equal(3, changes);
        Assert.False(mixer.BrowserTabs.Single(t => t.Id == 1).Capturing);
        Sync(Tab(1, "https://youtube.com", "Video") with { Capturing = true });
        Assert.Equal(4, changes);
        Assert.Equal("Video", Assert.Single(mixer.BrowserTabs).Title);
        Sync(Tab(1, "https://youtube.com", "Pending") with { Capturing = true });
        service.RemoveBrowserSession(owner);
        Assert.Equal(5, changes);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(5, changes);
        Assert.Empty(mixer.BrowserTabs);
    }

    [Fact]
    public async Task ATitleFlushSkipsATickWhileABuildHoldsTheMixerInsteadOfWaiting()
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        mixer.SyncBrowserTabs(owner, session, [Tab(1) with { Capturing = true }]);
        mixer.SyncBrowserTabs(owner, session, [Tab(1, title: "Call 2") with { Capturing = true }]);
        object gate = typeof(Mixer).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var build = new Thread(() => { lock (gate) { held.Set(); release.Wait(); } });
        build.Start();
        held.Wait();
        try
        {
            Task<bool> flush = Task.Run(mixer.FlushBrowserTabTitles);
            Assert.Same(flush, await Task.WhenAny(flush, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.False(await flush);
        }
        finally { release.Set(); build.Join(); }
        Assert.Equal("Call", Assert.Single(mixer.BrowserTabs).Title);
        Assert.True(mixer.FlushBrowserTabTitles());
        Assert.Equal("Call 2", Assert.Single(mixer.BrowserTabs).Title);
    }

    [Fact]
    public void OnlyCapturingOrAssignedTabsShareTheirTitles()
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2) with { Capturing = true }, Tab(3)]);
        mixer.AssignBrowserTab(session, 3, "https://teams.microsoft.com", "music", false, _ => null);
        Assert.Equal("", mixer.Snapshot().BrowserTabs.Single(t => t.Id == 1).Title);
        Assert.Equal("Call", mixer.Snapshot().BrowserTabs.Single(t => t.Id == 2).Title);
        Assert.Equal("Call", mixer.Snapshot().BrowserTabs.Single(t => t.Id == 3).Title);
        mixer.AssignBrowserTab(session, 3, "https://teams.microsoft.com", null, false, _ => null);
        Assert.Equal("", mixer.Snapshot().BrowserTabs.Single(t => t.Id == 3).Title);
    }

    [Fact]
    public void ActivationIsCheckedAgainstTheTabAndTheBrowserReportsItsStage()
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2, "https://youtube.com") with { Capturing = true }]);
        string Refusal(int id, string site = "https://teams.microsoft.com", string? other = null)
            => Assert.Throws<InvalidOperationException>(() => mixer.CheckBrowserActivation(other ?? session, id, site)).Message;
        Assert.Contains("enable individual browser tab routing", Refusal(1));
        mixer.SetPreserveBrowserOutputs(true);
        Assert.Contains("choose a channel", Refusal(1));
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", false, _ => null);
        mixer.AssignBrowserTab(session, 2, "https://youtube.com", "music", false, _ => null);
        mixer.CheckBrowserActivation(session, 1, "https://TEAMS.microsoft.com:443/");
        Assert.Contains("changed website", Refusal(1, "https://youtube.com"));
        Assert.Contains("already routed", Refusal(2, "https://youtube.com"));
        Assert.Contains("no longer connected", Refusal(3));
        Assert.Contains("no longer connected", Refusal(1, other: Guid.NewGuid().ToString("N")));

        // The browser reports the stage in its tab list; the choice survives it.
        mixer.SyncBrowserTabs(owner, session, [Tab(1) with { Activation = "popup" }, Tab(2, "https://youtube.com") with { Capturing = true }]);
        BrowserTabState waiting = mixer.BrowserTabs.Single(t => t.Id == 1);
        Assert.Equal(("popup", "voicechat"), (waiting.Activation, waiting.ChannelId));
        Assert.Contains("already activating", Refusal(1));
        mixer.SyncBrowserTabs(owner, session, [Tab(1) with { Capturing = true }, Tab(2, "https://youtube.com") with { Capturing = true }]);
        Assert.Null(mixer.BrowserTabs.Single(t => t.Id == 1).Activation);
        Assert.Contains("already routed", Refusal(1));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("starting", true)]
    [InlineData("popup", true)]
    [InlineData("browser", true)]
    [InlineData("capturing", false)]
    [InlineData("", false)]
    public void OnlyKnownActivationStagesRegister(string? activation, bool valid)
    {
        string? problem = BrowserTabRouting.CheckTabs([Tab(1) with { Activation = activation }]);
        if (valid) Assert.Null(problem);
        else Assert.Equal("syncBrowserTabs: activation must be starting, popup, browser or null", problem);
    }

    [Fact]
    public void ProvidersOwnTheirSessionsAndDisconnectDropsOnlyTheirTabs()
    {
        using var mixer = new Mixer();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(first);
        Assert.Equal(first.ToString("N"), session);
        Assert.Equal(session, mixer.OpenBrowserSession(first));
        string other = mixer.OpenBrowserSession(second);
        mixer.SyncBrowserTabs(first, session, [Tab(1)]);
        mixer.SyncBrowserTabs(second, other, [Tab(1)]);
        Assert.Throws<InvalidOperationException>(() => mixer.SyncBrowserTabs(second, session, [Tab(2)]));
        Assert.Equal(2, mixer.BrowserTabs.Count);
        Assert.True(mixer.RemoveBrowserSession(first));
        Assert.Equal(other, Assert.Single(mixer.BrowserTabs).Session);
        Assert.Throws<InvalidOperationException>(() => mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "music", false, _ => null));
    }

    [Fact]
    public void TabChoicesSurviveMetadataUpdatesAndOnlyRememberedOriginsEnterSettings()
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2, "https://youtube.com", "Video")]);
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", false, _ => throw new InvalidOperationException("Tab-only choices must not save."));
        mixer.SyncBrowserTabs(owner, session, [Tab(1, title: "New call") with { Capturing = true }, Tab(2, "https://youtube.com", "Video")]);
        Assert.Equal("voicechat", mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        Assert.Empty(mixer.ExportSettings().BrowserSiteChannels);
        MixerSettings? saved = null;
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", true, settings => { saved = settings; return null; });
        Assert.Equal("voicechat", saved!.BrowserSiteChannels["https://teams.microsoft.com"]);
        string json = JsonSerializer.Serialize(saved);
        Assert.DoesNotContain(session, json);
        Assert.DoesNotContain("New call", json);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(3)]);
        Assert.All(mixer.BrowserTabs, tab => { Assert.Equal("voicechat", tab.ChannelId); Assert.True(tab.Remember); });
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", null, false, _ => null);
        Assert.Null(mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        Assert.False(mixer.BrowserTabs.Single(t => t.Id == 1).Remember);
        Assert.DoesNotContain("BrowserSiteChannels", JsonSerializer.Serialize(mixer.ExportScene()));
        Assert.DoesNotContain("PreserveBrowserOutputs", JsonSerializer.Serialize(mixer.ExportScene()));
    }

    [Fact]
    public void NavigatingToAnotherOriginDropsOnlyTheTabsTemporaryChoice()
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2, "https://youtube.com")]);
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", false, _ => null);
        mixer.AssignBrowserTab(session, 2, "https://youtube.com", "music", true, _ => null);
        mixer.SyncBrowserTabs(owner, session, [Tab(1, "https://TEAMS.microsoft.com:443/", "New call"), Tab(2, "https://youtube.com")]);
        Assert.Equal("voicechat", mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        mixer.SyncBrowserTabs(owner, session, [Tab(1, "https://youtube.com", "Video"), Tab(2, "https://youtube.com")]);
        BrowserTabState navigated = mixer.BrowserTabs.Single(t => t.Id == 1);
        Assert.Equal("music", navigated.ChannelId);
        Assert.True(navigated.Remember);
        mixer.SyncBrowserTabs(owner, session, [Tab(1, "https://unassigned.example")]);
        Assert.Null(Assert.Single(mixer.BrowserTabs).ChannelId);
        Assert.False(Assert.Single(mixer.BrowserTabs).Remember);
        Assert.Equal("music", mixer.ExportSettings().BrowserSiteChannels["https://youtube.com"]);
    }

    [Theory]
    [InlineData("game", true)]
    [InlineData(null, true)]
    [InlineData("game", false)]
    public void AnAssignmentQueuedBeforeNavigationCannotChangeTheNewWebsite(string? channel, bool remember)
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2, "https://youtube.com")]);
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "music", true, _ => null);
        mixer.AssignBrowserTab(session, 2, "https://youtube.com", "voicechat", true, _ => null);
        mixer.SyncBrowserTabs(owner, session, [Tab(1, "https://youtube.com"), Tab(2, "https://youtube.com")]);
        BrowserTabState[] previousTabs = [.. mixer.BrowserTabs];
        var previousSites = mixer.ExportSettings().BrowserSiteChannels;
        int saves = 0;
        Assert.Throws<InvalidOperationException>(() => mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", channel, remember,
            _ => { saves++; return null; }));
        Assert.Equal(0, saves);
        Assert.Equal(previousTabs, mixer.BrowserTabs);
        Assert.Equal(previousSites, mixer.ExportSettings().BrowserSiteChannels);
        mixer.AssignBrowserTab(session, 1, "https://YOUTUBE.com:443/", "game", false, _ => { saves++; return null; });
        Assert.Equal("game", mixer.BrowserTabs.Single(tab => tab.Id == 1).ChannelId);
        Assert.Equal("voicechat", mixer.BrowserTabs.Single(tab => tab.Id == 2).ChannelId);
        Assert.Equal(0, saves);
        Assert.Equal(previousSites, mixer.ExportSettings().BrowserSiteChannels);
    }

    [Fact]
    public void RefusedPreferenceSaveKeepsThePreviousChoice()
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        mixer.SyncBrowserTabs(owner, session, [Tab(1)]);
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "music", false, _ => null);
        Assert.Throws<IOException>(() => mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", true, _ => "disk full"));
        Assert.Equal("music", Assert.Single(mixer.BrowserTabs).ChannelId);
        Assert.False(Assert.Single(mixer.BrowserTabs).Remember);
        Assert.Empty(mixer.ExportSettings().BrowserSiteChannels);
        Assert.Throws<IOException>(() => mixer.SetPreserveBrowserOutputs(true, _ => "disk full"));
        Assert.False(mixer.PreserveBrowserOutputs);
    }

    [Fact]
    public void ForgettingTheWebsiteClearsItsPreferenceAndPreservesOtherTabOverrides()
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2)]);
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", true, _ => null);
        mixer.AssignBrowserTab(session, 2, "https://teams.microsoft.com", "music", false, _ => null);
        Assert.Single(mixer.ExportSettings().BrowserSiteChannels);
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", null, true, _ => null);
        Assert.Empty(mixer.ExportSettings().BrowserSiteChannels);
        Assert.Null(mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        Assert.Equal("music", mixer.BrowserTabs.Single(t => t.Id == 2).ChannelId);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2), Tab(3)]);
        Assert.Null(mixer.BrowserTabs.Single(t => t.Id == 3).ChannelId);
    }

    [Fact]
    public void SavedWebsiteChoicesRestoreWithoutRuntimeTabs()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-browser-settings-").FullName;
        string path = Path.Combine(directory, "mixer.json");
        try
        {
            using var original = new Mixer();
            Guid owner = Guid.NewGuid();
            string session = original.OpenBrowserSession(owner);
            original.SyncBrowserTabs(owner, session, [Tab(1)]);
            original.SetPreserveBrowserOutputs(true);
            original.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", true, settings => settings.Save(path));
            using var restored = new Mixer();
            restored.ApplySettings(MixerSettings.Load(path)!);
            Assert.True(restored.PreserveBrowserOutputs);
            Assert.Empty(restored.BrowserTabs);
            Guid next = Guid.NewGuid();
            string fresh = restored.OpenBrowserSession(next);
            restored.SyncBrowserTabs(next, fresh, [Tab(99)]);
            Assert.Equal("voicechat", Assert.Single(restored.BrowserTabs).ChannelId);
            Assert.True(Assert.Single(restored.BrowserTabs).Remember);
            string json = File.ReadAllText(path);
            Assert.DoesNotContain(session, json);
            Assert.DoesNotContain("Call", json);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("""{"cmd":"setPreserveBrowserOutputs","value":"yes"}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"bad","tabId":1,"remember":false}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"00000000000000000000000000000000","tabId":-1,"remember":false}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"00000000000000000000000000000000","tabId":1,"channel":"music","remember":true}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"00000000000000000000000000000000","tabId":1,"site":"https://example.com/private","channel":"music","remember":true}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"00000000000000000000000000000000","tabId":1,"site":"https://teams.microsoft.com","channel":"ignore","remember":false}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"00000000000000000000000000000000","tabId":1,"site":"https://teams.microsoft.com","channel":"xlr1","remember":false}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"00000000000000000000000000000000","tabId":1,"site":"https://teams.microsoft.com","remember":null}""")]
    [InlineData("""{"cmd":"syncBrowserTabs","session":"00000000000000000000000000000000"}""")]
    [InlineData("""{"cmd":"syncBrowserTabs","session":"00000000000000000000000000000000","tabs":[null]}""")]
    [InlineData("""{"cmd":"syncBrowserTabs","session":"00000000000000000000000000000000","tabs":[{"id":1,"site":"https://example.com","title":"","activation":"capturing"}]}""")]
    [InlineData("""{"cmd":"activateBrowserTab","session":"bad","tabId":1,"site":"https://example.com"}""")]
    [InlineData("""{"cmd":"activateBrowserTab","session":"00000000000000000000000000000000","site":"https://example.com"}""")]
    [InlineData("""{"cmd":"activateBrowserTab","session":"00000000000000000000000000000000","tabId":-1,"site":"https://example.com"}""")]
    [InlineData("""{"cmd":"activateBrowserTab","session":"00000000000000000000000000000000","tabId":1}""")]
    [InlineData("""{"cmd":"activateBrowserTab","session":"00000000000000000000000000000000","tabId":1,"site":"https://example.com/private"}""")]
    public void MalformedBrowserCommandsAreRefused(string json)
    {
        using var mixer = new Mixer();
        Command command = JsonSerializer.Deserialize<Command>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.NotNull(CommandValidation.Check(command, mixer, _ => null));
    }

    [Fact]
    public void AWellFormedActivationPassesValidation()
    {
        using var mixer = new Mixer();
        Assert.Null(CommandValidation.Check(new Command { Cmd = "activateBrowserTab", Session = new string('0', 32), TabId = 1,
            Site = "https://EXAMPLE.com:443/" }, mixer, _ => null));
    }

    [Theory]
    [InlineData("chromium", true)]
    [InlineData("/opt/google/chrome/chrome", true)]
    [InlineData("Microsoft Edge", true)]
    [InlineData("brave-browser", true)]
    [InlineData("vesktop", false)]
    [InlineData("steamwebhelper", false)]
    [InlineData("chromium-electron", false)]
    [InlineData("firefox", false)]
    public void PreservationAppliesOnlyToRecognizedBrowserIdentities(string binary, bool expected)
        => Assert.Equal(expected, BrowserTabRouting.IsChromium(binary));

    [Theory]
    [InlineData("https://example.com/a")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com?q=private")]
    [InlineData("https://example.com#private")]
    [InlineData("file:///home/private")]
    [InlineData("https://example.com\n")]
    public void SitesCannotCarryBrowsingPathsOrCredentials(string site)
        => Assert.Null(BrowserTabRouting.CanonicalSite(site));

    // The browser's own serialization is the canonical form, so a tab the
    // companion lists is reported back with the text it sent.
    [Theory]
    [InlineData("http://[::ffff:102:304]", "http://[::ffff:102:304]")]
    [InlineData("http://[::ffff:1.2.3.4]", "http://[::ffff:102:304]")]
    [InlineData("http://[::102:304]", "http://[::102:304]")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:8080", "http://[::1]:8080")]
    [InlineData("http://[1:0:0:2:0:0:3:4]", "http://[1::2:0:0:3:4]")]
    [InlineData("http://[2001:DB8:0:0:1:0:0:0]", "http://[2001:db8:0:0:1::]")]
    [InlineData("https://EXAMPLE.com:443/", "https://example.com")]
    [InlineData("http://example.com:8080", "http://example.com:8080")]
    public void OriginsAreCanonicalInTheBrowsersForm(string site, string expected)
        => Assert.Equal(expected, BrowserTabRouting.CanonicalSite(site));

    [Theory]
    [InlineData("https://a*b.example")]
    [InlineData("https://a!b.example")]
    [InlineData("https://a$b.example")]
    [InlineData("https://a&b.example")]
    [InlineData("https://a(b).example")]
    public void OriginsWithoutACanonicalFormAreRefusedForTheWholeList(string site)
    {
        Assert.Null(BrowserTabRouting.CanonicalSite(site));
        Assert.NotNull(BrowserTabRouting.CheckTabs([Tab(1), Tab(2, site)]));
    }

    [Fact]
    public void RegistrationsAndSavedSitesHaveIndependentBounds()
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        Assert.Throws<ArgumentException>(() => mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(1)]));
        Assert.Throws<ArgumentException>(() => mixer.SyncBrowserTabs(owner, session, [Tab(1) with { Title = new string('x', 161) }]));
        Assert.Throws<ArgumentException>(() => mixer.SyncBrowserTabs(owner, session, [.. Enumerable.Range(0, 129).Select(id => Tab(id))]));
        for (int n = 0; n < 128; n++)
        {
            mixer.SyncBrowserTabs(owner, session, [Tab(1, $"https://site{n}.example")]);
            mixer.AssignBrowserTab(session, 1, $"https://site{n}.example", "music", true, _ => null);
        }
        mixer.SyncBrowserTabs(owner, session, [Tab(1, "https://last.example")]);
        Assert.Throws<InvalidOperationException>(() => mixer.AssignBrowserTab(session, 1, "https://last.example", "music", true, _ => null));
        Assert.Equal(128, mixer.ExportSettings().BrowserSiteChannels.Count);
        for (int n = 1; n < 8; n++) mixer.OpenBrowserSession(Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => mixer.OpenBrowserSession(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("music", "system")]
    [InlineData("system", "music")]
    public void DeletingAChannelMovesSavedSitesToTheFallbackWithoutChangingOtherTabs(string deleted, string fallback)
    {
        using var graph = new BrowserGraph();
        graph.Build();
        Guid owner = Guid.NewGuid();
        string session = graph.Mixer.OpenBrowserSession(owner);
        graph.Mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2, "https://youtube.com"), Tab(3)]);
        graph.Mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", deleted, true, _ => null);
        graph.Mixer.AssignBrowserTab(session, 2, "https://youtube.com", fallback, true, _ => null);
        graph.Mixer.AssignBrowserTab(session, 3, "https://teams.microsoft.com", deleted, false, _ => null);
        Assert.Throws<IOException>(() => graph.Mixer.DeleteApplicationChannel(deleted, _ => "disk full"));
        Assert.Equal(deleted, graph.Mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        Assert.Equal(deleted, graph.Mixer.ExportSettings().BrowserSiteChannels["https://teams.microsoft.com"]);
        Assert.Equal(deleted, graph.Mixer.BrowserTabs.Single(t => t.Id == 3).ChannelId);
        MixerSettings? saved = null;
        graph.Mixer.DeleteApplicationChannel(deleted, settings => { saved = settings; return null; });
        Assert.Equal(fallback, saved!.BrowserSiteChannels["https://teams.microsoft.com"]);
        Assert.Equal(fallback, graph.Mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        Assert.True(graph.Mixer.BrowserTabs.Single(t => t.Id == 1).Remember);
        Assert.Equal(fallback, graph.Mixer.BrowserTabs.Single(t => t.Id == 2).ChannelId);
        Assert.Equal(fallback, saved.BrowserSiteChannels["https://youtube.com"]);
        Assert.Null(graph.Mixer.BrowserTabs.Single(t => t.Id == 3).ChannelId);
        Assert.False(graph.Mixer.BrowserTabs.Single(t => t.Id == 3).Remember);
    }

    [Fact]
    public void BuildingAfterRestoringSettingsDropsUnknownWebsiteChannels()
    {
        using var graph = new BrowserGraph();
        graph.Mixer.ApplySettings(new MixerSettings
        {
            BrowserSiteChannels = new() { ["https://teams.microsoft.com"] = "deleted", ["https://youtube.com"] = "system" },
        });
        Guid owner = Guid.NewGuid();
        string session = graph.Mixer.OpenBrowserSession(owner);
        graph.Mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2, "https://youtube.com")]);
        graph.Mixer.Build(new MixerConfig { Channels = [new("system", "System")], Mixes = [] });
        Assert.Null(graph.Mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        Assert.False(graph.Mixer.BrowserTabs.Single(t => t.Id == 1).Remember);
        Assert.Equal("system", graph.Mixer.BrowserTabs.Single(t => t.Id == 2).ChannelId);
        Assert.Single(graph.Mixer.ExportSettings().BrowserSiteChannels);
    }

    [Fact]
    public void PreservingBrowserOutputsDoesNotMoveOrUnmuteTheirStreams()
    {
        using var graph = new BrowserGraph();
        graph.Mixer.ApplySettings(JsonSerializer.Deserialize<MixerSettings>("""{"PreserveBrowserOutputs":true}""")!);
        graph.Build();
        graph.Mixer.SyncStreams();
        Assert.DoesNotContain(graph.Calls, c => c.StartsWith("move-sink-input", StringComparison.Ordinal)
            || c.StartsWith("set-sink-input-mute", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitModeTransitionsSuspendAssignmentsAndReapplyThemAfterwards()
    {
        using var graph = new BrowserGraph();
        graph.Build();
        graph.Mixer.SyncStreams();
        graph.ClearCalls();
        graph.Mixer.SetPreserveBrowserOutputs(true);
        graph.Mixer.SyncStreams();
        graph.Mixer.AssignApp("chromium", "music");
        graph.Mixer.AssignStream(10, "music");
        Assert.DoesNotContain(graph.Calls, c => c.StartsWith("move-sink-input", StringComparison.Ordinal)
            || c.StartsWith("set-sink-input-mute", StringComparison.Ordinal));
        graph.ClearCalls();
        graph.Mixer.SetPreserveBrowserOutputs(false);
        graph.Mixer.SyncStreams();
        Assert.Contains("move-sink-input 100 OpenXLR_ch_music", graph.Calls);
        Assert.Contains("set-sink-input-mute 100 0", graph.Calls);
    }

    [Fact]
    public void AHeldBrowserKeepsItsSavedAssignmentAndReportsWhereItPlays()
    {
        using var graph = new BrowserGraph();
        graph.Build();
        graph.Mixer.SyncStreams();
        graph.Mixer.AssignApp("chromium", "music");
        graph.Mixer.SyncStreams();
        SavedApp[] known = [.. graph.Mixer.ExportSettings().KnownApps];
        Assert.Equal("music", Assert.Single(known).ChannelId);

        graph.Mixer.SetPreserveBrowserOutputs(true);
        // The browser picked another output meanwhile; OpenXLR leaves it there.
        graph.SetStreamSink(1);
        graph.ClearCalls();
        graph.Mixer.SyncStreams();
        Assert.DoesNotContain(graph.Calls, c => c.StartsWith("move-sink-input", StringComparison.Ordinal)
            || c.StartsWith("set-sink-input-mute", StringComparison.Ordinal));
        Assert.Equal(known, graph.Mixer.ExportSettings().KnownApps);
        StreamAssignment held = Assert.Single(graph.Mixer.Snapshot().Streams);
        Assert.True(held.BrowserHeld);
        Assert.Equal("music", held.ChannelId);
        Assert.Equal("system", held.OutputChannelId);

        Assert.False(graph.Mixer.SyncStreams());
        graph.SetStreamSink(2);
        Assert.True(graph.Mixer.SyncStreams());
        Assert.Equal("music", Assert.Single(graph.Mixer.Snapshot().Streams).OutputChannelId);
        Assert.False(graph.Mixer.SyncStreams());
        graph.SetStreamSink(3);
        Assert.True(graph.Mixer.SyncStreams());
        Assert.Null(Assert.Single(graph.Mixer.Snapshot().Streams).OutputChannelId);
        Assert.False(graph.Mixer.SyncStreams());
        Assert.DoesNotContain(graph.Calls, c => c.StartsWith("move-sink-input", StringComparison.Ordinal)
            || c.StartsWith("set-sink-input-mute", StringComparison.Ordinal));

        graph.Mixer.SetPreserveBrowserOutputs(false);
        graph.ClearCalls();
        graph.Mixer.SyncStreams();
        Assert.Contains("move-sink-input 100 OpenXLR_ch_music", graph.Calls);
        Assert.Equal(known, graph.Mixer.ExportSettings().KnownApps);
        StreamAssignment placed = Assert.Single(graph.Mixer.Snapshot().Streams);
        Assert.False(placed.BrowserHeld);
        Assert.Null(placed.OutputChannelId);
        Assert.Equal("music", placed.ChannelId);
    }

    [Fact]
    public void BrowserOutputSelectionSurvivesARestartAndReleasesTheSavedAssignmentWhenOff()
    {
        using var graph = new BrowserGraph();
        string path = Path.Combine(graph.Folder, "mixer.json");
        graph.Build();
        graph.Mixer.SyncStreams();
        graph.Mixer.AssignApp("chromium", "music");
        graph.Mixer.SetPreserveBrowserOutputs(true, settings => settings.Save(path));

        using var restored = new Mixer();
        graph.Build(restored);
        restored.ApplySettings(MixerSettings.Load(path)!);
        Assert.True(restored.PreserveBrowserOutputs);
        graph.SetStreamSink(1);
        graph.ClearCalls();
        restored.SyncStreams();
        Assert.DoesNotContain(graph.Calls, c => c.StartsWith("move-sink-input", StringComparison.Ordinal));
        Assert.True(Assert.Single(restored.Snapshot().Streams).BrowserHeld);

        restored.SetPreserveBrowserOutputs(false, settings => settings.Save(path));
        Assert.False(MixerSettings.Load(path)!.PreserveBrowserOutputs);
        graph.ClearCalls();
        restored.SyncStreams();
        Assert.Contains("move-sink-input 100 OpenXLR_ch_music", graph.Calls);
        Assert.Equal("music", Assert.Single(restored.Snapshot().Streams).ChannelId);
    }

    [Fact]
    public void AnElectronAppNamedChromiumIsNotTakenForABrowser()
    {
        // Discord and other Electron apps report "Chromium" as their name.
        // Without a browser binary the stream is routed as usual.
        using var graph = new BrowserGraph("""
            "application.name":"Chromium","media.name":"Discord"
            """);
        graph.Build();
        graph.Mixer.SetPreserveBrowserOutputs(true);
        graph.Mixer.SyncStreams();
        StreamAssignment stream = Assert.Single(graph.Mixer.Streams);
        Assert.Equal("Chromium", stream.Identity);
        graph.ClearCalls();
        graph.Mixer.AssignApp(stream.Identity, "system");
        Assert.Contains("move-sink-input 100 OpenXLR_ch_system", graph.Calls);
        graph.Mixer.SyncStreams();
        Assert.Equal("system", Assert.Single(graph.Mixer.Streams).ChannelId);
    }

    [Theory]
    [InlineData(false, "system")]
    [InlineData(false, "ignore")]
    [InlineData(true, "system")]
    [InlineData(true, "ignore")]
    public void AFormerBrowserIdentityDoesNotHoldAnElectronStream(bool singleStream, string channel)
    {
        using var graph = new BrowserGraph();
        graph.Build();
        graph.Mixer.SetPreserveBrowserOutputs(true);
        graph.Mixer.SyncStreams();
        graph.SetStream("\"application.name\":\"Chromium\",\"media.name\":\"Discord\"", 101);
        graph.Mixer.SyncStreams();
        Assert.False(Assert.Single(graph.Mixer.Streams).BrowserHeld);
        Assert.Equal(101, Assert.Single(graph.Mixer.Streams).Serial);
        Assert.Null(Assert.Single(graph.Mixer.Streams).OutputChannelId);
        Assert.False(Assert.Single(graph.Mixer.Snapshot().Streams).BrowserHeld);
        Assert.Null(Assert.Single(graph.Mixer.Snapshot().Streams).OutputChannelId);
        graph.ClearCalls();
        if (singleStream) graph.Mixer.AssignStream(10, channel);
        else graph.Mixer.AssignApp("Chromium", channel);
        Assert.Contains(graph.Calls, c => c.StartsWith("move-sink-input 101 ", StringComparison.Ordinal));
        graph.Mixer.SyncStreams();
        Assert.Equal(channel, Assert.Single(graph.Mixer.Streams).ChannelId);
    }

    [Theory]
    [InlineData("system", false)]
    [InlineData("ignore", false)]
    [InlineData("system", true)]
    [InlineData("ignore", true)]
    public void AssigningASharedIdentityMovesOnlyItsNonBrowserStreams(string channel, bool singleStream)
    {
        using var graph = new BrowserGraph();
        graph.Build();
        graph.Mixer.SetPreserveBrowserOutputs(true);
        graph.AddElectronStream();
        graph.Mixer.SyncStreams();
        Assert.True(graph.Mixer.Streams.Single(s => s.Serial == 100).BrowserHeld);
        Assert.False(graph.Mixer.Streams.Single(s => s.Serial == 101).BrowserHeld);
        graph.ClearCalls();
        if (singleStream) graph.Mixer.AssignStream(11, channel);
        else graph.Mixer.AssignApp("Chromium", channel);
        Assert.Contains(graph.Calls, c => c.StartsWith("move-sink-input 101 ", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Calls, c => c.StartsWith("move-sink-input 100 ", StringComparison.Ordinal));
        graph.Mixer.SyncStreams();
        Assert.Equal(channel, graph.Mixer.Streams.Single(s => s.Serial == 101).ChannelId);
    }

    [Fact]
    public void AnUnplacedReplacementDoesNotInheritTheBrowsersHeldOutput()
    {
        using var graph = new BrowserGraph();
        graph.Build();
        graph.Mixer.SetPreserveBrowserOutputs(true);
        graph.Mixer.SyncStreams();
        Assert.True(Assert.Single(graph.Mixer.Snapshot().Streams).BrowserHeld);
        Assert.Equal("music", Assert.Single(graph.Mixer.Snapshot().Streams).OutputChannelId);
        graph.SetStream("\"application.name\":\"Chromium\",\"media.name\":\"Discord\"", 101);
        graph.RefuseMoves();
        graph.Mixer.SyncStreams();
        Assert.Empty(graph.Mixer.Streams);
        StreamAssignment app = Assert.Single(graph.Mixer.Snapshot().Streams);
        Assert.Equal(101, app.Serial);
        Assert.True(app.Active);
        Assert.False(app.BrowserHeld);
        Assert.Null(app.OutputChannelId);
    }

    private sealed class BrowserGraph : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-browser-routing-").FullName;
        private readonly string? _oldPath = Environment.GetEnvironmentVariable("PATH");
        public Mixer Mixer { get; } = new();
        public string[] Calls => File.ReadAllLines(Path.Combine(_directory, "calls"));

        public const string ChromiumProps = """
            "application.process.binary":"chromium","application.name":"Chromium"
            """;

        public BrowserGraph(string props = ChromiumProps)
        {
            File.WriteAllText(Path.Combine(_directory, "calls"), "");
            File.WriteAllText(Path.Combine(_directory, "inputs"), "100\t2\n");
            SetStream(props);
            ExecutableScript.Write(Path.Combine(_directory, "pw-dump"), "dir=${0%/*}\n/bin/cat \"$dir/streams\"");
            ExecutableScript.Write(Path.Combine(_directory, "pactl"), """
                dir=${0%/*}
                echo "$*" >> "$dir/calls"
                case "$*" in
                  move-sink-input\ *) [ ! -e "$dir/refuse-moves" ] || exit 0 ;;
                esac
                case "$*" in
                  'list sinks short') printf '1\tOpenXLR_ch_system\n2\tOpenXLR_ch_music\n3\tdesktop\n' ;;
                  get-default-sink) printf 'desktop\n' ;;
                  'list sink-inputs short') /bin/cat "$dir/inputs" ;;
                  load-module\ *) printf '1\n' ;;
                  'move-sink-input 100 OpenXLR_ch_system') printf '100\t1\n' > "$dir/inputs" ;;
                  'move-sink-input 100 OpenXLR_ch_music') printf '100\t2\n' > "$dir/inputs" ;;
                  'move-sink-input 101 OpenXLR_ch_system') printf '101\t1\n' > "$dir/inputs" ;;
                  'move-sink-input 101 desktop') printf '101\t3\n' > "$dir/inputs" ;;
                esac
                """);
            Environment.SetEnvironmentVariable("PATH", _directory);
        }

        public string Folder => _directory;

        public void SetStream(string props, int serial = 100)
        {
            File.WriteAllText(Path.Combine(_directory, "streams"),
                """[{"id":10,"type":"PipeWire:Interface:Node","info":{"props":{"object.serial":SERIAL,"media.class":"Stream/Output/Audio",""".Replace("SERIAL", serial.ToString()) + props + "}}}]");
            File.WriteAllText(Path.Combine(_directory, "inputs"), $"{serial}\t2\n");
            // A fixture replacement is a new registry observation, not a
            // second read of the adapter's 400 ms standalone snapshot.
            var adapter = (PipeWireAdapter)typeof(Mixer).GetField("_pw", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Mixer)!;
            typeof(PipeWireAdapter).GetMethod("InvalidateDump", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(adapter, null);
        }

        public void RefuseMoves() => File.WriteAllText(Path.Combine(_directory, "refuse-moves"), "");

        public void AddElectronStream()
        {
            string path = Path.Combine(_directory, "streams");
            File.WriteAllText(path, File.ReadAllText(path).TrimEnd(']') + """
                ,{"id":11,"type":"PipeWire:Interface:Node","info":{"props":{"object.serial":101,"media.class":"Stream/Output/Audio","application.name":"Chromium","media.name":"Discord"}}}]
                """);
            File.WriteAllText(Path.Combine(_directory, "inputs"), "100\t2\n101\t2\n");
        }

        public void Build(Mixer? mixer = null)
        {
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer ?? Mixer,
                new MixerConfig { Channels = [new("system", "System"), new("music", "Music")], Mixes = [] });
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer ?? Mixer, true);
        }

        public void ClearCalls() => File.WriteAllText(Path.Combine(_directory, "calls"), "");

        /// <summary>Put the stream on sink 1 (system) or 2 (music), as the desktop would.</summary>
        public void SetStreamSink(int sink) => File.WriteAllText(Path.Combine(_directory, "inputs"), $"100\t{sink}\n");

        public void Dispose()
        {
            Mixer.Dispose();
            Environment.SetEnvironmentVariable("PATH", _oldPath);
            Directory.Delete(_directory, true);
        }
    }
}
