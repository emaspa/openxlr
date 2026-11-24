using System;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>Volume flyout shared by the application and browser tab lists.</summary>
public sealed class PlaybackVolumeViewModel(Func<double?, bool, Task<string?>> send, bool website = false) : ViewModelBase
{
    private readonly record struct Choice(double? Level, bool Remember, bool Locked = false);

    internal Func<double, bool, Task<string?>>? SendLock { get; init; }

    private readonly string _key = Guid.NewGuid().ToString("N");
    private Choice _reported = new(null, false);
    private Choice? _pending;
    private long _version;
    private bool _supported, _enabled, _browserHeld;
    // What the row plays at now when nothing is saved for it: the desktop's
    // level, which the slider starts from and the button shows when different from 100%.
    private double? _live;
    private string? _error;

    public bool Supported => _supported;
    public bool Enabled => _enabled;
    public bool Website => website;
    public bool LockSupported { get; private set; }
    public bool Locked
    {
        get => (_pending ?? _reported).Locked;
        set
        {
            if (CanToggleLock && value != Locked)
                Edit(new(Level, Remember, value), changeLock: true);
        }
    }
    private bool LockPending => _pending is { } pending && pending.Locked != _reported.Locked;
    public bool CanToggleLock => Enabled && LockSupported && !LockPending;
    public bool CanAdjust => Enabled && !Locked && !LockPending;
    public bool CanReset => CanAdjust && HasLevel;
    public bool CanRemember => Website && CanReset;
    private bool _boost;
    public double Maximum => _boost ? 1.5 : Math.Max(1, Level);
    internal void ApplyBoost(bool boost)
    {
        if (Set(ref _boost, boost, nameof(Maximum))) Raise(nameof(Level));
    }

    public bool HasLevel => (_pending ?? _reported).Level is not null;
    /// <summary>The level the button shows; empty at full volume, saved or not, where it shows a speaker.</summary>
    public string Label => Math.Round(Level * 100) != 100 ? $"{Level * 100:0}%" : "";
    public string Hint => website ? Localizer.Text("TabPlaybackVolumeHint")
        : _browserHeld ? Localizer.Text("BrowserPlaybackVolumeHint") : Localizer.Text("AppPlaybackVolumeHint");
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public double Level
    {
        get => (_pending ?? _reported).Level ?? _live ?? 1;
        set
        {
            if (CanAdjust && double.IsFinite(value))
            {
                double volume = Math.Round(Math.Clamp(value, 0, Maximum) * 100) / 100;
                if (!HasLevel || volume != Level) Edit(new(volume, Remember));
            }
        }
    }

    public bool Remember
    {
        get => (_pending ?? _reported).Remember;
        set { if (CanAdjust && Website && HasLevel && value != Remember) Edit(new(Level, value)); }
    }

    public void Reset()
    {
        if (CanAdjust && HasLevel) Edit(new(null, Remember));
    }

    internal void Apply(bool supported, bool enabled, double? level, bool remember = false, bool browserHeld = false,
        double? live = null, bool? locked = null)
    {
        Set(ref _supported, supported, nameof(Supported));
        Set(ref _browserHeld, browserHeld, nameof(Hint));
        SetEnabled(enabled);
        bool lockSupported = locked.HasValue && SendLock is not null;
        if (LockSupported != lockSupported)
        {
            LockSupported = lockSupported;
            Raise(nameof(LockSupported));
            Raise(nameof(CanToggleLock));
        }
        var state = new Choice(level, remember, locked == true);
        if (_reported == state && _live == live) return;
        _reported = state;
        _live = live;
        if (_pending is null) Refresh();
    }

    internal void SetEnabled(bool enabled)
    {
        Set(ref _enabled, Supported && enabled, nameof(Enabled));
        Raise(nameof(CanToggleLock));
        Raise(nameof(CanAdjust));
        Raise(nameof(CanReset));
        Raise(nameof(CanRemember));
        if (!Enabled && _pending is not null) Cancel();
    }

    internal void Cancel()
    {
        _version++;
        SliderSync.Forget(_key);
        _pending = null;
        Error = null;
        Refresh();
    }

    private void Edit(Choice choice, bool changeLock = false)
    {
        Error = null;
        _pending = choice;
        long version = ++_version;
        Refresh();
        SliderSync.Send(_key, () => _ = SubmitAsync(choice, version, changeLock));
    }

    private async Task SubmitAsync(Choice choice, long version, bool changeLock)
    {
        string? error = changeLock
            ? await SendLock!(choice.Level!.Value, choice.Locked)
            : await send(choice.Level, choice.Remember);
        if (version != _version) return;
        // As with routing, the daemon sends state before acknowledgement.
        // Resetting a tab override may reveal a saved website level.
        _pending = null;
        Error = error;
        Refresh();
    }

    private void Refresh()
    {
        Raise(nameof(Maximum));
        Raise(nameof(Level));
        Raise(nameof(Locked));
        Raise(nameof(CanToggleLock));
        Raise(nameof(CanAdjust));
        Raise(nameof(CanReset));
        Raise(nameof(CanRemember));
        Raise(nameof(Label));
        Raise(nameof(HasLevel));
        Raise(nameof(Remember));
    }
}
