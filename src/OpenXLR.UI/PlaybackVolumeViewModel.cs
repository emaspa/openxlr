using System;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>Volume flyout shared by the application and browser tab lists.</summary>
public sealed class PlaybackVolumeViewModel(Func<double?, bool, Task<string?>> send, bool website = false) : ViewModelBase
{
    private readonly record struct Choice(double? Level, bool Remember);

    private readonly string _key = Guid.NewGuid().ToString("N");
    private Choice _reported = new(null, false);
    private Choice? _pending;
    private long _version;
    private bool _supported, _enabled, _browserHeld;
    // What the row plays at now when nothing is saved for it: the desktop's
    // level, which the slider starts from and the button shows below 100%.
    private double? _live;
    private string? _error;

    public bool Supported => _supported;
    public bool Enabled => _enabled;
    public bool Website => website;
    private bool _boost, _companionBoost;
    private double _supportedMaximum = 1;
    // The most a tab plays at when the browser itself is at 100% or less;
    // the daemon lowers it while the browser plays louder.
    private const double TabBoostCeiling = 1.25;

    public double Maximum => Math.Max(Level, _boost ? _supportedMaximum : 1);
    private bool NeedsCompanionUpdate => Website && _boost && !_companionBoost;
    private bool HeldByBrowserLevel => Website && _boost && _companionBoost && _supportedMaximum < TabBoostCeiling;

    internal void ApplyBoost(bool boost)
    {
        if (!Set(ref _boost, boost, nameof(Maximum))) return;
        Raise(nameof(Hint));
    }

    public bool HasLevel => (_pending ?? _reported).Level is not null;
    /// <summary>The level the button shows; empty at full volume, saved or not, where it shows a speaker.</summary>
    public string Label => Math.Round(Level * 100) != 100 ? $"{Level * 100:0}%" : "";
    public string Hint => NeedsCompanionUpdate ? Localizer.Text("TabPlaybackBoostUnavailable")
        : HeldByBrowserLevel ? Localizer.Text("TabPlaybackBoostHeldByBrowser")
        : website ? Localizer.Text("TabPlaybackVolumeHint")
        : _browserHeld ? Localizer.Text("BrowserPlaybackVolumeHint") : Localizer.Text("AppPlaybackVolumeHint");
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public double Level
    {
        get => (_pending ?? _reported).Level ?? _live ?? 1;
        set
        {
            if (Enabled && double.IsFinite(value))
            {
                double volume = Math.Round(Math.Clamp(value, 0, Maximum) * 100) / 100;
                if (!HasLevel || volume != Level) Edit(new(volume, Remember));
            }
        }
    }

    public bool Remember
    {
        get => (_pending ?? _reported).Remember;
        set { if (Enabled && Website && HasLevel && value != Remember) Edit(new(Level, value)); }
    }

    public void Reset()
    {
        if (Enabled && HasLevel) Edit(new(null, Remember));
    }

    internal void Apply(bool supported, bool enabled, double? level, bool remember = false, bool browserHeld = false,
        double? live = null, double supportedMaximum = 1, bool companionBoost = false)
    {
        Set(ref _supported, supported, nameof(Supported));
        Set(ref _browserHeld, browserHeld, nameof(Hint));
        SetEnabled(enabled);
        var state = new Choice(level, remember);
        bool maximumChanged = _supportedMaximum != supportedMaximum || _companionBoost != companionBoost;
        if (_reported == state && _live == live && !maximumChanged) return;
        _supportedMaximum = supportedMaximum;
        _companionBoost = companionBoost;
        _reported = state;
        _live = live;
        if (maximumChanged) { Raise(nameof(Hint)); Raise(nameof(Maximum)); }
        if (_pending is null) Refresh();
    }

    internal void SetEnabled(bool enabled)
    {
        Set(ref _enabled, Supported && enabled, nameof(Enabled));
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

    private void Edit(Choice choice)
    {
        Error = null;
        _pending = choice;
        long version = ++_version;
        Refresh();
        SliderSync.Send(_key, () => _ = SubmitAsync(choice, version));
    }

    private async Task SubmitAsync(Choice choice, long version)
    {
        string? error = await send(choice.Level, choice.Remember);
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
        Raise(nameof(Label));
        Raise(nameof(HasLevel));
        Raise(nameof(Remember));
    }
}
