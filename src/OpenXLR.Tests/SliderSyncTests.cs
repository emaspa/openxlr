using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// A mixer fader whose command the daemon takes longer than the 80 ms
/// trailing edge must not queue values: a virtual mic mix's master rewrites
/// every channel's send into it, and a queue replayed old values onto the
/// fader after the drag.
/// </summary>
/// <remarks>
/// The fader keys are static and other UI tests run the 80 ms timer at the
/// same time, so a value that may go can be sent by that timer before the
/// test flushes it. The tests check what was sent, not who sent it.
/// </remarks>
public sealed class SliderSyncTests
{
    // The key's pending value has gone, by the test or by the shared timer.
    private static bool Went<T>(string key, List<T> sent, int count, long withinMs = 1000)
        => SpinWait.SpinUntil(() =>
        {
            SliderSync.TryFlush(key);
            return sent.Count >= count;
        }, TimeSpan.FromMilliseconds(withinMs));

    [Fact]
    public void AMixerFaderSendsAgainOnlyOnceTheDaemonAnsweredAndHoldsItsEchoesUntilThen()
    {
        string key = "mixvol:test-" + Guid.NewGuid().ToString("N");
        var sent = new List<double>();
        try
        {
            SliderSync.SendInOrder(key, () => sent.Add(.9));
            Assert.True(Went(key, sent, 1));
            // The daemon has not answered: the next values wait, only the newest is kept.
            SliderSync.SendInOrder(key, () => sent.Add(.8));
            SliderSync.SendInOrder(key, () => sent.Add(.7));
            Assert.False(SliderSync.TryFlush(key));
            Assert.Equal([.9], sent);
            // An echo of an older value must not land, even with the drag over.
            Assert.True(SliderSync.RecentlyTouched(key));

            SliderSync.StateArrived(SliderSync.Now() + 1);
            Assert.True(Went(key, sent, 2));
            Assert.Equal([.9, .7], sent);
            Assert.True(SliderSync.RecentlyTouched(key));

            // The state that answers the last send releases the fader.
            SliderSync.StateArrived(SliderSync.Now() + 1);
            Assert.False(SliderSync.RecentlyTouched(key));
        }
        finally { SliderSync.Forget(key); }
    }

    [Fact]
    public void AnUnansweredSendStopsHoldingAfterTheLimit()
    {
        string key = "mixvol:test-" + Guid.NewGuid().ToString("N");
        var sent = new List<int>();
        try
        {
            SliderSync.SendInOrder(key, () => sent.Add(1));
            Assert.True(Went(key, sent, 1));
            SliderSync.SendInOrder(key, () => sent.Add(2));
            Assert.False(SliderSync.TryFlush(key));
            // A refused command pushes no state; the limit lets the next value go.
            Assert.True(Went(key, sent, 2, SliderSync.InFlightLimitMs * 3));
            Assert.Equal([1, 2], sent);
        }
        finally { SliderSync.Forget(key); }
    }

    [Fact]
    public void OtherSlidersKeepThePlainTrailingEdge()
    {
        string key = "plain-" + Guid.NewGuid().ToString("N");
        var sent = new List<int>();
        try
        {
            SliderSync.Send(key, () => sent.Add(1));
            Assert.True(Went(key, sent, 1));
            SliderSync.Send(key, () => sent.Add(2));
            Assert.True(Went(key, sent, 2));
            Assert.Equal([1, 2], sent);
            Assert.False(SliderSync.RecentlyTouched(key));
        }
        finally { SliderSync.Forget(key); }
    }
}
