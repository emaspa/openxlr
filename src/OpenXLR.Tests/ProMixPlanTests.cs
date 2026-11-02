using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using static OpenXLR.Tests.ProDuckingTests;

namespace OpenXLR.Tests;

/// <summary>
/// The one plan for the Pro's block 0x0001 in today's mode: a table from
/// scenes to the bytes they change, each the edit the setters it replaced
/// made under the device manager's conditions (selectors, the USB Aux port's
/// cells, the Monitor return, both microphones' paths and levels, the
/// ducking fields); the check against the polled state agreeing with the
/// edit on the bytes; and a plan applied twice changing nothing the second
/// time, so the sweep that posts it every pass writes nothing at steady
/// state.
/// </summary>
public sealed class ProMixPlanTests
{
    /// <summary>A block 0x0001 with nothing in any mix, every output off and the ducking fields at Wave Link's 01.</summary>
    internal static byte[] Neutral()
    {
        var block = new byte[ProMixSections.Length];
        foreach (int s in ProMixSections.Starts)
        {
            block[s + 15] = 0xaa;
            block[s + 16] = 0x55;
            block[s + 17] = 0x55;
        }
        block[90] = block[91] = block[92] = block[93] = 0x23;
        block[94] = 0x02;
        block[98] = 0x54;
        return block;
    }

    /// <summary>The state a poll of <paramref name="block"/> publishes, as far as block 0x0001 goes.</summary>
    internal static DeviceState StateOf(byte[] block)
    {
        ProMixHeld mix = ProMixPlan.Read(block);
        return new()
        {
            GainDb = 30, OutHp1 = mix.Hp1, OutHp2 = mix.Hp2, OutUsbAux = mix.UsbAux, OutLineOut = mix.LineOut,
            AuxReturnEnabled = mix.AuxReturnEnabled, HpMixMonitorReturn = mix.MonitorReturn,
            HpMixMicDirect = mix.Mic1Direct, HpMixMicLevelDb = mix.Mic1LevelDb,
            HpMixMic2Direct = mix.Mic2Direct, HpMixMic2LevelDb = mix.Mic2LevelDb,
        };
    }

    private static byte[] Held(string bytes)
    {
        byte[] block = Neutral();
        foreach (string pair in bytes.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=');
            block[int.Parse(parts[0])] = Convert.ToByte(parts[1], 16);
        }
        return block;
    }

    private const ProMixChanges Sel = ProMixChanges.Selectors, Ret = ProMixChanges.MonitorReturn, Path = ProMixChanges.MicPaths,
        Lvl = ProMixChanges.Levels, Duck = ProMixChanges.DuckingFields, None = ProMixChanges.None;

    /// <summary>Each scene: the bytes held over <see cref="Neutral"/>, the plan, the bytes it changes as the journal writes them, and the parts.</summary>
    private static readonly Dictionary<string, (string Held, ProMixPlan Plan, string Diff, ProMixChanges Changes)> Scenes = new()
    {
        // The selectors: on is 1e, off 23; a jack already on another section counts as playing.
        ["hp1 on"] = ("", new() { Hp1 = true }, "90 23->1e", Sel),
        ["hp1 off"] = ("90=1e", new() { Hp1 = false }, "90 1e->23", Sel),
        ["hp1 on, playing section 18"] = ("90=1f", new() { Hp1 = true }, "no change", None),
        ["hp2 on"] = ("", new() { Hp2 = true }, "91 23->1e", Sel),
        ["line out on"] = ("", new() { LineOut = true }, "93 23->1e", Sel),
        ["line out off"] = ("93=1e", new() { LineOut = false }, "93 1e->23", Sel),
        // The USB Aux port: on plays section 36 with pair 5 open at 0 dB and
        // the unit's inputs out of it; off leaves the cells.
        ["usb aux on"] = ("45=f4,48=09,49=00", new() { UsbAux = true }, "45 f4->00, 48 09->00, 49 00->02, 92 23->20", Sel),
        ["usb aux on, held"] = ("45=00,48=09,49=02,92=20", new() { UsbAux = true }, "no change", None),
        ["usb aux on, cell down"] = ("45=f4,48=01,49=02,92=20", new() { UsbAux = true }, "45 f4->00, 48 01->00", Sel),
        ["usb aux off"] = ("45=00,48=09,49=02,92=20", new() { UsbAux = false }, "92 20->23", Sel),
        // The Monitor return: on opens pair 1 at 0 dB and takes Line In and
        // every other pair out, keeping the microphones and USB Aux.
        ["return on, left by wave link"] = ("5=c9,12=e9,13=0d", new() { MonitorReturn = true }, "5 c9->00, 12 e9->21, 13 0d->00", Ret),
        ["return on, usb aux kept"] = ("12=24", new() { MonitorReturn = true }, "no change", None),
        ["return on, bits above pair 7"] = ("12=20,13=f0", new() { MonitorReturn = true }, "no change", None),
        ["return off"] = ("12=21", new() { MonitorReturn = false }, "12 21->01", Ret),
        ["return off, already off with line in"] = ("12=c8", new() { MonitorReturn = false }, "no change", None),
        ["return left"] = ("5=c9,12=e9,13=0d", new(), "no change", None),
        // XLR 1's path: the level with the path, in half dB as compared;
        // nothing of the level while the path is off.
        ["mic 1 opens at 0 dB"] = ("0=64,12=20", new() { Mic1 = new(true, 0) }, "0 64->00, 12 20->21", Lvl | Path),
        ["mic 1 level only"] = ("0=00,12=21", new() { Mic1 = new(true, -18.06) }, "0 00->dc", Lvl),
        ["mic 1 within the step"] = ("0=dc,12=21", new() { Mic1 = new(true, -18.1) }, "no change", None),
        ["mic 1 to the floor"] = ("0=dc,12=21", new() { Mic1 = new(true, double.NegativeInfinity) }, "0 dc->88", Lvl),
        ["mic 1 below the floor"] = ("0=80,12=21", new() { Mic1 = new(true, double.NegativeInfinity) }, "no change", None),
        ["mic 1 above the cap"] = ("0=64,12=21", new() { Mic1 = new(true, 30) }, "no change", None),
        ["mic 1 closes"] = ("0=dc,12=21", new() { Mic1 = new(false, 0) }, "12 21->20", Path),
        ["mic 1 off, level left"] = ("0=88,12=20", new() { Mic1 = new(false, 0) }, "no change", None),
        // XLR 2's path, bit 1 at byte 1.
        ["mic 2 opens"] = ("", new() { Mic2 = new(true, -6) }, "1 00->f4, 12 00->02", Lvl | Path),
        ["mic 2 closes"] = ("1=f4,12=03", new() { Mic2 = new(false, 0) }, "12 03->01", Path),
        // Ducking: both bits for a target pair in the first three sections, the high bit off elsewhere.
        ["ducking on"] = ("", new() { Ducking = On(ProReturnPairs.Monitor) }, "16 55->5d, 34 55->5d, 52 55->5d", Duck),
        ["ducking off"] = ("16=5d,17=fd,34=5d,35=fd,52=5d,53=fd", new() { Ducking = ProDuckingTarget.Off },
            "16 5d->55, 17 fd->55, 34 5d->55, 35 fd->55, 52 5d->55, 53 fd->55", Duck),
        // Everything at once is one edit of one block.
        ["all at once"] = ("0=64", new() { Hp1 = true, MonitorReturn = true, Mic1 = new(true, 0), Ducking = On(ProReturnPairs.Monitor) },
            "0 64->00, 12 00->21, 16 55->5d, 34 55->5d, 52 55->5d, 90 23->1e", Sel | Ret | Path | Lvl | Duck),
    };

