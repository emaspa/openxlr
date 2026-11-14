using System.Text;
using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>One value a step changed, as it was before and as the step left it.</summary>
internal sealed record SceneChange(SceneKey Key, object Before, object After);

/// <summary>One undo step: what it is called and the values it changed.</summary>
internal sealed class HistoryStep
{
    public required long Id { get; init; }
    public required string Kind { get; init; }
    public string? Channel { get; init; }
    public string? Mix { get; init; }
    public string? Group { get; init; }
    public string? Insert { get; init; }
    public required string Label { get; init; }
    /// <summary>The connection the step came from; HTTP requests count as one.</summary>
    public required string Client { get; init; }
    /// <summary>When the newest command joined, on <see cref="MixHistory.Clock"/>.</summary>
    public long Last { get; set; }
    /// <summary>The distinct things the step changed, for a burst's count.</summary>
    public HashSet<string> Targets { get; } = new(StringComparer.Ordinal);
    public List<SceneChange> Changes { get; set; } = [];
    /// <summary>The serialized size of <see cref="Changes"/>, counted against <see cref="MixHistory.MaxBytes"/>.</summary>
    public long Bytes { get; set; }

    public HistoryStepInfo Info()
    {
        string label = Targets.Count > 1 ? $"{Label} and {Targets.Count - 1} more" : Label;
        return new(Id, Kind, Channel, Mix, Group, Math.Max(1, Targets.Count), label) { Insert = Insert };
    }
}

/// <summary>
/// The daemon's one undo history, shared by every client and held in
/// memory only. A recorded command becomes a step, or joins the newest step
/// from the same client: within <see cref="JoinSameValue"/> when it changes
/// a value that step holds, within <see cref="JoinBurst"/> whatever it
/// changes. A joined step keeps the first before value and the last after
/// value, and a step whose values end where they began goes. A new step
/// clears redo. At most <see cref="MaxSteps"/> steps and
/// <see cref="MaxBytes"/> of serialized values are kept, the oldest dropped
/// first. Applying a step is the hub's job; this class only holds them.
/// </summary>
internal sealed class MixHistory
{
    public const int MaxSteps = 50;
    public const long MaxBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan JoinSameValue = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan JoinBurst = TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();
    // Oldest first; the newest step is the last.
    private readonly List<HistoryStep> _undo = [];
    private readonly List<HistoryStep> _redo = [];
    // The step a following command may join, until an undo, a redo or a
    // clear closes it.
    private HistoryStep? _open;
    private long _nextId = 1;
    private string? _note;

    /// <summary>Milliseconds on a monotonic clock; a test sets its own.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>
    /// Record what a command changed. Returns true when the history changed,
    /// so the caller sends the state.
    /// </summary>
    public bool Record(string client, HistoryTarget target, string label, IReadOnlyList<SceneChange> changes)
    {
        if (changes.Count == 0) return false;
        lock (_gate)
        {
            long now = Clock();
            _note = null;
            if (_open is { } open && _undo.Count > 0 && ReferenceEquals(_undo[^1], open) && open.Client == client
                && (now - open.Last <= (long)JoinBurst.TotalMilliseconds
                    || now - open.Last <= (long)JoinSameValue.TotalMilliseconds && changes.Any(c => open.Changes.Any(o => o.Key == c.Key))))
            {
                Join(open, target, changes, now);
                if (open.Changes.Count == 0)
                {
                    _undo.RemoveAt(_undo.Count - 1);
                    _open = null;
                }
                Trim();
                return true;
            }
            var step = new HistoryStep
            {
                Id = _nextId++,
                Kind = target.Kind,
                Channel = target.Channel,
                Mix = target.Mix,
                Group = target.Group,
                Insert = target.Insert,
                Label = label,
                Client = client,
                Last = now,
                Changes = [.. changes],
            };
            step.Targets.Add(target.Identity);
            step.Bytes = SizeOf(step.Changes);
            _redo.Clear();
            _undo.Add(step);
            _open = step;
            Trim();
            return true;
        }
    }

    /// <summary>The step <c>undo</c> (or with <paramref name="redo"/>, <c>redo</c>) would take, or null.</summary>
    public HistoryStep? Peek(bool redo)
    {
        lock (_gate)
        {
            List<HistoryStep> stack = redo ? _redo : _undo;
            return stack.Count > 0 ? stack[^1] : null;
        }
    }

