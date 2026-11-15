namespace OpenXLR.Core;

/// <summary>
/// A value computed once and kept, like a Lazy, that can be computed again.
/// A refresh builds the new value aside and swaps it in once it is complete,
/// so reads during a refresh get the old value without waiting for it. Only
/// a read before the first value exists waits for a computation.
/// </summary>
public sealed class Refreshable<T>
{
    private readonly Func<T> _compute;
    private readonly Lock _refreshing = new();
    private Lazy<T> _current;

    public Refreshable(Func<T> compute)
    {
        _compute = compute;
        _current = Fresh();
    }

    private Lazy<T> Fresh() => new(_compute, LazyThreadSafetyMode.ExecutionAndPublication);

    public T Value => Volatile.Read(ref _current).Value;

    /// <summary>
    /// Compute the value again on the calling thread and publish it when it
    /// is done. A computation that throws is published the way a first read
    /// keeps it: every read after rethrows, until the next refresh. Refreshes
    /// run one at a time.
    /// </summary>
    public void Refresh()
    {
        lock (_refreshing)
        {
            Lazy<T> next = Fresh();
            try { _ = next.Value; }
            catch (Exception) { /* kept in next, and rethrown to its readers */ }
            Volatile.Write(ref _current, next);
        }
    }
}
