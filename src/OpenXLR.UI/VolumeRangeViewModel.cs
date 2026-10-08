using System;

namespace OpenXLR.UI;

/// <summary>
/// Explicit desktop-style boost range. Incoming boosted state opens the range
/// before a bound slider sees the value, so it cannot coerce it back to 100%.
/// </summary>
public sealed class VolumeRangeViewModel(Action limitToUnity) : ViewModelBase
{
    private bool _boost;
    public bool Boost
    {
        get => _boost;
        set
        {
            if (_boost == value) return;
            Apply(value);
            Changed?.Invoke(value);
        }
    }

    /// <summary>A change made in the window, as opposed to one applied from the desktop.</summary>
    internal event Action<bool>? Changed;

    /// <summary>Sets the range from the desktop without echoing it back as a change.</summary>
    internal void Apply(bool boost)
    {
        if (!Set(ref _boost, boost, nameof(Boost))) return;
        if (!boost) limitToUnity();
        Raise(nameof(Maximum));
    }

    public double Maximum => Boost ? 1.5 : 1;

    public void Include(double volume)
    {
        if (volume > 1) Boost = true;
    }
}
