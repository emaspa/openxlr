using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class RestartPolicyTests
{
    [Fact]
    public void AChainThatKeepsDyingIsLeftOffUntilTheWindowPasses()
    {
        long now = 0;
        var policy = new RestartPolicy(() => now, limit: 3, windowMs: 300_000);
        Assert.False(policy.Blocked("xlr1"));

        // Three deaths are three rebuilds; the fourth leaves the chain off.
        for (int death = 1; death <= 3; death++)
        {
            now += 1_000;
            policy.Failed("xlr1");
            Assert.Equal(death == 3, policy.Blocked("xlr1"));
        }

        // Another chain's history is its own.
        policy.Failed("mix:stream");
        Assert.False(policy.Blocked("mix:stream"));

        // The window runs from the first death, so a chain that has been quiet
        // for long enough gets its chances back.
        now += 300_001;
        Assert.False(policy.Blocked("xlr1"));
    }

    [Fact]
    public void ChangingAChainMakesItNewAgain()
    {
        long now = 0;
        var policy = new RestartPolicy(() => now, limit: 2, windowMs: 300_000);
        policy.Failed("xlr1");
        policy.Failed("xlr1");
        Assert.True(policy.Blocked("xlr1"));

        policy.Forget("xlr1");
        Assert.False(policy.Blocked("xlr1"));
    }
}
