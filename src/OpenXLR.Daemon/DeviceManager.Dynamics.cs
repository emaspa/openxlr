using Microsoft.Extensions.Logging;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// The microphone compressor and expander on the unit's own DSP, posted,
/// written and read back the way the EQ is (<see cref="PostEq"/>): the mixer
/// posts what each input should hold, the device loop writes it at the start
/// of a sweep at most once per <see cref="EqWriteSpacing"/> and never while
/// phantom power settles, and the poll in the same sweep reads it back. A
/// value the unit does not keep is written once more, then the input fails
/// and its microphone's compressor and expander run in software. A verified
/// value another program changes is written back at most every
/// <see cref="EqRewriteSpacing"/>.
///
/// While the mixer drives an input's compressor and expander, the unit's own
/// <c>compressor</c> and <c>expander</c> controls are the microphone's: the
/// device state no longer offers them and <c>set</c> refuses them, so two
/// clients cannot pull the same bits two ways.
/// </summary>
public sealed partial class DeviceManager
{
    private sealed class DynamicsSlot
    {
        public DeviceDynamics? Pending;   // the latest post, not written yet
        public DeviceDynamics? Wanted;    // what the input should hold: the last post
        public DeviceDynamics? Written;   // written, or found already held; the next poll reads it back
        public DeviceDynamics? Verified;  // read back equal
        public string? Failed;
        public string Owner = "";
        public int Misses;
        public bool Settled;              // the first write of this connection is done
        public long RewriteNotBefore;
    }

    private readonly object _dynamicsGate = new();
    private readonly Dictionary<int, DynamicsSlot> _dynamicsSlots = [];
    private long _dynamicsLastWrite = long.MinValue / 2;
    private readonly HashSet<string> _dynamicsElsewhereLogged = [];
    // Whether the published state already hides the unit's own switches.
    private bool _dynamicsOwnedPublished;

    /// <summary>What a unit that did not keep a written compressor and expander reports, after the unit's name.</summary>
    internal const string DynamicsNotKept = "did not keep the compressor and expander written to it";

    /// <summary>Whether the mixer drives the unit's compressor and expander: something was posted in this connection and not forgotten since.</summary>
    private bool DynamicsOwned
    {
        get { lock (_dynamicsGate) return _dynamicsSlots.Count > 0; }
    }

    /// <summary>
    /// The unit's controls as clients may use them: its own, without the
    /// <c>compressor</c> and <c>expander</c> switches while the mixer drives
    /// them as the microphone's.
    /// </summary>
    private DeviceCapabilities EffectiveCapabilitiesLocked(IAudioDevice dev)
        => dev.Capabilities.HardwareDynamics && DynamicsOwned
            ? dev.Capabilities with { Compressor = false, Expander = false }
            : dev.Capabilities;

    /// <summary>
    /// The compressor and expander one input of the driven unit should hold,
    /// replacing a value not written yet. A value equal to the one the unit was
    /// last read holding is not written again. Dropped while no unit is driven.
    /// </summary>
    internal void PostDynamics(int input, DeviceDynamics dynamics, string owner)
    {
        if (_published.Info is null || input < 1 || input > (_published.Message.Capabilities?.XlrInputs ?? 1)) return;
        bool wake;
        lock (_dynamicsGate)
        {
            if (!_dynamicsSlots.TryGetValue(input, out DynamicsSlot? slot)) _dynamicsSlots[input] = slot = new DynamicsSlot();
            slot.Owner = owner;
            slot.Wanted = dynamics;
            if (slot.Failed is null && slot.Pending is null && slot.Written is null && dynamics.Equals(slot.Verified)) return;
            slot.Pending = dynamics;
            slot.Failed = null;
            slot.Misses = 0;
            wake = NowMs - _dynamicsLastWrite >= (long)EqWriteSpacing.TotalMilliseconds;
        }
        if (wake) Wake();
    }

    /// <summary>
    /// No microphone holds this input any more (<see cref="IDeviceEqPort.ForgetDynamics"/>):
    /// its post is dropped, so nothing is written back, and the next sweep
    /// offers the unit's own switches again once no input is held.
    /// </summary>
    internal void ForgetDynamics(int input)
    {
        bool forgot;
        lock (_dynamicsGate) forgot = _dynamicsSlots.Remove(input);
        if (forgot) Wake();
    }

    /// <summary>Where the last compressor and expander post to one input stands.</summary>
    internal DeviceDynamicsStatus DynamicsStatus(int input)
    {
        if (_published.Info is null) return DeviceDynamicsStatus.None;
        lock (_dynamicsGate)
        {
            if (!_dynamicsSlots.TryGetValue(input, out DynamicsSlot? slot)) return DeviceDynamicsStatus.None;
            if (slot.Failed is { } reason) return new(DeviceEqPhase.Failed, null, reason);
            if (slot.Pending is not null || slot.Written is not null) return new(DeviceEqPhase.Pending);
            return slot.Verified is { } held ? new(DeviceEqPhase.Verified, held) : DeviceDynamicsStatus.None;
        }
    }

    /// <summary>
    /// The compressor and expander the input held at the last read, raw;
    /// null when not known. Kept by the device loop, so the mixer asking
    /// never waits on a USB transfer.
    /// </summary>
    internal DeviceDynamics? HeldDynamics(int input)
    {
        lock (_dynamicsGate) return _heldDynamics.GetValueOrDefault(input);
    }

    // What each input held at the last read: kept at publish and after each poll.
    private readonly Dictionary<int, DeviceDynamics> _heldDynamics = [];

