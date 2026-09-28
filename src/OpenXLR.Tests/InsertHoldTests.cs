using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
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

    [Theory]
    [InlineData("renew")]
    [InlineData("end")]
    public async Task ExistingHoldsDoNotWaitForPluginInstallation(string action)
    {
        await using var fixture = new PluginWineTraceFixture();
        object gate = typeof(WebSocketHub).GetField("_installGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fixture.Hub)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task install = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(); } });
        Task? command = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            string json = System.Text.Json.JsonSerializer.Serialize(new { cmd = "holdInsert", action, holdId = Guid.NewGuid().ToString("N") });
            command = Task.Run(() => fixture.Hub.ExecuteForApiAsync(json));
            await command.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); await install; if (command is not null) await command; }
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
        mixer.SetInserts("mix:monitor", [Effect("one", false)]); // An explicit replacement wins over the old hold.
        Assert.False(mixer.InsertInChain("mix:monitor", "one")!.Bypass);
        Assert.False(mixer.HoldInsert(id, "renew", null, null));
        Assert.False(mixer.HoldInsert(id, "end", null, null));
    }
    [MonitorPipeWireFact]
    public void WholeChainRecallDuringAHoldUsesEveryRequestedBypassState()
    {
        using var mixer = new Mixer();
        mixer.Build(new MixerConfig { Channels = [new("software", "Software")], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        foreach (bool wasBypassed in new[] { false, true })
        foreach (bool requestedBypass in new[] { false, true })
        {
            mixer.SetInserts("mix:monitor", [Effect("one", wasBypassed), Effect("two", false)]);
            string hold = Guid.NewGuid().ToString("N");
            mixer.HoldInsert(hold, "begin", "mix:monitor", null);
            mixer.SetInserts("mix:monitor", [Effect("one", requestedBypass), Effect("two", true)]);
            Assert.Equal(requestedBypass, mixer.InsertInChain("mix:monitor", "one")!.Bypass);
            Assert.True(mixer.InsertInChain("mix:monitor", "two")!.Bypass);
            Assert.False(mixer.HoldInsert(hold, "end", null, null));
            Assert.Equal(requestedBypass, mixer.ExportSettings().Inserts["mix:monitor"][0].Bypass);
        }
    }

    [MonitorPipeWireFact]
    public void ProfileRecallRestoresUnspecifiedHoldsAndHonoursExplicitBypass()
    {
        using var mixer = new Mixer();
        mixer.Build(new MixerConfig
        {
            Channels = [new("software", "Software")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)],
        });
        foreach (string recall in new[] { "scene", "settings", "old scene", "partial settings" })
        foreach (bool requestedBypass in new[] { false, true })
        {
            mixer.SetInserts("mix:monitor", [Effect("one", true)]);
            mixer.SetInserts("mix:chat", [Effect("one", true)]);
            string monitor = Guid.NewGuid().ToString("N"), chat = Guid.NewGuid().ToString("N");
            mixer.HoldInsert(monitor, "begin", "mix:monitor", "one");
            mixer.HoldInsert(chat, "begin", "mix:chat", "one");
            var inserts = new Dictionary<string, List<InsertDefinition>>
            {
                ["mix:monitor"] = [Effect("one", requestedBypass)],
                ["mix:chat"] = [Effect("one", true)],
            };
            switch (recall)
            {
                case "scene": mixer.ApplyScene(mixer.ExportScene() with { Inserts = inserts }); break;
                case "settings": mixer.ApplySettings(mixer.ExportSettings() with { Inserts = inserts }); break;
                case "old scene": mixer.ApplyScene(mixer.ExportScene() with { Inserts = null }); break;
                case "partial settings":
                    inserts.Remove("mix:chat");
                    mixer.ApplySettings(mixer.ExportSettings() with { Inserts = inserts });
                    break;
            }
            bool expected = recall == "old scene" || requestedBypass;
            Assert.Equal(expected, mixer.InsertInChain("mix:monitor", "one")!.Bypass);
            Assert.True(mixer.InsertInChain("mix:chat", "one")!.Bypass);
            Assert.False(mixer.HoldInsert(monitor, "renew", null, null));
            Assert.False(mixer.HoldInsert(monitor, "end", null, null));
            Assert.False(mixer.HoldInsert(chat, "end", null, null));
            Assert.Equal(expected, mixer.ExportSettings().Inserts["mix:monitor"][0].Bypass);
        }
    }

}
