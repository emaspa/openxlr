using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class DeviceControlSupportTests
{
    private static readonly DeviceCapabilities Dock = new()
    {
        Gain = true,
        Mute = true,
        HpVolume = true,
        Phantom = true,
        LowImpedance = true,
        XlrInputs = 1,
        HpOutputs = 1,
    };

    [Theory]
    [InlineData("gain")]
    [InlineData("mute")]
    [InlineData("phantom")]
    [InlineData("lowImpedance")]
    [InlineData("hpVolumeDb")]
    [InlineData("gainLock")]
    public void Dock_AcceptsSupportedControls(string control)
        => Assert.Null(DeviceManager.UnsupportedControlReason(Dock, control));

    [Theory]
    [InlineData("lowCut")]
    [InlineData("gain2")]
    [InlineData("hp2VolumeDb")]
    [InlineData("outHp1")]
    [InlineData("lineOutLevelDb")]
    [InlineData("auxLevelDb")]
    public void Dock_RejectsUnsupportedControls(string control)
        => Assert.Contains("not supported",
            DeviceManager.UnsupportedControlReason(Dock, control));

    [Fact]
    public void Pro_AcceptsSecondInputAndOutputRouting()
    {
        var pro = Dock with
        {
            LowCut = true,
            OutputRouting = true,
            AuxInput = true,
            XlrInputs = 2,
            HpOutputs = 2,
        };

        Assert.Null(DeviceManager.UnsupportedControlReason(pro, "lowCut2"));
        Assert.Null(DeviceManager.UnsupportedControlReason(pro, "hp2VolumeDb"));
        Assert.Null(DeviceManager.UnsupportedControlReason(pro, "outLineOut"));
        Assert.Null(DeviceManager.UnsupportedControlReason(pro, "lineOutLevelDb"));
        Assert.Null(DeviceManager.UnsupportedControlReason(pro, "auxLevelLock"));
    }
}
