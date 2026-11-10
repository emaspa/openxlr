using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using static OpenXLR.Tests.ProDuckingTests;

namespace OpenXLR.Tests;

/// <summary>
/// Ducking through the Pro's driver and the device manager, against a unit
/// modelled as its blocks: both blocks read fresh and written only when
/// they change, 0x0008 first, on the Pro's bank and with no commit; the
/// Pro's existing block 0x0001 writes carrying the ducking fields through;
/// nothing written before the mixer's first post; a post pending at once,
/// written by the loop and verified by the poll; the last target posted, or
/// off, written on every connect, which clears what Wave Link left; a
/// change by another program written back no sooner than the spacing and
/// said once; a target the unit does not keep failed after two misses; and
/// nothing on a model without it.
/// </summary>
[Collection("xdg-config")]
public sealed class ProDuckingDeviceTests
{
    /// <summary>Block 0x0004 of the desk's Pro as capture 16 read it.</summary>
    internal const string SettingsBlock =
        "34d800d4200a00c8001832f4040a00640006" + "01003c00f6030de300ff05f45c03f20507fd27e8" +
        "1e0004d4200a00c800180af4040a00640006" + "01003c00f6030de300ff05f45c03f20507fd27e8" + "14000200";

    /// <summary>A Pro as Wave Link left the desk's unit: ducking on pairs 1, 5, 6 and 7, Low, Low, Slow.</summary>
    private static Mk2HardwareEqTests.Unit LeftByWaveLink()
    {
        var unit = new Mk2HardwareEqTests.Unit { Bank = 0x0103 };
        unit.Blocks.Clear();
        unit.Blocks[0x0001] = Block1(0x5d, 0xfd);
        unit.Blocks[0x0003] = new byte[12];
        unit.Blocks[0x0004] = Convert.FromHexString(SettingsBlock);
        unit.Blocks[0x0005] = Convert.FromHexString("0000000050000000");
        unit.Blocks[0x0008] = Block8(LowLowSlowBytes, WaveLinkFlags);
        return unit;
    }

    private static IEnumerable<ushort> Writes(Mk2HardwareEqTests.Unit unit)
        => unit.Calls.Where(c => c.Type == 0x41).Select(c => c.Block);

    private static byte[] Fields(byte[] block1)
        => [.. ProDucking.MixFieldOffsets.SelectMany(at => block1[at..(at + 2)])];

    [Fact]
    public void BothBlocksAreWrittenOnTheProsBankWithoutACommitAndOnlyWhenTheyChange()
    {
        var unit = LeftByWaveLink();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        Assert.True(pro.Capabilities.Ducking);
        unit.Calls.Clear();

        ProMixWrite write = pro.WriteMix(new ProMixPlan { Ducking = On(ProReturnPairs.Monitor) })!;
        Assert.True(write.Wrote8 && write.Wrote1);
        // Medium presets in every entry; pairs 5, 6 and 7 (entries 9 to 11, keys at 78, 86, 94) let go,
        // and Wave Link's leftover keys cleared (XLR 2 on entry 1, XLR 1 and XLR 2 on entries 4, 7 and 8).
        Assert.StartsWith("0 fd->f4, 1 06->0c, 2 64->50, 4 d0->b0, 5 07->04, 8 fd->f4", write.Change8);
        Assert.Contains("14 02->00, ", write.Change8);
        Assert.Contains("38 03->00, ", write.Change8);
        Assert.Contains("78 01->00, ", write.Change8);
        Assert.EndsWith("86 01->00, 88 fd->f4, 89 06->0c, 90 64->50, 92 d0->b0, 93 07->04, 94 01->00", write.Change8);
        Assert.Equal(new ushort[] { 0x0008, 0x0001 }, Writes(unit));
        Assert.All(unit.Calls, c => Assert.Equal(0x0103, c.Bank));
        Assert.Equal(new byte[] { 0x5d, 0x55, 0x5d, 0x55, 0x5d, 0x55, 0x5d, 0x55 }, Fields(unit.Blocks[0x0001]));
        Assert.Equal(Block8(MediumBytes, Xlr1Keys(5)), unit.Blocks[0x0008]);
        Assert.Equal([1], pro.ReadDucking()!.Pairs);

        unit.Calls.Clear();
        Assert.False(pro.WriteMix(new ProMixPlan { Ducking = On(ProReturnPairs.Monitor) })!.Wrote);
        Assert.Empty(Writes(unit));
        Assert.Equal(2, unit.Calls.Count);   // the two reads
    }

