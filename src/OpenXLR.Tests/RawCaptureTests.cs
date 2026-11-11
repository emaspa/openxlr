using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// Raw capture: the unit sends the computer an input before its own
/// processing. On the Wave XLR Pro (firmware 1.0.14) block 0x0001 bytes 94,
/// 95 and 97 hold the taps of XLR 1, XLR 2 and Line In; on the Wave XLR MK.2
/// (1.0.15) block 0x0001 byte 4 holds its one input's; in bits 0 and 1, 00
/// the raw input, every other bit carried as read (desk measurements of
/// 2026-11-11). Byte 96 showed no effect and is never written. The XLR Dock
/// MK.2 and the Wave:3 MK.2 were not measured and never get it. The daemon
/// holds the setting per unit, writes it again at every connect, and turning
/// it off puts back the tap it found, or 02.
/// </summary>
[Collection("xdg-config")]
public sealed class RawCaptureTests
{
    private static WaveXlrProDevice Pro(ProHardwareEqTests.Unit unit)
    {
        var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        return pro;
    }

    private static ProHardwareEqTests.Unit ProUnit(byte xlr1 = 0x02, byte xlr2 = 0x02, byte aux = 0x02, byte lineIn = 0x02)
    {
        var unit = new ProHardwareEqTests.Unit();
        byte[] b1 = unit.Blocks[0x0001];
        for (int i = 0; i < b1.Length; i++) b1[i] = (byte)(0x40 + i);   // every byte distinct, so a stray write shows
        (b1[94], b1[95], b1[96], b1[97]) = (xlr1, xlr2, aux, lineIn);
        return unit;
    }

    private static IEnumerable<byte[]> MixWrites(ProHardwareEqTests.Unit unit)
        => unit.Calls.Where(c => c.Type == 0x41 && c.Block == 0x0001).Select(c => c.Data);

