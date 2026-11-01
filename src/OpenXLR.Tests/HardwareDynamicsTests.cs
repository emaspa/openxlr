using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The compressor and expander a unit runs on its own DSP, in the driver,
/// against units modelled as their blocks. The codec reads and writes the
/// fields of one 38-byte input struct of block 0x0004 and nothing else: the
/// Wave:3 MK.2's writes reproduce the bytes its captures recorded, byte for
/// byte, including both ends of every control's limits. Each model writes
/// with one read and one whole-block write and no commit: the Wave:3 MK.2 at
/// bank 0x0103, the XLR Dock MK.2 and the Wave XLR MK.2 at 0x0203, the Wave
/// XLR Pro into XLR 1's struct at 0 and XLR 2's at 38, every other flag bit,
/// byte, struct and the Pro's tail kept. The gate follows the EQ's, and a
/// struct holding values outside the unit's limits leaves them to software.
/// The mapping from the software settings rounds and clamps into the unit's
/// limits, and every expander ratio the unit takes comes back to itself.
/// </summary>
public sealed class HardwareDynamicsTests
{
    /// <summary>Wave:3 MK.2 block 0x0004 with the compressor on (0x80), as read before the on and off writes.</summary>
    private const string W3CompressorOn = "28ea01d4200a00c8001410f4040a0064000601003c00f60308c800f60504b80b0129f8401ff6";
    /// <summary>The same block after the compressor was switched off: 0xea to 0x6a and nothing else.</summary>
    private const string W3CompressorOff = "286a01d4200a00c8001410f4040a0064000601003c00f60308c800f60504b80b0129f8401ff6";
    /// <summary>Before the limits were driven: expander -27 dB, 2.375 (38), 139 ms, 1000 ms, 60 dB; compressor -38 dB, 13, 73 ms, 1000 ms, 12 dB.</summary>
    private const string W3BeforeLimits = "28e201e5268b00e8033c10da0d4900e8030c01003c00f60308c800f60504b80b0129f8401ff6";
    /// <summary>
    /// Every setting at the end the capture left it: the thresholds at their
    /// lowest, -60 dB, and the rest at their highest: expander 3.0 (48),
    /// 200 ms, 1000 ms, 60 dB; compressor 20, 300 ms, 1000 ms, 20 dB.
    /// </summary>
    private const string W3AtMaximum = "28e201c430c800e8033c10c4142c01e8031401003c00f60308c800f60504b80b0129f8401ff6";
    /// <summary>The expander at its lowest (-1 dB threshold, 1.0, 1 ms, 10 ms, 0 dB) beside the compressor at its highest.</summary>
    private const string W3ExpanderAtMinimum = "28e201ff1001000a000010c4142c01e8031401003c00f60308c800f60504b80b0129f8401ff6";

    private static readonly DeviceDynamics AtMaximum = new(true, true, new(-60, 20, 300, 1000, 20, -60, 48, 200, 1000, 60));
    private static readonly DeviceDynamics ExpanderAtMinimum = new(true, true, new(-60, 20, 300, 1000, 20, -1, 16, 1, 10, 0));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static Mk2HardwareEqTests.Unit Wave3Mk2Unit(string block, byte major = 1, byte minor = 0, byte patch = 1) => new()
    {
        Bank = 0x0103,
        Blocks =
        {
            [0x0000] = Mk2HardwareEqTests.Unit.Identity(major, minor, patch),
            [0x0004] = Convert.FromHexString(block),
        },
    };

    // --- the codec ---

    [Fact]
    public void TheCapturedBlocksDecodeFieldByField()
    {
        DeviceDynamics before = WaveFxDynamics.Decode(Convert.FromHexString(W3BeforeLimits))!;
        Assert.Equal(new DeviceDynamics(true, true, new(-38, 13, 73, 1000, 12, -27, 38, 139, 1000, 60)), before);
        Assert.Equal(AtMaximum, WaveFxDynamics.Decode(Convert.FromHexString(W3AtMaximum)));
        Assert.Equal(ExpanderAtMinimum, WaveFxDynamics.Decode(Convert.FromHexString(W3ExpanderAtMinimum)));
        // The compressor's attack is a 16-bit field: 300 ms is 2c 01.
        Assert.Equal(300, WaveFxDynamics.Decode(Convert.FromHexString(W3AtMaximum))!.Values!.CompressorAttack);
        Assert.False(WaveFxDynamics.Decode(Convert.FromHexString(W3CompressorOff))!.Compressor);
        Assert.True(WaveFxDynamics.Decodes(before));
        Assert.Null(WaveFxDynamics.Decode(new byte[17]));
    }

