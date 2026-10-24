using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>Display metadata from the browser companion. Site is an origin; no audio is carried.</summary>
public sealed record BrowserTabRegistration
{
    public int Id { get; init; } = -1;
    public string Site { get; init; } = "";
    public string Title { get; init; } = "";
    public bool Capturing { get; init; }
    public string? Error { get; init; }
    /// <summary>Where an activation OpenXLR asked for stands in the browser; null when none is in progress.</summary>
    public string? Activation { get; init; }
}

/// <summary>A connected tab and the output choice the browser companion should apply.</summary>
public sealed record BrowserTabState(string Session, int Id, string Site, string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ChannelId,
    bool Remember, bool Capturing, string? Error, string? Activation = null);

public static class BrowserTabRouting
{
    public const int MaxProviders = 8, MaxTabs = 128, MaxSites = 128;
    public const int MaxOrigin = 512, MaxTitle = 160, MaxError = 200;

    /// <summary>The origin as the browser serializes it; see <see cref="BrowserOrigin"/>.</summary>
    public static string? CanonicalSite(string? value) => BrowserOrigin.Canonical(value);

    /// <summary>
    /// An activation OpenXLR requested: <c>starting</c> while the browser brings
    /// the tab forward and tries to start it, <c>popup</c> once its popup is
    /// open on the tab, <c>browser</c> when the user has to invoke the
    /// extension on the tab first.
    /// </summary>
    public static bool ValidActivation(string? activation) => activation is null or "starting" or "popup" or "browser";

    public static bool ValidSession(string? session)
        => session is { Length: 32 } && Guid.TryParseExact(session, "N", out _);

    public static string? CheckTabs(IReadOnlyList<BrowserTabRegistration>? tabs)
    {
        if (tabs is null || tabs.Count > MaxTabs) return $"syncBrowserTabs: supply at most {MaxTabs} tabs";
        var seen = new HashSet<int>();
        foreach (BrowserTabRegistration? tab in tabs)
        {
            if (tab is null || tab.Id < 0 || !seen.Add(tab.Id)) return "syncBrowserTabs: tab ids must be distinct non-negative integers";
            if (CanonicalSite(tab.Site) is null) return "syncBrowserTabs: site must be an HTTP or HTTPS origin without credentials, path, query or fragment";
            if (tab.Title is null || tab.Title.Length > MaxTitle || tab.Title.Any(char.IsControl)) return $"syncBrowserTabs: title must have at most {MaxTitle} characters and no controls";
            if (tab.Error is { } error && (error.Length > MaxError || error.Any(char.IsControl))) return $"syncBrowserTabs: error must have at most {MaxError} characters and no controls";
            if (!ValidActivation(tab.Activation)) return "syncBrowserTabs: activation must be starting, popup, browser or null";
        }
        return null;
    }

    internal static bool IsChromium(string? binary)
    {
        if (binary is null) return false;
        string name = Path.GetFileName(binary).ToLowerInvariant();
        return name is "chrome" or "google-chrome" or "google-chrome-stable" or "google-chrome-beta" or "google-chrome-unstable"
            or "google chrome" or "chromium" or "chromium-browser" or "ungoogled-chromium"
            or "brave" or "brave-browser" or "brave-browser-beta" or "brave-browser-nightly"
            or "msedge" or "microsoft-edge" or "microsoft-edge-stable" or "microsoft-edge-beta" or "microsoft-edge-dev" or "microsoft edge"
            or "vivaldi" or "vivaldi-bin" or "opera" or "opera-beta" or "opera-developer";
    }

    internal static Dictionary<string, string> SiteChannels(IReadOnlyDictionary<string, string>? values)
    {
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string site, string? channel) in values ?? new Dictionary<string, string>())
            if (kept.Count < MaxSites && CanonicalSite(site) is { } origin
                && channel is { Length: > 0 and <= 36 } && !channel.Any(char.IsControl)) kept.TryAdd(origin, channel);
        return kept;
    }
}

public sealed partial class Mixer
{
    private sealed record BrowserTab(BrowserTabRegistration Metadata, string PublishedTitle, bool Override = false, string? Channel = null);
    private readonly Dictionary<string, Dictionary<int, BrowserTab>> _browserSessions = new(StringComparer.Ordinal);
    private Dictionary<string, string> _browserSiteChannels = new(StringComparer.Ordinal);
    private bool _preserveBrowserOutputs;

