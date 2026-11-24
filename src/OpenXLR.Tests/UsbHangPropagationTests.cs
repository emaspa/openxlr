using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// A transfer that hangs reaches the device manager from every device read
/// that can meet one: the Pro's ducking read in a poll and the block dump of
/// every model. A device that caught the hang as a failed read would carry
/// on over a closed transport, and the manager would reopen it at once
/// instead of counting the hang, waiting and setting the unit aside.
/// </summary>
[Collection("xdg-config")]
public sealed class UsbHangPropagationTests
{
    /// <summary>
    /// Another transport whose transfers to the chosen blocks hang, as the
    /// helper transport reports one: the device is closed, then
    /// <see cref="UsbHungException"/>. Later transfers find it closed.
    /// </summary>
    private sealed class HangingUsb(IUsbTransport inner) : IUsbTransport
    {
        private readonly HashSet<ushort> _hangs = [];
        public int Opens, Hangs;
        public void Hang(ushort block) => _hangs.Add(block);
        public bool IsOpen => inner.IsOpen;
        public bool Open(ushort vendorId, ushort productId) { Opens++; return inner.Open(vendorId, productId); }
        public void Close() => inner.Close();
        public void Dispose() => inner.Dispose();
        public int ControlTransfer(byte type, byte request, ushort block, ushort index, byte[] data, ushort length, uint timeout)
        {
            if (!inner.IsOpen) throw new InvalidOperationException("USB device not open");
            if (_hangs.Contains(block))
            {
                Hangs++;
                inner.Close();
                throw new UsbHungException($"USB control transfer did not return (test): wValue {block:X4}");
            }
            return inner.ControlTransfer(type, request, block, index, data, length, timeout);
        }
    }

    /// <summary>A unit that answers every read in full with zeros and takes every write.</summary>
    private sealed class ZeroUsb : IUsbTransport
    {
        public bool IsOpen { get; private set; }
        public bool Open(ushort vendorId, ushort productId) { IsOpen = true; return true; }
        public void Close() => IsOpen = false;
        public void Dispose() => Close();
        public int ControlTransfer(byte type, byte request, ushort block, ushort index, byte[] data, ushort length, uint timeout)
        {
            if ((type & 0x80) != 0) Array.Clear(data, 0, length);
            return length;
        }
    }

    [Fact]
    public void AProWhoseDuckingReadHangsThrowsTheHangFromItsPoll()
    {
        var usb = new HangingUsb(new ProHardwareEqTests.Unit());
        using var pro = new WaveXlrProDevice(usb);
        pro.Connect();
        usb.Hang(ProDucking.Block);
        Assert.Throws<UsbHungException>(() => pro.ReadState());
        Assert.Equal(1, usb.Hangs);
        Assert.False(pro.Connected);
    }

    [Fact]
    public void AProStillAnswersItsPollWhenTheDuckingBlockIsRefused()
    {
        // The unit modelled here has no block 0x0008 and stalls it.
        using var pro = new WaveXlrProDevice(new ProHardwareEqTests.Unit());
        pro.Connect();
        Assert.NotNull(pro.ReadState());
        Assert.True(pro.Connected);
    }

    public static TheoryData<string, ushort> DumpHangs => new()
    {
        { "Wave XLR Pro", 0x0001 },
        { "Wave XLR MK.2", 0x0004 },
        { "Wave XLR MK.2", Mk2Status.Block },
        { "Wave:3", 0x0000 },
        { "Wave XLR", 0x0000 },
        { "Wave XLR", 0x000A },
        { "XLR Dock", 0x0000 },
        { "XLR Dock", 0x000A },
    };

    private static (IAudioDevice Device, HangingUsb Usb) Model(string model)
    {
        HangingUsb usb;
        IAudioDevice device;
        switch (model)
        {
            case "Wave XLR Pro":
                device = new WaveXlrProDevice(usb = new HangingUsb(new ProHardwareEqTests.Unit()));
                break;
            case "Wave XLR MK.2":
                // Firmware 1.0.15: the meter and status blocks are dumped as well.
                device = new WaveXlrMk2Device(usb = new HangingUsb(new Mk2HardwareEqTests.Unit()));
                break;
            case "Wave:3":
                device = new Wave3Device(usb = new HangingUsb(new Wave3DeviceTests.FakeUsb()));
                break;
            case "Wave XLR":
                device = new WaveXlrMk1Device(usb = new HangingUsb(new ZeroUsb()), amixer: null, findCard: null);
                break;
            case "XLR Dock":
                // A card without the three controls: all of them through the config block.
                device = new XlrDockDevice(usb = new HangingUsb(new ZeroUsb()),
                    _ => new ProcessResult(0, Encoding.UTF8.GetBytes(""), "", false, false), () => 7);
                break;
            default:
                throw new ArgumentException(model);
        }
        device.Connect();
        return (device, usb);
    }

    [Theory]
    [MemberData(nameof(DumpHangs))]
    public void ABlockDumpThatHangsThrowsTheHang(string model, ushort block)
    {
        (IAudioDevice device, HangingUsb usb) = Model(model);
        using (device)
        {
            Assert.NotEmpty(device.DumpBlocks());   // every block answers
            usb.Hang(block);
            Assert.Throws<UsbHungException>(() => device.DumpBlocks());
            Assert.Equal(1, usb.Hangs);
        }
    }

    private static void WithConfigDir(Action body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-hang-propagation-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { body(); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static int HungCount(DeviceManager manager, IAudioDevice device)
        => ((HungTransferPolicy)typeof(DeviceManager).GetField("_hung", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!).HungCount(device.Info.InstanceId);

    [Fact]
    public void APollWhoseDuckingReadHangsReachesTheHangPolicy() => WithConfigDir(() =>
    {
        var usb = new HangingUsb(new ProHardwareEqTests.Unit());
        var pro = new WaveXlrProDevice(usb);
        using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [pro], time: new ManualTime());
        manager.SweepOnce();
        Assert.True(manager.Snapshot().Connected);

        usb.Hang(ProDucking.Block);
        manager.SweepOnce();
        Assert.Equal(1, usb.Hangs);
        Assert.Equal(1, HungCount(manager, pro));
        Assert.False(manager.Snapshot().Connected);
        Assert.Contains("did not return", manager.DumpBlocks()["usbFault"]);

        // The hang's reconnect delay holds the reopen.
        int opens = usb.Opens;
        manager.SweepOnce();
        Assert.Equal(opens, usb.Opens);
    });

    [Fact]
    public void ABlockDumpThatHangsReachesTheHangPolicy() => WithConfigDir(() =>
    {
        var usb = new HangingUsb(new ProHardwareEqTests.Unit());
        var pro = new WaveXlrProDevice(usb);
        using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [pro], time: new ManualTime());
        manager.SweepOnce();
        Assert.True(manager.Snapshot().Connected);

        usb.Hang(0x0001);
        IReadOnlyDictionary<string, string> dump = manager.DumpBlocks();
        Assert.Contains("did not return", dump["error"]);
        Assert.Equal(1, HungCount(manager, pro));
        Assert.False(manager.Snapshot().Connected);
        // The fault is recorded for the next archive.
        Assert.Contains("did not return", manager.DumpBlocks()["usbFault"]);

        int opens = usb.Opens;
        manager.SweepOnce();
        Assert.Equal(opens, usb.Opens);
    });
}
