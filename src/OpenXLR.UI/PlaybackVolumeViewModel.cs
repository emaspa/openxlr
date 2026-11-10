using System;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>Volume flyout of a row with a playback level; a website row can remember its level.</summary>
public sealed class PlaybackVolumeViewModel(Func<double?, bool, Task<string?>> send, bool website = false) : ViewModelBase
{
    private readonly record struct Choice(double? Level, bool Remember);

    private readonly string _key = Guid.NewGuid().ToString("N");
    private Choice _reported = new(null, false);
    private Choice? _pending;
    private long _version;
    private bool _supported, _enabled;
    private string? _error;

    public bool Supported => _supported;
    public bool Enabled => _enabled;
    public bool Website => website;
    public bool HasLevel => (_pending ?? _reported).Level is not null;
    public string Label => (_pending ?? _reported).Level is double level
        ? $"{level * 100:0}%" : Localizer.Text("PlaybackVolumeShort");
    public string? Hint => website ? Localizer.Text("TabPlaybackVolumeHint") : null;
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public double Level
    {
        get => (_pending ?? _reported).Level ?? 1;
        set
        {
            if (Enabled && double.IsFinite(value))
            {
                double volume = Math.Round(Math.Clamp(value, 0, 1) * 100) / 100;
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

    internal void Apply(bool supported, bool enabled, double? level, bool remember = false)
    {
        Set(ref _supported, supported, nameof(Supported));
        Set(ref _enabled, supported && enabled, nameof(Enabled));
        if (!Enabled && _pending is not null) Cancel();
        var state = new Choice(level, remember);
        if (_reported == state) return;
        _reported = state;
        if (_pending is null) Refresh();
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
        Raise(nameof(Level));
        Raise(nameof(Label));
        Raise(nameof(HasLevel));
        Raise(nameof(Remember));
    }
}