    // Identities whose streams came from a recognized browser binary. The
    // application name is no evidence: Electron apps such as Discord report
    // "Chromium" there, and so does an identity taken from that name.
    private readonly HashSet<string> _browserIdentities = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when browser output selection leaves this application's streams alone.</summary>
    private bool BrowserHeldLocked(string identity) => _preserveBrowserOutputs && _browserIdentities.Contains(identity);

    public bool PreserveBrowserOutputs { get { lock (_gate) return _preserveBrowserOutputs; } }

    public IReadOnlyList<BrowserTabState> BrowserTabs { get { lock (_gate) return BrowserTabsLocked(); } }

    public string OpenBrowserSession(Guid owner)
    {
        lock (_gate)
        {
            string session = owner.ToString("N");
            if (!_browserSessions.ContainsKey(session))
            {
                if (_browserSessions.Count >= BrowserTabRouting.MaxProviders) throw new InvalidOperationException("At most eight browser companions can connect.");
                _browserSessions[session] = [];
            }
            return session;
        }
    }

    public bool RemoveBrowserSession(Guid owner)
    {
        lock (_gate) return _browserSessions.Remove(owner.ToString("N"));
    }

    public bool SyncBrowserTabs(Guid owner, string session, IReadOnlyList<BrowserTabRegistration> tabs)
    {
        if (BrowserTabRouting.CheckTabs(tabs) is { } problem) throw new ArgumentException(problem);
        lock (_gate)
        {
            if (session != owner.ToString("N") || !_browserSessions.TryGetValue(session, out Dictionary<int, BrowserTab>? previous))
                throw new InvalidOperationException("syncBrowserTabs: this connection does not own the browser session");
            IReadOnlyList<BrowserTabState> before = BrowserTabsLocked();
            // Title-only updates wait for the timer, including in snapshots
            // sent for unrelated mixer changes.
            _browserSessions[session] = tabs.Select(t => t with { Site = BrowserTabRouting.CanonicalSite(t.Site)! })
                .ToDictionary(t => t.Id, t => previous.TryGetValue(t.Id, out BrowserTab? held) && held.Metadata.Site == t.Site
                    ? held with { Metadata = t, PublishedTitle = (held.Metadata with { Title = t.Title }) == t ? held.PublishedTitle : t.Title }
                    : new BrowserTab(t, t.Title));
            return !before.SequenceEqual(BrowserTabsLocked());
        }
    }

    /// <summary>Publish the latest titles once per daemon tick, without delaying routing changes.</summary>
    public bool FlushBrowserTabTitles()
    {
        // A timer calls this every second, and a graph build holds the gate for
        // seconds. Waiting would tie up a pool thread per tick; the next tick
        // publishes the titles instead.
        if (!Monitor.TryEnter(_gate)) return false;
        try
        {
            IReadOnlyList<BrowserTabState> before = BrowserTabsLocked();
            foreach (Dictionary<int, BrowserTab> tabs in _browserSessions.Values)
                foreach ((int id, BrowserTab tab) in tabs.ToArray())
                    if (tab.PublishedTitle != tab.Metadata.Title) tabs[id] = tab with { PublishedTitle = tab.Metadata.Title };
            return !before.SequenceEqual(BrowserTabsLocked());
        }
        finally { Monitor.Exit(_gate); }
    }

    public void AssignBrowserTab(string session, int tabId, string site, string? channel, bool remember, Func<MixerSettings, string?> persist)
    {
        lock (_gate)
        {
            if (!_browserSessions.TryGetValue(session, out Dictionary<int, BrowserTab>? tabs) || !tabs.TryGetValue(tabId, out BrowserTab? tab))
                throw new InvalidOperationException("assignBrowserTab: the browser tab is no longer connected");
            string expectedSite = BrowserTabRouting.CanonicalSite(site)
                ?? throw new ArgumentException("assignBrowserTab: supply the tab's HTTP or HTTPS origin");
            if (tab.Metadata.Site != expectedSite) throw new InvalidOperationException("assignBrowserTab: the tab changed website; select it again");
            if (channel is not null && !HasApplicationChannel(channel)) throw new ArgumentException("assignBrowserTab: select an application channel or browser default");
            if (remember && channel is not null && !_browserSiteChannels.ContainsKey(tab.Metadata.Site)
                && _browserSiteChannels.Count >= BrowserTabRouting.MaxSites) throw new InvalidOperationException("At most 128 browser sites can be remembered.");
            var previous = new Dictionary<string, string>(_browserSiteChannels, StringComparer.Ordinal);
            tabs[tabId] = tab with { Override = !remember, Channel = channel };
            if (!remember) return;
            if (channel is null) _browserSiteChannels.Remove(tab.Metadata.Site);
            else _browserSiteChannels[tab.Metadata.Site] = channel;
            try { PersistLocked(persist); }
            catch { _browserSiteChannels = previous; tabs[tabId] = tab; throw; }
        }
    }

