namespace OpenXLR.Core.Mixing;

internal sealed class InsertHolds(Func<long> now)
{
    internal const int MaxHolds = 128;
    internal const int LifetimeMs = 5000;
    internal sealed record Change(string Chain, string Insert, bool Bypass);
    private sealed record Hold(string Chain, string[] Inserts, long Expires);
    private readonly Dictionary<string, Hold> _holds = [];
    private readonly Dictionary<(string Chain, string Insert), bool> _saved = [];

    public IReadOnlyList<Change> Begin(string id, string chain, IReadOnlyList<InsertDefinition> inserts)
    {
        if (_holds.TryGetValue(id, out var existing))
        {
            if (existing.Chain != chain || !existing.Inserts.SequenceEqual(inserts.Select(i => i.Id)))
                throw new ArgumentException("That hold id already belongs to another effect action.");
            Renew(id);
            return [];
        }
        if (_holds.Count >= MaxHolds) throw new InvalidOperationException("Too many held effect actions.");
        if (inserts.Count == 0) throw new ArgumentException("The effect chain is empty.");
        var changes = new List<Change>();
        foreach (var insert in inserts)
        {
            if (_saved.TryAdd((chain, insert.Id), insert.Bypass) && insert.Bypass)
                changes.Add(new(chain, insert.Id, false));
        }
        _holds[id] = new(chain, [.. inserts.Select(i => i.Id)], now() + LifetimeMs);
        return changes;
    }
    public void Renew(string id)
    {
        if (_holds.TryGetValue(id, out var hold)) _holds[id] = hold with { Expires = now() + LifetimeMs };
    }
    public bool SavedBypass(string chain, string insert, bool fallback) => _saved.GetValueOrDefault((chain, insert), fallback);
    public IReadOnlyList<Change> End(string id)
    {
        if (!_holds.Remove(id, out var hold)) return [];
        var changes = new List<Change>();
        foreach (string insert in hold.Inserts)
        {
            if (_holds.Values.Any(h => h.Chain == hold.Chain && h.Inserts.Contains(insert))) continue;
            if (_saved.Remove((hold.Chain, insert), out bool bypass) && bypass)
                changes.Add(new(hold.Chain, insert, true));
        }
        return changes;
    }
    public IReadOnlyList<Change> Expire()
        => EndWhere(h => h.Expires <= now());
    public IReadOnlyList<Change> Cancel(string? chain = null)
        => EndWhere(h => chain is null || h.Chain == chain);
    private IReadOnlyList<Change> EndWhere(Func<Hold, bool> predicate)
    {
        var changes = new List<Change>();
        foreach (string id in _holds.Where(p => predicate(p.Value)).Select(p => p.Key).ToArray()) changes.AddRange(End(id));
        return changes;
    }
}

public sealed partial class Mixer
{
    private readonly InsertHolds _insertHolds = new(() => Environment.TickCount64);

    /// <summary>Transient activation, restored after release or a lost client's lease expires.</summary>
    public bool HoldInsert(string holdId, string action, string? channel, string? insertId)
    {
        lock (_gate)
        {
            bool changed = ApplyHoldChangesLocked(_insertHolds.Expire());
            IReadOnlyList<InsertHolds.Change> changes;
            switch (action)
            {
                case "begin":
                    if (!_built) throw new InvalidOperationException("The mixer is not running.");
                    var inserts = InsertsFor(channel!);
                    var selected = insertId is null ? inserts : inserts.Where(i => i.Id == insertId).ToList();
                    changes = _insertHolds.Begin(holdId, channel!, selected);
                    break;
                case "renew": _insertHolds.Renew(holdId); return changed;
                case "end": changes = _insertHolds.End(holdId); break;
                default: throw new ArgumentException("Unknown effect hold action.");
            }
            changed = ApplyHoldChangesLocked(changes) || changed;
            if (action == "begin") _insertHolds.Renew(holdId); // Loading effects may take longer than one lease.
            return changed;
        }
    }

    private bool ApplyHoldChangesLocked(IReadOnlyList<InsertHolds.Change> changes, bool rewire = true)
    {
        var keys = new HashSet<string>();
        foreach (var change in changes)
        {
            if (!_inserts.TryGetValue(change.Chain, out var inserts)) continue;
            int index = inserts.FindIndex(i => i.Id == change.Insert);
            if (index < 0 || inserts[index].Bypass == change.Bypass) continue;
            inserts[index] = inserts[index] with { Bypass = change.Bypass };
            keys.Add(change.Chain);
        }
        if (rewire && _built)
            foreach (string key in keys) RewireInsertKeyLocked(key);
        return keys.Count > 0;
    }
}
