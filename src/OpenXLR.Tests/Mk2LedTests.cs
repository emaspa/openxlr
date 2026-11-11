using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR MK.2's LED block 0x0016 on firmware 1.0.15, as measured on
/// the desk on 2026-11-11: brightness of the ring at byte 0 and of the level
/// meter at byte 2, and the ring's colour in the gain, headphone and mix
/// modes at bytes 11, 19 and 27, four bytes each in the order blue, green,
/// red, white. Every other byte is written back as read.
/// </summary>
public sealed class Mk2LedTests
{
    /// <summary>
    /// The desk unit's factory fields (bytes 0 to 2 at 0xff, white in the
    /// three slots); the untested bytes are filler that has to survive
    /// every write.
    /// </summary>
    internal static byte[] FactoryBlock()
    {
        byte[] block = new byte[WaveFxLed.Length];
        for (int i = 0; i < block.Length; i++) block[i] = (byte)(0x40 + i);
        block[0] = 0xff; block[1] = 0xff; block[2] = 0xff;
        foreach (int slot in new[] { 11, 19, 27 })
        {
            block[slot] = 0x00; block[slot + 1] = 0x00; block[slot + 2] = 0x00; block[slot + 3] = 0xff;
        }
        return block;
    }

    private static Mk2HardwareEqTests.Unit UnitWithLeds(byte major = 1, byte minor = 0, byte patch = 15, int length = WaveFxLed.Length)
    {
        var unit = new Mk2HardwareEqTests.Unit();
        unit.Blocks[0x0000] = Mk2HardwareEqTests.Unit.Identity(major, minor, patch);
        unit.Blocks[WaveFxLed.Block] = FactoryBlock()[..length];
        return unit;
    }

    private static IEnumerable<(byte Type, ushort Block, ushort Bank, byte[] Data)> LedCalls(Mk2HardwareEqTests.Unit unit)
        => unit.Calls.Where(c => c.Block == WaveFxLed.Block);

    [Fact]
    public void TheFactoryBlockDecodesAsFullBrightnessAndWhite()
    {
        Assert.Equal(new DeviceLed
        {
            RingBrightness = 100, MeterBrightness = 100,
            GainColour = "#ffffff", HeadphoneColour = "#ffffff", MixColour = "#ffffff",
        }, WaveFxLed.Decode(FactoryBlock()));
        Assert.Null(WaveFxLed.Decode(FactoryBlock()[..51]));
    }

    [Fact]
    public void TheDesksColoursEncodeAsBlueGreenRedWhite()
    {
        // Written on the desk: blue in the gain mode, red in the headphone mode, green in the mix mode.
        byte[] block = FactoryBlock();
        WaveFxLed.Encode(block, new DeviceLedChange { GainColour = "#0000FF", HeadphoneColour = "#ff0000", MixColour = "#00ff00" });
        byte[] expected = FactoryBlock();
        Convert.FromHexString("ff000000").CopyTo(expected, 11);
        Convert.FromHexString("0000ff00").CopyTo(expected, 19);
        Convert.FromHexString("00ff0000").CopyTo(expected, 27);
        Assert.Equal(expected, block);
        DeviceLed led = WaveFxLed.Decode(block)!;
        Assert.Equal(("#0000ff", "#ff0000", "#00ff00"), (led.GainColour, led.HeadphoneColour, led.MixColour));
    }

    [Fact]
    public void WhiteUsesTheWhiteLedAndAMixtureTheColourLeds()
    {
        byte[] slot = new byte[4];
        WaveFxLed.WriteColour(slot, "#FFFFFF");
        Assert.Equal("000000ff", Convert.ToHexString(slot).ToLowerInvariant());
        WaveFxLed.WriteColour(slot, "#ff8000");
        Assert.Equal("0080ff00", Convert.ToHexString(slot).ToLowerInvariant());
        Assert.Equal("#ff8000", WaveFxLed.Hex(slot));
        // A slot set elsewhere with the white LED and a colour reads as their sum.
        Assert.Equal("#ff8080", WaveFxLed.Hex(Convert.FromHexString("00008080")));
        Assert.Throws<ArgumentException>(() => WaveFxLed.WriteColour(slot, "red"));
        Assert.False(WaveFxLed.IsColour("#fff"));
        Assert.False(WaveFxLed.IsColour("#gg0000"));
        Assert.True(WaveFxLed.IsColour("#A0b1C2"));
        Assert.Equal("#a0b1c2", WaveFxLed.Normalize("#A0b1C2"));
    }

