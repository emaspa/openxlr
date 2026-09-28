using System.Collections.Concurrent;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class PlasmaVolumeRangeTests
{
    [PlasmaConfigFact]
    public async Task RealKConfigHelpersPreserveOtherSettingsAndNotifyPlasma()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-plasma-config-").FullName;
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? oldConfigDirs = Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS");
        string? oldBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        string address = "unix:path=" + directory + "/bus";
        using var stop = new CancellationTokenSource();
        Task<OpenXLR.Core.ProcessResult>? busProcess = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            Environment.SetEnvironmentVariable("XDG_CONFIG_DIRS", directory);
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", address);
            busProcess = OpenXLR.Core.ProcessRunner.RunAsync("dbus-daemon", ["--session", "--nofork", "--address=" + address],
                TimeSpan.FromSeconds(30), cancel: stop.Token);
            await Wait(() => File.Exists(directory + "/bus"));
            using var bus = new Tmds.DBus.Protocol.DBusConnection(address);
            await bus.ConnectAsync();
            var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var match = await bus.AddMatchAsync(new Tmds.DBus.Protocol.MatchRule
            {
                Type = Tmds.DBus.Protocol.MessageType.Signal, Path = "/plasmaparc",
                Interface = "org.kde.kconfig.notify", Member = "ConfigChanged",
            }, static (_, _) => true, _ => notified.TrySetResult(), emitOnCapturedContext: false);
            string file = Path.Combine(directory, "plasmaparc");
            File.WriteAllText(file, "[General]\nVolumeStep=7\n");
            var values = new ConcurrentQueue<bool>();
            var errors = new ConcurrentQueue<string?>();
            await using var range = new PlasmaVolumeRange(values.Enqueue, errors.Enqueue, action => action());
            range.Start();
            await Wait(() => values.TryPeek(out bool boost) && !boost);
            range.Set(true);
            await Wait(() => values.Last());
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains("VolumeStep=7", File.ReadAllText(file));
            var result = await OpenXLR.Core.ProcessRunner.RunAsync("kwriteconfig6",
                ["--file", "plasmaparc", "--group", "General", "--key", "RaiseMaximumVolume", "--type", "bool", "--notify", "false"]);
            Assert.True(result.Ok, result.Stderr);
            await Wait(() => !values.Last());
            Assert.All(errors, error => Assert.Null(error));
        }
        finally
        {
            stop.Cancel();
            if (busProcess is not null) await busProcess;
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
            Environment.SetEnvironmentVariable("XDG_CONFIG_DIRS", oldConfigDirs);
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", oldBus);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WatchesAtomicReplacementDeletionAndWritesWithDesktopNotification()
    {
        await using var test = new Helpers();
        test.Range.Start();
        await test.WaitFor(false);
        test.Replace("true");
        await test.WaitFor(true);
        test.Range.Set(false);
        await test.WaitFor(false);
        Assert.Contains("--type bool --notify false", File.ReadAllText(test.PathOf("writes")));
        test.Replace("true");
        await test.WaitFor(true);
        File.Delete(test.PathOf("plasmaparc"));
        await test.WaitFor(false);
        Assert.All(test.Errors, error => Assert.Null(error));
    }

    [Fact]
    public async Task AnIdleRangeDoesNotLaunchMoreHelpers()
    {
        await using var test = new Helpers();
        test.Range.Start();
        await test.WaitFor(false);
        int reads = File.ReadAllLines(test.PathOf("reads")).Length;
        await Task.Delay(250);
        Assert.Equal(reads, File.ReadAllLines(test.PathOf("reads")).Length);
        Assert.False(File.Exists(test.PathOf("writes")));
    }

    [Fact]
    public async Task AnOldReadCannotOverwriteANewerRequestAndRapidChangesCoalesce()
    {
        await using var test = new Helpers();
        File.WriteAllText(test.PathOf("hold"), "");
        test.Range.Start();
        await Wait(() => File.Exists(test.PathOf("reading")));
        for (int i = 0; i < 100; i++) test.Range.Set(i % 2 == 1);
        File.Delete(test.PathOf("hold"));
        await test.WaitFor(true);
        Assert.DoesNotContain(false, test.Values);
        Assert.Single(File.ReadAllLines(test.PathOf("writes")));
        Assert.Equal("true\n", File.ReadAllText(test.PathOf("plasmaparc")));
    }

    [Theory]
    [InlineData("fail-write")]
    [InlineData("ignore-write")]
    public async Task AFailedWriteReportsAnErrorAndRestoresTheDesktopRange(string failure)
    {
        await using var test = new Helpers();
        test.Range.Start();
        await test.WaitFor(false);
        File.WriteAllText(test.PathOf(failure), "");
        test.Range.Set(true);
        await Wait(() => test.Errors.Any(e => e is not null));
        Assert.False(test.Values.Last());
        Assert.Contains("Cannot synchronize", test.Errors.Last()!);
        File.Delete(test.PathOf(failure));
        test.Range.Set(true);
        await test.WaitFor(true);
        await Wait(() => test.Errors.Last() is null);
    }

    [Fact]
    public async Task ARefreshDuringAFailedWriteDoesNotHideTheSaveError()
    {
        await using var test = new Helpers();
        test.Range.Start();
        await test.WaitFor(false);
        File.WriteAllText(test.PathOf("fail-write"), "");
        File.WriteAllText(test.PathOf("hold-write"), "");
        test.Range.Set(true);
        await Wait(() => File.Exists(test.PathOf("writing")));
        // Activation or a file event can arrive before the helper reports failure.
        test.Range.Refresh();
        File.Delete(test.PathOf("hold-write"));
        await Wait(() => test.Errors.Any(e => e is not null));
        Assert.False(test.Values.Last());
        Assert.NotNull(test.Errors.Last());
        int reports = test.Errors.Count;
        test.Range.Refresh();
        await Wait(() => test.Errors.Count > reports);
        Assert.NotNull(test.Errors.Last());
        test.Replace("true");
        await test.WaitFor(true);
        await Wait(() => test.Errors.Last() is null);
    }

    [Fact]
    public async Task ADelayedUiKeepsOnlyOnePendingPublication()
    {
        var callbacks = new ConcurrentQueue<Action>();
        await using var test = new Helpers(callbacks.Enqueue);
        test.Range.Start();
        for (int i = 1; i <= 25; i++)
        {
            int count = i;
            await Wait(() => File.Exists(test.PathOf("reads")) && File.ReadAllLines(test.PathOf("reads")).Length >= count);
            test.Range.Refresh();
        }
        Assert.Single(callbacks);
        test.Range.Set(true);
        await Wait(() =>
        {
            Assert.InRange(callbacks.Count, 0, 1);
            while (callbacks.TryDequeue(out var callback)) callback();
            return test.Values.TryPeek(out _) && test.Values.Last();
        });
        Assert.DoesNotContain(false, test.Values);
        Assert.All(test.Errors, error => Assert.Null(error));
    }

    [Theory]
    [InlineData("not-a-boolean")]
    [InlineData(" ")]
    public async Task MalformedSettingsDoNotInventADesktopPreference(string input)
    {
        await using var test = new Helpers();
        test.Replace(input);
        test.Range.Start();
        await Wait(() => test.Errors.Any(e => e is not null));
        Assert.Empty(test.Values);
        Assert.Contains("invalid volume range", test.Errors.Last()!);
    }

    [Fact]
    public async Task MissingHelpersKeepLocalControlsUsableAndReportTheFailure()
    {
        await using var test = new Helpers();
        File.Delete(test.PathOf("kreadconfig6"));
        test.Range.Start();
        await Wait(() => test.Errors.Any(e => e is not null));
        Assert.Empty(test.Values);
    }

    [Fact]
    public async Task DisposalCancelsHelpersAndQueuedUiCallbacks()
    {
        var callbacks = new ConcurrentQueue<Action>();
        await using var test = new Helpers(callbacks.Enqueue);
        test.Range.Start();
        await Wait(() => !callbacks.IsEmpty);
        File.WriteAllText(test.PathOf("hold"), "");
        File.Delete(test.PathOf("reading"));
        test.Range.Refresh();
        await Wait(() => File.Exists(test.PathOf("reading")));
        await test.Range.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        while (callbacks.TryDequeue(out var callback)) callback();
        Assert.Empty(test.Values);
        Assert.Empty(test.Errors);
        test.Range.Set(true);
        test.Range.Refresh();
        Assert.False(File.Exists(test.PathOf("writes")));
    }

    [Fact]
    public void PlasmaDetectionAcceptsCombinedDesktopNamesOnly()
    {
        string? old = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        try
        {
            foreach (var (name, expected) in new[] { ("KDE", true), ("foo:KDE", true), ("GNOME", false), ("notKDE", false), ("", false) })
            {
                Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", name);
                Assert.Equal(expected, PlasmaVolumeRange.IsPlasma);
            }
        }
        finally { Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", old); }
    }

    private static async Task Wait(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(condition(), "Desktop volume range did not settle.");
    }

    private sealed class Helpers : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-volume-range-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        internal ConcurrentQueue<bool> Values { get; } = new();
        internal ConcurrentQueue<string?> Errors { get; } = new();
        internal PlasmaVolumeRange Range { get; }
        internal string PathOf(string name) => Path.Combine(_directory, name);
        internal Helpers(Action<Action>? dispatch = null)
        {
            ExecutableScript.Write(PathOf("kreadconfig6"), """
                dir=${0%/*}
                value=false
                if [ -f "$dir/plasmaparc" ]; then value=$(/bin/cat "$dir/plasmaparc"); fi
                /usr/bin/touch "$dir/reading"
                printf 'read\n' >> "$dir/reads"
                while [ -f "$dir/hold" ]; do /bin/sleep 0.01; done
                printf '%s\n' "$value"
                """);
            ExecutableScript.Write(PathOf("kwriteconfig6"), """
                dir=${0%/*}
                printf '%s\n' "$*" >> "$dir/writes"
                /usr/bin/touch "$dir/writing"
                while [ -f "$dir/hold-write" ]; do /bin/sleep 0.01; done
                if [ -f "$dir/fail-write" ]; then exit 2; fi
                if [ -f "$dir/ignore-write" ]; then exit 0; fi
                for value in "$@"; do :; done
                printf '%s\n' "$value" > "$dir/next"
                /bin/mv "$dir/next" "$dir/plasmaparc"
                """);
            Environment.SetEnvironmentVariable("PATH", _directory);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
            Range = new PlasmaVolumeRange(Values.Enqueue, Errors.Enqueue, dispatch ?? (action => action()));
        }
        internal void Replace(string value)
        {
            File.WriteAllText(PathOf("next"), value);
            File.Move(PathOf("next"), PathOf("plasmaparc"), overwrite: true);
        }
        internal Task WaitFor(bool value) => Wait(() => Values.TryPeek(out _) && Values.Last() == value);
        public async ValueTask DisposeAsync()
        {
            await Range.DisposeAsync();
            Environment.SetEnvironmentVariable("PATH", _path);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config);
            Directory.Delete(_directory, recursive: true);
        }
    }
}

internal sealed class PlasmaConfigFactAttribute : FactAttribute
{
    public PlasmaConfigFactAttribute()
    {
        string[] paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        if (new[] { "dbus-daemon", "kreadconfig6", "kwriteconfig6" }.Any(tool => !paths.Any(path => File.Exists(Path.Combine(path, tool)))))
            Skip = "Plasma's KConfig helpers and a private D-Bus session are required.";
    }
}
