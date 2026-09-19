using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class InsertHoldTests
{
    private static InsertDefinition Effect(string id, bool bypass) => new() { Id = id, Kind = "lv2", Plugin = "urn:test", Bypass = bypass };

    [Fact]
    public void OverlappingKeysRestoreEachOriginalStateOnlyAfterItsLastRelease()
    {
        var holds = new InsertHolds(() => 0);
        var first = holds.Begin("a", "xlr1", [Effect("one", true), Effect("two", false)]);
        Assert.Equal(new InsertHolds.Change("xlr1", "one", false), Assert.Single(first));
        Assert.True(holds.SavedBypass("xlr1", "one", false));
        Assert.False(holds.SavedBypass("xlr1", "two", true));
        Assert.Empty(holds.Begin("b", "xlr1", [Effect("one", false)]));
        Assert.Empty(holds.End("a"));
        Assert.Equal(new InsertHolds.Change("xlr1", "one", true), Assert.Single(holds.End("b")));
        Assert.Empty(holds.End("b"));
        Assert.False(holds.SavedBypass("xlr1", "one", false));
    }

    [Fact]
    public void LostReleaseExpiresAndRenewalCannotRecreateAnExpiredOrCancelledHold()
    {
        long now = 0;
        var holds = new InsertHolds(() => now);
        holds.Begin("one", "xlr1", [Effect("one", true)]);
        now = InsertHolds.LifetimeMs - 1;
        Assert.Empty(holds.Expire());
        holds.Renew("one");
        now += InsertHolds.LifetimeMs - 1;
        Assert.Empty(holds.Expire());
        now++;
        Assert.Single(holds.Expire());
        holds.Renew("one");
        Assert.Empty(holds.Expire());
        holds.Begin("two", "xlr1", [Effect("one", true)]);
        Assert.Single(holds.Cancel("xlr1"));
        holds.Renew("two");
        Assert.Empty(holds.End("two"));
    }

    [Fact]
    public void DuplicateDownIsIdempotentAndIdsCannotBeReusedForAnotherAction()
    {
        var holds = new InsertHolds(() => 0);
        Assert.Single(holds.Begin("one", "xlr1", [Effect("effect", true)]));
        Assert.Empty(holds.Begin("one", "xlr1", [Effect("effect", false)]));
        Assert.Throws<ArgumentException>(() => holds.Begin("one", "xlr2", [Effect("effect", true)]));
        Assert.Throws<ArgumentException>(() => holds.Begin("empty", "xlr1", []));
        for (int i = 1; i < InsertHolds.MaxHolds; i++) holds.Begin(i.ToString(), "xlr1", [Effect("effect", false)]);
        Assert.Throws<InvalidOperationException>(() => holds.Begin("overflow", "xlr1", [Effect("effect", false)]));
        Assert.Single(holds.Cancel());
        Assert.Single(holds.Begin("new", "xlr2", [Effect("effect", true)]));
    }

    [Fact]
    public void PartialCancellationDoesNotTouchOtherChainsWithTheSameSlotId()
    {
        var holds = new InsertHolds(() => 0);
        holds.Begin("one", "xlr1", [Effect("shared", true)]);
        holds.Begin("two", "mix:monitor", [Effect("shared", true)]);
        Assert.Equal("xlr1", Assert.Single(holds.Cancel("xlr1")).Chain);
        Assert.True(holds.SavedBypass("mix:monitor", "shared", false));
        Assert.Equal("mix:monitor", Assert.Single(holds.End("two")).Chain);
    }

    [Theory]
    [InlineData("begin", "xlr1", "one", true)]
    [InlineData("begin", "xlr1", null, true)]
    [InlineData("begin", "missing", null, false)]
    [InlineData("begin", "xlr1", "missing", false)]
    [InlineData("renew", null, null, true)]
    [InlineData("end", null, null, true)]
    [InlineData("unknown", "xlr1", "one", false)]
    public void CommandBoundaryValidatesActionsAndTargets(string action, string? channel, string? insert, bool valid)
    {
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [Effect("one", true)]);
        var command = new Command { Cmd = "holdInsert", HoldId = Guid.NewGuid().ToString("N"), Action = action, Channel = channel, InsertId = insert };
        Assert.Equal(valid, CommandValidation.Check(command, mixer, _ => null) is null);
        Assert.NotNull(CommandValidation.Check(command with { HoldId = "not-a-uuid" }, mixer, _ => null));
    }

    [MonitorPipeWireFact]
    public void HeldStatesAreNotSavedAndManualChangesCancelLateRestoration()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig
        {
            Channels = [new("software", "Software")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
        });
        // A missing plugin still exercises bypass ownership without an audio
        // dependency; real processing is covered by the insert-host tests.
        mixer.SetInserts("mix:monitor", [Effect("one", true), Effect("two", false)]);
        string id = Guid.NewGuid().ToString("N");
        Assert.True(mixer.HoldInsert(id, "begin", "mix:monitor", null));
        Assert.False(mixer.InsertInChain("mix:monitor", "one")!.Bypass);
        Assert.True(mixer.ExportSettings().Inserts["mix:monitor"][0].Bypass);
        Assert.True(mixer.ExportScene().Inserts!["mix:monitor"][0].Bypass);
        mixer.SetInsertBypass("mix:monitor", "one", false);
        Assert.False(mixer.HoldInsert(id, "end", null, null));
        Assert.False(mixer.ExportSettings().Inserts["mix:monitor"][0].Bypass);
        mixer.SetInsertBypass("mix:monitor", "one", true);
        mixer.HoldInsert(id, "begin", "mix:monitor", "one");
        mixer.SetInserts("mix:monitor", [Effect("one", false)]); // A reorder/host edit keeps the pre-hold state.
        Assert.True(mixer.InsertInChain("mix:monitor", "one")!.Bypass);
        Assert.False(mixer.HoldInsert(id, "renew", null, null));
        Assert.False(mixer.HoldInsert(id, "end", null, null));
    }
}