    /// <summary>
    /// Start an undo or redo: close the newest step, so no command joins it
    /// while it is written, and hand out the step <c>undo</c> (or with
    /// <paramref name="redo"/>, <c>redo</c>) takes with a copy of its values
    /// and its label, or null when that side is empty.
    /// </summary>
    public (HistoryStep Step, SceneChange[] Changes, string Label)? Begin(bool redo)
    {
        lock (_gate)
        {
            _open = null;
            List<HistoryStep> stack = redo ? _redo : _undo;
            if (stack.Count == 0) return null;
            HistoryStep step = stack[^1];
            return (step, [.. step.Changes], step.Info().Label);
        }
    }

    /// <summary>
    /// An undo or redo of <paramref name="step"/> wrote <paramref name="done"/>:
    /// the step moves to the other side holding only those values. With none,
    /// the step is dropped. <paramref name="note"/> is the sentence the state
    /// carries about what was skipped, or null. A step a barrier removed
    /// meanwhile stays gone.
    /// </summary>
    public void Complete(HistoryStep step, bool redo, IReadOnlyList<SceneChange> done, string? note)
    {
        lock (_gate)
        {
            _open = null;
            _note = note;
            List<HistoryStep> from = redo ? _redo : _undo;
            if (!from.Remove(step) || done.Count == 0) return;
            step.Changes = [.. done];
            step.Bytes = SizeOf(step.Changes);
            (redo ? _undo : _redo).Add(step);
            Trim();
        }
    }

    /// <summary>Empty both sides. Returns true when there was anything to empty.</summary>
    public bool Clear()
    {
        lock (_gate)
        {
            bool had = _undo.Count > 0 || _redo.Count > 0 || _note is not null;
            _undo.Clear();
            _redo.Clear();
            _open = null;
            _note = null;
            return had;
        }
    }

    /// <summary>The two ends of the history, for the state.</summary>
    public HistoryState State()
    {
        lock (_gate)
            return new(_undo.Count > 0 ? _undo[^1].Info() : null, _redo.Count > 0 ? _redo[^1].Info() : null,
                _undo.Count, _redo.Count, _note);
    }

    /// <summary>The serialized size of every step held, for the bound.</summary>
    internal long Bytes
    {
        get { lock (_gate) return _undo.Sum(s => s.Bytes) + _redo.Sum(s => s.Bytes); }
    }

    private static void Join(HistoryStep step, HistoryTarget target, IReadOnlyList<SceneChange> changes, long now)
    {
        step.Last = now;
        step.Targets.Add(target.Identity);
        foreach (SceneChange change in changes)
        {
            int index = step.Changes.FindIndex(c => c.Key == change.Key);
            if (index < 0) step.Changes.Add(change);
            else step.Changes[index] = step.Changes[index] with { After = change.After };
        }
        step.Changes.RemoveAll(c => SceneValues.Same(c.Before, c.After));
        step.Bytes = SizeOf(step.Changes);
    }

    // Oldest first. A new step clears redo, so the undo side is where the
    // old steps are; the newest step stays even when it alone is over the
    // byte bound.
    private void Trim()
    {
        while (_undo.Count + _redo.Count > MaxSteps && _undo.Count + _redo.Count > 1)
            (_undo.Count > 0 ? _undo : _redo).RemoveAt(0);
        long bytes = _undo.Sum(s => s.Bytes) + _redo.Sum(s => s.Bytes);
        while (bytes > MaxBytes && _undo.Count + _redo.Count > 1)
        {
            List<HistoryStep> side = _undo.Count > 1 || _redo.Count == 0 ? _undo : _redo;
            bytes -= side[0].Bytes;
            side.RemoveAt(0);
        }
    }

    private static long SizeOf(IReadOnlyList<SceneChange> changes)
    {
        long bytes = 0;
        foreach (SceneChange change in changes)
            bytes += Encoding.UTF8.GetByteCount(change.Key.Key) + 16
                + JsonSerializer.SerializeToUtf8Bytes(change.Before).Length
                + JsonSerializer.SerializeToUtf8Bytes(change.After).Length;
        return bytes;
    }
}