    [Fact]
    public void TheProsOtherBlockOneWritesCarryTheDuckingFieldsThrough()
    {
        var unit = LeftByWaveLink();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        pro.WriteMix(new ProMixPlan { Ducking = On(ProReturnPairs.Monitor, ProReturnPairs.Aux) });
        byte[] ducked = Fields(unit.Blocks[0x0001]);
        Assert.Equal(new byte[] { 0x5d, 0x5d, 0x5d, 0x5d, 0x5d, 0x5d, 0x5d, 0x5d }, ducked);

        // Every part of a plan without ducking: the microphone's path and
        // level in the jacks' mix, the four selectors with the aux cell, and
        // the headphone mix membership.
        ProMixPlan[] plans =
        [
            new() { Mic1 = new(true, -6) }, new() { Hp1 = true }, new() { Hp2 = true },
            new() { LineOut = true }, new() { UsbAux = true }, new() { UsbAux = false },
            new() { MonitorReturn = false }, new() { Mic1 = new(false, -6) },
        ];
        foreach (ProMixPlan plan in plans)
        {
            pro.WriteMix(plan);
            Assert.Equal(ducked, Fields(unit.Blocks[0x0001]));
        }
        byte[] routed = unit.Blocks[0x0001];
        Assert.Equal((0xf4, 0x1e, 0x1e, 0x1e), (routed[0], routed[90], routed[91], routed[93]));

        // And ducking off keeps what those writers set.
        pro.WriteMix(new ProMixPlan { Ducking = ProDuckingTarget.Off });
        byte[] after = unit.Blocks[0x0001];
        for (int i = 0; i < after.Length; i++)
            if (!ProDucking.MixFieldOffsets.Any(at => i == at || i == at + 1)) Assert.Equal(routed[i], after[i]);
        Assert.Equal(new byte[] { 0x55, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55 }, Fields(after));
    }

    [Fact]
    public void APlanWithoutDuckingLeavesBlock0008UnreadAndCommitsOnlyForASelectorOrMembership()
    {
        var unit = LeftByWaveLink();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        // XLR 1's path already open: a level alone is one read and one
        // write of block 0x0001, with no commit.
        byte[] block = unit.Blocks[0x0001];
        block[12] |= 0x01;
        unit.Blocks[0x0001] = block;
        unit.Calls.Clear();
        ProMixWrite level = pro.WriteMix(new ProMixPlan { Mic1 = new(true, -6) })!;
        Assert.Equal((ProMixChanges.Levels, true, false), (level.Changes, level.Wrote1, level.Committed));
        Assert.Equal(new ushort[] { 0x0001 }, Writes(unit));
        Assert.DoesNotContain(unit.Calls, c => c.Block == 0x0008);

        // Two selectors and the return: one write, one commit.
        unit.Calls.Clear();
        ProMixWrite routed = pro.WriteMix(new ProMixPlan { Hp1 = true, Hp2 = true, MonitorReturn = false })!;
        Assert.Equal(ProMixChanges.Selectors | ProMixChanges.MonitorReturn, routed.Changes);
        Assert.True(routed.Committed);
        Assert.Equal(new ushort[] { 0x0001, 0x0003 }, Writes(unit));
        Assert.Equal("90 23->1e, 91 23->1e", routed.Change1[(routed.Change1.IndexOf("90 ", StringComparison.Ordinal))..]);
    }

