namespace OpenXLR.Daemon;

/// <summary>
/// What to do with a device whose USB transfers keep hanging. Each hung
/// transfer abandons a native handle and a parked thread (see LibUsb), so
/// reconnecting for ever leaks them for ever; after <see cref="Limit"/>
/// hangs in one run the device is set aside: the daemon stops driving it,
/// keeps the mixer and any other interface alive, and says so in the
/// state. Unplugging the device and plugging it back in (its firmware
/// restarts) or restarting the daemon gives it a fresh count.
/// </summary>
public sealed class HungTransferPolicy
{
    public const int Limit = 3;

    private readonly Dictionary<ushort, int> _hung = [];
    private readonly HashSet<ushort> _setAside = [];

    /// <summary>Record a hung transfer; true when this one crossed the limit.</summary>
    public bool NoteHung(ushort productId)
    {
        int n = _hung.GetValueOrDefault(productId) + 1;
        _hung[productId] = n;
        if (n < Limit) return false;
        _setAside.Add(productId);
        return true;
    }

    public int HungCount(ushort productId) => _hung.GetValueOrDefault(productId);

    public bool IsSetAside(ushort productId) => _setAside.Contains(productId);

    /// <summary>The device left the bus and came back: its firmware restarted, so it gets a fresh count.</summary>
    public void Returned(ushort productId)
    {
        _hung.Remove(productId);
        _setAside.Remove(productId);
    }

    public IEnumerable<ushort> SetAside => _setAside;
}