    /// <summary>
    /// Check that a connected tab can be asked to start routing: tab routing
    /// is on, the tab still shows the origin the caller saw, it has a channel
    /// and is neither routed nor already being activated. Throws otherwise.
    /// </summary>
    public void CheckBrowserActivation(string session, int tabId, string site)
    {
        lock (_gate)
        {
            if (!_browserSessions.TryGetValue(session, out Dictionary<int, BrowserTab>? tabs) || !tabs.TryGetValue(tabId, out BrowserTab? tab))
                throw new InvalidOperationException("activateBrowserTab: the browser tab is no longer connected");
            if (tab.Metadata.Site != BrowserTabRouting.CanonicalSite(site))
                throw new InvalidOperationException("activateBrowserTab: the tab changed website; select it again");
            if (!_preserveBrowserOutputs) throw new InvalidOperationException("activateBrowserTab: enable individual browser tab routing first");
            if ((tab.Override ? tab.Channel : _browserSiteChannels.GetValueOrDefault(tab.Metadata.Site)) is null)
                throw new InvalidOperationException("activateBrowserTab: choose a channel for the tab first");
            if (tab.Metadata.Capturing) throw new InvalidOperationException("activateBrowserTab: the tab is already routed");
            if (tab.Metadata.Activation is not null) throw new InvalidOperationException("activateBrowserTab: the browser is already activating the tab");
        }
    }

    public void SetPreserveBrowserOutputs(bool enabled, Func<MixerSettings, string?>? persist = null)
    {
        lock (_gate)
        {
            bool previous = _preserveBrowserOutputs;
            _preserveBrowserOutputs = enabled;
            try { if (persist is not null) PersistLocked(persist); }
            catch { _preserveBrowserOutputs = previous; throw; }
            if (previous == enabled) return;
            // The next sweep places these streams again: held where they play
            // while the option is on, moved to their assignment once it is off.
            // The assignment itself is never rewritten here.
            foreach (int id in _streams.Where(p => _browserIdentities.Contains(p.Value.Identity)).Select(p => p.Key).ToArray()) _streams.Remove(id);
            foreach ((string identity, StreamAssignment app) in _apps.ToArray())
                if (_browserIdentities.Contains(identity))
                    _apps[identity] = app with { BrowserHeld = enabled, OutputChannelId = null };
        }
    }

    private IReadOnlyList<BrowserTabState> BrowserTabsLocked()
        => [.. _browserSessions.OrderBy(s => s.Key, StringComparer.Ordinal).SelectMany(s => s.Value.Values.OrderBy(t => t.Metadata.Id)
            .Select(t => new BrowserTabState(s.Key, t.Metadata.Id, t.Metadata.Site,
                t.Metadata.Capturing || (t.Override ? t.Channel : _browserSiteChannels.GetValueOrDefault(t.Metadata.Site)) is not null ? t.PublishedTitle : "",
                t.Override ? t.Channel : _browserSiteChannels.GetValueOrDefault(t.Metadata.Site),
                !t.Override && _browserSiteChannels.ContainsKey(t.Metadata.Site), t.Metadata.Capturing, t.Metadata.Error,
                t.Metadata.Activation)))];

    private void RestoreBrowserSettingsLocked(MixerSettings settings)
    {
        SetPreserveBrowserOutputs(settings.PreserveBrowserOutputs);
        _browserSiteChannels = BrowserTabRouting.SiteChannels(settings.BrowserSiteChannels)
            .Where(p => !_built || HasApplicationChannel(p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    private void PruneBrowserChannelsLocked()
    {
        foreach (string site in _browserSiteChannels.Where(p => !HasApplicationChannel(p.Value)).Select(p => p.Key).ToArray()) _browserSiteChannels.Remove(site);
        foreach (Dictionary<int, BrowserTab> tabs in _browserSessions.Values)
            foreach ((int id, BrowserTab tab) in tabs.ToArray())
                if (tab.Channel is not null && !HasApplicationChannel(tab.Channel)) tabs[id] = tab with { Channel = null };
    }
}