    [Fact]
    public void BrightnessIsAPercentageOf0xffHeldTo0x08()
    {
        Assert.Equal(0xff, WaveFxLed.BrightnessByte(100));
        Assert.Equal(0x08, WaveFxLed.BrightnessByte(3));
        Assert.Equal(0x08, WaveFxLed.BrightnessByte(0));
        Assert.Equal(0x80, WaveFxLed.BrightnessByte(50));
        Assert.Equal(3, WaveFxLed.Percent(0x08));
        for (int percent = 3; percent <= 100; percent++)
            Assert.Equal(percent, WaveFxLed.Percent(WaveFxLed.BrightnessByte(percent)));

        // Byte 1, which showed nothing on the desk, keeps what was read.
        byte[] block = FactoryBlock();
        WaveFxLed.Encode(block, new DeviceLedChange { RingBrightness = 3, MeterBrightness = 50 });
        byte[] expected = FactoryBlock();
        expected[0] = 0x08;
        expected[2] = 0x80;
        Assert.Equal(expected, block);
    }

    [Fact]
    public void TheWaveXlrMk2On1015ReadsAndWritesTheBlockWhole()
    {
        var unit = UnitWithLeds();
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.True(mk2.Capabilities.LedRing);
        Assert.Equal(100, mk2.ReadState().Led!.RingBrightness);

        unit.Calls.Clear();
        Assert.True(mk2.SetLed(new DeviceLedChange { MixColour = "#00ff00", RingBrightness = 3 }));
        // One read and one write of the whole block, at the unit's bank.
        Assert.Equal(new byte[] { 0xC1, 0x41 }, unit.Calls.Select(c => c.Type));
        Assert.All(unit.Calls, c => Assert.Equal((WaveFxLed.Block, (ushort)0x0203), (c.Block, c.Bank)));
        byte[] written = unit.Calls[1].Data;
        byte[] expected = FactoryBlock();
        expected[0] = 0x08;
        Convert.FromHexString("00ff0000").CopyTo(expected, 27);
        Assert.Equal(expected, written);
        Assert.Equal(new DeviceLed
        {
            RingBrightness = 3, MeterBrightness = 100,
            GainColour = "#ffffff", HeadphoneColour = "#ffffff", MixColour = "#00ff00",
        }, mk2.ReadState().Led);

        // The poll decodes the block without reading it again.
        unit.Calls.Clear();
        mk2.ReadState();
        Assert.Empty(LedCalls(unit));

        // What the unit already holds is not written again.
        Assert.False(mk2.SetLed(new DeviceLedChange { MixColour = "#00FF00" }));
        Assert.DoesNotContain(unit.Calls, c => c.Type == 0x41);
    }

    [Theory]
    [InlineData(1, 0, 14, WaveFxLed.Length)]
    [InlineData(1, 0, 0, 51)]
    [InlineData(1, 0, 15, 51)]
    public void OlderFirmwareOrAShortBlockLeavesTheLedsAlone(byte major, byte minor, byte patch, int length)
    {
        var unit = UnitWithLeds(major, minor, patch, length);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.False(mk2.Capabilities.LedRing);
        Assert.Null(mk2.ReadState().Led);
        Assert.Throws<InvalidOperationException>(() => mk2.SetLed(WaveFxLed.Factory));
        Assert.DoesNotContain(unit.Calls, c => c.Type == 0x41 && c.Block == WaveFxLed.Block);
    }

    [Fact]
    public void AnUnreadVersionLeavesTheLedsAlone()
    {
        var unit = UnitWithLeds();
        unit.Blocks.Remove(0x0000);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.False(mk2.Capabilities.LedRing);
        Assert.Empty(LedCalls(unit));
    }

    [Fact]
    public void TheDockAndTheWave3Mk2NeverTouchTheBlock()
    {
        var dockUnit = UnitWithLeds();
        using (var dock = new XlrDockMk2Device(dockUnit))
        {
            dock.Connect();
            dock.ReadState();
            Assert.False(dock.Capabilities.LedRing);
            Assert.Null(dock.ReadState().Led);
            Assert.Throws<InvalidOperationException>(() => dock.SetLed(WaveFxLed.Factory));
            dock.DumpBlocks();
        }
        Assert.Empty(LedCalls(dockUnit));

        var micUnit = UnitWithLeds(0, 2, 3);
        micUnit.Bank = 0x0103;
        using (var mic = new Wave3Mk2Device(micUnit))
        {
            mic.Connect();
            Assert.False(mic.Capabilities.LedRing);
            mic.DumpBlocks();
        }
        Assert.Empty(LedCalls(micUnit));
    }

