namespace OpenXLR.Daemon;

/// <summary>
/// What to do with a device whose USB transfers keep hanging. Each hung
/// transfer abandons a native handle and a parked thread (see LibUsb), so
/// reconnecting for ever leaks them for ever; after <see cref="Limit"/>
/// hangs in one run the device is set aside: the daemon stops driving it,
/// keeps the mixer and any other interface alive, and says so in the
/// state. Unplugging the device and plugging it back in (its firmware
/// restarts) or restarting the daemon gives it a fresh count. Nothing
/// leaks meanwhile: each hang costs the USB helper process, which is
/// killed and started again. Units are counted by instance id, so one
/// faulty unit does not set aside another unit of the same model.
/// </summary>
public sealed class HungTransferPolicy
{
    public const int Limit = 3;

    private readonly Dictionary<string, int> _hung = [];
    private readonly HashSet<string> _setAside = [];

    /// <summary>Record a hung transfer; true when this one crossed the limit.</summary>
    public bool NoteHung(string instanceId)
    {
        int n = _hung.GetValueOrDefault(instanceId) + 1;
        _hung[instanceId] = n;
        if (n < Limit) return false;
        _setAside.Add(instanceId);
        return true;
    }

    public int HungCount(string instanceId) => _hung.GetValueOrDefault(instanceId);

    public bool IsSetAside(string instanceId) => _setAside.Contains(instanceId);

    /// <summary>The device left the bus and came back: its firmware restarted, so it gets a fresh count.</summary>
    public void Returned(string instanceId)
    {
        _hung.Remove(instanceId);
        _setAside.Remove(instanceId);
    }

    public IEnumerable<string> SetAside => _setAside;
}
