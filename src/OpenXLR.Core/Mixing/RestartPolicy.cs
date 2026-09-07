namespace OpenXLR.Core.Mixing;

/// <summary>
/// How often one insert chain may be rebuilt for dying on its own. A plugin
/// that crashes the moment it starts would otherwise be started again on
/// every sweep, and rebuilding an input chain interrupts the microphone, so a
/// chain that keeps failing is left off with an error until it is changed or
/// the window passes.
/// </summary>
internal sealed class RestartPolicy(Func<long> clock, int limit = 3, long windowMs = 300_000)
{
    /// <summary>What a client sees for a chain that has been given up on.</summary>
    internal const string GivenUp = "this chain kept failing and is off; change or bypass its plugins to try again";

    private readonly Dictionary<string, (int Count, long Since)> _failures = new(StringComparer.Ordinal);

    /// <summary>Record that a chain died on its own, not by anyone's request.</summary>
    public void Failed(string key)
    {
        long now = clock();
        if (!_failures.TryGetValue(key, out (int Count, long Since) seen) || now - seen.Since > windowMs)
            _failures[key] = (1, now);
        else if (seen.Count < limit)
            _failures[key] = (seen.Count + 1, seen.Since);
    }

    /// <summary>Whether a chain has failed too often to be worth building again.</summary>
    public bool Blocked(string key)
        => _failures.TryGetValue(key, out (int Count, long Since) seen)
            && seen.Count >= limit && clock() - seen.Since <= windowMs;

    /// <summary>Forget a chain's history: the user changed it, so it is new again.</summary>
    public void Forget(string key) => _failures.Remove(key);
}
