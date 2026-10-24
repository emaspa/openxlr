using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>A browser tab reported by the extension, with routing confirmed by the daemon.</summary>
public sealed class BrowserTabViewModel : ViewModelBase
{
    /// <summary>The picker entry that leaves this tab on the browser's normal destination.</summary>
    public const string BrowserDefault = "";

    private sealed record Route(string? Channel, bool Remember);
    /// <summary>How long a request waits for the browser to report that it is acting on it.</summary>
    public static readonly TimeSpan ActivationAnswerTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<string, int, string, string?, bool, Task<string?>> _assign;
    private readonly Func<string, int, string, Task<string?>>? _activate;
    private readonly Action<string> _reportError;
    private readonly TimeSpan _activationTimeout;
    private Route _reported = new(null, false);
    private Route? _pending;
    private long _editVersion, _requestVersion;
    private bool _applying, _canEdit, _isPending, _capturing, _requested;
    private string _title = "", _site = "";
    private string? _error, _activation, _requestError;

    public BrowserTabViewModel(string session, int id,
        Func<string, int, string, string?, bool, Task<string?>> assign, Action<string> reportError,
        Func<string, int, string, Task<string?>>? activate = null, TimeSpan? activationTimeout = null)
    {
        Session = session; Id = id; _assign = assign; _reportError = reportError;
        _activate = activate; _activationTimeout = activationTimeout ?? ActivationAnswerTimeout;
    }

    public string Session { get; }
    public int Id { get; }
    public string Title => _title;
    public string Site => _site;
    public ObservableCollection<ChannelChoice> Channels { get; } = [];
    public bool CanEdit => _canEdit;
    public bool CanRemember => CanEdit && ChannelId is not null;
    public bool IsPending => _isPending;
    public bool Capturing => _capturing;
    public string? ChannelId => (_pending ?? _reported).Channel;

    /// <summary>What the browser reports about an activation OpenXLR asked for: starting, popup, browser or null.</summary>
    public string? Activation => _activation;

    /// <summary>An activation was asked for and the browser has not finished it.</summary>
    public bool IsActivating => _requested || _activation is not null;

    /// <summary>Activate offers itself only when it can start something.</summary>
    public bool CanActivate => _activate is not null && CanEdit && ChannelId is not null && !IsPending && !Capturing && !IsActivating;
    public ChannelChoice? SelectedChannel
    {
        get => Channels.FirstOrDefault(c => c.Id == (ChannelId ?? BrowserDefault));
        set
        {
            if (_applying || !CanEdit || value is null || !Channels.Contains(value)) return;
            string? channel = value.Id == BrowserDefault ? null : value.Id;
            if (channel == ChannelId) return;
            Submit(new(channel, channel is not null && Remember));
        }
    }

    public bool Remember
    {
        get => (_pending ?? _reported).Remember;
        set
        {
            if (_applying || !CanRemember || value == Remember) return;
            Submit(new(ChannelId, value));
        }
    }

    /// <summary>Remove the website's saved choice and return this tab to the browser default.</summary>
    public void ForgetWebsite()
    {
        if (CanEdit) Submit(new(null, false), forgetWebsite: true);
    }

    /// <summary>
    /// Ask the browser to bring this tab forward and start routing it. The
    /// browser decides: it starts capture only for a tab the user invoked the
    /// extension on, so the row says when the rest has to happen there.
    /// </summary>
    public void Activate()
    {
        if (!CanActivate) return;
        long version = ++_requestVersion;
        _requested = true;
        _requestError = _error;
        RaiseRoute();
        _ = ActivateAsync(Site, version);
        _ = ExpireRequestAsync(version);
    }

    private async Task ActivateAsync(string site, long version)
    {
        string? error = await _activate!(Session, Id, site);
        if (error is null || version != _requestVersion) return;
        EndRequest();
        _error = error;
        _reportError(error);
        RaiseRoute();
    }

    private async Task ExpireRequestAsync(long version)
    {
        await Task.Delay(_activationTimeout);
        if (version != _requestVersion) return;
        EndRequest();
        RaiseRoute();
    }

