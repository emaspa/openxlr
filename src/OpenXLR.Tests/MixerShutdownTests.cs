using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MixerShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopJoinsTheSweepAndCancelsItsPendingDefaultWrite(bool hostDeadlineExpired)
    {
        using var fixture = new Fixture();
        fixture.SetBuilt(true);
        fixture.Mixer.SetEnforcedDefaults("wanted-output", "wanted-input");
        fixture.Write("pause");
        using var sweep = new Timer(_ => fixture.Service.SweepOnce(), null, Timeout.Infinite, Timeout.Infinite);
        ServiceField("_streamSweep").SetValue(fixture.Service, sweep);
        try
        {
            sweep.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            Assert.True(SpinWait.SpinUntil(() => fixture.Exists("entered"), TimeSpan.FromSeconds(3)));
            Task stop = fixture.Service.StopAsync(new CancellationToken(hostDeadlineExpired));
            bool returnedBeforeCallback = stop.IsCompleted;
            fixture.Write("release");
            await stop.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(returnedBeforeCallback, "Stop returned while the default-device check was still running.");
            Assert.False(fixture.Exists("writes"));
            Assert.False(fixture.Service.Built);
            string[] calls = fixture.Read("calls");
            // A callback queued just before disposal must also stop at entry.
            fixture.Service.SweepOnce();
            Assert.Equal(calls, fixture.Read("calls"));
        }
        finally { fixture.Write("release"); await sweep.DisposeAsync(); }
    }

    [Fact]
    public async Task StopJoinsMeterCallbacksEvenAfterTheHostDeadlineExpires()
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var meter = new Timer(_ =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
        }, null, Timeout.Infinite, Timeout.Infinite);
        ServiceField("_meterPush").SetValue(fixture.Service, meter);
        try
        {
            meter.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Task stop = fixture.Service.StopAsync(new CancellationToken(true));
            bool returnedBeforeCallback = stop.IsCompleted;
            release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(returnedBeforeCallback);
        }
        finally { release.Set(); await meter.DisposeAsync(); }
    }

    [Fact]
    public async Task AnExpiredHostDeadlineStillDrainsTheStartupDefaultDefense()
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServiceField("_defaultDefense").SetValue(fixture.Service, pending.Task);
        Task stop = fixture.Service.StopAsync(new CancellationToken(true));
        bool returnedBeforeDefense = stop.IsCompleted;
        pending.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(returnedBeforeDefense);
    }

    [Fact]
    public void DefaultsOnlyWriteWhileTheGraphIsBuilt()
    {
        using var fixture = new Fixture();
        fixture.Mixer.SetEnforcedDefaults("wanted-output", "wanted-input");
        Assert.False(fixture.Exists("calls"));
        fixture.SetBuilt(true);
        fixture.Write("different");
        Assert.True(fixture.Mixer.EnforceDefaults());
        Assert.Equal(["set-default-sink wanted-output", "set-default-source wanted-input"], fixture.Read("writes"));
        fixture.Mixer.TearDown();
        string[] calls = fixture.Read("calls");
        Assert.False(fixture.Mixer.EnforceDefaults());
        Assert.Equal(calls, fixture.Read("calls"));
    }

    [Fact]
    public async Task ShutdownWaitsForADefaultWriteInProgressBeforeTearingDown()
    {
        using var fixture = new Fixture();
        fixture.SetBuilt(true);
        fixture.Mixer.SetEnforcedDefaults("wanted-output", null);
        fixture.Write("different");
        fixture.Write("pause-write");
        Task<bool> repair = Task.Run(() => fixture.Mixer.EnforceDefaults());
        try
        {
            Assert.True(SpinWait.SpinUntil(() => fixture.Exists("entered"), TimeSpan.FromSeconds(3)));
            Task stop = Task.Run(() => fixture.Service.StopAsync(CancellationToken.None));
            // The write holds the mixer, so teardown cannot run underneath it.
            Assert.False(SpinWait.SpinUntil(() => stop.IsCompleted, TimeSpan.FromMilliseconds(300)));
            Assert.True(fixture.Mixer.Built);
            fixture.Write("release");
            Assert.True(await repair.WaitAsync(TimeSpan.FromSeconds(3)));
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(fixture.Mixer.Built);
            Assert.Equal(["set-default-sink wanted-output"], fixture.Read("writes"));
            Assert.False(fixture.Mixer.EnforceDefaults());
        }
        finally { fixture.Write("release"); await repair.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task ADefaultCheckStillReadingNeitherHoldsTheMixerNorWritesAfterTeardown()
    {
        using var fixture = new Fixture();
        fixture.SetBuilt(true);
        fixture.Mixer.SetEnforcedDefaults("wanted-output", null);
        fixture.Write("pause");
        Task<bool> repair = Task.Run(() => fixture.Mixer.EnforceDefaults());
        try
        {
            Assert.True(SpinWait.SpinUntil(() => fixture.Exists("entered"), TimeSpan.FromSeconds(3)));
            // The read runs without the mixer lock: shutdown tears down while it waits.
            await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(fixture.Mixer.Built);
            fixture.Write("release");
            Assert.False(await repair.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(fixture.Exists("writes"));
        }
        finally { fixture.Write("release"); await repair.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task ADefaultCheckSkipsItsWriteWhenTheChoiceChangedWhileItRead()
    {
        using var fixture = new Fixture();
        fixture.SetBuilt(true);
        fixture.Mixer.SetEnforcedDefaults("wanted-output", null);
        fixture.Write("pause");
        Task<bool> repair = Task.Run(() => fixture.Mixer.EnforceDefaults());
        try
        {
            Assert.True(SpinWait.SpinUntil(() => fixture.Exists("entered"), TimeSpan.FromSeconds(3)));
            fixture.Mixer.SetEnforcedDefaults(null, null);
            fixture.Write("release");
            Assert.False(await repair.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(fixture.Exists("writes"));
        }
        finally { fixture.Write("release"); await repair.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static FieldInfo ServiceField(string name)
        => typeof(MixerService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-stop-").FullName;
        private readonly string? _oldPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly DeviceManager _devices;
        internal MixerService Service { get; }
        internal Mixer Mixer { get; }

        internal Fixture()
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
            ExecutableScript.Write(Path.Combine(_directory, "pw-dump"), "#!/bin/sh\necho '[]'\n");
            ExecutableScript.Write(Path.Combine(_directory, "pactl"), """
                #!/bin/sh
                dir=${0%/*}
                echo "$*" >> "$dir/calls"
                case "$1" in
                  get-default-sink)
                    if [ -e "$dir/pause" ]; then
                      : > "$dir/entered"
                      while [ ! -e "$dir/release" ]; do /bin/sleep .01; done
                      echo other-output
                    elif [ -e "$dir/different" ]; then echo other-output
                    else echo wanted-output
                    fi ;;
                  get-default-source)
                    if [ -e "$dir/different" ]; then echo other-input
                    else echo wanted-input
                    fi ;;
                  set-default-sink|set-default-source)
                    if [ -e "$dir/pause-write" ]; then
                      : > "$dir/entered"
                      while [ ! -e "$dir/release" ]; do /bin/sleep .01; done
                    fi
                    echo "$*" >> "$dir/writes" ;;
                  *) exit 1 ;;
                esac
                """);
            Environment.SetEnvironmentVariable("PATH", _directory);
            var config = new ConfigurationBuilder().Build();
            _devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            Service = new MixerService(NullLogger<MixerService>.Instance, config, _devices);
            Mixer = (Mixer)ServiceField("_mixer").GetValue(Service)!;
            // No physical graph or audio process is needed for these lifetime checks.
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Mixer, new MixerConfig { Channels = [], Mixes = [] });
        }

        internal void SetBuilt(bool built) => typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, built);
        internal void Write(string name) => File.WriteAllText(Path.Combine(_directory, name), "");
        internal bool Exists(string name) => File.Exists(Path.Combine(_directory, name));
        internal string[] Read(string name) => File.ReadAllLines(Path.Combine(_directory, name));
        public void Dispose()
        {
            Service.Dispose();
            Mixer.Dispose();
            _devices.Dispose();
            Environment.SetEnvironmentVariable("PATH", _oldPath);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