    public static TheoryData<string> SceneNames() => [.. Scenes.Keys];

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void EachSceneChangesExactlyItsBytes(string scene)
    {
        (string held, ProMixPlan plan, string diff, ProMixChanges changes) = Scenes[scene];
        byte[] before = Held(held);
        var after = new byte[ProMixSections.Length];
        Assert.Equal(changes, plan.Apply(before, after));
        Assert.Equal(diff, ProDucking.Diff(before, after));
    }

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void TheCheckOnThePolledStateAgreesWithTheEdit(string scene)
    {
        (string held, ProMixPlan plan, _, ProMixChanges changes) = Scenes[scene];
        // What the device manager decides from the last poll, without a transfer.
        Assert.Equal(changes & ~ProMixChanges.DuckingFields, plan.Needs(ProMixHeld.Of(StateOf(Held(held)))));
    }

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void APlanTheUnitHoldsChangesNothing(string scene)
    {
        (string held, ProMixPlan plan, _, _) = Scenes[scene];
        var once = new byte[ProMixSections.Length];
        var twice = new byte[ProMixSections.Length];
        plan.Apply(Held(held), once);
        Assert.Equal(ProMixChanges.None, plan.Apply(once, twice));
        Assert.Equal(once, twice);
        Assert.Equal(ProMixChanges.None, plan.Needs(ProMixPlan.Read(once)));
    }

    [Fact]
    public void AnEmptyPlanLeavesEveryByte()
    {
        byte[] held = Convert.FromHexString(ProMixSectionsTests.DeskBlock);
        var into = new byte[ProMixSections.Length];
        Assert.Equal(ProMixChanges.None, ProMixPlan.Nothing.Apply(held, into));
        Assert.Equal(held, into);
    }

    [Fact]
    public void OnlySelectorsAndMembershipCommitAndOnlyMembershipRestartsTheStream()
    {
        Assert.True((ProMixChanges.Selectors).Commits());
        Assert.True((ProMixChanges.MonitorReturn).Commits());
        Assert.True((ProMixChanges.MicPaths).Commits());
        Assert.False((ProMixChanges.Levels | ProMixChanges.DuckingFields).Commits());
        Assert.True((ProMixChanges.MonitorReturn).Membership());
        Assert.True((ProMixChanges.MicPaths).Membership());
        Assert.False((ProMixChanges.Selectors | ProMixChanges.Levels | ProMixChanges.DuckingFields).Membership());
    }

    [Theory]
    [InlineData(0.0, 0x00)]
    [InlineData(-6.0, 0xf4)]
    [InlineData(-28.0, 0xc8)]
    [InlineData(-18.06, 0xdc)]
    [InlineData(10.6, 0x15)]
    [InlineData(30.0, 0x18)]
    [InlineData(-80.0, 0x88)]
    [InlineData(double.NegativeInfinity, 0x88)]
    [InlineData(double.NaN, 0x88)]
    public void TheMicLevelCellIsHalfDecibelsBetweenTheFloorAndTheCap(double db, int expected)
        => Assert.Equal(expected, ProMixPlan.LevelCell(db));

    [Fact]
    public void AWrongLengthOrABadDuckingTargetIsRefused()
    {
        Assert.Throws<ArgumentException>(() => ProMixPlan.Read(new byte[107]));
        Assert.Throws<ArgumentException>(() => ProMixPlan.Nothing.Apply(Neutral(), new byte[107]));
        var bad = new ProMixPlan { Ducking = On(8) };
        Assert.Throws<ArgumentException>(() => bad.Apply(Neutral(), new byte[ProMixSections.Length]));
    }
}