    [Fact]
    public void EncodingReproducesTheCapturedWritesAndKeepsEveryOtherByte()
    {
        byte[] block = Convert.FromHexString(W3BeforeLimits);
        WaveFxDynamics.Encode(block, AtMaximum);
        Assert.Equal(W3AtMaximum, Hex(block));
        WaveFxDynamics.Encode(block, ExpanderAtMinimum);
        Assert.Equal(W3ExpanderAtMinimum, Hex(block));
        // The switches alone: the compressor's bit and nothing else, the low cut
        // (0x02), voice tune (0x40) and the bit nobody has named (0x08) kept.
        block = Convert.FromHexString(W3CompressorOn);
        WaveFxDynamics.Encode(block, DeviceDynamics.Off with { Expander = true });
        Assert.Equal(W3CompressorOff, Hex(block));
        // Every bit the codec does not own survives both ways.
        block = Convert.FromHexString(W3BeforeLimits);
        block[1] = 0xff;
        WaveFxDynamics.Encode(block, DeviceDynamics.Off);
        Assert.Equal(0x5f, block[1]);
        WaveFxDynamics.Encode(block, new DeviceDynamics(true, true, null));
        Assert.Equal(0xff, block[1]);
        Assert.Equal(W3BeforeLimits[4..], Hex(block)[4..]);
    }

    [Theory]
    [InlineData(0, 4, 10, 100, 6, -44, 32, 10, 200, 24)]       // threshold 0: the compressor's highest is -1
    [InlineData(-12, 21, 10, 100, 6, -44, 32, 10, 200, 24)]    // ratio past 20
    [InlineData(-12, 4, 0, 100, 6, -44, 32, 10, 200, 24)]      // attack 0
    [InlineData(-12, 4, 301, 100, 6, -44, 32, 10, 200, 24)]    // attack past 300
    [InlineData(-12, 4, 10, 49, 6, -44, 32, 10, 200, 24)]      // release under 50
    [InlineData(-12, 4, 10, 100, 21, -44, 32, 10, 200, 24)]    // make-up past 20
    [InlineData(-12, 4, 10, 100, 6, -61, 32, 10, 200, 24)]     // the expander's threshold under -60
    [InlineData(-12, 4, 10, 100, 6, -44, 15, 10, 200, 24)]     // ratio under 1.0
    [InlineData(-12, 4, 10, 100, 6, -44, 49, 10, 200, 24)]     // ratio past 3.0
    [InlineData(-12, 4, 10, 100, 6, -44, 32, 201, 200, 24)]    // attack past 200
    [InlineData(-12, 4, 10, 100, 6, -44, 32, 10, 1001, 24)]    // release past 1000
    [InlineData(-12, 4, 10, 100, 6, -44, 32, 10, 200, 61)]     // range past 60
    public void AValueOutsideTheUnitsLimitsDoesNotDecodeAndIsNeverWritten(int ct, int cr, int ca, int crl, int cm, int et, int er, int ea, int erl, int eg)
    {
        var outside = new DeviceDynamics(true, true, new(ct, cr, ca, crl, cm, et, er, ea, erl, eg));
        Assert.False(WaveFxDynamics.Decodes(outside));
        byte[] block = Convert.FromHexString(W3BeforeLimits);
        Assert.Throws<ArgumentException>(() => WaveFxDynamics.Encode(block, outside));
        Assert.Equal(W3BeforeLimits, Hex(block));
        Assert.True(WaveFxDynamics.Decodes(new DeviceDynamics(false, false, new(-12, 4, 10, 100, 6, -44, 32, 10, 200, 24))));
    }

    // --- the mapping ---