    [Fact]
    public void ThePollReadsTheDuckingAndAUnitThatRefusesItsBlockStillAnswers()
    {
        var unit = LeftByWaveLink();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        Assert.Null(pro.ReadDucking());
        DeviceState state = pro.ReadState();
        Assert.Equal(52, state.GainDb);
        DuckingReadback ducking = pro.ReadDucking()!;
        Assert.Equal([1, 5, 6, 7], ducking.Pairs);
        Assert.Equal(("low", "low", "slow"), (ducking.Sensitivity, ducking.Amount, ducking.Speed));

        unit.Blocks.Remove(0x0008);
        Assert.Equal(state, pro.ReadState());
        Assert.Null(pro.ReadDucking());
    }

    // --- the device manager ---

    private const string Unit = "0fd9:00b4";

    /// <summary>The device manager keeps last settings under the configuration directory: a private one per test.</summary>
    private static void WithConfigDir(Action body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-ducking-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { body(); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static DeviceManager Manager(Func<IReadOnlyList<IAudioDevice>> detect, ManualTime time, DeviceEqWriterTests.Lines log)
    {
        var manager = new DeviceManager(log, new ConfigurationBuilder().Build(), detect, time: time);
        manager.SweepOnce();
        return manager;
    }

    private static DeviceManager Manager(IAudioDevice unit, ManualTime time, DeviceEqWriterTests.Lines log)
    {
        DeviceManager manager = Manager(() => [unit], time, log);
        Assert.True(unit.Connected);
        return manager;
    }

    private static int Raised(DeviceManager manager, Action act)
    {
        int raised = 0;
        void Count() => Interlocked.Increment(ref raised);
        manager.DuckingStatusChanged += Count;
        try { act(); }
        finally { manager.DuckingStatusChanged -= Count; }
        return raised;
    }

    [Fact]
    public void NothingIsWrittenBeforeTheMixersFirstPost() => WithConfigDir(() =>
    {
        var unit = LeftByWaveLink();
        var log = new DeviceEqWriterTests.Lines();
        using DeviceManager manager = Manager(new WaveXlrProDevice(unit), new ManualTime(), log);
        manager.SweepOnce();

        // Hardware-control mode never posts, so Wave Link's ducking stays.
        Assert.DoesNotContain((ushort)0x0008, Writes(unit));
        Assert.Equal(new byte[] { 0x5d, 0xfd, 0x5d, 0xfd, 0x5d, 0xfd, 0x5d, 0xfd }, Fields(unit.Blocks[0x0001]));
        Assert.Equal(Block8(LowLowSlowBytes, WaveLinkFlags), unit.Blocks[0x0008]);
        Assert.DoesNotContain(log.Written, line => line.StartsWith("ducking (", StringComparison.Ordinal));
        DeviceDuckingStatus status = manager.DuckingStatus(Unit);
        Assert.Equal(DeviceDuckingPhase.None, status.Phase);
        Assert.Equal([1, 5, 6, 7], status.Readback!.Pairs);
        Assert.Equal(("low", "low", "slow"), (status.Readback.Sensitivity, status.Readback.Amount, status.Readback.Speed));
        (string id, UnitDuckingSupport support) = manager.PublishedDuckingSupport!.Value;
        Assert.Equal((Unit, "Wave XLR Pro", true, null), (id, support.Name, support.Ready, support.Reason));
        // The state's ducking is the mixer's now; the device half carries none.
        Assert.Null(manager.Snapshot().Ducking);
        Assert.True(manager.Snapshot().Capabilities!.Ducking);
    });

    [Fact]
    public void APostIsPendingAtOnceWrittenByTheLoopAndVerifiedByThePoll() => WithConfigDir(() =>
    {
        var unit = LeftByWaveLink();
        var log = new DeviceEqWriterTests.Lines();
        using DeviceManager manager = Manager(new WaveXlrProDevice(unit), new ManualTime(), log);
        unit.Calls.Clear();

        manager.PostDucking(Unit, On(ProReturnPairs.Monitor), "XLR 1");
        // A post does no USB work: it waits for the loop.
        Assert.Empty(unit.Calls);
        Assert.Equal(new DeviceDuckingStatus(DeviceDuckingPhase.Pending, On(ProReturnPairs.Monitor)), manager.DuckingStatus(Unit) with { Readback = null });

        Assert.True(Raised(manager, manager.SweepOnce) > 0);
        Assert.Equal(new ushort[] { 0x0008, 0x0001 }, Writes(unit));
        DeviceDuckingStatus status = manager.DuckingStatus(Unit);
        Assert.Equal(DeviceDuckingPhase.Verified, status.Phase);
        Assert.Equal(On(ProReturnPairs.Monitor), status.Value);
        Assert.Equal([1], status.Readback!.Pairs);
        Assert.Equal(("medium", "medium", "medium"), (status.Readback.Sensitivity, status.Readback.Amount, status.Readback.Speed));
        Assert.Contains(log.Written, line => line.StartsWith(
            "ducking (update) on Elgato Wave XLR Pro: on, pairs 1, keys XLR 1, sensitivity medium, amount medium, speed medium, keyed by XLR 1. Block 0x0008 written: ",
            StringComparison.Ordinal) && line.Contains("Block 0x0001 written: 17 fd->55, 35 fd->55, 53 fd->55, 71 fd->55.", StringComparison.Ordinal));

        // The same target again is not written; another unit's post is dropped.
        unit.Calls.Clear();
        manager.PostDucking(Unit, On(ProReturnPairs.Monitor), "XLR 1");
        manager.PostDucking("0fd9:00b4@elsewhere", ProDuckingTarget.Off, "XLR 1");
        manager.SweepOnce();
        Assert.Empty(Writes(unit));
        Assert.Equal(DeviceDuckingPhase.Verified, manager.DuckingStatus(Unit).Phase);
        Assert.Equal(DeviceDuckingStatus.None, manager.DuckingStatus("0fd9:00b4@elsewhere"));

        // Off lets every pair go and leaves the presets as they were.
        manager.PostDucking(Unit, ProDuckingTarget.Off with { Pairs = new HashSet<int> { 1 } }, "XLR 1");
        manager.SweepOnce();
        Assert.Empty(manager.DuckingStatus(Unit).Readback!.Pairs);
        Assert.Contains(log.Written, line => line.StartsWith("ducking (update) on Elgato Wave XLR Pro: off. ", StringComparison.Ordinal));
    });

    [Fact]
    public void ADuckingTargetWaitingAndASelectorChangeInOneTickAreOneWrite() => WithConfigDir(() =>
    {
        var unit = LeftByWaveLink();
        var log = new DeviceEqWriterTests.Lines();
        using DeviceManager manager = Manager(new WaveXlrProDevice(unit), new ManualTime(), log);
        unit.Calls.Clear();

        manager.PostDucking(Unit, On(ProReturnPairs.Monitor), "XLR 1");
        Assert.False(manager.EnsureMix(new ProMixPlan { Hp1 = true }));
        // Block 0x0008 first, then block 0x0001 once with the selector and the
        // fields, then the one commit the selector needs.
        Assert.Equal(new ushort[] { 0x0008, 0x0001, 0x0003 }, Writes(unit));
        Assert.Equal(0x1e, unit.Blocks[0x0001][90]);
        Assert.Equal(new byte[] { 0x5d, 0x55, 0x5d, 0x55, 0x5d, 0x55, 0x5d, 0x55 }, Fields(unit.Blocks[0x0001]));
        Assert.Contains(log.Written, line => line.StartsWith(
            "ducking (update) on Elgato Wave XLR Pro: on, pairs 1, keys XLR 1, sensitivity medium, amount medium, speed medium, keyed by XLR 1. Block 0x0008 written: ",
            StringComparison.Ordinal) && line.Contains("Block 0x0001 written: 17 fd->55, 35 fd->55, 53 fd->55, 71 fd->55, 90 23->1e.", StringComparison.Ordinal));

        // The loop finds nothing left to write, and the poll verifies the target.
        unit.Calls.Clear();
        manager.SweepOnce();
        Assert.Empty(Writes(unit));
        Assert.Equal(DeviceDuckingPhase.Verified, manager.DuckingStatus(Unit).Phase);
    });

    [Fact]
    public void AtSteadyStateTheMixersPlanCostsNoTransfer() => WithConfigDir(() =>
    {
        var unit = LeftByWaveLink();
        using DeviceManager manager = Manager(new WaveXlrProDevice(unit), new ManualTime(), new DeviceEqWriterTests.Lines());
        var plan = new ProMixPlan
        {
            Hp1 = true, Hp2 = false, UsbAux = true, LineOut = false, MonitorReturn = true,
            Mic1 = new(true, -18.06), Mic2 = new(false, 0),
        };
        Assert.True(manager.EnsureMix(plan));
        Assert.True(manager.Snapshot().State!.HpMixMicDirect);
        for (int i = 0; i < 3; i++)
        {
            manager.SweepOnce();
            unit.Calls.Clear();
            Assert.False(manager.EnsureMix(plan));
            Assert.Empty(unit.Calls);
        }

        // Another program moving a selector is put back on the next sweep that reads it.
        byte[] block = unit.Blocks[0x0001];
        block[90] = 0x23;
        unit.Blocks[0x0001] = block;
        manager.SweepOnce();
        unit.Calls.Clear();
        Assert.False(manager.EnsureMix(plan));
        Assert.Equal(new ushort[] { 0x0001, 0x0003 }, Writes(unit));
        Assert.Equal(0x1e, unit.Blocks[0x0001][90]);
    });

    [Fact]
    public void ASetCommandMovesOneSelectorThroughThePlan() => WithConfigDir(() =>
    {
        var unit = LeftByWaveLink();
        using DeviceManager manager = Manager(new WaveXlrProDevice(unit), new ManualTime(), new DeviceEqWriterTests.Lines());
        byte[] before = unit.Blocks[0x0001];
        unit.Calls.Clear();
        Assert.Null(manager.Apply("outLineOut", JsonSerializer.SerializeToElement(true)));
        Assert.Equal(new ushort[] { 0x0001, 0x0003 }, Writes(unit));
        Assert.Equal("93 23->1e", ProDucking.Diff(before, unit.Blocks[0x0001]));
        Assert.True(manager.Snapshot().State!.OutLineOut);
    });

    [Fact]
    public void EveryConnectWritesTheLastTargetPostedOrOff() => WithConfigDir(() =>
    {
        var unit = LeftByWaveLink();
        var log = new DeviceEqWriterTests.Lines();
        var time = new ManualTime();
        List<IAudioDevice> attached = [new WaveXlrProDevice(unit)];
        using DeviceManager manager = Manager(() => attached, time, log);
        var target = new ProDuckingTarget(true, "high", "low", "fast", new HashSet<int> { 1, 5 });
        manager.PostDucking(Unit, target, "XLR 1");
        manager.SweepOnce();
        Assert.Equal(DeviceDuckingPhase.Verified, manager.DuckingStatus(Unit).Phase);

        void Replug()
        {
            attached.Clear();
            unit.Close();
            manager.SweepOnce();
            Assert.False(manager.Snapshot().Connected);
            Assert.Equal(DeviceDuckingStatus.None, manager.DuckingStatus(Unit));
            // Wave Link had the unit meanwhile and put its own back.
            unit.Blocks[0x0001] = Block1(0x5d, 0xfd);
            unit.Blocks[0x0008] = Block8(LowLowSlowBytes, WaveLinkFlags);
            attached.Add(new WaveXlrProDevice(unit));
            unit.Calls.Clear();
            manager.SweepOnce();
            Assert.True(manager.Snapshot().Connected);
        }

        Replug();
        Assert.Equal(new ushort[] { 0x0008, 0x0001 }, Writes(unit));
        DeviceDuckingStatus held = manager.DuckingStatus(Unit);
        Assert.Equal((DeviceDuckingPhase.Verified, target), (held.Phase, held.Value));
        Assert.Equal([1, 5], held.Readback!.Pairs);
        Assert.Equal(Block8([0xe8, 0x06, 0x1e, 0x00, 0xc8, 0x00], Xlr1Keys(5, 9)), unit.Blocks[0x0008]);
        Assert.Contains(log.Written, line => line.StartsWith(
            "ducking (connect) on Elgato Wave XLR Pro: on, pairs 1 5, keys XLR 1, sensitivity high, amount low, speed fast, keyed by XLR 1.", StringComparison.Ordinal));

        // With off posted, the connect clears what Wave Link left, its leftover keys included.
        manager.PostDucking(Unit, ProDuckingTarget.Off, "XLR 1");
        manager.SweepOnce();
        Replug();
        Assert.Equal(Block8(LowLowSlowBytes, NoKeys), unit.Blocks[0x0008]);
        Assert.Equal(new byte[] { 0x55, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55 }, Fields(unit.Blocks[0x0001]));
        Assert.Equal((DeviceDuckingPhase.Verified, ProDuckingTarget.Off), (manager.DuckingStatus(Unit).Phase, manager.DuckingStatus(Unit).Value));
        Assert.Contains(log.Written, line => line.StartsWith("ducking (connect) on Elgato Wave XLR Pro: off. Block 0x0008 written: ", StringComparison.Ordinal)
            && line.Contains("Block 0x0001 written: 16 5d->55, 17 fd->55, 34 5d->55", StringComparison.Ordinal));
    });

    [Fact]
    public void AChangeByAnotherProgramIsWrittenBackNoSoonerThanTheSpacingAndSaidOnce() => WithConfigDir(() =>
    {
        var unit = LeftByWaveLink();
        var log = new DeviceEqWriterTests.Lines();
        var time = new ManualTime();
        using DeviceManager manager = Manager(new WaveXlrProDevice(unit), time, log);
        manager.PostDucking(Unit, ProDuckingTarget.Off, "XLR 1");
        manager.SweepOnce();
        byte[] off8 = unit.Blocks[0x0008], off1 = unit.Blocks[0x0001];

        void Meddle()
        {
            unit.Blocks[0x0001] = Block1(0x5d, 0xfd);
            unit.Blocks[0x0008] = Block8(LowLowSlowBytes, WaveLinkFlags);
        }
        Meddle();
        unit.Calls.Clear();
        manager.SweepOnce();
        Assert.Equal(off8, unit.Blocks[0x0008]);
        Assert.Equal(off1, unit.Blocks[0x0001]);
        Assert.Equal(new ushort[] { 0x0008, 0x0001 }, Writes(unit));
        manager.SweepOnce();
        Assert.Equal(DeviceDuckingPhase.Verified, manager.DuckingStatus(Unit).Phase);

        Meddle();
        unit.Calls.Clear();
        for (int i = 0; i < 49; i++) { time.Advance(TimeSpan.FromMilliseconds(100)); manager.SweepOnce(); }
        Assert.Empty(Writes(unit));
        // Not held while it waits out the spacing.
        Assert.Equal(DeviceDuckingPhase.Pending, manager.DuckingStatus(Unit).Phase);
        time.Advance(TimeSpan.FromMilliseconds(100));
        manager.SweepOnce();
        Assert.Equal(new ushort[] { 0x0008, 0x0001 }, Writes(unit));
        Assert.Single(log.Written, line => line.Contains("no longer matches OpenXLR's (off)", StringComparison.Ordinal));
        Assert.Equal(2, log.Written.Count(line => line.StartsWith("ducking (restore)", StringComparison.Ordinal)));
        manager.SweepOnce();
        Assert.Equal(DeviceDuckingPhase.Verified, manager.DuckingStatus(Unit).Phase);
    });

    [Fact]
    public void ATargetTheUnitDoesNotKeepFailsAfterTwoMissesAndANewPostTriesAgain() => WithConfigDir(() =>
    {
        var unit = LeftByWaveLink();
        var log = new DeviceEqWriterTests.Lines();
        using DeviceManager manager = Manager(new WaveXlrProDevice(unit), new ManualTime(), log);
        // The unit stops answering its ducking block; everything else still answers.
        byte[] block8 = unit.Blocks[0x0008];
        unit.Blocks.Remove(0x0008);
        manager.SweepOnce();
        Assert.Equal(("unsupportedBlock", false), (manager.PublishedDuckingSupport!.Value.Support.Reason, manager.PublishedDuckingSupport!.Value.Support.Ready));

        manager.PostDucking(Unit, On(ProReturnPairs.Monitor), "XLR 1");
        manager.SweepOnce();
        Assert.Equal(DeviceDuckingPhase.Pending, manager.DuckingStatus(Unit).Phase);
        Assert.True(Raised(manager, manager.SweepOnce) > 0);
        DeviceDuckingStatus failed = manager.DuckingStatus(Unit);
        Assert.Equal(DeviceDuckingPhase.Failed, failed.Phase);
        Assert.StartsWith(DeviceManager.DuckingNotKept, failed.Reason);
        Assert.Contains(log.Written, line => line.Contains(DeviceManager.DuckingNotKept, StringComparison.Ordinal));
        manager.SweepOnce();
        Assert.Equal(DeviceDuckingPhase.Failed, manager.DuckingStatus(Unit).Phase);

        // The block answers again and a new post is written and verified.
        unit.Blocks[0x0008] = block8;
        manager.SweepOnce();
        Assert.True(manager.PublishedDuckingSupport!.Value.Support.Ready);
        manager.PostDucking(Unit, On(ProReturnPairs.Monitor), "XLR 1");
        Assert.Equal(DeviceDuckingPhase.Pending, manager.DuckingStatus(Unit).Phase);
        manager.SweepOnce();
        Assert.Equal(DeviceDuckingPhase.Verified, manager.DuckingStatus(Unit).Phase);
    });

    [Fact]
    public void AModelWithoutDuckingTakesNoPost() => WithConfigDir(() =>
    {
        var usb = new Mk2HardwareEqTests.Unit();
        using DeviceManager manager = Manager(new XlrDockMk2Device(usb), new ManualTime(), new DeviceEqWriterTests.Lines());
        StateMessage state = manager.Snapshot();
        Assert.False(state.Capabilities!.Ducking);
        Assert.Null(state.Ducking);
        Assert.Null(manager.PublishedDuckingSupport);
        manager.PostDucking("0fd9:00c7", On(ProReturnPairs.Monitor), "XLR 1");
        manager.SweepOnce();
        Assert.DoesNotContain(usb.Calls, c => c.Block == 0x0008);
        Assert.Equal(DeviceDuckingStatus.None, manager.DuckingStatus("0fd9:00c7"));
    });

    [Fact]
    public void ADuckingFileFromTheHardwareBuildIsIgnored() => WithConfigDir(() =>
    {
        string devices = Path.Combine(OpenXlrPaths.ConfigDir, "devices", "0fd9-00b4");
        Directory.CreateDirectory(devices);
        File.WriteAllText(Path.Combine(devices, "ducking.json"), """{"enabled":true,"key":"xlr1","targets":["monitor","aux"]}""");
        var unit = LeftByWaveLink();
        using DeviceManager manager = Manager(new WaveXlrProDevice(unit), new ManualTime(), new DeviceEqWriterTests.Lines());
        manager.SweepOnce();
        Assert.DoesNotContain((ushort)0x0008, Writes(unit));
        Assert.Equal(DeviceDuckingPhase.None, manager.DuckingStatus(Unit).Phase);
        manager.PostDucking(Unit, ProDuckingTarget.Off, "XLR 1");
        manager.SweepOnce();
        Assert.Equal("""{"enabled":true,"key":"xlr1","targets":["monitor","aux"]}""", File.ReadAllText(Path.Combine(devices, "ducking.json")));
    });
}
