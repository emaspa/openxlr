namespace OpenXLR.Core.Mixing;

/// <summary>
/// How often one insert chain may be rebuilt for dying on its own. A plugin
/// that crashes the moment it starts would otherwise be started again on
/// every sweep, and rebuilding an input chain interrupts the microphone, so a
/// chain that keeps failing is left off with an error until it is changed.
/// Nothing retries it when the window passes, so a plugin that always crashes
/// cannot keep cutting the microphone out; the window only decides whether a
/// later rebuild of the route, such as a recall, includes the chain again.
/// The EQ, ducking and delay stages share this policy, and their own sweeps
/// do start them again once the window has passed.
/// </summary>
internal sealed class RestartPolicy(Func<long> clock, int limit = 3, long windowMs = 300_000)
{
    /// <summary>What a client sees for a chain that has been given up on.</summary>
    internal const string GivenUp = "this chain kept failing and is off; change or bypass its plugins to try again";

    /// <summary>How much of a helper's last line is shown beside the message.</summary>
    internal const int ReasonCap = 200;

    private readonly Dictionary<string, (int Count, long Since, string? Reason)> _failures = new(StringComparer.Ordinal);

    /// <summary>
    /// Record that a chain died on its own, not by anyone's request.
    /// <paramref name="reason"/> is what its helper last said, when it said
    /// anything; the latest one is kept for <see cref="Message"/>.
    /// </summary>
    public void Failed(string key, string? reason = null)
    {
        long now = clock();
        if (!_failures.TryGetValue(key, out (int Count, long Since, string? Reason) seen) || now - seen.Since > windowMs)
            _failures[key] = (1, now, reason);
        else
            _failures[key] = (Math.Min(seen.Count + 1, limit), seen.Since, reason ?? seen.Reason);
        Noted?.Invoke(key, reason, Blocked(key));
    }

    /// <summary>
    /// Told of each death <see cref="Failed"/> records: the chain's key, what
    /// its helper last said, and whether the chain is now given up on.
    /// </summary>
    public Action<string, string?, bool>? Noted { get; set; }

    /// <summary>Whether a chain has failed too often to be worth building again.</summary>
    public bool Blocked(string key)
        => _failures.TryGetValue(key, out (int Count, long Since, string? Reason) seen)
            && seen.Count >= limit && clock() - seen.Since <= windowMs;

    /// <summary>
    /// What a client sees for a chain that has been given up on: the fixed
    /// message, and the helper's last line when one was recorded, since
    /// "kept failing" alone says nothing about why.
    /// </summary>
    public string Message(string key) => Message(key, GivenUp);

    /// <summary>The same, for a stage that says something other than <see cref="GivenUp"/> when given up on.</summary>
    public string Message(string key, string givenUp)
    {
        string? reason = _failures.TryGetValue(key, out (int Count, long Since, string? Reason) seen) ? seen.Reason?.Trim() : null;
        if (string.IsNullOrEmpty(reason)) return givenUp;
        if (reason.Length > ReasonCap) reason = reason[..ReasonCap];
        return $"{givenUp}. The plugin host's last message: {reason}";
    }

    /// <summary>Forget a chain's history: the user changed it, so it is new again.</summary>
    public void Forget(string key) => _failures.Remove(key);
}