    [Theory]
    [InlineData(0x02, 0, 0x00)]
    [InlineData(0x06, 0, 0x04)]   // bit 2, believed the capture limiter's bypass, stays
    [InlineData(0xff, 2, 0xfe)]
    [InlineData(0x00, 3, 0x03)]
    public void TheTapIsBitsZeroAndOneAlone(byte held, int tap, byte written)
    {
        Assert.Equal(written, CaptureTap.With(held, tap));
        Assert.Equal(tap, CaptureTap.Field(written));
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureTap.With(held, 4));
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(7, 2)]
    public void OffPutsBackTheTapFoundOrTwo(int? found, int restored) => Assert.Equal(restored, CaptureTap.Restored(found));

    [Fact]
    public void TheProReadsEachInputsTapFromItsOwnByte()
    {
        var unit = ProUnit(xlr1: 0x04, xlr2: 0x06, lineIn: 0x00);
        using WaveXlrProDevice pro = Pro(unit);
        Assert.True(pro.Capabilities.RawCapture);
        DeviceState state = pro.ReadState();
        Assert.Equal(((bool?)true, (bool?)false, (bool?)true), (state.RawCapture, state.RawCapture2, state.LineInRawCapture));
    }

    [Theory]
    [InlineData(RawCaptureInput.Xlr1, 94)]
    [InlineData(RawCaptureInput.Xlr2, 95)]
    [InlineData(RawCaptureInput.LineIn, 97)]
    public void AProWriteChangesTheTapBitsOfOneByteWithNoCommit(RawCaptureInput input, int offset)
    {
        var unit = ProUnit(xlr1: 0x06, xlr2: 0x06, lineIn: 0x06);
        using WaveXlrProDevice pro = Pro(unit);
        byte[] before = (byte[])unit.Blocks[0x0001].Clone();
        unit.Calls.Clear();

        Assert.Equal(2, pro.SetCaptureTap(input, CaptureTap.Raw));

        byte[] written = Assert.Single(MixWrites(unit));
        Assert.Empty(unit.Commits);
        Assert.Equal(108, written.Length);
        for (int i = 0; i < written.Length; i++)
            Assert.True(i == offset ? written[i] == 0x04 : written[i] == before[i], $"byte {i}: {written[i]:x2}, read {before[i]:x2}");
        Assert.Equal([(byte)0xC1, (byte)0x41], unit.Calls.Select(c => c.Type));

        // Held already: one read, nothing written.
        unit.Calls.Clear();
        Assert.Equal(0, pro.SetCaptureTap(input, CaptureTap.Raw));
        Assert.Empty(MixWrites(unit));
    }

    [Fact]
    public void AProBeforeFirmware1014HasNoRawCaptureAndWritesNothing()
    {
        var unit = ProUnit();
        unit.Blocks[0x0000] = Mk2HardwareEqTests.Unit.Identity(1, 0, 13);
        using WaveXlrProDevice pro = Pro(unit);
        Assert.False(pro.Capabilities.RawCapture);
        Assert.Null(pro.ReadState().RawCapture);
        unit.Calls.Clear();
        Assert.Throws<InvalidOperationException>(() => pro.SetCaptureTap(RawCaptureInput.Xlr1, 0));
        Assert.Empty(unit.Calls);
    }

    [Fact]
    public void DiagnosticsDecodeTheFourTapBytes()
    {
        byte[] block = new byte[108];
        (block[94], block[95], block[96], block[97]) = (0x02, 0x00, 0x02, 0x01);
        IReadOnlyDictionary<string, string> d = ProMixSections.Describe(block);
        Assert.Equal("processed (byte 94 02)", d["xlr1Capture"]);
        Assert.Equal("before the unit's processing (byte 95 00)", d["xlr2Capture"]);
        Assert.Equal("no effect seen (byte 96 02)", d["usbAuxCapture"]);
        Assert.Equal("after the insert return (byte 97 01)", d["lineInCapture"]);
        block[97] = 0x03;
        Assert.Equal("silent (byte 97 03)", ProMixSections.Describe(block)["lineInCapture"]);
    }

    [Fact]
    public void TheWaveXlrMk2WritesByte4AndKeepsTheCrossfade()
    {
        var unit = new Mk2HardwareEqTests.Unit();
        unit.Blocks[0x0001] = Convert.FromHexString("640102030600");
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.True(mk2.Capabilities.RawCapture);
        Assert.False(mk2.ReadState().RawCapture);
        unit.Calls.Clear();

        Assert.Equal(2, mk2.SetCaptureTap(RawCaptureInput.Xlr1, CaptureTap.Raw));

        byte[] written = Assert.Single(unit.Calls.Where(c => c.Type == 0x41).Select(c => c.Data));
        Assert.Equal(Convert.FromHexString("640102030400"), written);
        Assert.True(mk2.ReadState().RawCapture);
        Assert.Equal(100, mk2.ReadState().Crossfade);
        Assert.Throws<InvalidOperationException>(() => mk2.SetCaptureTap(RawCaptureInput.Xlr2, 0));
        Assert.Equal("before the unit's processing (byte 4 04)", mk2.DumpBlocks()["usbCapture"]);
    }

    [Fact]
    public void OnlyTheWaveXlrMk2OnFirmware1015GetsIt()
    {
        var older = new Mk2HardwareEqTests.Unit();
        older.Blocks[0x0000] = Mk2HardwareEqTests.Unit.Identity(1, 0, 14);
        using (var mk2 = new WaveXlrMk2Device(older))
        {
            mk2.Connect();
            Assert.False(mk2.Capabilities.RawCapture);
            Assert.Null(mk2.ReadState().RawCapture);
        }

        var dockUnit = new Mk2HardwareEqTests.Unit();
        using (var dock = new XlrDockMk2Device(dockUnit))
        {
            dock.Connect();
            Assert.Equal("1.0.15", dock.Firmware);
            Assert.False(dock.Capabilities.RawCapture);
            Assert.Null(dock.ReadState().RawCapture);
            Assert.False(dock.DumpBlocks().ContainsKey("usbCapture"));
            Assert.Throws<InvalidOperationException>(() => dock.SetCaptureTap(RawCaptureInput.Xlr1, 0));
        }

        var wave3Unit = new Mk2HardwareEqTests.Unit { Bank = 0x0103 };
        using var wave3 = new Wave3Mk2Device(wave3Unit);
        wave3.Connect();
        Assert.False(wave3.Capabilities.RawCapture);
        Assert.Null(wave3.ReadState().RawCapture);
    }

    [Fact]
    public void EachControlNeedsTheInputOnAUnitWithRawCapture()
    {
        var pro = new DeviceCapabilities { RawCapture = true, XlrInputs = 2, LineInput = true };
        var mk2 = new DeviceCapabilities { RawCapture = true, XlrInputs = 1 };
        foreach (string control in new[] { ControlNames.RawCapture, ControlNames.RawCapture2, ControlNames.LineInRawCapture })
        {
            Assert.Null(DeviceManager.UnsupportedControlReason(pro, control));
            Assert.Equal($"control '{control}' is not supported by the active device",
                DeviceManager.UnsupportedControlReason(pro with { RawCapture = false }, control));
        }
        Assert.Null(DeviceManager.UnsupportedControlReason(mk2, ControlNames.RawCapture));
        Assert.NotNull(DeviceManager.UnsupportedControlReason(mk2, ControlNames.RawCapture2));
        Assert.NotNull(DeviceManager.UnsupportedControlReason(mk2, ControlNames.LineInRawCapture));
    }

    [Fact]
    public void TheDaemonSetsItSavesItAndPutsBackTheTapItFound()
    {
        WithConfigDir(() =>
        {
            var unit = ProUnit(xlr1: 0x03, lineIn: 0x06);
            using var pro = new WaveXlrProDevice(unit);
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [pro]);
            manager.SweepOnce();
            Assert.True(manager.Snapshot().Capabilities!.RawCapture);
            Assert.False(manager.Snapshot().State!.RawCapture);
            Assert.False(manager.Xlr1RawCapture);

            Assert.Null(manager.Apply(ControlNames.RawCapture, Json("true")));
            Assert.Null(manager.Apply(ControlNames.LineInRawCapture, Json("true")));
            Assert.Equal(0x00, unit.Blocks[0x0001][94]);
            Assert.Equal(0x04, unit.Blocks[0x0001][97]);
            Assert.True(manager.Snapshot().State!.RawCapture);
            Assert.True(manager.Snapshot().State!.LineInRawCapture);
            Assert.False(manager.Snapshot().State!.RawCapture2);
            Assert.True(manager.Xlr1RawCapture);
            Assert.Equal("""{"0fd9:00b4":{"xlr1":3,"lineIn":2}}""", File.ReadAllText(RawCaptureFile));

            // Off: XLR 1 back to the 03 it held, Line In to its 02, bit 2 as read.
            Assert.Null(manager.Apply(ControlNames.RawCapture, Json("false")));
            Assert.Null(manager.Apply(ControlNames.LineInRawCapture, Json("false")));
            Assert.Equal(0x03, unit.Blocks[0x0001][94]);
            Assert.Equal(0x06, unit.Blocks[0x0001][97]);
            Assert.Equal("{}", File.ReadAllText(RawCaptureFile));
            Assert.False(manager.Snapshot().State!.RawCapture);
            Assert.Empty(unit.Commits);

            // A unit already raw with nothing saved goes back to 02.
            unit.Blocks[0x0001][95] = 0x00;
            Assert.Null(manager.Apply(ControlNames.RawCapture2, Json("true")));
            Assert.Null(manager.Apply(ControlNames.RawCapture2, Json("false")));
            Assert.Equal(0x02, unit.Blocks[0x0001][95]);
            Assert.Equal(0x02, unit.Blocks[0x0001][96]);
            Assert.NotNull(manager.Apply(ControlNames.RawCapture, Json("2")));
        });
    }

    [Fact]
    public void EveryConnectPutsItBackOnTheInputsTheSettingNames()
    {
        WithConfigDir(() =>
        {
            var unit = ProUnit();
            using (var pro = new WaveXlrProDevice(unit))
            using (var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [pro]))
            {
                manager.SweepOnce();
                Assert.Null(manager.Apply(ControlNames.RawCapture2, Json("true")));
            }
            // Another program put XLR 2 back to 01 meanwhile; XLR 1 is not named and stays.
            unit.Blocks[0x0001][95] = 0x01;
            unit.Blocks[0x0001][94] = 0x03;
            unit.Calls.Clear();
            using var again = new WaveXlrProDevice(unit);
            using var next = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [again]);
            next.SweepOnce();
            Assert.Equal(0x00, unit.Blocks[0x0001][95]);
            Assert.Equal(0x03, unit.Blocks[0x0001][94]);
            Assert.True(next.Snapshot().State!.RawCapture2);
            Assert.Equal("""{"0fd9:00b4":{"xlr2":1}}""", File.ReadAllText(RawCaptureFile));
            Assert.Single(MixWrites(unit));

            Assert.Null(next.Apply(ControlNames.RawCapture2, Json("false")));
            Assert.Equal(0x01, unit.Blocks[0x0001][95]);
        });
    }

    [Fact]
    public void ItIsLeftOutOfSavedSettingsAndProfiles()
    {
        DeviceState state = new() { RawCapture = true, RawCapture2 = false, LineInRawCapture = true };
        DeviceState saved = DeviceStateStore.Hardware(state);
        Assert.Null(saved.RawCapture);
        Assert.Null(saved.RawCapture2);
        Assert.Null(saved.LineInRawCapture);
    }

    private static string RawCaptureFile => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")!, "openxlr", "rawcapture.json");

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static void WithConfigDir(Action body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-test-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = false }.Save();
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