    /// <summary>Stop waiting for the browser's answer; late replies to the request are ignored.</summary>
    private void EndRequest()
    {
        _requestVersion++;
        _requested = false;
    }

    /// <summary>Capture is always extension state. Assigning a channel cannot activate capture.</summary>
    public string StatusText => !CanEdit ? Localizer.Text("BrowserTabRoutingDisabled")
        : _activation == "popup" ? Localizer.Text("BrowserTabActivatePopup")
        : _activation == "browser" ? Localizer.Text("BrowserTabActivateToolbar")
        : IsActivating ? Localizer.Text("BrowserTabActivating")
        : _error is { Length: > 0 } ? _error
        : IsPending ? Localizer.Text("BrowserTabApplying")
        : Capturing ? Localizer.Text("BrowserTabCapturing")
        : ChannelId is not null ? Localizer.Text("BrowserTabActivationRequired")
        : Localizer.Text("BrowserTabUsesDefault");

    /// <summary>Update the same row without echoing state or closing an unchanged channel picker.</summary>
    public void ApplyFromDaemon(string title, string site, string? channel, bool remember, bool capturing,
        string? error, IReadOnlyList<ChannelChoice> channels, bool canEdit, string? activation = null)
    {
        _applying = true;
        try
        {
            bool navigated = _site.Length > 0 && _site != site;
            Set(ref _title, title.Length == 0 ? site : title, nameof(Title));
            Set(ref _site, site, nameof(Site));
            _reported = new(channel, channel is not null && remember);
            if (navigated) CancelPending();
            if (_pending?.Channel is { } target && !channels.Any(c => c.Id == target)) CancelPending();
            if (!Channels.SequenceEqual(channels))
            {
                Channels.Clear();
                foreach (ChannelChoice choice in channels) Channels.Add(choice);
            }
            Set(ref _capturing, capturing, nameof(Capturing));
            _activation = activation;
            // The browser has answered a request once it reports the request,
            // capture, or an error it did not show before. A cleared error
            // counts as not shown: the browser clears it on taking the request.
            if (_requested && error is null) _requestError = null;
            if (_requested && (navigated || capturing || activation is not null || error is not null && error != _requestError)) EndRequest();
            _error = error;
            SetCanEdit(canEdit);
            RaiseRoute();
        }
        finally { _applying = false; }
    }

    internal void SetCanEdit(bool value)
    {
        if (!value) { EndRequest(); CancelPending(); }
        Set(ref _canEdit, value, nameof(CanEdit));
        Raise(nameof(CanRemember));
        Raise(nameof(IsActivating));
        Raise(nameof(CanActivate));
        Raise(nameof(StatusText));
    }

    /// <summary>Invalidate replies from a removed tab or a lost connection.</summary>
    internal void CancelPending()
    {
        _editVersion++;
        _pending = null;
        Set(ref _isPending, false, nameof(IsPending));
        RaiseRoute();
    }

    private void Submit(Route route, bool forgetWebsite = false)
    {
        string site = Site;
        long version = ++_editVersion;
        _pending = route;
        _error = null;
        Set(ref _isPending, true, nameof(IsPending));
        RaiseRoute();
        _ = SubmitAsync(site, route, version, forgetWebsite);
    }

    private async Task SubmitAsync(string site, Route route, long version, bool forgetWebsite)
    {
        string? error = await _assign(Session, Id, site, route.Channel, forgetWebsite || route.Remember);
        if (version != _editVersion) return;
        Set(ref _isPending, false, nameof(IsPending));
        // The daemon publishes state before its acknowledgement. A newer
        // client's state can already be here too, so the latest report wins.
        _pending = null;
        if (error is not null)
        {
            _error = error;
            _reportError(error);
        }
        RaiseRoute();
    }

    private void RaiseRoute()
    {
        Raise(nameof(ChannelId)); Raise(nameof(SelectedChannel)); Raise(nameof(Remember));
        Raise(nameof(CanRemember)); Raise(nameof(Activation)); Raise(nameof(IsActivating)); Raise(nameof(CanActivate));
        Raise(nameof(StatusText));
    }
}
