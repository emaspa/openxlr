using OpenXLR.Core;
using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

public sealed class XlrDockMk2BankTests
{
    private sealed class Usb : IUsbTransport
    {
        public bool IsOpen { get; private set; }
        public ushort Bank = 0x0103;
        public int? WriteError;
        public Func<ushort, ushort, int?>? Reply;
        public readonly List<(byte Type, ushort Block, ushort Bank)> Calls = [];
        public bool Open(ushort vendor, ushort product) { IsOpen = true; return true; }
        public void Close() => IsOpen = false;
        public void Dispose() => Close();
        public int ControlTransfer(byte type, byte request, ushort block, ushort bank, byte[] data, ushort length, uint timeout)
        {
            Assert.True(IsOpen);
            Assert.Equal(1, request);
            Calls.Add((type, block, bank));
            if (type == 0x41 && WriteError is int error) return error;
            if (Reply?.Invoke(block, bank) is int result) return result;
            if (bank != Bank) return -9;
            if (type == 0xC1) Array.Clear(data);
            return length;
        }
    }

    [Theory]
    [InlineData(0x0103)]
    [InlineData(0x0203)]
    public void DetectsBankByReadingAllBlocksAndKeepsItForControls(int bank)
    {
        var usb = new Usb { Bank = (ushort)bank };
        using var device = new XlrDockMk2Device(usb);
        device.Connect();
        Assert.True(device.Connected);
        Assert.All(usb.Calls, call => Assert.Equal(0xC1, call.Type));
        Assert.Equal(new ushort[] { 4, 5, 1 }, usb.Calls.Where(c => c.Bank == bank).Select(c => c.Block));
        // The ordinary bank is not worth a note; the other one names itself.
        if (bank == 0x0103) Assert.Null(device.ConnectionNote);
        else Assert.Contains($"0x{bank:x4}", device.ConnectionNote);
        usb.Calls.Clear();
        device.ReadState();
        device.SetGainDb(30);
        device.SetHpVolumeDb(-12);
        device.SetCrossfade(100);
        Assert.All(usb.Calls, call => Assert.Equal(bank, call.Bank));
        Assert.Equal(3, usb.Calls.Count(c => c.Type == 0x41));
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(4, 11)]
    [InlineData(5, 1)]
    [InlineData(1, 1)]
    [InlineData(5, -9)]
    public void IncompletePreferredBankCannotAuthorizeWrites(int block, int length)
    {
        var usb = new Usb { Bank = 0x0203, Reply = (b, bank) => bank == 0x0103
            ? b == block ? length : b == 4 ? 38 : b == 5 ? 2 : 6 : null };
        using var device = new XlrDockMk2Device(usb);
        device.Connect();
        usb.Calls.Clear();
        device.SetMute(true);
        Assert.All(usb.Calls, c => Assert.Equal(0x0203, c.Bank));
    }

    [Theory]
    [InlineData(-4)] // disconnected
    [InlineData(-7)] // timeout
    [InlineData(-1)] // I/O error, including the existing retry
    public void TransportFailuresDoNotSelectAnotherBank(int error)
    {
        var usb = new Usb { Reply = (_, _) => error };
        using var device = new XlrDockMk2Device(usb);
        Assert.Throws<InvalidOperationException>(device.Connect);
        Assert.False(device.Connected);
        Assert.Null(device.ConnectionNote);
        Assert.All(usb.Calls, c => Assert.Equal(0x0103, c.Bank));
        Assert.Equal(error == -1 ? 2 : 1, usb.Calls.Count);
    }

    [Fact]
    public void TransientIoErrorRetriesTheSameBank()
    {
        int calls = 0;
        var usb = new Usb { Reply = (_, _) => ++calls == 1 ? -1 : null };
        using var device = new XlrDockMk2Device(usb);
        device.Connect();
        Assert.Equal(4, usb.Calls.Count);
        Assert.All(usb.Calls, c => Assert.Equal(0x0103, c.Bank));
    }

    [Fact]
    public void ReconnectDetectsTheNewUnitInsteadOfKeepingTheOldBank()
    {
        var usb = new Usb { Bank = 0x0203 };
        using var device = new XlrDockMk2Device(usb);
        device.Connect();
        device.Disconnect();
        Assert.Null(device.ConnectionNote);
        usb.Bank = 0x0103;
        usb.Calls.Clear();
        device.Connect();
        Assert.Equal(3, usb.Calls.Count);
        Assert.All(usb.Calls, c => Assert.Equal(0x0103, c.Bank));
    }

    [Fact]
    public void BothBanksFailingKeepsThePreferredBankOpenWithoutAnyWrites()
    {
        var usb = new Usb { Reply = (_, _) => -9 };
        using var device = new XlrDockMk2Device(usb);
        device.Connect();
        // A third variant has to reach the block dump to be understood, so
        // the device stays open and says so instead of being refused.
        Assert.True(device.Connected);
        Assert.Contains("0x0103", device.ConnectionNote);
        Assert.Contains("0x0203", device.ConnectionNote);
        Assert.All(usb.Calls, c => Assert.Equal(0xC1, c.Type));
        Assert.Equal(2, usb.Calls.Count);
        usb.Calls.Clear();
        Assert.Throws<InvalidOperationException>(device.ReadState);
        Assert.All(usb.Calls, c => Assert.Equal(0x0103, c.Bank));
    }

    // The reader tolerates a block shorter than the capture as long as every
    // offset it indexes is there. Detection must not turn such a device away.
    [Fact]
    public void AShortButUsableBlockStillConnectsOnTheBankThatAnswers()
    {
        var usb = new Usb { Reply = (b, bank) => bank == 0x0203 ? -9 : b == 4 ? 11 : null };
        using var device = new XlrDockMk2Device(usb);
        device.Connect();
        Assert.True(device.Connected);
        usb.Calls.Clear();
        Assert.Equal(0, device.ReadState().GainDb);
        Assert.All(usb.Calls, c => Assert.Equal(0x0103, c.Bank));
    }

    [Fact]
    public void HungTransportClosesWithoutProbingTheAlternative()
    {
        var usb = new Usb { Reply = (_, _) => throw new UsbHungException("timed out") };
        using var device = new XlrDockMk2Device(usb);
        Assert.Throws<UsbHungException>(device.Connect);
        Assert.False(device.Connected);
        Assert.Single(usb.Calls);
    }

    [Fact]
    public void AWriteFailureNeverSwitchesTheSelectedBank()
    {
        var usb = new Usb();
        using var device = new XlrDockMk2Device(usb);
        device.Connect();
        usb.Calls.Clear();
        usb.WriteError = -9;
        Assert.Throws<InvalidOperationException>(() => device.SetMute(true));
        Assert.Contains(usb.Calls, c => c.Type == 0x41);
        Assert.All(usb.Calls, c => Assert.Equal(0x0103, c.Bank));
    }
}
