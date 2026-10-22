using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorMuteDeferredPulseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void APhysicalConsumeDeferredFromABlanketPulseDoesNotBecomeANewKey(bool secondKey, bool identityVerification)
    {
        using var fixture = new IndependentSinks();
        fixture.SelectExternalOutput(beforeRegistry: identityVerification);
        fixture.SetNative("OpenXLR_ch_system", true);
        fixture.SetNative("external", true);
        fixture.DeferNextPhysicalPulse(identityVerification);
        fixture.Publish();

        fixture.Mixer.SyncOwnSinkLevels(out _);
        fixture.Mixer.SyncDeviceVolumes();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.True(fixture.Native("external"));
        Assert.False(fixture.Native("OpenXLR_ch_system"));

        if (secondKey) fixture.SetNative("external", false);
        fixture.Publish();
        fixture.Mixer.SyncOwnSinkLevels(out _);
        fixture.Mixer.SyncDeviceVolumes();

        Assert.Equal(!secondKey, fixture.Mixer.MonitorMuted);
        Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external"));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("browser")]
    public void OnlyTheDefaultApplicationControlsMonitoringWhileOtherApplicationsAreRepaired(string id)
    {
        using var fixture = new IndependentSinks();
        string active = "OpenXLR_ch_" + id;
        string other = "OpenXLR_ch_" + (id == "system" ? "browser" : "system");
        fixture.SetDefault(active);
        fixture.SetNative(other, true);
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native(other));

        fixture.SetNative(active, true);
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native(active));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARepairedDefaultApplicationIsNotCountedAgainAfterPhysicalAcknowledgement(bool readFailure)
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.SetNative("OpenXLR_ch_system", true);
        fixture.SetNative("external", true);
        fixture.DeferNextPulse("OpenXLR_ch_system", readFailure);
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("OpenXLR_ch_system"));
        Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external"));

        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        fixture.SetNative("OpenXLR_ch_system", true);
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchingDefaultForgetsOldApplicationRetriesWithoutInventingKeys(bool newDefaultMuted)
    {
        using var fixture = new IndependentSinks();
        fixture.SetNative("OpenXLR_ch_system", true);
        fixture.DeferNextPulse("OpenXLR_ch_system");
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("OpenXLR_ch_system"));

        fixture.SetDefault("OpenXLR_ch_browser");
        fixture.SetNative("OpenXLR_ch_system", true);
        fixture.SetNative("OpenXLR_ch_browser", newDefaultMuted);
        fixture.Sweep();
        Assert.Equal(newDefaultMuted, fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("OpenXLR_ch_system"));
        fixture.Sweep();
        Assert.Equal(newDefaultMuted, fixture.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredPhysicalAcknowledgementsKeepANewerExplicitMonitorState(bool muted)
    {
        using var fixture = new IndependentSinks();
        fixture.DeferBlanketPhysicalPulse();
        fixture.Mixer.SetMonitorMuted(muted);
        fixture.Sweep();
        Assert.Equal(muted, fixture.Mixer.MonitorMuted);
        Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external"));

        fixture.SetNative("external", !muted);
        fixture.Sweep();
        Assert.Equal(!muted, fixture.Mixer.MonitorMuted);
    }

    [Fact]
    public void DeferredPhysicalAcknowledgementsKeepANewerAtomicApiToggle()
    {
        using var fixture = new IndependentSinks();
        fixture.DeferBlanketPhysicalPulse();
        fixture.Mixer.ToggleMonitorMuted();
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        fixture.SetNative("external", true);
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredPhysicalAcknowledgementsDoNotCarryAcrossSelectionOrReplacement(bool replacement)
    {
        using var fixture = new IndependentSinks();
        fixture.DeferBlanketPhysicalPulse();
        if (replacement)
        {
            fixture.ReplaceExternal();
            fixture.Sweep(2);
            Assert.True(fixture.Mixer.MonitorMuted);
            Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external"));
            fixture.SetNative("external", false);
            fixture.Sweep(2);
        }
        else
        {
            fixture.Mixer.SetMonitorOutputs([]);
            fixture.Mixer.SetMonitorOutputs(["external"]);
            fixture.Sweep();
            Assert.True(fixture.Mixer.MonitorMuted);
            fixture.SetNative("external", false);
            fixture.Sweep();
        }
        Assert.False(fixture.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleDeferredPhysicalOutputsKeepOneBlanketAction(bool secondKey)
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external", "external2"]);
        fixture.SetNative("OpenXLR_ch_system", true);
        foreach (string name in new[] { "external", "external2" })
        {
            fixture.SetNative(name, true);
            fixture.DeferNextPulse(name);
        }
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        if (secondKey)
            foreach (string name in new[] { "external", "external2" }) fixture.SetNative(name, false);
        fixture.Sweep();
        Assert.Equal(!secondKey, fixture.Mixer.MonitorMuted);
        Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external"));
        Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external2"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AFailedIdentityReadConfirmsNativeParityDespiteALateRegistryMute(bool registryMuted, bool nativeMuted)
    {
        using var fixture = new IndependentSinks();
        fixture.SelectExternalOutput(beforeRegistry: true);
        fixture.SetNative("OpenXLR_ch_system", true);
        fixture.SetNative("external", nativeMuted);
        fixture.DeferNextPhysicalPulse(identityVerification: true);
        fixture.Sweep(externalRegistryMuted: registryMuted);
        Assert.True(fixture.Mixer.MonitorMuted);

        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external"));
    }

    [Fact]
    public void AFailedApplicationReadDoesNotCreditAnAlreadyConsumedStaleRegistryFlag()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.SetDefault("OpenXLR_ch_browser");
        fixture.SetNative("OpenXLR_ch_browser", true);
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("OpenXLR_ch_browser"));

        int before = fixture.Calls.Length;
        fixture.SetNative("external", false);
        fixture.DeferNextPulse("OpenXLR_ch_browser", readFailure: true);
        fixture.Sweep(browserRegistryMuted: true);
        string[] calls = fixture.Calls[before..];
        Assert.Contains("get-sink-mute OpenXLR_ch_browser", calls);
        Assert.Single(calls, call => call == "--format=json list sinks");
        Assert.DoesNotContain("set-sink-mute OpenXLR_ch_browser 0", calls);
        Assert.False(fixture.Mixer.MonitorMuted);
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void APhysicalBlanketPulseKeepsMixedOriginalMixMutesWhenTheDefaultApplicationIsDeferred(bool firstMuted, bool readFailure)
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        Assert.Null(fixture.Mixer.SetMonitorFeed("external", "monitor+monitor_b"));
        fixture.Mixer.SetMixMuted("monitor", firstMuted);
        fixture.Mixer.SetMixMuted("monitor_b", !firstMuted);
        foreach (string name in new[] { "OpenXLR_ch_system", "OpenXLR_ch_browser" })
            fixture.SetNative(name, true);
        fixture.DeferNextPulse("OpenXLR_ch_system", readFailure);
        fixture.SetNative("external", true);
        fixture.SetNative("OpenXLR_mix_monitor", true);
        fixture.SetNative("OpenXLR_mix_monitor_b", true);
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        CheckMixes();
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        CheckMixes();
        fixture.Mixer.ToggleMonitorMuted();
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        CheckMixes();

        void CheckMixes()
        {
            Assert.Equal([firstMuted ? "monitor" : "monitor_b"], fixture.Mixer.ExportSettings().MixMuted);
            Assert.Equal(firstMuted, fixture.Native("OpenXLR_mix_monitor"));
            Assert.Equal(!firstMuted, fixture.Native("OpenXLR_mix_monitor_b"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnmuteObservedDuringIdentityFailureKeepsTheNextKeyUnlessTheOutputWasReplaced(bool replacement)
    {
        using var fixture = new IndependentSinks();
        fixture.SetNative("external", true);
        fixture.SelectExternalOutput(beforeRegistry: true);
        fixture.SetNative("external", false);
        fixture.DeferNextPhysicalPulse(identityVerification: true);
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external"));

        if (replacement) fixture.ReplaceExternal();
        fixture.SetNative("external", true);
        fixture.Sweep(replacement ? 2 : 1);
        Assert.Equal(!replacement, fixture.Mixer.MonitorMuted);
        Assert.True(fixture.Native("external"));
        fixture.Sweep(replacement ? 2 : 1);
        Assert.Equal(!replacement, fixture.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ABlanketMuteWithAPhysicalDefaultKeepsTheIndividualMonitorMixMutes(bool lateRegistry, bool refusedConsume)
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.SetDefault("external");
        fixture.Mixer.SetMixMuted("monitor_b", true);
        foreach (string name in new[] { "OpenXLR_ch_system", "OpenXLR_ch_browser", "OpenXLR_mix_monitor", "OpenXLR_mix_monitor_b", "external" })
            fixture.SetNative(name, true);
        if (refusedConsume) fixture.DeferNextPulse("external");
        fixture.Publish(externalRegistryMuted: lateRegistry ? false : null);
        fixture.Mixer.SyncOwnSinkLevels(out _);
        Assert.Equal(!refusedConsume, fixture.Mixer.MonitorMuted);
        CheckMixes();
        fixture.Mixer.SyncDeviceVolumes();
        Assert.True(fixture.Mixer.MonitorMuted);
        CheckMixes();
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        fixture.Mixer.SetMonitorMuted(false);
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        CheckMixes();

        void CheckMixes()
        {
            Assert.Equal(["monitor_b"], fixture.Mixer.ExportSettings().MixMuted);
            Assert.False(fixture.Native("OpenXLR_mix_monitor"));
            Assert.True(fixture.Native("OpenXLR_mix_monitor_b"));
        }
    }

    [Fact]
    public void ASecondPhysicalKeyCancelsAnUncountedBlanketActionWithoutChangingMixChoices()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.SetDefault("external");
        fixture.Mixer.SetMixMuted("monitor_b", true);
        fixture.SetNative("OpenXLR_ch_browser", true);
        fixture.SetNative("OpenXLR_mix_monitor", true);
        fixture.SetNative("external", true);
        fixture.DeferNextPulse("external");
        fixture.Publish();
        fixture.Mixer.SyncOwnSinkLevels(out _);
        Assert.False(fixture.Mixer.MonitorMuted);
        fixture.SetNative("external", false);
        fixture.Publish();
        fixture.Mixer.SyncDeviceVolumes();
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.Equal(["monitor_b"], fixture.Mixer.ExportSettings().MixMuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANonDefaultApplicationMuteWithoutAPhysicalPulseKeepsAnIndependentNativeMixEdit(bool staleRegistry)
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.SetDefault("external");
        fixture.SetNative("OpenXLR_ch_browser", true);
        fixture.SetNative("OpenXLR_mix_monitor", true);
        fixture.Sweep(externalRegistryMuted: staleRegistry);
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("OpenXLR_ch_browser"));
        Assert.Equal(["monitor"], fixture.Mixer.ExportSettings().MixMuted);
        Assert.True(fixture.Native("OpenXLR_mix_monitor"));
    }

    [Fact]
    public void ADeferredPhysicalBlanketAcknowledgementKeepsANewerManualMixChoice()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.SetDefault("external");
        fixture.Mixer.SetMixMuted("monitor_b", true);
        fixture.SetNative("OpenXLR_ch_browser", true);
        fixture.SetNative("OpenXLR_mix_monitor", true);
        fixture.SetNative("external", true);
        fixture.DeferNextPulse("external");
        fixture.Publish();
        fixture.Mixer.SyncOwnSinkLevels(out _);
        fixture.Mixer.SetMixMuted("monitor", true);
        fixture.Mixer.SetMixMuted("monitor_b", false);
        fixture.Mixer.SyncDeviceVolumes();
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.Equal(["monitor"], fixture.Mixer.ExportSettings().MixMuted);
        Assert.True(fixture.Native("OpenXLR_mix_monitor"));
        Assert.False(fixture.Native("OpenXLR_mix_monitor_b"));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void AnOldPhysicalAcknowledgementKeepsALaterIndependentNativeMixEdit(bool credited, bool refusedAgain, bool identityVerification)
    {
        using var fixture = new IndependentSinks();
        if (credited) fixture.DeferBlanketPhysicalPulse();
        else
        {
            fixture.SelectExternalOutput(beforeRegistry: identityVerification);
            fixture.SetDefault("external");
            fixture.SetNative("external", true);
            fixture.DeferNextPhysicalPulse(identityVerification);
            fixture.Publish();
            fixture.Mixer.SyncDeviceVolumes();
            Assert.False(fixture.Mixer.MonitorMuted);
        }
        fixture.SetNative("OpenXLR_ch_browser", true);
        fixture.SetNative("OpenXLR_mix_monitor", true);
        if (refusedAgain) fixture.DeferNextPulse("external");
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.Equal(["monitor"], fixture.Mixer.ExportSettings().MixMuted);
        Assert.True(fixture.Native("OpenXLR_mix_monitor"));
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.Equal(["monitor"], fixture.Mixer.ExportSettings().MixMuted);
    }

    [Fact]
    public void IdentityBackoffSkipsFallbackMuteReadsForEverySelectedOutput()
    {
        using var fixture = new IndependentSinks();
        fixture.DeferNextPhysicalPulse(identityVerification: true);
        fixture.Mixer.SetMonitorOutputs(["external", "external2"]);
        fixture.SetNative("external", true);
        fixture.SetNative("external2", true);
        for (int sweep = 0; sweep < 2; sweep++)
        {
            fixture.DeferNextPhysicalPulse(identityVerification: true);
            fixture.Sweep();
            Assert.False(fixture.Mixer.MonitorMuted);
        }
        int before = fixture.Calls.Length;
        for (int sweep = 0; sweep < 5; sweep++) fixture.Sweep();
        string[] calls = fixture.Calls[before..];
        Assert.DoesNotContain("--format=json list sinks", calls);
        Assert.DoesNotContain(calls, call => call.StartsWith("get-sink-mute ", StringComparison.Ordinal));
        Assert.True(fixture.Native("external"));
        Assert.True(fixture.Native("external2"));

        fixture.Now = 1_000;
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external"));
        Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external2"));
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
    }

    [Fact]
    public void AnApplicationPulseAlsoConfirmsPhysicalFlagsWhoseRegistryEchoIsLate()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.SetNative("OpenXLR_ch_system", true);
        fixture.SetNative("external", true);
        fixture.Publish(externalRegistryMuted: false);

        Assert.True(fixture.Mixer.SyncOwnSinkLevels(out _));
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("OpenXLR_ch_system"));
        Assert.True(fixture.Native("external"));
        fixture.Mixer.SyncDeviceVolumes();
        Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external"));
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.Contains("set-sink-mute external toggle", fixture.Calls);

        fixture.Sweep(externalRegistryMuted: true);
        Assert.True(fixture.Mixer.MonitorMuted);
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
    }

    [Fact]
    public void AGraphFailureDoesNotSuppressTheNextPhysicalKeyAsPartOfAnOldApplicationPulse()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SyncDeviceVolumes();
        fixture.SetNative("OpenXLR_ch_system", true);
        fixture.Publish();
        Assert.True(fixture.Mixer.SyncOwnSinkLevels(out _));
        Assert.False(fixture.Native("OpenXLR_ch_system"));
        Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external"));
        fixture.GraphUnavailable(true);
        Assert.True(fixture.Mixer.SyncDeviceVolumes()); // Save the newly owned native baseline even without a graph.
        Assert.False(fixture.Mixer.ExportSettings().MonitorOutputMutes["external"]);
        Assert.False(fixture.Mixer.SyncDeviceVolumes());
        Assert.True(fixture.Mixer.MonitorMuted);
        fixture.GraphUnavailable(false);

        fixture.SetNative("external", false);
        fixture.Publish();
        Assert.True(fixture.Mixer.SyncDeviceVolumes());
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARefusedMonitorMixRestorationKeepsItsIntentUntilNativeReadbackConfirmsIt(bool staleRegistry)
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMixMuted("monitor_b", true);
        foreach (string name in new[] { "OpenXLR_ch_system", "OpenXLR_mix_monitor", "external" })
            fixture.SetNative(name, true);
        fixture.DeferNextReset("OpenXLR_mix_monitor");
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("OpenXLR_ch_system"));
        Assert.True(fixture.Native("OpenXLR_mix_monitor"));
        Assert.Equal(["monitor_b"], fixture.Mixer.ExportSettings().MixMuted);

        fixture.DeferNextReset("OpenXLR_mix_monitor");
        fixture.Publish(monitorRegistryMuted: staleRegistry ? false : null);
        fixture.Mixer.SyncOwnSinkLevels(out _);
        fixture.Mixer.SyncDeviceVolumes();
        Assert.True(fixture.Native("OpenXLR_mix_monitor"));
        Assert.Equal(["monitor_b"], fixture.Mixer.ExportSettings().MixMuted);
        fixture.Sweep();
        Assert.False(fixture.Native("OpenXLR_mix_monitor"));

        // A successful write is still pending until a later native read.
        fixture.SetNative("OpenXLR_mix_monitor", true);
        fixture.Publish(monitorRegistryMuted: false);
        fixture.Mixer.SyncMonitorVolumes();
        Assert.False(fixture.Native("OpenXLR_mix_monitor"));
        fixture.Sweep();
        fixture.Mixer.SetMonitorMuted(false);
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("OpenXLR_mix_monitor"));
        Assert.True(fixture.Native("OpenXLR_mix_monitor_b"));
        Assert.Equal(["monitor_b"], fixture.Mixer.ExportSettings().MixMuted);

        fixture.SetNative("OpenXLR_mix_monitor", true);
        fixture.Sweep();
        Assert.Contains("monitor", fixture.Mixer.ExportSettings().MixMuted);
    }

    [Fact]
    public void ExplicitlyOpeningOneMonitorWhilePausedReplacesThePreviousRestorationSelection()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMixMuted("monitor_b", true);
        fixture.Mixer.SetMonitorMuted(true);

        fixture.Mixer.SetMixMuted("monitor_b", false);

        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.Equal(["monitor"], fixture.Mixer.ExportSettings().MixMuted);
        fixture.Mixer.ToggleMonitorMuted();
        fixture.Mixer.ToggleMonitorMuted();
        Assert.Equal(["monitor"], fixture.Mixer.ExportSettings().MixMuted);
        Assert.False(fixture.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlobalPauseMirrorsTheNativeOutputAndRestoresItsPriorMute(bool originallyMuted)
    {
        using var fixture = new IndependentSinks();
        fixture.SetNative("external", originallyMuted);
        fixture.Publish();
        fixture.Mixer.SetMonitorOutputs(["external"]);

        fixture.Mixer.SetMonitorMuted(true);
        Assert.True(fixture.Native("external"));
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        fixture.Mixer.SetMonitorMuted(false);
        Assert.Equal(originallyMuted, fixture.Native("external"));
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
    }

    [Fact]
    public void ExplicitMonitorUnmuteOpensItsHardwareAndForgetsAnOlderNativeMute()
    {
        using var fixture = new IndependentSinks();
        fixture.SetNative("external", true);
        fixture.Publish();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMonitorMuted(true);

        fixture.Mixer.SetMixMuted("monitor", false);

        Assert.False(fixture.Native("external"));
        Assert.False(fixture.Mixer.MonitorMuted);
        fixture.Mixer.ToggleMonitorMuted();
        Assert.True(fixture.Native("external"));
        fixture.Mixer.ToggleMonitorMuted();
        Assert.False(fixture.Native("external"));
    }

    [Fact]
    public void MutingOneOfASharedOutputsFeedsKeepsTheDeviceOpenUntilTheLastFeedCloses()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        Assert.Null(fixture.Mixer.SetMonitorFeed("external", "monitor+monitor_b"));

        fixture.Mixer.SetMixMuted("monitor", true);
        Assert.False(fixture.Native("external"));
        fixture.Mixer.SetMixMuted("monitor_b", true);
        Assert.True(fixture.Native("external"));
        fixture.Mixer.SetMixMuted("monitor", false);
        Assert.False(fixture.Native("external"));
        Assert.Contains("monitor_b", fixture.Mixer.ExportSettings().MixMuted);
    }

    [Fact]
    public void AHardwareUnmuteOfAnIndividuallyClosedFeedDoesNotStartAGlobalPause()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMixMuted("monitor", true);
        Assert.True(fixture.Native("external"));

        fixture.SetNative("external", false);
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("external"));
        Assert.Contains("monitor", fixture.Mixer.ExportSettings().MixMuted);
        int before = fixture.Calls.Length;
        fixture.Sweep();
        Assert.False(fixture.Native("external"));
        Assert.DoesNotContain("--format=json list sinks", fixture.Calls[before..]);
        fixture.Mixer.SetMixMuted("monitor", true);
        Assert.True(fixture.Native("external"));
        fixture.Mixer.SetMixMuted("monitor", false);
        Assert.False(fixture.Native("external"));
    }

    [Theory]
    [InlineData(false, "deselect", false)]
    [InlineData(true, "deselect", false)]
    [InlineData(false, "feed", false)]
    [InlineData(true, "feed", false)]
    [InlineData(false, "resume-and-feed", false)]
    [InlineData(true, "resume-and-feed", false)]
    [InlineData(false, "restart-and-feed", false)]
    [InlineData(true, "restart-and-feed", false)]
    [InlineData(false, "deselect", true)]
    [InlineData(true, "deselect", true)]
    [InlineData(false, "feed", true)]
    [InlineData(true, "feed", true)]
    [InlineData(false, "resume-and-feed", true)]
    [InlineData(true, "resume-and-feed", true)]
    [InlineData(false, "restart-and-feed", true)]
    [InlineData(true, "restart-and-feed", true)]
    public void AnAcceptedNativeUnmuteReplacesAnOlderOwnedMuteBaseline(bool originallyMuted, string transition, bool deferredIdentity)
    {
        using var fixture = new IndependentSinks();
        fixture.SetNative("external", originallyMuted);
        fixture.Publish();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMixMuted("monitor", true);
        fixture.Sweep(); // Confirm any authored mute before testing a native edit.
        Assert.True(fixture.Native("external"));

        fixture.SetNative("external", false);
        if (deferredIdentity)
        {
            fixture.DeferNextPhysicalPulse(identityVerification: true);
            fixture.Sweep(externalRegistrySerial: 2);
            Assert.False(fixture.Mixer.MonitorMuted);
            Assert.Equal(originallyMuted, fixture.Mixer.ExportSettings().MonitorOutputMutes["external"]);
        }
        fixture.Sweep(); // The original native and registry generation agree.
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("external"));
        Assert.Equal(["monitor"], fixture.Mixer.ExportSettings().MixMuted);
        MixerSettings saved = DiskSettings(fixture.Mixer);

        switch (transition)
        {
            case "deselect": fixture.Mixer.SetMonitorOutputs([]); break;
            case "feed": Assert.Null(fixture.Mixer.SetMonitorFeed("external", "monitor_b")); break;
            case "resume-and-feed":
                fixture.Mixer.SetMonitorMuted(true);
                fixture.Mixer.SetMonitorMuted(false);
                Assert.True(fixture.Native("external")); // The individual mix mute still closes its feed.
                Assert.Null(fixture.Mixer.SetMonitorFeed("external", "monitor_b"));
                break;
            case "restart-and-feed":
                using (var restored = new IndependentSinks())
                {
                    restored.SetNative("external", false);
                    restored.Publish();
                    restored.Mixer.ApplySettings(saved);
                    Assert.Null(restored.Mixer.SetMonitorFeed("external", "monitor_b"));
                    Assert.False(restored.Native("external"));
                    Assert.False(restored.Mixer.MonitorMuted);
                    Assert.Equal(["monitor"], restored.Mixer.ExportSettings().MixMuted);
                }
                break;
            default: throw new InvalidOperationException("unknown transition");
        }

        Assert.False(fixture.Native("external"));
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.Equal(["monitor"], fixture.Mixer.ExportSettings().MixMuted);
        Assert.False(saved.MonitorOutputMutes["external"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeferredNativeUnmuteDoesNotReplaceAReplacementOutputsOwnedBaseline(bool originallyMuted)
    {
        using var fixture = new IndependentSinks();
        fixture.SetNative("external", originallyMuted);
        fixture.Publish();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMixMuted("monitor", true);
        fixture.Sweep();
        fixture.SetNative("external", false);
        fixture.DeferNextPhysicalPulse(identityVerification: true);
        fixture.Sweep(externalRegistrySerial: 2);
        Assert.Equal(originallyMuted, fixture.Mixer.ExportSettings().MonitorOutputMutes["external"]);

        fixture.ReplaceExternal();
        fixture.Sweep(externalRegistrySerial: 2);

        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.True(fixture.Native("external")); // The replacement still follows the individually closed feed.
        Assert.Equal(["monitor"], fixture.Mixer.ExportSettings().MixMuted);
        Assert.Equal(originallyMuted, fixture.Mixer.ExportSettings().MonitorOutputMutes["external"]);
        Assert.Null(fixture.Mixer.SetMonitorFeed("external", "monitor_b"));
        Assert.Equal(originallyMuted, fixture.Native("external"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewerExplicitMonitorMuteSupersedesAnUnverifiedNativeUnmute(bool originallyMuted)
    {
        using var fixture = new IndependentSinks();
        fixture.SetNative("external", originallyMuted);
        fixture.Publish();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMixMuted("monitor", true);
        fixture.Sweep();
        fixture.SetNative("external", false);
        fixture.DeferNextPhysicalPulse(identityVerification: true);
        fixture.Sweep(externalRegistrySerial: 2);
        Assert.False(fixture.Native("external"));

        fixture.Mixer.SetMixMuted("monitor", true);
        fixture.Sweep(); // Verify the same native generation before applying the newer mute.

        Assert.True(fixture.Native("external"));
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.Equal(["monitor"], fixture.Mixer.ExportSettings().MixMuted);
        fixture.Sweep();
        int before = fixture.Calls.Length;
        fixture.Sweep();
        Assert.DoesNotContain("--format=json list sinks", fixture.Calls[before..]);
        Assert.True(fixture.Native("external"));
        Assert.False(fixture.Mixer.MonitorMuted);
        fixture.Mixer.SetMixMuted("monitor", false);
        Assert.False(fixture.Native("external"));
    }

    [Fact]
    public void APhysicalMuteKeyRearmsAReleasedIndividuallyMutedOutput()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMixMuted("monitor", true);
        fixture.SetNative("external", false);
        fixture.Sweep();

        fixture.SetNative("external", true);
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.True(fixture.Native("external"));
        fixture.SetNative("external", false);
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("external"));
        Assert.Contains("monitor", fixture.Mixer.ExportSettings().MixMuted);
        fixture.Sweep(); // Confirm the native write once, then remain idle.
        int before = fixture.Calls.Length;
        fixture.Sweep();
        Assert.DoesNotContain("--format=json list sinks", fixture.Calls[before..]);
        Assert.False(fixture.Native("external"));
        fixture.SetNative("external", true);
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.True(fixture.Native("external"));
        fixture.SetNative("external", false);
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("external"));
        Assert.Contains("monitor", fixture.Mixer.ExportSettings().MixMuted);
    }

    [Fact]
    public void TenMonitorsKeepOnlyTheExplicitlyOpenedMixAfterTheOldMuteSelectionIsReplaced()
    {
        using var fixture = new IndependentSinks();
        fixture.AddMixes(Enumerable.Range(3, 8).Select(i => new MixDefinition("monitor" + i, "Monitor " + i, MixKind.Monitor)));
        fixture.AddMixes([new("chat", "Recording", MixKind.VirtualMic)]);
        fixture.Mixer.SetMixMuted("chat", true);
        foreach (string id in new[] { "monitor6", "monitor7", "monitor8", "monitor9", "monitor10" }) fixture.Mixer.SetMixMuted(id, true);
        fixture.Mixer.SetMonitorMuted(true);

        fixture.Mixer.SetMixMuted("monitor8", false);
        fixture.Mixer.ToggleMonitorMuted();
        fixture.Mixer.ToggleMonitorMuted();

        var mixes = fixture.Mixer.Snapshot().Mixes;
        Assert.False(mixes.Single(m => m.Id == "monitor8").Muted);
        Assert.All(mixes.Where(m => m.Id != "monitor8"), m => Assert.True(m.Muted));
        Assert.False(fixture.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData("OpenXLR_mix_monitor")]
    [InlineData("OpenXLR_mix_monitor_b")]
    public void ARefusedExplicitUnmuteRestoresThePreviousSelectionAndKeepsMonitoringPaused(string refused)
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMixMuted("monitor_b", true);
        fixture.Mixer.SetMonitorMuted(true);
        fixture.DeferNextReset(refused);

        Assert.Throws<InvalidOperationException>(() => fixture.Mixer.SetMixMuted("monitor_b", false));

        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.Equal(["monitor_b"], fixture.Mixer.ExportSettings().MixMuted);
        fixture.Sweep();
        Assert.False(fixture.Native("OpenXLR_mix_monitor"));
        Assert.True(fixture.Native("OpenXLR_mix_monitor_b"));
        Assert.True(fixture.Native("external"));
    }

    [Fact]
    public void ARefusedHardwareWriteRetriesWithoutInventingANativeMuteKey()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.DeferNextPulse("external");
        fixture.Mixer.SetMonitorMuted(true);
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("external"));

        fixture.Sweep();

        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.True(fixture.Native("external"));
        fixture.Mixer.SetMonitorMuted(false);
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("external"));
    }

    [Fact]
    public void DeselectingAnOwnedOutputRestoresItsNativeMuteWithoutChangingOtherOutputs()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external", "external2"]);
        fixture.Mixer.SetMonitorMuted(true);

        fixture.Mixer.SetMonitorOutputs(["external"]);

        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.True(fixture.Native("external"));
        Assert.False(fixture.Native("external2"));
        Assert.Equal(["external"], fixture.Mixer.ExportSettings().MonitorOutputMutes.Keys);
    }

    [Fact]
    public void TheNativeRestoreBaselineSurvivesSavingAndRestartingWhilePaused()
    {
        MixerSettings saved;
        using (var original = new IndependentSinks())
        {
            original.Mixer.SetMonitorOutputs(["external"]);
            original.Mixer.SetMonitorMuted(true);
            saved = DiskSettings(original.Mixer);
            Assert.False(saved.MonitorOutputMutes["external"]);
        }
        using var restored = new IndependentSinks();
        restored.SetNative("external", true);
        restored.Publish();
        restored.Mixer.ApplySettings(saved);
        Assert.True(restored.Mixer.MonitorMuted);
        Assert.True(restored.Native("external"));

        restored.Mixer.SetMonitorMuted(false);

        Assert.False(restored.Native("external"));
        Assert.False(restored.Mixer.ExportSettings().MonitorOutputMutes["external"]);
        restored.Sweep();
        Assert.Empty(restored.Mixer.ExportSettings().MonitorOutputMutes);
    }

    [Fact]
    public void FastSuccessivePhysicalKeysKeepTheNativeFlagAndMasterInStep()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMixMuted("monitor_b", true);
        for (int press = 0; press < 50; press++)
        {
            fixture.SetNative("external", !fixture.Native("external"));
            fixture.Sweep();
            Assert.Equal(press % 2 == 0, fixture.Mixer.MonitorMuted);
            Assert.Equal(fixture.Mixer.MonitorMuted, fixture.Native("external"));
            Assert.Equal(["monitor_b"], fixture.Mixer.ExportSettings().MixMuted);
        }
    }

    [Fact]
    public void ASharedUnmutedRecordingFeedKeepsTheHardwareOpenWhenItsMonitorMixCloses()
    {
        using var fixture = new IndependentSinks();
        fixture.AddMixes([new("chat", "Recording", MixKind.VirtualMic)]);
        fixture.Mixer.SetMonitorOutputs(["external"]);
        Assert.Null(fixture.Mixer.SetMonitorFeed("external", "monitor+chat"));

        fixture.Mixer.SetMixMuted("monitor", true);
        Assert.False(fixture.Native("external"));
        fixture.Mixer.SetMixMuted("chat", true);
        Assert.True(fixture.Native("external"));
        fixture.Mixer.SetMixMuted("chat", false);
        Assert.False(fixture.Native("external"));
        Assert.Contains("monitor", fixture.Mixer.ExportSettings().MixMuted);
    }

    [Fact]
    public void AnIndependentAuxReturnKeepsItsSharedNativeSinkOpenDuringMonitorPause()
    {
        using var fixture = new IndependentSinks();
        fixture.AddMixes([new("auxout", "Aux", MixKind.AuxPort)]);
        fixture.Mixer.SetMonitorOutputs(["external#hp1"]);
        typeof(Mixer).GetField("_auxTargetSink", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Mixer, "external");

        fixture.Mixer.SetMonitorMuted(true);

        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("external"));
        Assert.False(fixture.Mixer.IsMonitorOutputAudible("external#hp1"));
        Assert.DoesNotContain("auxout", fixture.Mixer.ExportSettings().MixMuted);
    }

    [Fact]
    public void ApplyingAnotherOutputSelectionRestoresTheNativeFlagOfTheOutputItReplaces()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMonitorMuted(true);

        fixture.Mixer.ApplySettings(new() { MonitorOutputs = ["external2"], MonitorMuted = true });

        Assert.False(fixture.Native("external"));
        Assert.True(fixture.Native("external2"));
        Assert.True(fixture.Mixer.MonitorMuted);
    }

    [Fact]
    public void ApplyingLegacySettingsForTheSameOutputKeepsItsOwnedNativeRestoreFlag()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMonitorMuted(true);

        fixture.Mixer.ApplySettings(new() { MonitorOutputs = ["external"], MonitorMuted = true });
        fixture.Mixer.SetMonitorMuted(false);

        Assert.False(fixture.Native("external"));
    }

    [Fact]
    public void DisablingAnIndependentAuxReturnRearmsTheNativeMuteWhileMonitoringIsPaused()
    {
        using var fixture = new IndependentSinks();
        fixture.AddMixes([new("auxout", "Aux", MixKind.AuxPort)]);
        fixture.Mixer.SetMonitorOutputs(["external#hp1"]);
        typeof(Mixer).GetField("_auxTargetSink", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Mixer, "external");
        fixture.Mixer.SetMonitorMuted(true);
        Assert.False(fixture.Native("external"));

        fixture.Mixer.SetAuxPortEnabled(false);

        Assert.True(fixture.Native("external"));
        Assert.True(fixture.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARefusedReleaseRetriesOnlyForTheSameDeselectedNativeInstance(bool replaced)
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMonitorMuted(true);
        fixture.DeferNextReset("external");

        fixture.Mixer.SetMonitorOutputs([]);
        Assert.True(fixture.Native("external"));
        Assert.Empty(fixture.Mixer.ExportSettings().MonitorOutputMutes);
        if (replaced) fixture.ReplaceExternal();
        int before = fixture.Calls.Length;
        fixture.Sweep(externalRegistrySerial: replaced ? 2 : 1);

        Assert.Equal(replaced, fixture.Native("external"));
        if (replaced) Assert.DoesNotContain("set-sink-mute external 0", fixture.Calls[before..]);
        fixture.Sweep(externalRegistrySerial: replaced ? 2 : 1);
        before = fixture.Calls.Length;
        fixture.Sweep(externalRegistrySerial: replaced ? 2 : 1);
        Assert.DoesNotContain("--format=json list sinks", fixture.Calls[before..]);
    }

    [Fact]
    public void ReselectingAnOutputWithARefusedReleaseCancelsOnlyItsOldReleaseAndKeepsTheOriginalBaseline()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMonitorMuted(true);
        fixture.DeferNextReset("external");
        fixture.Mixer.SetMonitorOutputs([]);
        Assert.True(fixture.Native("external"));

        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.True(fixture.Native("external"));
        fixture.Mixer.SetMonitorMuted(false);
        Assert.False(fixture.Native("external"));
    }

    [Fact]
    public void ARefusedResumeKeepsItsNativeRestoreBaselineAcrossTheNextRestart()
    {
        MixerSettings saved;
        using (var original = new IndependentSinks())
        {
            original.Mixer.SetMonitorOutputs(["external"]);
            original.Mixer.SetMonitorMuted(true);
            original.DeferNextPulse("external");
            original.Mixer.SetMonitorMuted(false);
            Assert.True(original.Native("external"));
            saved = DiskSettings(original.Mixer);
            Assert.False(saved.MonitorMuted);
            Assert.False(saved.MonitorOutputMutes["external"]);
        }
        using var restored = new IndependentSinks();
        restored.SetNative("external", true);
        restored.Publish();

        restored.Mixer.ApplySettings(saved);

        Assert.False(restored.Mixer.MonitorMuted);
        Assert.False(restored.Native("external"));
    }

    [Fact]
    public void ARefusedFirstMonitorWriteDoesNotProbeOrRestoreUntouchedPeers()
    {
        using var fixture = new IndependentSinks();
        fixture.AddMixes(Enumerable.Range(3, 16).Select(i => new MixDefinition("monitor" + i, "Monitor " + i, MixKind.Monitor)));
        fixture.Mixer.SetMonitorMuted(true);
        fixture.DeferNextReset("OpenXLR_mix_monitor");
        int before = fixture.Calls.Length;

        Assert.Throws<InvalidOperationException>(() => fixture.Mixer.SetMixMuted("monitor18", false));

        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.Empty(fixture.Mixer.ExportSettings().MixMuted);
        foreach (MixDefinition mix in fixture.Mixer.Config.Mixes.Where(m => m.Id != "monitor"))
        {
            Assert.DoesNotContain("get-sink-mute " + mix.SinkName, fixture.Calls[before..]);
            Assert.DoesNotContain(fixture.Calls[before..], call => call.StartsWith("set-sink-mute " + mix.SinkName + " ", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALateMonitorWriteFailureQueuesEveryRestoreAndStopsEachBatchAtTheFirstRefusedHelper(bool refuseReads)
    {
        using var fixture = new IndependentSinks();
        fixture.AddMixes(Enumerable.Range(3, 16).Select(i => new MixDefinition("monitor" + i, "Monitor " + i, MixKind.Monitor)));
        fixture.Mixer.SetMixMuted("monitor7", true);
        fixture.Mixer.SetMixMuted("monitor12", true);
        string[] originalMutes = [.. fixture.Mixer.ExportSettings().MixMuted.Order(StringComparer.Ordinal)];
        fixture.Mixer.SetMonitorMuted(true);
        fixture.DeferNextReset("OpenXLR_mix_monitor18");
        fixture.RefuseMixMuteRestores(true, refuseReads);
        int before = fixture.Calls.Length;

        Assert.Throws<InvalidOperationException>(() => fixture.Mixer.SetMixMuted("monitor18", false));

        Assert.Single(fixture.Calls[before..], IsMixMuteRead);
        AssertSelectionAndPending(18);
        for (int sweep = 0; sweep < 3; sweep++)
        {
            before = fixture.Calls.Length;
            fixture.Sweep();
            Assert.Single(fixture.Calls[before..], IsMixMuteRead);
            Assert.Equal(refuseReads ? 0 : 1, fixture.Calls[before..].Count(call => call.StartsWith("set-sink-mute OpenXLR_mix_", StringComparison.Ordinal)));
            AssertSelectionAndPending(18);
        }

        fixture.RefuseMixMuteRestores(false, refuseReads);
        fixture.Now = 30_000;
        fixture.Sweep();
        fixture.Sweep(); // Accepted writes are removed only after readback.
        AssertSelectionAndPending(0);
        foreach (MixDefinition mix in fixture.Mixer.Config.Mixes)
            Assert.Equal(originalMutes.Contains(mix.Id), fixture.Native(mix.SinkName));

        static bool IsMixMuteRead(string call) => call.StartsWith("get-sink-mute OpenXLR_mix_", StringComparison.Ordinal);
        void AssertSelectionAndPending(int pending)
        {
            Assert.True(fixture.Mixer.MonitorMuted);
            Assert.Equal(originalMutes, fixture.Mixer.ExportSettings().MixMuted.Order(StringComparer.Ordinal));
            var resets = (System.Collections.IDictionary)typeof(Mixer)
                .GetField("_desktopMixMuteResets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mixer)!;
            Assert.Equal(pending, resets.Count);
        }
    }

    [Fact]
    public void UnknownNativeGenerationsKeepSoftwarePausedWithoutAuthoringBlindHardwareMutes()
    {
        using var fixture = new IndependentSinks();
        fixture.DeferNextPhysicalPulse(identityVerification: true);
        fixture.Mixer.SetMonitorOutputs(["external"]);
        int before = fixture.Calls.Length;

        fixture.Mixer.SetMonitorMuted(true);

        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("external"));
        Assert.Empty(fixture.Mixer.ExportSettings().MonitorOutputMutes);
        Assert.DoesNotContain(fixture.Calls[before..], call => call.StartsWith("set-sink-mute external ", StringComparison.Ordinal));
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.True(fixture.Native("external"));
        fixture.Mixer.SetMonitorMuted(false);
        fixture.Sweep();
        Assert.False(fixture.Native("external"));
    }

    [Fact]
    public void AResumeAcknowledgedBeforeNativeReadbackKeepsItsRestoreBaselineAcrossRestart()
    {
        MixerSettings saved;
        using (var original = new IndependentSinks())
        {
            original.Mixer.SetMonitorOutputs(["external"]);
            original.Mixer.SetMonitorMuted(true);
            original.DeferSuccess("external");
            original.Mixer.SetMonitorMuted(false);
            Assert.True(original.Native("external"));
            saved = DiskSettings(original.Mixer);
            Assert.False(saved.MonitorMuted);
            Assert.False(saved.MonitorOutputMutes["external"]);
        }
        using var restored = new IndependentSinks();
        restored.SetNative("external", true);
        restored.Publish();

        restored.Mixer.ApplySettings(saved);
        Assert.False(restored.Native("external"));
        Assert.False(restored.Mixer.ExportSettings().MonitorOutputMutes["external"]);
        restored.Sweep();
        Assert.Empty(restored.Mixer.ExportSettings().MonitorOutputMutes);
    }

    [Fact]
    public void AFirstHardwareWriteRefusalQueuesTheOtherOutputsWithoutTryingEachHelper()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external", "external2"]);
        fixture.DeferNextPulse("external");
        fixture.DeferNextPulse("external2");
        int before = fixture.Calls.Length;

        fixture.Mixer.SetMonitorMuted(true);

        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.Single(fixture.Calls[before..], call => call.StartsWith("set-sink-mute external", StringComparison.Ordinal));
        Assert.False(fixture.Native("external"));
        Assert.False(fixture.Native("external2"));
        fixture.DeferNextReset("external");
        fixture.DeferNextReset("external2");
        before = fixture.Calls.Length;
        fixture.Sweep();
        Assert.Single(fixture.Calls[before..], call => call.StartsWith("set-sink-mute external", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Calls[before..], call => call.StartsWith("get-sink-mute external", StringComparison.Ordinal));
        Assert.False(fixture.Native("external"));
        Assert.False(fixture.Native("external2"));
        fixture.Sweep();
        fixture.Sweep();
        Assert.True(fixture.Native("external"));
        Assert.True(fixture.Native("external2"));
    }

    [Fact]
    public void AQueuedHardwareWriteUsesItsNativeReadbackWithoutProbingEveryOutputAgain()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external", "external2"]);
        fixture.DeferNextPulse("external");
        fixture.Mixer.SetMonitorMuted(true);
        // A native write may apply even when its helper reported failure.
        fixture.SetNative("external", true);
        fixture.SetNative("external2", true);
        fixture.DeferNextPulse("external", readFailure: true);
        fixture.DeferNextPulse("external2", readFailure: true);
        int before = fixture.Calls.Length;

        fixture.Sweep();

        Assert.Single(fixture.Calls[before..], call => call == "--format=json list sinks");
        Assert.DoesNotContain(fixture.Calls[before..], call => call.StartsWith("get-sink-mute external", StringComparison.Ordinal));
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.True(fixture.Native("external"));
        Assert.True(fixture.Native("external2"));
    }

    [Fact]
    public void ReleasingSeveralOutputsUsesOneSnapshotAndStopsAfterTheFirstWriteRefusal()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external", "external2"]);
        fixture.Mixer.SetMonitorMuted(true);
        fixture.DeferNextReset("external");
        fixture.DeferNextReset("external2");
        int before = fixture.Calls.Length;

        fixture.Mixer.SetMonitorOutputs([]);

        Assert.Single(fixture.Calls[before..], call => call == "--format=json list sinks");
        Assert.Single(fixture.Calls[before..], call => call.StartsWith("set-sink-mute external", StringComparison.Ordinal));
        Assert.True(fixture.Native("external"));
        Assert.True(fixture.Native("external2"));
        for (int sweep = 0; sweep < 4; sweep++) fixture.Sweep();
        Assert.False(fixture.Native("external"));
        Assert.False(fixture.Native("external2"));
        before = fixture.Calls.Length;
        fixture.Sweep();
        Assert.DoesNotContain("--format=json list sinks", fixture.Calls[before..]);
    }

    [Fact]
    public void NativeRestorationReadbackRequestsASaveWithoutAVolumeOrPauseChange()
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMonitorMuted(true);
        fixture.Sweep();
        fixture.Mixer.SetMonitorMuted(false);
        Assert.False(fixture.Mixer.MonitorMuted);
        Assert.False(DiskSettings(fixture.Mixer).MonitorOutputMutes["external"]);
        fixture.Sweep(); // Confirm the native write without changing software intent.
        Assert.True(fixture.LastDeviceChange);
        Assert.Empty(DiskSettings(fixture.Mixer).MonitorOutputMutes);
        fixture.Sweep();
        Assert.False(fixture.LastDeviceChange);
    }

    private static MixerSettings DiskSettings(Mixer mixer)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-native-mute-settings-").FullName;
        try
        {
            string path = Path.Combine(directory, "mixer.json");
            Assert.Null(mixer.ExportSettings().Save(path));
            MixerSettings saved = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
            Assert.Null(warning);
            return saved;
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class IndependentSinks : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-deferred-pulse-").FullName;
        private readonly string? _previousPath = Environment.GetEnvironmentVariable("PATH");
        private readonly PipeWireAdapter _adapter = new();
        public Mixer Mixer { get; }
        public string[] Calls => File.ReadAllLines(Path.Combine(_directory, "calls"));
        public long Now { get; set; }
        public bool LastDeviceChange { get; private set; }

        public IndependentSinks()
        {
            File.WriteAllText(Path.Combine(_directory, "calls"), "");
            foreach (string name in Names) SetNative(name, false);
            File.WriteAllText(Path.Combine(_directory, "native-serial"), "1");
            SetDefault("OpenXLR_ch_system");
            ExecutableScript.Write(Path.Combine(_directory, "pw-dump"), """
                dir=${0%/*}
                if [ -f "$dir/graph-unavailable" ]; then exit 1; fi
                /bin/cat "$dir/graph"
                """);
            ExecutableScript.Write(Path.Combine(_directory, "pw-link"), "exit 0\n");
            ExecutableScript.Write(Path.Combine(_directory, "pactl"), """
                dir=${0%/*}
                printf '%s\n' "$*" >> "$dir/calls"
                if [ "$1" = --format=json ]; then
                    if [ -f "$dir/fail-native" ]; then /bin/rm "$dir/fail-native"; exit 1; fi
                    mute=$(/bin/cat "$dir/external")
                    mute2=$(/bin/cat "$dir/external2")
                    serial=$(/bin/cat "$dir/native-serial")
                    system=$(/bin/cat "$dir/OpenXLR_ch_system")
                    browser=$(/bin/cat "$dir/OpenXLR_ch_browser")
                    printf '[{"index":1,"name":"external","mute":%s,"properties":{"object.serial":"%s"}},{"index":2,"name":"external2","mute":%s,"properties":{"object.serial":"2"}},{"index":3,"name":"OpenXLR_ch_system","mute":%s,"properties":{"object.serial":"3"}},{"index":4,"name":"OpenXLR_ch_browser","mute":%s,"properties":{"object.serial":"4"}}]' "$mute" "$serial" "$mute2" "$system" "$browser"
                    exit
                fi
                case "$1" in
                  get-default-sink) /bin/cat "$dir/default" ;;
                  get-sink-volume) printf 'Volume: front-left: 65536 / 100%% / 0.00 dB\n' ;;
                  get-sink-mute)
                    case "$2" in OpenXLR_mix_*) if [ -f "$dir/refuse-mix-mute-reads" ]; then exit 1; fi ;; esac
                    if [ -f "$dir/fail-read-$2" ]; then /bin/rm "$dir/fail-read-$2"; exit 1; fi
                    mute=$(/bin/cat "$dir/$2")
                    if [ "$mute" = true ]; then echo 'Mute: yes'; else echo 'Mute: no'; fi ;;
                  set-sink-mute)
                    if [ "$3" = toggle ] && [ -f "$dir/defer-success-$2" ]; then
                        /bin/rm "$dir/defer-success-$2"
                        exit 0
                    fi
                    if [ "$3" != toggle ] && [ -f "$dir/fail-reset-$2" ]; then
                        /bin/rm "$dir/fail-reset-$2"
                        echo 'temporary native restoration failure' >&2
                        exit 1
                    fi
                    case "$2:$3" in OpenXLR_mix_*:0) if [ -f "$dir/refuse-mix-mute-writes" ]; then exit 1; fi ;; esac
                    if [ "$3" = toggle ] && [ -f "$dir/fail-$2" ]; then
                        /bin/rm "$dir/fail-$2"
                        echo 'temporary native consume failure' >&2
                        exit 1
                    fi
                    case "$3" in
                      0) printf false > "$dir/$2" ;;
                      1) printf true > "$dir/$2" ;;
                      toggle)
                        mute=$(/bin/cat "$dir/$2")
                        if [ "$mute" = true ]; then printf false > "$dir/$2"; else printf true > "$dir/$2"; fi ;;
                    esac ;;
                esac
                """);
            Environment.SetEnvironmentVariable("PATH", _directory);
            Mixer = new(_adapter) { Clock = () => Now };
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, true);
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer,
                new MixerConfig { Channels = [new("system", "System"), new("browser", "Browser")], Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("monitor_b", "Monitor B", MixKind.Monitor)] });
            Publish();
        }

        public void SetDefault(string name) => File.WriteAllText(Path.Combine(_directory, "default"), name);
        public void SetNative(string name, bool muted) => File.WriteAllText(Path.Combine(_directory, name), muted ? "true" : "false");
        public bool Native(string name) => File.ReadAllText(Path.Combine(_directory, name)) == "true";
        private readonly List<string> _extraNames = [];
        public void AddMixes(IEnumerable<MixDefinition> mixes)
        {
            MixDefinition[] added = [.. mixes];
            foreach (MixDefinition mix in added)
            {
                _extraNames.Add(mix.SinkName);
                SetNative(mix.SinkName, false);
            }
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer,
                Mixer.Config with { Mixes = [.. Mixer.Config.Mixes, .. added] });
            Publish();
        }
        private static readonly string[] Names = ["external", "external2", "OpenXLR_ch_system", "OpenXLR_ch_browser", "OpenXLR_mix_monitor", "OpenXLR_mix_monitor_b"];
        public void DeferNextPhysicalPulse(bool identityVerification) => File.WriteAllText(Path.Combine(_directory, identityVerification ? "fail-native" : "fail-external"), "");
        public void DeferNextPulse(string name, bool readFailure = false) => File.WriteAllText(Path.Combine(_directory, $"fail-{(readFailure ? "read-" : "")}{name}"), "");
        public void DeferSuccess(string name) => File.WriteAllText(Path.Combine(_directory, "defer-success-" + name), "");
        public void DeferNextReset(string name) => File.WriteAllText(Path.Combine(_directory, "fail-reset-" + name), "");
        public void RefuseMixMuteRestores(bool refuse, bool reads)
        {
            string path = Path.Combine(_directory, "refuse-mix-mute-" + (reads ? "reads" : "writes"));
            if (refuse) File.WriteAllText(path, "");
            else File.Delete(path);
        }
        public void ReplaceExternal() => File.WriteAllText(Path.Combine(_directory, "native-serial"), "2");
        public void SelectExternalOutput(bool beforeRegistry)
        {
            if (beforeRegistry) Publish(externalPresent: false);
            Mixer.SetMonitorOutputs(["external"]);
            Publish();
        }
        public void GraphUnavailable(bool unavailable)
        {
            string path = Path.Combine(_directory, "graph-unavailable");
            if (unavailable) File.WriteAllText(path, "");
            else File.Delete(path);
            typeof(PipeWireAdapter).GetMethod("InvalidateDump", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_adapter, null);
        }
        public void Sweep(int externalRegistrySerial = 1, bool? externalRegistryMuted = null, bool? browserRegistryMuted = null)
        {
            Publish(externalRegistrySerial, externalRegistryMuted, browserRegistryMuted);
            Mixer.SyncOwnSinkLevels(out _);
            LastDeviceChange = Mixer.SyncDeviceVolumes();
        }
        public void DeferBlanketPhysicalPulse()
        {
            Mixer.SetMonitorOutputs(["external"]);
            SetNative("OpenXLR_ch_system", true);
            SetNative("external", true);
            DeferNextPulse("external");
            Sweep();
            Assert.True(Mixer.MonitorMuted);
            Assert.True(Native("external"));
        }

        public void Publish(int externalRegistrySerial = 1, bool? externalRegistryMuted = null, bool? browserRegistryMuted = null,
            bool? monitorRegistryMuted = null, bool externalPresent = true)
        {
            File.WriteAllBytes(Path.Combine(_directory, "graph"), JsonSerializer.SerializeToUtf8Bytes(Names.Concat(_extraNames).Select((name, index) => new
            {
                id = index + 1, type = "PipeWire:Interface:Node", info = new
                {
                    props = new Dictionary<string, string> { ["node.name"] = name, ["media.class"] = "Audio/Sink", ["object.serial"] = (name == "external" ? externalRegistrySerial : index + 1).ToString() },
                    @params = new { Props = new[] { new { channelVolumes = new[] { 1.0 }, mute = name == "external" ? externalRegistryMuted ?? Native(name)
                        : name == "OpenXLR_ch_browser" ? browserRegistryMuted ?? Native(name)
                        : name == "OpenXLR_mix_monitor" ? monitorRegistryMuted ?? Native(name) : Native(name) } } },
                },
            }).Where(node => externalPresent || node.info.props["node.name"] != "external")));
            typeof(PipeWireAdapter).GetMethod("InvalidateDump", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_adapter, null);
        }

        public void Dispose()
        {
            try { Mixer.Dispose(); }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", _previousPath);
                Directory.Delete(_directory, true);
            }
        }
    }
}
