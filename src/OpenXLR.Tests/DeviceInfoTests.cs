using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

public sealed class DeviceInfoTests
{
    /// <summary>
    /// The daemon finds a device's capture node and card by the model as udev
    /// writes it into the node name: spaces and colons become underscores.
    /// The Wave:3 case is the node name in an owner's dump
    /// (LukasParke/wave3-research, pipewire/pactl-source-wave3.txt).
    /// </summary>
    [Theory]
    [InlineData("Wave XLR", "Wave_XLR")]
    [InlineData("Wave XLR Pro", "Wave_XLR_Pro")]
    [InlineData("XLR Dock", "XLR_Dock")]
    [InlineData("Wave:3", "Wave_3")]
    public void TheNodeNameFragmentSpellsTheModelLikeUdev(string model, string fragment)
    {
        var info = new DeviceInfo("Elgato", model, 0x0fd9, 0x0000);
        Assert.Equal(fragment, info.NodeNameFragment);
        Assert.Equal($"Elgato {model}", info.DisplayName);
    }
}