    private void KeepHeldDynamicsLocked(IAudioDevice dev)
    {
        if (dev.DynamicsSupport?.Ready != true) return;
        lock (_dynamicsGate)
            for (int input = 1; input <= dev.Capabilities.XlrInputs; input++)
                if (dev.ReadDynamics(input) is { } held) _heldDynamics[input] = held;
    }

    private void ForgetDynamics()
    {
        bool had;
        lock (_dynamicsGate)
        {
            had = _dynamicsSlots.Count > 0;
            _dynamicsSlots.Clear();
            _heldDynamics.Clear();
        }
        if (had) EqStatusChanged?.Invoke();
    }

    /// <summary>
    /// Write what was posted since the last write, by the rules of
    /// <see cref="WriteEq"/>. The first post of a connection also takes the
    /// unit's own switches out of the published controls.
    /// </summary>
    private void WriteDynamics()
    {
        lock (_gate)
        {
            if (_device is not { Connected: true } dev || dev.DynamicsSupport?.Ready != true) return;
            bool owned = DynamicsOwned;
            if (owned != _dynamicsOwnedPublished)
            {
                _dynamicsOwnedPublished = owned;
                RaiseFromLocked();
            }
            long now = NowMs;
            if (now - _dynamicsLastWrite < (long)EqWriteSpacing.TotalMilliseconds) return;
            if (_last is { PhantomSettling: true } or { PhantomSettling2: true }) return;
            List<(int Input, DynamicsSlot Slot, DeviceDynamics Value)> due = [];
            lock (_dynamicsGate)
                foreach ((int input, DynamicsSlot slot) in _dynamicsSlots)
                    if (slot.Pending is { } value) due.Add((input, slot, value));
            if (due.Count == 0) return;
            bool failed = false;
            foreach ((int input, DynamicsSlot slot, DeviceDynamics value) in due)
            {
                if (input > dev.Capabilities.XlrInputs)
                {
                    lock (_dynamicsGate) _dynamicsSlots.Remove(input);
                    continue;
                }
                bool wrote;
                try { wrote = dev.SetDynamics(input, value); }
                catch (UsbHungException) { throw; }
                catch (Exception ex)
                {
                    _log.LogDebug("compressor and expander write to {dev} input {input}: {msg}", dev.Info.DisplayName, input, ex.Message);
                    lock (_dynamicsGate)
                    {
                        if (!ReferenceEquals(slot.Pending, value)) continue;
                        if (++slot.Misses >= 2) { slot.Pending = null; slot.Failed = $"{DynamicsNotKept} ({ex.Message})"; failed = true; }
                    }
                    continue;
                }
                _dynamicsLastWrite = now;
                bool first;
                lock (_dynamicsGate)
                {
                    if (ReferenceEquals(slot.Pending, value)) slot.Pending = null;
                    slot.Written = value;
                    first = !slot.Settled;
                    slot.Settled = true;
                }
                if (!wrote) continue;
                if (first)
                    _log.LogInformation(value.SwitchOnly
                        ? "{owner}'s compressor and expander are not the {model}'s to run, so its own are switched off."
                        : "{owner}'s compressor and expander were written to the {model}, replacing what it held.", slot.Owner, dev.Info.Model);
                else _log.LogDebug("compressor and expander written to {dev} input {input}: {value}", dev.Info.DisplayName, input, value);
            }
            _dynamicsLastWrite = now;
            if (failed) EqStatusChanged?.Invoke();
        }
    }

    /// <summary>After a poll read the block: what was written must read back equal, by the rules of <see cref="VerifyEqLocked"/>.</summary>
    private void VerifyDynamicsLocked(IAudioDevice dev)
    {
        if (dev.DynamicsSupport?.Ready != true) return;
        KeepHeldDynamicsLocked(dev);
        bool settled = false, rewrite = false;
        long now = NowMs;
        lock (_dynamicsGate)
        {
            foreach ((int input, DynamicsSlot slot) in _dynamicsSlots)
            {
                if (dev.ReadDynamics(input) is not { } held) continue;
                if (slot.Written is { } written)
                {
                    slot.Written = null;
                    if (written.HeldBy(held))
                    {
                        slot.Verified = written;
                        slot.Misses = 0;
                        settled = true;
                    }
                    else if (++slot.Misses >= 2)
                    {
                        slot.Pending = null;
                        slot.Failed = DynamicsNotKept;
                        settled = true;
                        _log.LogWarning("{dev} {reason}: input {input} read back {held} after {wanted}; {owner}'s compressor and expander run in software.",
                            dev.Info.DisplayName, DynamicsNotKept, input, held, written, slot.Owner);
                    }
                    else slot.Pending ??= written;
                }
                else if (slot.Pending is null && slot.Failed is null && slot.Verified is { } verified && !verified.HeldBy(held)
                         && now >= slot.RewriteNotBefore)
                {
                    slot.Pending = slot.Wanted ?? verified;
                    slot.RewriteNotBefore = now + (long)EqRewriteSpacing.TotalMilliseconds;
                    rewrite = true;
                    if (_dynamicsElsewhereLogged.Add(dev.Info.InstanceId))
                        _log.LogInformation("Another program changed the compressor or expander {owner} runs on the {model}; OpenXLR writes its own back, at most every {s} s.",
                            slot.Owner, dev.Info.Model, (int)EqRewriteSpacing.TotalSeconds);
                }
            }
        }
        if (settled) EqStatusChanged?.Invoke();
        if (rewrite) Wake();
    }
}
