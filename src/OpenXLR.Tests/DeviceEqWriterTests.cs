using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The device manager's EQ writer: the first change goes out at once, a drag
/// at most ten times a second and the last value always; nothing while
/// phantom power settles and the latest once it ends; gain, phantom and EQ
/// writes all land in the one block; a unit that drops a write gets one
/// rewrite, then the input fails; an EQ another program changed is written
/// back no sooner than the rewrite spacing and logged once; a post the unit
/// already holds writes nothing; and nothing is written to a unit that is
/// gone.
/// </summary>
[Collection("xdg-config")]
public sealed class DeviceEqWriterTests
{
    internal sealed class Lines : ILogger<DeviceManager>
    {
        public readonly List<string> Written = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Written) Written.Add(formatter(state, exception));
        }
    }

    /// <summary>A unit that runs the EQ, held as its raw value. With <see cref="Keep"/> off it answers writes and keeps what it held.</summary>
    private sealed class EqUnit : IAudioDevice
    {
        public DeviceEq Held = Eq(false, 0);
        public bool Keep = true;
        public bool Mute;
        public ManualTime? Clock;
        public readonly List<(long At, DeviceEq Eq)> Writes = [];
        public int Calls;
        private DeviceEq? _read;
        public void Dispose() { }
        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR Dock MK.2", 0x0fd9, 0x00c7);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true, Phantom = true, HardwareEq = true };
        public DeviceEqSupport? EqSupport { get; } = new(true);
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public DeviceState ReadState() { lock (this) _read = Held; return new() { Mute = Mute }; }
        public DeviceEq? ReadEq(int input) { lock (this) return input == 1 ? _read : null; }
        public bool SetEq(int input, DeviceEq eq)
        {
            lock (this)
            {
                Calls++;
                if (eq.HeldBy(Held)) return false;
                Writes.Add(((long)(Clock?.Elapsed.TotalMilliseconds ?? 0), eq));
                if (Keep) Held = eq.Bands is null ? Held with { Enabled = eq.Enabled } : eq;
                return true;
            }
        }
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    internal static DeviceEq Eq(bool on, int gain)
        => new(on, [new(true, 0, 0, 36, -10), new(true, 2, gain, 310, -10), new(true, 2, 0, 2172, -10), new(false, 5, 0, 15049, -10)]);

    private static T WithConfigDir<T>(Func<T> body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-eq-writer-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { return body(); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static DeviceManager Manager(IAudioDevice unit, ManualTime time, Lines? log = null)
    {
        var manager = new DeviceManager(log ?? new Lines(), new ConfigurationBuilder().Build(), () => [unit], time: time);
        manager.SweepOnce();
        Assert.True(unit.Connected);
        return manager;
    }

    /// <summary>Sweep every 5 ms of the manual clock for <paramref name="milliseconds"/>, with <paramref name="between"/> before each sweep.</summary>
    private static void SweepFor(DeviceManager manager, ManualTime time, int milliseconds, Action<long>? between = null)
    {
        for (long at = 0; at < milliseconds; at += 5)
        {
            between?.Invoke(at);
            manager.SweepOnce();
            time.Advance(TimeSpan.FromMilliseconds(5));
        }
    }

    private static long Now(ManualTime time) => (long)time.Elapsed.TotalMilliseconds;

    [Fact]
    public void EqWritesContinueAcrossTheLongUptimeTimestampBoundary() => WithConfigDir(() =>
    {
        var time = new ManualTime(timestampFrequency: 1_000_000_000);
        // Linux's nanosecond clock passes long.MaxValue / 1000 after about
        // 107 days. Put the two writes on opposite sides of that instant.
        time.Advance(TimeSpan.FromTicks(long.MaxValue / 1000 / 100 - TimeSpan.TicksPerMillisecond * 50));
        var unit = new EqUnit { Clock = time };
        using DeviceManager manager = Manager(unit, time);
        manager.PostEq(1, Eq(true, 1), "XLR 1");
        manager.SweepOnce();
        Assert.Single(unit.Writes);
        time.Advance(DeviceManager.EqWriteSpacing);
        manager.PostEq(1, Eq(true, 2), "XLR 1");
        manager.SweepOnce();
        Assert.Equal([Eq(true, 1), Eq(true, 2)], unit.Writes.Select(w => w.Eq));
        return 0;
    });

    [Fact]
    public void ADragGoesOutAtMostTenTimesASecondFirstAtOnceAndEndingOnTheLast() => WithConfigDir(() =>
    {
        var time = new ManualTime();
        var unit = new EqUnit { Clock = time };
        using DeviceManager manager = Manager(unit, time);
        long first = Now(time);
        manager.PostEq(1, Eq(true, 1), "XLR 1");
        manager.SweepOnce();
        Assert.Single(unit.Writes);
        Assert.Equal(first, unit.Writes[0].At);
        // Fifty posts in a second, one every 20 ms, a sweep every 5 ms.
        int posted = 1;
        SweepFor(manager, time, 1000, at => { if (at % 20 == 0 && posted < 50) manager.PostEq(1, Eq(true, ++posted), "XLR 1"); });
        SweepFor(manager, time, 200);
        Assert.Equal(50, posted);
        Assert.InRange(unit.Writes.Count, 10, 11);
        for (int i = 1; i < unit.Writes.Count; i++)
            Assert.True(unit.Writes[i].At - unit.Writes[i - 1].At >= 100, $"writes {i} and {i + 1} came {unit.Writes[i].At - unit.Writes[i - 1].At} ms apart");
        Assert.Equal(Eq(true, 50), unit.Writes[^1].Eq);
        Assert.True(unit.Writes[^1].At - 980 <= 100, "the last value lands within 100 ms of being set");
        Assert.Equal(new DeviceEqStatus(DeviceEqPhase.Verified, Eq(true, 50)), manager.EqStatus(1));
        return 0;
    });

    /// <summary>
    /// A post to an input the unit does not have, the second input of a
    /// one-input dock, is dropped: no write, no rewrite and no failure.
    /// </summary>
    [Fact]
    public void APostToAnInputTheUnitDoesNotHaveIsDroppedNotFailed() => WithConfigDir(() =>
    {
        var time = new ManualTime();
        var unit = new EqUnit { Clock = time };
        using DeviceManager manager = Manager(unit, time);
        manager.PostEq(2, Eq(true, 3), "XLR 2");
        SweepFor(manager, time, 1000);
        Assert.Equal(0, unit.Calls);
        Assert.Equal(DeviceEqStatus.None, manager.EqStatus(2));
        manager.PostEq(1, Eq(true, 3), "XLR 1");
        SweepFor(manager, time, 200);
        Assert.Equal([Eq(true, 3)], unit.Writes.Select(w => w.Eq));
        Assert.Equal(DeviceEqPhase.Verified, manager.EqStatus(1).Phase);
        return 0;
    });

    [Fact]
    public void NothingIsWrittenWhilePhantomPowerSettlesAndTheLatestAfter() => WithConfigDir(() =>
    {
        var time = new ManualTime();
        var unit = new EqUnit { Clock = time };
        using DeviceManager manager = Manager(unit, time);
        using JsonDocument on = JsonDocument.Parse("true");
        // The firmware mutes the input for as long as it settles.
        unit.Mute = true;
        Assert.Null(manager.Apply("phantom", on.RootElement));
        manager.PostEq(1, Eq(true, 4), "XLR 1");
        manager.PostEq(1, Eq(true, 8), "XLR 1");
        SweepFor(manager, time, 5000);
        Assert.Empty(unit.Writes);
        Assert.Equal(DeviceEqPhase.Pending, manager.EqStatus(1).Phase);
        // The firmware unmutes; the hold ends, and the latest value goes out once.
        unit.Mute = false;
        SweepFor(manager, time, 200);
        Assert.Equal([Eq(true, 8)], unit.Writes.Select(w => w.Eq));
        return 0;
    });

    [Fact]
    public void GainPhantomAndEqWritesInterleavedAllStayInTheBlock() => WithConfigDir(() =>
    {
        var time = new ManualTime();
        var usb = new Mk2HardwareEqTests.Unit();
        var dock = new XlrDockMk2Device(usb);
        using DeviceManager manager = Manager(dock, time);
        using JsonDocument gain = JsonDocument.Parse("40"), on = JsonDocument.Parse("true");
        manager.PostEq(1, Eq(true, 12), "XLR 1");
        manager.SweepOnce();
        Assert.Null(manager.Apply("gain", gain.RootElement));
        Assert.Null(manager.Apply("phantom", on.RootElement));
        manager.PostEq(1, DeviceEq.Off, "XLR 1");
        // Held while phantom power settles, then written.
        SweepFor(manager, time, 1000);
        Assert.Equal(0x01, usb.Blocks[0x0004][2] & 0x01);
        SweepFor(manager, time, 1500);
        byte[] block = usb.Blocks[0x0004];
        Assert.Equal(40, block[0]);
        Assert.Equal(0x02, block[1] & 0x02);
        Assert.Equal(0x00, block[2] & 0x01);
        Assert.Equal(Eq(false, 12), WaveFxEq.Decode(block));
        Assert.Equal(new DeviceEqStatus(DeviceEqPhase.Verified, DeviceEq.Off), manager.EqStatus(1));
        return 0;
    });

    /// <summary>
    /// The Wave XLR Pro: XLR 1's and XLR 2's posts both land in the one
    /// settings block, each in its own struct, verified by the poll; phantom
    /// power switched on XLR 2 holds both until it settles; the gain written
    /// meanwhile stays; no commit goes with an EQ write; and the journal says
    /// once per input that the EQ Wave Link left was replaced.
    /// </summary>
    [Fact]
    public void TheProsTwoInputsAreWrittenInTheirOwnStructsAndHeldWhilePhantomSettles() => WithConfigDir(() =>
    {
        var time = new ManualTime();
        var usb = new ProHardwareEqTests.Unit();
        var log = new Lines();
        var pro = new WaveXlrProDevice(usb);
        using DeviceManager manager = Manager(pro, time, log);
        Assert.Equal(2, manager.PublishedEqSupport!.Inputs);
        Assert.Equal("1.0.14", manager.PublishedEqSupport.MinimumFirmware);
        Assert.Equal("1.0.14", manager.PublishedEqSupport.Firmware);
        using JsonDocument gain = JsonDocument.Parse("30"), on = JsonDocument.Parse("true");
        manager.PostEq(1, Eq(true, 12), "XLR 1");
        manager.PostEq(2, Eq(true, 48), "XLR 2");
        manager.SweepOnce();
        byte[] block = usb.Blocks[0x0004];
        Assert.Equal(Eq(true, 12), WaveFxEq.Decode(block.AsSpan(0, 38)));
        Assert.Equal(Eq(true, 48), WaveFxEq.Decode(block.AsSpan(38, 38)));
        Assert.Equal(new DeviceEqStatus(DeviceEqPhase.Verified, Eq(true, 48)), manager.EqStatus(2));
        Assert.Equal(DeviceEqPhase.Verified, manager.EqStatus(1).Phase);
        Assert.Single(log.Written, l => l == "XLR 1's EQ was written to the Wave XLR Pro, replacing the one it held.");
        Assert.Single(log.Written, l => l == "XLR 2's EQ was written to the Wave XLR Pro, replacing the one it held.");

        Assert.Null(manager.Apply("phantom2", on.RootElement));
        Assert.Null(manager.Apply("gain2", gain.RootElement));
        manager.PostEq(2, DeviceEq.Off, "XLR 2");
        manager.PostEq(1, Eq(false, 12), "XLR 1");
        SweepFor(manager, time, 1000);
        Assert.Equal(0x01, usb.Blocks[0x0004][40] & 0x01);
        Assert.Equal(0x01, usb.Blocks[0x0004][2] & 0x01);
        SweepFor(manager, time, 1500);
        block = usb.Blocks[0x0004];
        Assert.Equal(30, block[38]);
        Assert.Equal(0x02, block[39] & 0x02);
        Assert.Equal(0x04, block[40]);
        Assert.Equal(0x00, block[2] & 0x01);
        Assert.Equal(Eq(false, 48), WaveFxEq.Decode(block.AsSpan(38, 38)));
        Assert.Equal(ProHardwareEqTests.IdleBlock[^8..], Convert.ToHexString(block[76..]).ToLowerInvariant());
        Assert.Equal(new DeviceEqStatus(DeviceEqPhase.Verified, DeviceEq.Off), manager.EqStatus(2));
        Assert.DoesNotContain(usb.Calls, c => c.Type == 0x41 && c.Block == 3);
        return 0;
    });

    [Fact]
    public void AUnitThatDropsAWriteGetsOneRewriteThenTheInputFails() => WithConfigDir(() =>
    {
        var time = new ManualTime();
        var unit = new EqUnit { Clock = time, Keep = false };
        var log = new Lines();
        using DeviceManager manager = Manager(unit, time, log);
        int settled = 0;
        manager.EqStatusChanged += () => Interlocked.Increment(ref settled);
        manager.PostEq(1, Eq(true, 6), "XLR 1");
        SweepFor(manager, time, 500);
        Assert.Equal(2, unit.Writes.Count);
        Assert.Equal(100, unit.Writes[1].At - unit.Writes[0].At);
        DeviceEqStatus status = manager.EqStatus(1);
        Assert.Equal(DeviceEqPhase.Failed, status.Phase);
        Assert.Equal(DeviceManager.EqNotKept, status.Reason);
        Assert.True(settled >= 1);
        Assert.Contains(log.Written, l => l.Contains(DeviceManager.EqNotKept));
        // A new post tries again.
        unit.Keep = true;
        manager.PostEq(1, Eq(true, 6), "XLR 1");
        SweepFor(manager, time, 300);
        Assert.Equal(DeviceEqPhase.Verified, manager.EqStatus(1).Phase);
        return 0;
    });

    [Fact]
    public void AChangeFromElsewhereIsWrittenBackNoSoonerThanTheSpacingAndLoggedOnce() => WithConfigDir(() =>
    {
        var time = new ManualTime();
        var unit = new EqUnit { Clock = time };
        var log = new Lines();
        using DeviceManager manager = Manager(unit, time, log);
        manager.PostEq(1, Eq(true, 6), "XLR 1");
        SweepFor(manager, time, 200);
        Assert.Single(unit.Writes);
        // Wave Link writes its own: written back at once.
        lock (unit) unit.Held = Eq(true, -20);
        SweepFor(manager, time, 200);
        Assert.Equal(2, unit.Writes.Count);
        Assert.Equal(Eq(true, 6), unit.Held);
        long rewritten = unit.Writes[^1].At;
        // Again soon after: not before the spacing.
        lock (unit) unit.Held = Eq(false, -20);
        SweepFor(manager, time, 4000);
        Assert.Equal(2, unit.Writes.Count);
        SweepFor(manager, time, 1500);
        Assert.Equal(3, unit.Writes.Count);
        Assert.InRange(unit.Writes[2].At - rewritten, (long)DeviceManager.EqRewriteSpacing.TotalMilliseconds, 5200);
        Assert.Single(log.Written, l => l.Contains("Another program changed the EQ"));
        Assert.Equal(DeviceEqPhase.Verified, manager.EqStatus(1).Phase);
        return 0;
    });

    /// <summary>
    /// The hand-over leaves an input switched off, then the mixer forgets it
    /// because no microphone holds the unit any more: an EQ another program
    /// sets there afterwards stays, with nothing written back and nothing
    /// logged, and the input reports no post.
    /// </summary>
    [Fact]
    public void AForgottenInputKeepsAnEqSetElsewhere() => WithConfigDir(() =>
    {
        var time = new ManualTime();
        var unit = new EqUnit { Clock = time, Held = Eq(true, 6) };
        var log = new Lines();
        using DeviceManager manager = Manager(unit, time, log);
        manager.PostEq(1, DeviceEq.Off, "XLR 1");
        SweepFor(manager, time, 200);
        Assert.Single(unit.Writes);
        Assert.False(unit.Held.Enabled);
        Assert.True(manager.EqStatus(1).IsOff);
        manager.ForgetEq(1);
        Assert.Equal(DeviceEqStatus.None, manager.EqStatus(1));
        lock (unit) unit.Held = Eq(true, -20);
        SweepFor(manager, time, (int)DeviceManager.EqRewriteSpacing.TotalMilliseconds * 3);
        Assert.Single(unit.Writes);
        Assert.Equal(Eq(true, -20), unit.Held);
        Assert.DoesNotContain(log.Written, l => l.Contains("Another program changed the EQ"));
        return 0;
    });

    [Fact]
    public void APostTheUnitAlreadyHoldsWritesNothingAndIsVerified() => WithConfigDir(() =>
    {
        DeviceEq factory = WaveFxEq.Decode(Convert.FromHexString(Mk2HardwareEqTests.FactoryBlock))!;
        var time = new ManualTime();
        var usb = new Mk2HardwareEqTests.Unit();
        var log = new Lines();
        using (DeviceManager manager = Manager(new XlrDockMk2Device(usb), time, log))
        {
            // A daemon restart: the unit holds the channel's EQ already.
            manager.PostEq(1, factory, "XLR 1");
            SweepFor(manager, time, 150);
            Assert.Empty(usb.SettingsWrites);
            Assert.Equal(new DeviceEqStatus(DeviceEqPhase.Verified, factory), manager.EqStatus(1));
            manager.PostEq(1, factory with { Enabled = true }, "XLR 1");
            SweepFor(manager, time, 150);
            Assert.Single(usb.SettingsWrites);
            Assert.DoesNotContain(log.Written, l => l.Contains("replacing"));
        }
        // A unit that holds another EQ, Wave Link's: written over, and the journal says so once.
        usb = new Mk2HardwareEqTests.Unit();
        log = new Lines();
        using (DeviceManager manager = Manager(new XlrDockMk2Device(usb), time, log))
        {
            manager.PostEq(1, Eq(true, 8), "XLR 1");
            SweepFor(manager, time, 150);
            manager.PostEq(1, Eq(true, 9), "XLR 1");
            SweepFor(manager, time, 150);
            Assert.Equal(2, usb.SettingsWrites.Count());
            Assert.Single(log.Written, l => l == "XLR 1's EQ was written to the Wave XLR Dock MK.2, replacing the one it held.");
        }
        return 0;
    });

    [Fact]
    public void NothingIsWrittenToAUnitThatIsGoneAndItsPostsAreForgotten() => WithConfigDir(() =>
    {
        var time = new ManualTime();
        var unit = new EqUnit { Clock = time };
        bool attached = true;
        using var manager = new DeviceManager(new Lines(), new ConfigurationBuilder().Build(), () => attached ? [unit] : [], time: time);
        Assert.Equal(DeviceEqStatus.None, manager.EqStatus(1));
        manager.PostEq(1, Eq(true, 3), "XLR 1");
        Assert.Equal(DeviceEqStatus.None, manager.EqStatus(1));
        manager.SweepOnce();
        manager.PostEq(1, Eq(true, 3), "XLR 1");
        manager.SweepOnce();
        Assert.Single(unit.Writes);
        Assert.Equal(DeviceEqPhase.Verified, manager.EqStatus(1).Phase);
        // Unplugged: the next sweep drops it, and its status with it.
        attached = false;
        unit.Disconnect();
        manager.SweepOnce();
        Assert.Equal(DeviceEqStatus.None, manager.EqStatus(1));
        manager.PostEq(1, Eq(false, 3), "XLR 1");
        Assert.Equal(DeviceEqStatus.None, manager.EqStatus(1));
        Assert.Single(unit.Writes);
        return 0;
    });
}
