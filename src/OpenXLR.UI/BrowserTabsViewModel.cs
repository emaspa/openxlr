using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

public sealed partial class MainViewModel
{
    public ObservableCollection<BrowserTabViewModel> BrowserTabs { get; } = [];
    public bool HasBrowserTabs => BrowserTabs.Count > 0;
    private bool _browserTabSupport, _preserveBrowserOutputs, _browserRoutingPending;
    private bool _reportedPreserveBrowserOutputs;
    private long _browserRoutingVersion;
    public bool BrowserTabSupport => _browserTabSupport;
    public bool CanSetBrowserRouting => BrowserTabSupport && DaemonConnected && HasMixer && !_browserRoutingPending;

    /// <summary>Shown as enabled only after the daemon acknowledges its machine routing setting.</summary>
    public bool PreserveBrowserOutputs
    {
        get => _preserveBrowserOutputs;
        set
        {
            if (_applying || !CanSetBrowserRouting || value == _preserveBrowserOutputs) return;
            _browserRoutingPending = true;
            Raise(nameof(PreserveBrowserOutputs)); Raise(nameof(CanSetBrowserRouting));
            _ = SetBrowserRoutingAsync(value, ++_browserRoutingVersion);
        }
    }

    private async Task SetBrowserRoutingAsync(bool value, long version)
    {
        string? error = await _client.SetPreserveBrowserOutputsAsync(value);
        if (version != _browserRoutingVersion) return;
        _browserRoutingPending = false;
        Set(ref _preserveBrowserOutputs, _reportedPreserveBrowserOutputs, nameof(PreserveBrowserOutputs));
        if (error is not null)
        {
            Status = error;
        }
        foreach (BrowserTabViewModel tab in BrowserTabs) tab.SetCanEdit(_preserveBrowserOutputs);
        Raise(nameof(CanSetBrowserRouting)); Raise(nameof(PreserveBrowserOutputs));
    }

    private void ApplyBrowserTabs(JsonNode? mixer)
    {
        bool supported = mixer?["preserveBrowserOutputs"] is JsonValue;
        if (!supported) { ResetBrowserTabs(); return; }
        Set(ref _browserTabSupport, supported, nameof(BrowserTabSupport));
        bool preserve = mixer?["preserveBrowserOutputs"]?.GetValue<bool>() ?? false;
        _reportedPreserveBrowserOutputs = preserve;
        if (!_browserRoutingPending)
        {
            Set(ref _preserveBrowserOutputs, preserve, nameof(PreserveBrowserOutputs));
        }
        Raise(nameof(CanSetBrowserRouting));
        List<ChannelChoice> choices = [new(BrowserTabViewModel.BrowserDefault, Localizer.Text("BrowserTabDefault")),
            .. Channels.Where(c => c.IsApplication).Select(c => new ChannelChoice(c.Id, c.Name))];
        var existing = BrowserTabs.ToDictionary(t => (t.Session, t.Id));
        var seen = new HashSet<(string, int)>();
        foreach (JsonNode? node in mixer?["browserTabs"] as JsonArray ?? [])
        {
            if (node is null) continue;
            string session = node["session"]?.GetValue<string>() ?? "";
            int id = node["id"]?.GetValue<int>() ?? -1;
            if (session.Length != 32 || !session.All(Uri.IsHexDigit) || id < 0 || !seen.Add((session, id))) continue;
            if (!existing.TryGetValue((session, id), out BrowserTabViewModel? tab))
            {
                tab = new(session, id, _client.AssignBrowserTabAsync, error => Status = error, _client.ActivateBrowserTabAsync);
                BrowserTabs.Add(tab);
            }
            tab.ApplyFromDaemon(node["title"]?.GetValue<string>() ?? "", node["site"]?.GetValue<string>() ?? "",
                node["channelId"]?.GetValue<string>(), node["remember"]?.GetValue<bool>() ?? false,
                node["capturing"]?.GetValue<bool>() ?? false, node["error"]?.GetValue<string>(), choices,
                supported && _preserveBrowserOutputs,
                node["activation"] is JsonValue activation && activation.TryGetValue<string>(out string? stage)
                    && stage is "starting" or "popup" or "browser" ? stage : null);
        }
        for (int i = BrowserTabs.Count - 1; i >= 0; i--)
            if (!seen.Contains((BrowserTabs[i].Session, BrowserTabs[i].Id)))
            {
                BrowserTabs[i].SetCanEdit(false);
                BrowserTabs.RemoveAt(i);
            }
        Raise(nameof(HasBrowserTabs));
    }

    private void ResetBrowserTabs()
    {
        _browserRoutingVersion++;
        _browserRoutingPending = false;
        _reportedPreserveBrowserOutputs = false;
        Set(ref _browserTabSupport, false, nameof(BrowserTabSupport));
        Set(ref _preserveBrowserOutputs, false, nameof(PreserveBrowserOutputs));
        foreach (BrowserTabViewModel tab in BrowserTabs) { tab.SetCanEdit(false); tab.CancelPending(); }
        BrowserTabs.Clear();
        Raise(nameof(CanSetBrowserRouting)); Raise(nameof(HasBrowserTabs));
    }
}
