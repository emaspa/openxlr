using System;
using System.Linq;

namespace OpenXLR.UI;

/// <summary>
/// The submixer's three views, chosen in Options and kept in ui.json: the
/// full mixer, a compact one with a single chosen channel, and a mini view
/// with that channel's send into one chosen mix and the input, headphone and
/// application sections set aside. Only what is drawn changes; every
/// channel, send and mix keeps its level, mute and routing.
/// </summary>
public sealed partial class MainViewModel
{
    public const string FullView = "full";
    public const string CompactView = "compact";
    public const string MiniView = "mini";

    private string _mixerView = FullView;
    private string? _compactChannelId;
    private string? _compactMixId;

    private void LoadMixerView()
    {
        UiSettings settings = UiSettings.Load();
        _mixerView = NormalizeView(settings.MixerView);
        _compactChannelId = settings.CompactChannel;
        _compactMixId = settings.CompactMix;
    }

    private static string NormalizeView(string? view) => view is CompactView or MiniView ? view : FullView;

    /// <summary><see cref="FullView"/>, <see cref="CompactView"/> or <see cref="MiniView"/>.</summary>
    public string MixerView => _mixerView;
    public bool IsCompactView => _mixerView == CompactView;
    public bool IsMiniView => _mixerView == MiniView;
    /// <summary>INPUTS and HEADPHONES, which the mini view sets aside.</summary>
    public bool ShowDetailedSections => !IsMiniView;
    public bool ShowApplications => HasMixer && !IsMiniView;
    public bool ShowChannelSelector => _mixerView != FullView;
    public bool ShowMixSelector => IsMiniView;

    /// <summary>
    /// Switch the view for this run and save it. Returns why the save
    /// failed, or null; the view stays on either way.
    /// </summary>
    public string? ChooseMixerView(string view)
    {
        view = NormalizeView(view);
        if (view == _mixerView) return null;
        _mixerView = view;
        foreach (string name in new[] { nameof(MixerView), nameof(IsCompactView), nameof(IsMiniView),
                     nameof(ShowDetailedSections), nameof(ShowApplications), nameof(ShowChannelSelector), nameof(ShowMixSelector) })
            Raise(name);
        RefreshChannelPresentation();
        return SaveView(s => s with { MixerView = view == FullView ? null : view });
    }

    private ChannelViewModel? _selectedCompactChannel;
    /// <summary>The channel the compact and mini views show; a hidden channel can be chosen.</summary>
    public ChannelViewModel? SelectedCompactChannel
    {
        get => _selectedCompactChannel;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedCompactChannel) || !Channels.Contains(value)) return;
            _compactChannelId = value.Id;
            _selectedCompactChannel = value;
            Raise(nameof(SelectedCompactChannel));
            RefreshChannelPresentation();
            ViewPreferenceError = SaveView(s => s with { CompactChannel = value.Id });
        }
    }

    private MixViewModel? _selectedCompactMix;
    /// <summary>The mix the mini view shows.</summary>
    public MixViewModel? SelectedCompactMix
    {
        get => _selectedCompactMix;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedCompactMix) || !Mixes.Contains(value)) return;
            _compactMixId = value.Id;
            _selectedCompactMix = value;
            Raise(nameof(SelectedCompactMix));
            RefreshChannelPresentation();
            ViewPreferenceError = SaveView(s => s with { CompactMix = value.Id });
        }
    }

    private string? _viewPreferenceError;
    /// <summary>Why the last channel or mix choice could not be saved, or null. The choice stays for this run.</summary>
    public string? ViewPreferenceError { get => _viewPreferenceError; private set => Set(ref _viewPreferenceError, value); }

    private static string? SaveView(Func<UiSettings, UiSettings> change) => change(UiSettings.Load()).Save();

    /// <summary>
    /// Which strips, sends and masters the submixer draws. The full view
    /// draws every channel the device can feed except hidden ones. The
    /// compact view draws the chosen channel, hidden or not, with all its
    /// sends; the mini view narrows that to the chosen mix. A choice that is
    /// gone, after a layout or device change, falls back to the first shown
    /// item without forgetting the saved one, so it returns when it does.
    /// </summary>
    private void RefreshChannelPresentation()
    {
        bool single = _mixerView != FullView;
        ChannelViewModel? channel = Channels.FirstOrDefault(c => c.Id == _compactChannelId && c.Visible)
            ?? Channels.FirstOrDefault(c => c.Visible && !c.Appearance.Hidden) ?? Channels.FirstOrDefault(c => c.Visible);
        if (!ReferenceEquals(channel, _selectedCompactChannel))
        {
            _selectedCompactChannel = channel;
            Raise(nameof(SelectedCompactChannel));
        }
        MixViewModel? mix = Mixes.FirstOrDefault(m => m.Id == _compactMixId && m.Visible) ?? Mixes.FirstOrDefault(m => m.Visible);
        if (!ReferenceEquals(mix, _selectedCompactMix))
        {
            _selectedCompactMix = mix;
            Raise(nameof(SelectedCompactMix));
        }
        foreach (MixViewModel m in Mixes)
            m.DisplayVisible = m.Visible && (!IsMiniView || ReferenceEquals(m, mix));
        foreach (ChannelViewModel c in Channels)
        {
            c.DisplayVisible = c.Visible && (single ? ReferenceEquals(c, channel) : !c.Appearance.Hidden);
            foreach (SendViewModel send in c.Sends)
                send.DisplayVisible = send.Visible && (!IsMiniView || send.MixId == mix?.Id);
        }
    }
}