    [Fact]
    public void SettingsGoToTheUnitRoundedAndClampedIntoItsLimits()
    {
        DeviceDynamics defaults = DeviceDynamicsMapping.ToDevice(MicCompressor.Default with { Enabled = true }, MicExpander.Default);
        Assert.Equal(new DeviceDynamics(true, false, new(-18, 3, 5, 150, 0, -50, 48, 1, 150, 24)), defaults);
        var compressor = new MicCompressor { Enabled = true, Threshold = 0, Ratio = 3.5, Attack = 0.1, Release = 2000, Makeup = 24 };
        var expander = new MicExpander { Enabled = true, Threshold = -80, Ratio = 20, Range = 80, Attack = 200, Release = 2000 };
        Assert.Equal(new DeviceDynamics(true, true, new(-1, 4, 1, 1000, 20, -60, 48, 200, 1000, 60)),
            DeviceDynamicsMapping.ToDevice(compressor, expander));
        Assert.Equal(50, DeviceDynamicsMapping.ToDevice(compressor with { Release = 10 }, expander).Values!.CompressorRelease);
        Assert.Equal(3, DeviceDynamicsMapping.ToDevice(compressor with { Makeup = 2.5 }, expander).Values!.CompressorMakeup);
        // The expander's ratio in sixteenths, as the unit was seen to hold 2.1 and 2.4.
        Assert.Equal(34, DeviceDynamicsMapping.ToDevice(compressor, expander with { Ratio = 2.1 }).Values!.ExpanderRatio);
        Assert.Equal(38, DeviceDynamicsMapping.ToDevice(compressor, expander with { Ratio = 2.4 }).Values!.ExpanderRatio);
        Assert.Equal(16, DeviceDynamicsMapping.ToDevice(compressor, expander with { Ratio = 1 }).Values!.ExpanderRatio);
        // The state reports what the unit holds, on the same rule.
        Assert.Equal(compressor with { Threshold = -1, Ratio = 4, Attack = 1, Release = 1000, Makeup = 20 }, UnitDynamics.Compressor(compressor));
        Assert.Equal(expander with { Threshold = -60, Ratio = 3, Range = 60, Release = 1000 }, UnitDynamics.Expander(expander));
        Assert.True(WaveFxDynamics.Decodes(DeviceDynamicsMapping.ToDevice(compressor, expander)));
    }

    [Fact]
    public void EveryExpanderRatioTheUnitTakesComesBackToItself()
    {
        for (int tenths = 10; tenths <= 30; tenths++)
        {
            double ratio = tenths / 10.0;
            DeviceDynamics raw = DeviceDynamicsMapping.ToDevice(MicCompressor.Default, MicExpander.Default with { Ratio = ratio });
            Assert.Equal(ratio, DeviceDynamicsMapping.FromDevice(raw)!.Value.Expander.Ratio);
            Assert.Equal(ratio, UnitDynamics.Expander(MicExpander.Default with { Ratio = ratio }).Ratio);
        }
    }

    [Fact]
    public void WhatAUnitHoldsReadsBackIntoTheSoftwaresRanges()
    {
        (MicCompressor c, MicExpander e) = DeviceDynamicsMapping.FromDevice(WaveFxDynamics.Decode(Convert.FromHexString(W3BeforeLimits))!)!.Value;
        Assert.Equal(MicCompressor.Default with { Enabled = true, Threshold = -38, Ratio = 13, Attack = 73, Release = 1000, Makeup = 12 }, c);
        Assert.Equal(MicExpander.Default with { Enabled = true, Threshold = -27, Ratio = 2.4, Attack = 139, Release = 1000, Range = 60 }, e);
        // 300 ms is past the software's 200.
        Assert.Equal(200, DeviceDynamicsMapping.FromDevice(AtMaximum)!.Value.Compressor.Attack);
        Assert.Null(DeviceDynamicsMapping.FromDevice(new DeviceDynamics(true, true, new(0, 4, 10, 100, 6, -44, 32, 10, 200, 24))));
        Assert.Null(DeviceDynamicsMapping.FromDevice(DeviceDynamics.Off));
    }

    // --- each model ---

