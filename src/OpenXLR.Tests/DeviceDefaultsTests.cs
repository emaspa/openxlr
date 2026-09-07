using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

public sealed class DeviceDefaultsTests
{
    [Fact]
    public void TheProBaselineIsSafeAndKeepsRouting()
    {
        var info = new DeviceInfo("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4);
        var current = new DeviceState
        {
            GainDb = 72, Gain2Db = 0, Phantom = true, Phantom2 = true, ClipGuard = true, Compressor = true,
            LowCut = true, Expander = true, VoiceTune = true, VoiceTuneStrength = 90, Mute = true,
            HpVolumeDb = 0, Hp2VolumeDb = -60, LowImpedance = true, Crossfade = 0, AuxLevelDb = 0, AuxLevelLock = true,
            OutHp1 = true, OutHp2 = false, OutUsbAux = true, OutLineOut = false,
            HpMixMonitorReturn = true, HpMixMicDirect = false, AuxReturnEnabled = true,
        };
        DeviceState b = DeviceDefaults.Baseline(info, current)!;
        Assert.Equal((30, 30), (b.GainDb, b.Gain2Db));
        Assert.False(b.Phantom || b.Phantom2 || b.ClipGuard || b.Compressor || b.LowCut || b.Expander || b.VoiceTune || b.Mute || b.LowImpedance || b.AuxLevelLock);
        Assert.Equal((-30.0, -30.0, 200, -30.0, 50), (b.HpVolumeDb, b.Hp2VolumeDb, b.Crossfade, b.AuxLevelDb, b.VoiceTuneStrength));
        // Routing and mix membership are the mixer's business and stay.
        Assert.Equal((true, false, true, false), (b.OutHp1, b.OutHp2, b.OutUsbAux, b.OutLineOut));
        Assert.Equal((true, false, true), (b.HpMixMonitorReturn, b.HpMixMicDirect, b.AuxReturnEnabled));
    }

    [Fact]
    public void OnlyModelsWithABaselineGetOne()
    {
        Assert.Null(DeviceDefaults.Baseline(new DeviceInfo("Elgato", "Wave XLR", 0x0fd9, 0x007d), new DeviceState()));
        Assert.False(DeviceDefaults.HasBaseline(new DeviceInfo("Elgato", "XLR Dock MK.2", 0x0fd9, 0x00c7)));
        Assert.True(DeviceDefaults.HasBaseline(new DeviceInfo("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4)));
    }
}
