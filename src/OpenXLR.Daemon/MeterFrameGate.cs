namespace OpenXLR.Daemon;

/// <summary>
/// Which meter frames go out. A frame equal to the last one sent is held
/// back until <see cref="Keepalive"/> has passed since that send: an idle
/// mixer reads all zeros, and every client redrew the same levels fifteen
/// times a second. The repeat keeps a client that times its meters out
/// (the Omarchy bar clears them after a second without a frame) showing
/// them, and gives a client that dropped the last changed frame the levels
/// again.
/// </summary>
internal sealed class MeterFrameGate(Func<long> nowMs)
{
    internal const long Keepalive = 500;

    private readonly Lock _lock = new();
    private byte[]? _last;
    private long _sentAt;

    internal bool ShouldSend(byte[] frame)
    {
        lock (_lock)
        {
            long now = nowMs();
            if (_last is not null && frame.AsSpan().SequenceEqual(_last) && now - _sentAt < Keepalive) return false;
            _last = frame;
            _sentAt = now;
            return true;
        }
    }
}