    [Fact]
    public void TheWave3Mk2WritesTheCapturedBlockWithOneReadAndOneWrite()
    {
        Mk2HardwareEqTests.Unit unit = Wave3Mk2Unit(W3BeforeLimits);
        using var device = new Wave3Mk2Device(unit);
        device.Connect();
        Assert.Equal("1.0.1", device.Firmware);
        Assert.True(device.Capabilities.HardwareDynamics);
        Assert.Equal(new DeviceEqSupport(true) { MinimumFirmware = "0.2.3" }, device.DynamicsSupport);
        Assert.Equal(WaveFxDynamics.Decode(Convert.FromHexString(W3BeforeLimits)), device.ReadDynamics(1));
        Assert.Null(device.ReadDynamics(2));
        unit.Calls.Clear();
        Assert.True(device.SetDynamics(1, AtMaximum));
        Assert.Equal(new byte[] { 0xC1, 0x41 }, unit.Calls.Select(c => c.Type));
        Assert.All(unit.Calls, c => Assert.Equal(0x0103, c.Bank));
        Assert.Equal(W3AtMaximum, Hex(Assert.Single(unit.SettingsWrites)));
        Assert.Equal(AtMaximum, device.ReadDynamics(1));
        // What the unit already holds is not written again.
        unit.Calls.Clear();
        Assert.False(device.SetDynamics(1, AtMaximum));
        Assert.Empty(unit.SettingsWrites);
        // Handing them to software clears both switches and keeps the settings.
        Assert.True(device.SetDynamics(1, DeviceDynamics.Off));
        byte[] off = unit.Blocks[0x0004];
        Assert.Equal(0x42, off[1]);   // voice tune and the low cut stay on
        Assert.Equal(W3AtMaximum[4..], Hex(off)[4..]);
        Assert.DoesNotContain(unit.Calls, c => c.Block == 0x0003);
        Assert.Throws<InvalidOperationException>(() => device.SetDynamics(2, DeviceDynamics.Off));
    }

    [Fact]
    public void TheDockMk2SwitchesItsCompressorOnAtItsBankWithItsOwnFactorySettings()
    {
        var unit = new Mk2HardwareEqTests.Unit();   // the dock on 1.0.15, bank 0x0203
        using var dock = new XlrDockMk2Device(unit);
        dock.Connect();
        Assert.True(dock.Capabilities.HardwareDynamics);
        DeviceDynamics factory = dock.ReadDynamics(1)!;
        Assert.Equal(new DeviceDynamics(false, false, new(-12, 4, 10, 100, 6, -44, 32, 10, 200, 24)), factory);
        unit.Calls.Clear();
        Assert.True(dock.SetDynamics(1, factory with { Compressor = true }));
        Assert.All(unit.Calls, c => Assert.Equal(0x0203, c.Bank));
        Assert.Equal("259000d4200a00c8001800f4040a0064000601003c00f6030de3000005f45c03f22507fd27e8", Hex(Assert.Single(unit.SettingsWrites)));
        // New settings, both on: the low cut bit (0x10) and the EQ bytes kept.
        var mine = new DeviceDynamics(true, true, new(-20, 6, 15, 250, 3, -50, 40, 5, 300, 30));
        Assert.True(dock.SetDynamics(1, mine));
        Assert.Equal("25b000ce2805002c011e00ec060f00fa000301003c00f6030de3000005f45c03f22507fd27e8", Hex(unit.SettingsWrites.Last()));
        Assert.Equal(mine, dock.ReadDynamics(1));
    }

    [Fact]
    public void TheWaveXlrMk2DrivesTheSameStruct()
    {
        var unit = new Mk2HardwareEqTests.Unit();
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.True(mk2.Capabilities.HardwareDynamics);
        var mine = new DeviceDynamics(false, true, new(-12, 4, 10, 100, 6, -40, 36, 10, 200, 24));
        Assert.True(mk2.SetDynamics(1, mine));
        Assert.Equal("253000d8240a00c8001800f4040a0064000601003c00f6030de3000005f45c03f22507fd27e8", Hex(Assert.Single(unit.SettingsWrites)));
    }