    [Fact]
    public void AReconnectReadsTheBlockAgain()
    {
        var unit = UnitWithLeds();
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        unit.Blocks[WaveFxLed.Block][0] = 0x80;
        mk2.Connect();
        Assert.Equal(50, mk2.ReadState().Led!.RingBrightness);
        mk2.Disconnect();
        Assert.False(mk2.Capabilities.LedRing);
    }

    [Theory]
    [InlineData("ringBrightness", "40", null)]
    [InlineData("meterBrightness", "0", null)]
    [InlineData("meterBrightness", "101", "setLed: meterBrightness must be a whole number from 0 to 100")]
    [InlineData("ringBrightness", "4.5", "setLed: ringBrightness must be a whole number from 0 to 100")]
    [InlineData("ringBrightness", "\"40\"", "setLed: ringBrightness must be a whole number from 0 to 100")]
    [InlineData("gainColour", "\"#0000ff\"", null)]
    [InlineData("mixColour", "\"blue\"", "setLed: mixColour must be a colour as #rrggbb")]
    [InlineData("headphoneColour", "12", "setLed: headphoneColour must be a colour as #rrggbb")]
    [InlineData("reset", "null", null)]
    [InlineData("byte1", "8", "setLed: control must be ringBrightness, meterBrightness, gainColour, headphoneColour, mixColour or reset")]
    public void SetLedIsValidated(string control, string value, string? error)
    {
        using var json = JsonDocument.Parse(value);
        var cmd = new Command { Cmd = "setLed", Control = control, Value = json.RootElement.Clone() };
        Assert.Equal(error, CommandValidation.CheckLed(cmd));
        using var mixer = new OpenXLR.Core.Mixing.Mixer();
        Assert.Equal(error, CommandValidation.Check(cmd, mixer, _ => null));
    }

    [Fact]
    public void ACommandBecomesTheChangeItNames()
    {
        static DeviceLedChange? Change(string control, string value)
        {
            using var json = JsonDocument.Parse(value);
            return CommandValidation.LedChange(new Command { Cmd = "setLed", Control = control, Value = json.RootElement.Clone() });
        }
        Assert.Equal(new DeviceLedChange { RingBrightness = 40 }, Change("ringBrightness", "40"));
        Assert.Equal(new DeviceLedChange { MeterBrightness = 7 }, Change("meterBrightness", "7"));
        Assert.Equal(new DeviceLedChange { GainColour = "#0000ff" }, Change("gainColour", "\"#0000FF\""));
        Assert.Equal(new DeviceLedChange { HeadphoneColour = "#ff0000" }, Change("headphoneColour", "\"#ff0000\""));
        Assert.Equal(new DeviceLedChange { MixColour = "#00ff00" }, Change("mixColour", "\"#00ff00\""));
        Assert.Null(Change("reset", "null"));
    }
}

