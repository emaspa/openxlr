using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>A mix's stable routing id and its current display name.</summary>
public sealed record MixOption(string Id, string Name);

/// <summary>One mix in an output's multi-select feed picker.</summary>
public sealed class MonitorMixChoice : ViewModelBase
{
    private readonly Action _changed;
    private bool _isSelected;
    private bool _canToggle;

    internal MonitorMixChoice(MixOption mix, Action changed) { Id = mix.Id; Name = mix.Name; _changed = changed; }
    public string Id { get; }
    public string Name { get; }
    public bool CanToggle => _canToggle;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) _changed(); }
    }

    internal void Sync(bool selected, bool canToggle)
    {
        Set(ref _isSelected, selected, nameof(IsSelected));
        Set(ref _canToggle, canToggle, nameof(CanToggle));
    }
}

/// <summary>An output selection and the existing mix ids summed into it at unity.</summary>
public sealed class MonitorOutputItem : ViewModelBase
{
    private readonly Action _changed;
    private readonly Func<string, string, Task<string?>> _feedChanged;
    private readonly Action<string>? _reportError;
    private readonly Func<string, bool, Task<string?>>? _idleReleaseChanged;
    private MixOption? _feed;
    private string _label;
    private bool _isPresent;
    private bool _isSelected;
    private string? _pendingFeed;
    private string? _reportedFeed;
    private long _editVersion;
    private bool _idleRelease;
    private bool _reportedIdleRelease;
    private bool? _pendingIdleRelease;
    private long _idleVersion;

    public MonitorOutputItem(string name, string label, Action changed, Func<string, string, Task<string?>> feedChanged,
        bool isPresent = true, Action<string>? reportError = null, Func<string, bool, Task<string?>>? idleReleaseChanged = null)
    {
        Name = name;
        _label = label;
        _isPresent = isPresent;
        _changed = changed;
        _feedChanged = feedChanged;
        _reportError = reportError;
        _idleReleaseChanged = idleReleaseChanged;
    }

    public string Name { get; }
    public string Label => _label;
    internal void SyncLabel(string label) => Set(ref _label, label, nameof(Label));
    /// <summary>A missing selected output remains configured until the user removes it.</summary>
    public bool IsPresent => _isPresent;

    /// <summary>
    /// The device left or came back. The row stays the same object, so a
    /// feed edit still waiting for the daemon keeps its answer, and an error
    /// in it still reaches the status line.
    /// </summary>
    internal void SyncPresent(bool present) => Set(ref _isPresent, present, nameof(IsPresent));
    public ObservableCollection<MonitorMixChoice> FeedMixes { get; } = [];
    public MixOption? Feed => _feed;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value)) return;
            if (!value) { CancelFeedEdit(); CancelIdleReleaseEdit(); }
            _changed();
        }
    }

    public void Sync(bool selected)
    {
        if (!selected) { CancelFeedEdit(); CancelIdleReleaseEdit(); }
        Set(ref _isSelected, selected, nameof(IsSelected));
    }

    /// <summary>A Bluetooth output, the only kind offered the idle release.</summary>
    public bool IsBluetooth => Name.StartsWith("bluez_output.", StringComparison.Ordinal);

    /// <summary>
    /// The output's link comes down while its feed is silent, so multipoint
    /// headphones can switch to another device. The daemon refuses it for
    /// anything but a selected Bluetooth output.
    /// </summary>
    public bool IdleRelease
    {
        get => _idleRelease;
        set
        {
            if (!Set(ref _idleRelease, value) || _idleReleaseChanged is null || !IsSelected || !IsBluetooth) return;
            _pendingIdleRelease = value;
            _ = SubmitIdleReleaseAsync(value, ++_idleVersion);
        }
    }

    /// <summary>Apply the daemon's value unless an edit is still waiting for its answer.</summary>
    internal void SyncIdleRelease(bool enabled)
    {
        _reportedIdleRelease = enabled;
        Set(ref _idleRelease, _pendingIdleRelease ?? enabled, nameof(IdleRelease));
    }

    private void CancelIdleReleaseEdit() { _idleVersion++; _pendingIdleRelease = null; }

    private async Task SubmitIdleReleaseAsync(bool enabled, long version)
    {
        string? error = await _idleReleaseChanged!(Name, enabled);
        if (version != _idleVersion) return;
        _pendingIdleRelease = null;
        if (error is null) return;
        Set(ref _idleRelease, _reportedIdleRelease, nameof(IdleRelease));
        _reportError?.Invoke(error);
    }

    internal void CancelFeedEdit() { _editVersion++; _pendingFeed = null; }

    /// <summary>Apply authoritative state without echoing commands or rebuilding unchanged picker rows.</summary>
    public void SyncFeed(IReadOnlyList<MixOption> options, string? mixId)
    {
        _reportedFeed = mixId;
        if (_pendingFeed is { } pending && pending.Split('+').Any(id => !options.Any(o => o.Id == id))) CancelFeedEdit();
        if (!options.Select(o => (o.Id, o.Name)).SequenceEqual(FeedMixes.Select(f => (f.Id, f.Name))))
        {
            FeedMixes.Clear();
            foreach (MixOption option in options) FeedMixes.Add(new(option, OnFeedChanged));
        }
        var selected = ((_pendingFeed ?? mixId)?.Split('+',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (MonitorMixChoice choice in FeedMixes) choice.Sync(selected.Contains(choice.Id), true);
        UpdateFeed();
    }

    private void OnFeedChanged()
    {
        // Disconnecting is the output checkbox's job. Refuse an empty sum,
        // including programmatic changes while the final choice is disabled.
        if (!FeedMixes.Any(f => f.IsSelected))
        {
            var previous = (_feed?.Id.Split('+') ?? []).ToHashSet(StringComparer.Ordinal);
            foreach (MonitorMixChoice choice in FeedMixes) choice.Sync(previous.Contains(choice.Id), true);
            UpdateFeed();
            return;
        }
        UpdateFeed();
        if (IsSelected && _feed is { } feed)
        {
            _pendingFeed = feed.Id;
            _ = SubmitFeedAsync(feed.Id, ++_editVersion);
        }
    }

    private async Task SubmitFeedAsync(string feed, long version)
    {
        string? error = await _feedChanged(Name, feed);
        if (version != _editVersion) return;
        _pendingFeed = null;
        if (error is not null)
        {
            SyncFeed([.. FeedMixes.Select(f => new MixOption(f.Id, f.Name))], _reportedFeed);
            _reportError?.Invoke(error);
        }
    }

    private void UpdateFeed()
    {
        List<MonitorMixChoice> selected = [.. FeedMixes.Where(f => f.IsSelected)];
        foreach (MonitorMixChoice choice in FeedMixes)
            choice.Sync(choice.IsSelected, !choice.IsSelected || selected.Count > 1);
        MixOption? feed = selected.Count == 0 ? null : new(string.Join('+', selected.Select(f => f.Id)),
            MainViewModel.SummedName(selected.Select(f => f.Name)));
        Set(ref _feed, feed, nameof(Feed));
    }
}