    [Fact]
    public void TheProWritesEachInputsOwnStructAndKeepsTheOtherAndTheTail()
    {
        var unit = new ProHardwareEqTests.Unit();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        Assert.True(pro.Capabilities.HardwareDynamics);
        Assert.Equal(new DeviceEqSupport(true) { MinimumFirmware = "1.0.14" }, pro.DynamicsSupport);
        // Both inputs hold the factory settings; XLR 1's compressor switch is on (0xd8).
        DeviceDynamicsValues factory = new(-12, 4, 10, 100, 6, -44, 32, 10, 200, 24);
        Assert.Equal(new DeviceDynamics(true, false, factory), pro.ReadDynamics(1));
        Assert.Equal(new DeviceDynamics(false, false, factory), pro.ReadDynamics(2));
        string idle = ProHardwareEqTests.IdleBlock;

        unit.Calls.Clear();
        var mine = new DeviceDynamics(true, true, new(-20, 6, 15, 250, 3, -50, 40, 5, 300, 30));
        Assert.True(pro.SetDynamics(2, mine));
        Assert.Equal(new byte[] { 0xC1, 0x41 }, unit.Calls.Select(c => c.Type));
        Assert.Empty(unit.Commits);
        string written = Hex(Assert.Single(unit.SettingsWrites));
        Assert.Equal(80, written.Length / 2);
        Assert.Equal(idle[..76], written[..76]);                                   // XLR 1's struct as read
        Assert.Equal("1ea004ce2805002c011e0aec060f00fa0003", written[76..112]);  // XLR 2's fields, ClipGuard and voice tune kept
        Assert.Equal(idle[112..], written[112..]);                                 // XLR 2's EQ and the tail as read
        Assert.Equal(mine, pro.ReadDynamics(2));

        // XLR 1 handed to software: 0xd8 to 0x58, the low cut, voice tune and the unnamed bit kept.
        Assert.True(pro.SetDynamics(1, DeviceDynamics.Off));
        string off = Hex(unit.SettingsWrites.Last());
        Assert.Equal("3458", off[..4]);
        Assert.Equal(idle[4..76], off[4..76]);
        Assert.Equal(written[76..], off[76..]);
        Assert.Throws<InvalidOperationException>(() => pro.SetDynamics(3, DeviceDynamics.Off));
    }

    [Fact]
    public void OlderFirmwareLeavesThemToSoftwareAndWritesNothing()
    {
        Mk2HardwareEqTests.Unit unit = Wave3Mk2Unit(W3BeforeLimits, 0, 2, 2);
        using var device = new Wave3Mk2Device(unit);
        device.Connect();
        Assert.False(device.Capabilities.HardwareDynamics);
        Assert.Equal(DeviceEqSupport.FirmwareOld, device.DynamicsSupport!.Reason);
        Assert.Null(device.ReadDynamics(1));
        Assert.Throws<InvalidOperationException>(() => device.SetDynamics(1, DeviceDynamics.Off));
        Assert.Empty(unit.SettingsWrites);
    }

    [Fact]
    public void FieldsOutsideTheLimitsLeaveThemToSoftwareWhileTheEqStillRunsOnTheUnit()
    {
        byte[] block = Convert.FromHexString(W3BeforeLimits);
        block[WaveFxDynamics.ExpanderRatioOffset] = 0x0f;
        Mk2HardwareEqTests.Unit unit = Wave3Mk2Unit(Hex(block));
        using var device = new Wave3Mk2Device(unit);
        device.Connect();
        Assert.True(device.Capabilities.HardwareEq);
        Assert.False(device.Capabilities.HardwareDynamics);
        Assert.Equal(DeviceEqSupport.UnsupportedBlock, device.DynamicsSupport!.Reason);
        Assert.Contains("compressor or expander settings", device.DynamicsSupport.Note);

        // On the Pro one input outside its limits keeps both inputs in software.
        var pro = new ProHardwareEqTests.Unit();
        pro.Blocks[0x0004][38 + WaveFxDynamics.CompressorRatioOffset] = 0;
        using var proDevice = new WaveXlrProDevice(pro);
        proDevice.Connect();
        Assert.True(proDevice.Capabilities.HardwareEq);
        Assert.False(proDevice.Capabilities.HardwareDynamics);
        Assert.Null(proDevice.ReadDynamics(1));
    }
}
