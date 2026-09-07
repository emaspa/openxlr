namespace OpenXLR.Core;

/// <summary>
/// A value computed once and kept, like a Lazy, that can be thrown away so
/// the next read computes it again. Reads during a reset get either the old
/// value or the new one, never an exception from the swap.
/// </summary>
public sealed class Refreshable<T>
{
    private readonly Func<T> _compute;
    private Lazy<T> _current;

    public Refreshable(Func<T> compute)
    {
        _compute = compute;
        _current = Fresh();
    }

    private Lazy<T> Fresh() => new(_compute, LazyThreadSafetyMode.ExecutionAndPublication);

    public T Value => Volatile.Read(ref _current).Value;

    /// <summary>Forget the value; the next read computes it again.</summary>
    public void Reset() => Volatile.Write(ref _current, Fresh());
}