/// <summary>
/// The daemon's side of the LEDs: setLed writes the active unit and keeps
/// what was set for it in leds.json, a connect writes that back, and reset
/// writes the factory LEDs and forgets it.
/// </summary>
// leds.json lives under the configuration directory, which these tests redirect.
[Collection("xdg-config")]
public sealed class Mk2LedDaemonTests
{
    private static void WithConfigDir(Action<string> body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-leds-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { body(Path.Combine(dir, "openxlr", "leds.json")); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static DeviceManager Manager(IAudioDevice unit)
    {
        var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [unit]);
        manager.SweepOnce();
        Assert.True(unit.Connected);
        return manager;
    }

    private static Mk2HardwareEqTests.Unit Unit()
    {
        var unit = new Mk2HardwareEqTests.Unit();
        unit.Blocks[WaveFxLed.Block] = Mk2LedTests.FactoryBlock();
        return unit;
    }

    private static string? Set(DeviceManager manager, string control, string value)
    {
        using var json = JsonDocument.Parse(value);
        var cmd = new Command { Cmd = "setLed", Control = control, Value = json.RootElement.Clone() };
        return CommandValidation.CheckLed(cmd) ?? manager.ApplyLed(CommandValidation.LedChange(cmd));
    }

    [Fact]
    public void SetLedWritesTheUnitKeepsItAndAConnectWritesItBack() => WithConfigDir(path =>
    {
        var usb = Unit();
        using (DeviceManager manager = Manager(new WaveXlrMk2Device(usb)))
        {
            Assert.True(manager.Snapshot().Capabilities!.LedRing);
            Assert.Null(Set(manager, "gainColour", "\"#0000FF\""));
            Assert.Null(Set(manager, "meterBrightness", "50"));
            Assert.Equal("ff000000", Convert.ToHexString(usb.Blocks[WaveFxLed.Block][11..15]).ToLowerInvariant());
            Assert.Equal(0x80, usb.Blocks[WaveFxLed.Block][2]);
            DeviceLed led = manager.Snapshot().State!.Led!;
            Assert.Equal(("#0000ff", 50), (led.GainColour, led.MeterBrightness));
            // Kept per unit, only the fields set.
            Assert.Equal(new DeviceLedChange { GainColour = "#0000ff", MeterBrightness = 50 }, DeviceManager.LoadLeds()["0fd9:00b6"]);
            // The file holds the fields set and nothing derived from them.
            using (JsonDocument file = JsonDocument.Parse(File.ReadAllText(path)))
                Assert.Equal(["gainColour", "meterBrightness"],
                    file.RootElement.GetProperty("0fd9:00b6").EnumerateObject().Select(p => p.Name).Order());
            // Neither a profile nor a saved state carries them.
            Assert.Null(manager.SavedState()!.Led);
        }

        // Set back to white elsewhere: the next connect writes OpenXLR's blue again, and only that.
        usb.Blocks[WaveFxLed.Block][11] = 0x00;
        usb.Blocks[WaveFxLed.Block][14] = 0xff;
        usb.Blocks[WaveFxLed.Block][27] = 0x12;
        usb.Calls.Clear();
        using (DeviceManager manager = Manager(new WaveXlrMk2Device(usb)))
        {
            Assert.Equal("ff000000", Convert.ToHexString(usb.Blocks[WaveFxLed.Block][11..15]).ToLowerInvariant());
            Assert.Equal(0x12, usb.Blocks[WaveFxLed.Block][27]);
            Assert.Single(usb.Calls, c => c.Type == 0x41 && c.Block == WaveFxLed.Block);

            // Reset writes the factory LEDs and forgets what was kept.
            Assert.Null(Set(manager, "reset", "null"));
            byte[] block = usb.Blocks[WaveFxLed.Block];
            Assert.Equal(new byte[] { 0xff, 0xff, 0xff }, block[..3]);
            Assert.All(new[] { 11, 19, 27 }, slot => Assert.Equal("000000ff", Convert.ToHexString(block[slot..(slot + 4)]).ToLowerInvariant()));
            Assert.Empty(DeviceManager.LoadLeds());
            Assert.True(File.Exists(path));
        }
    });

    [Fact]
    public void AUnitWithoutLedsRefusesSetLedAndKeepsNothing() => WithConfigDir(path =>
    {
        var usb = Unit();
        using DeviceManager manager = Manager(new XlrDockMk2Device(usb));
        Assert.False(manager.Snapshot().Capabilities!.LedRing);
        Assert.Equal("setLed: the active device has no LEDs OpenXLR can set", Set(manager, "ringBrightness", "40"));
        Assert.False(File.Exists(path));
        Assert.DoesNotContain(usb.Calls, c => c.Block == WaveFxLed.Block);
    });

    [Fact]
    public void AHandEditOutsideTheRangesIsDroppedFieldByField() => WithConfigDir(path =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"0fd9:00b6":{"ringBrightness":400,"meterBrightness":20,"gainColour":"blue","mixColour":"#00FF00"}}""");
        var usb = Unit();
        using DeviceManager manager = Manager(new WaveXlrMk2Device(usb));
        byte[] block = usb.Blocks[WaveFxLed.Block];
        Assert.Equal(0xff, block[0]);
        Assert.Equal(WaveFxLed.BrightnessByte(20), block[2]);
        Assert.Equal("000000ff", Convert.ToHexString(block[11..15]).ToLowerInvariant());
        Assert.Equal("00ff0000", Convert.ToHexString(block[27..31]).ToLowerInvariant());
    });
}
