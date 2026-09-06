namespace OpenXLR.Daemon;

/// <summary>
/// A per-client token bucket for commands. A fader drag sends a few dozen
/// commands a second and the deck plugin bursts a stack at once, so the
/// bucket is generous; only a client that never stops (a bug, or a page
/// that got past the origin check) runs dry and is disconnected.
/// </summary>
public sealed class CommandBudget
{
    private readonly int _capacity;
    private readonly double _refillPerSecond;
    private readonly Func<long> _clock;   // monotonic milliseconds
    private double _tokens;
    private long _last;

    public CommandBudget(int capacity = 300, double refillPerSecond = 100, Func<long>? clock = null)
    {
        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
        _clock = clock ?? (() => Environment.TickCount64);
        _tokens = capacity;
        _last = _clock();
    }

    public bool TryTake()
    {
        long now = _clock();
        _tokens = Math.Min(_capacity, _tokens + (now - _last) / 1000.0 * _refillPerSecond);
        _last = now;
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }
}
