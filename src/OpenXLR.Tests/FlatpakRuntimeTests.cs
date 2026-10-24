using OpenXLR.Daemon;
using ProcessRunner = OpenXLR.Core.ProcessRunner;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class FlatpakRuntimeTests
{
    [Fact]
    public void LogsUseTheStateDirectoryInsteadOfThePreferenceDirectory()
    {
        string? previous = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", "/tmp/openxlr-test-state");
            Assert.Equal("/tmp/openxlr-test-state/openxlr", OpenXLR.Core.OpenXlrPaths.StateDir);
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", "");
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "openxlr"),
                OpenXLR.Core.OpenXlrPaths.StateDir);
        }
        finally { Environment.SetEnvironmentVariable("XDG_STATE_HOME", previous); }
    }

    [Fact]
    public async Task OwnerExitAndDaemonCancellationAreDifferent()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.False(await FlatpakParentLifetime.WaitForOwnerAsync(Environment.ProcessId, cancelled.Token));
        Assert.True(await FlatpakParentLifetime.WaitForOwnerAsync(int.MaxValue, CancellationToken.None));
    }

    [Fact]
    public void FlatpakNeverHandsItsWindowToTheNativeInstallation()
    {
        string? previous = Environment.GetEnvironmentVariable("FLATPAK_ID");
        try
        {
            Environment.SetEnvironmentVariable("FLATPAK_ID", null);
            string native = OpenXLR.UI.SingleInstance.DefaultPath;
            Environment.SetEnvironmentVariable("FLATPAK_ID", OpenXLR.UI.FlatpakEnvironment.AppId);
            Assert.NotEqual(native, OpenXLR.UI.SingleInstance.DefaultPath);
            Assert.Equal(Path.GetDirectoryName(native), Path.GetDirectoryName(OpenXLR.UI.SingleInstance.DefaultPath));
            Assert.False(OpenXLR.UI.StartupIntegration.SetWindowAtLogin(true));
            Assert.False(OpenXLR.UI.StartupIntegration.SetDaemonAtLogin(true));
        }
        finally { Environment.SetEnvironmentVariable("FLATPAK_ID", previous); }
    }

    [Fact]
    public void FlatpakClosesToTheTrayUntilTheUserChoosesOtherwise()
    {
        string? previous = Environment.GetEnvironmentVariable("FLATPAK_ID");
        try
        {
            Environment.SetEnvironmentVariable("FLATPAK_ID", null);
            Assert.False(new OpenXLR.UI.UiSettings().MinimizeToTray);
            // The Flatpak UI owns its daemon, so a plain close would stop audio.
            Environment.SetEnvironmentVariable("FLATPAK_ID", OpenXLR.UI.FlatpakEnvironment.AppId);
            Assert.True(new OpenXLR.UI.UiSettings().MinimizeToTray);
            Assert.True(System.Text.Json.JsonSerializer.Deserialize<OpenXLR.UI.UiSettings>("{}")!.MinimizeToTray);
            Assert.False(System.Text.Json.JsonSerializer.Deserialize<OpenXLR.UI.UiSettings>("{\"MinimizeToTray\":false}")!.MinimizeToTray);
        }
        finally { Environment.SetEnvironmentVariable("FLATPAK_ID", previous); }
    }

    [Fact]
    public void SessionRulesKeepExistingUserFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-rules-" + Guid.NewGuid());
        string source = Path.Combine(root, "source"), config = Path.Combine(root, "config");
        Directory.CreateDirectory(source); Directory.CreateDirectory(config);
        try
        {
            foreach (string rule in OpenXLR.UI.FlatpakSetup.Rules) File.WriteAllText(Path.Combine(source, rule), "rule");
            OpenXLR.UI.FlatpakSetup.InstallRules(source, config);
            OpenXLR.UI.FlatpakSetup.InstallRules(source, config);
            string target = Path.Combine(config, "wireplumber", "wireplumber.conf.d", OpenXLR.UI.FlatpakSetup.Rules[0]);
            File.WriteAllText(target, "user rule");
            Assert.Throws<IOException>(() => OpenXLR.UI.FlatpakSetup.InstallRules(source, config));
            Assert.Equal("user rule", File.ReadAllText(target));
            File.Delete(target); File.CreateSymbolicLink(target, Path.Combine(source, OpenXLR.UI.FlatpakSetup.Rules[0]));
            Assert.Throws<IOException>(() => OpenXLR.UI.FlatpakSetup.InstallRules(source, config));
            Assert.Equal("rule", File.ReadAllText(target));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void SessionRulesAreReadableAndLeaveTheDirectoryMode()
    {
        string root = Directory.CreateTempSubdirectory("openxlr-rule-modes-").FullName;
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string directory = Path.Combine(root, "config", "wireplumber", "wireplumber.conf.d");
        const UnixFileMode Shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        try
        {
            foreach (string rule in OpenXLR.UI.FlatpakSetup.Rules) File.WriteAllText(Path.Combine(source, rule), "rule");
            Directory.CreateDirectory(directory, Shared);
            File.SetUnixFileMode(directory, Shared);
            OpenXLR.UI.FlatpakSetup.InstallRules(source, Path.Combine(root, "config"));
            Assert.Equal(Shared, File.GetUnixFileMode(directory));
            foreach (string rule in OpenXLR.UI.FlatpakSetup.Rules)
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    File.GetUnixFileMode(Path.Combine(directory, rule)));
            Assert.Equal(OpenXLR.UI.FlatpakSetup.Rules.Order(), Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Order());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("wireplumber")]
    [InlineData("wireplumber/wireplumber.conf.d")]
    public void SessionRulesNeverFollowConfigurationDirectoryLinks(string linkedDirectory)
    {
        string root = Directory.CreateTempSubdirectory("openxlr-rule-links-").FullName;
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string config = Directory.CreateDirectory(Path.Combine(root, "config")).FullName;
        string other = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
        try
        {
            foreach (string rule in OpenXLR.UI.FlatpakSetup.Rules) File.WriteAllText(Path.Combine(source, rule), "rule");
            string link = Path.Combine(config, linkedDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            Directory.CreateSymbolicLink(link, other);
            Assert.Throws<IOException>(() => OpenXLR.UI.FlatpakSetup.InstallRules(source, config));
            Assert.Empty(Directory.EnumerateFileSystemEntries(other));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RestartWaitsForThePreviousDaemonToStop()
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-session-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string script = ExecutableScript.Write(Path.Combine(root, "child"),
                "#!/bin/sh\nroot='" + root + "'\ntrap 'echo stop >> \"$root/events\"; exit 0' TERM\necho start >> \"$root/events\"\nwhile :; do sleep 0.05; done\n");
            using (var session = new OpenXLR.UI.FlatpakSession(script))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                string path = Path.Combine(root, "events");
                while (!File.Exists(path)) await Task.Delay(10, deadline.Token);
                Assert.True(session.Restart());
                while (File.ReadAllLines(path).Length < 3) await Task.Delay(10, deadline.Token);
                Assert.Equal(new[] { "start", "stop", "start" }, File.ReadAllLines(path));
            }
            Assert.Equal(new[] { "start", "stop", "start", "stop" }, File.ReadAllLines(Path.Combine(root, "events")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("gamescope", true)]
    [InlineData("KDE:gamescope", true)]
    [InlineData("KDE", false)]
    [InlineData(null, false)]
    public void GameModeIsTheGamescopeSession(string? desktop, bool gameMode)
    {
        string? previous = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", desktop);
            Assert.Equal(gameMode, OpenXLR.UI.MainWindow.GameMode);
        }
        finally { Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", previous); }
    }

    [Fact]
    public async Task ADaemonAlreadyHoldingTheLockLeavesTheSessionAttached()
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-attached-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            // 73 is the sandbox daemon's answer when the user service holds the lock.
            string script = ExecutableScript.Write(Path.Combine(root, "child"),
                "#!/bin/sh\necho start >> '" + root + "/events'\nexit 73\n");
            using var session = new OpenXLR.UI.FlatpakSession(script);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!session.Attached) await Task.Delay(10, deadline.Token);
            await Task.Delay(300, deadline.Token);
            Assert.Equal(new[] { "start" }, File.ReadAllLines(Path.Combine(root, "events")));
            // The UI's own retry loop has ended; the service restarts its daemon.
            Assert.False(session.Restart());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedChildStopsGracefullyOrIsKilledWithinItsDeadline(bool ignoreTerm)
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-owned-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        using var stop = new CancellationTokenSource();
        Task<int>? run = null;
        try
        {
            string script = ExecutableScript.Write(Path.Combine(root, "child"),
                "#!/bin/sh\n" + (ignoreTerm ? "trap '' TERM\n" : "trap 'echo stopped > \"$1/done\"; exit 0' TERM\n")
                + "echo ready > \"$1/ready\"\nwhile :; do sleep 0.05; done\n");
            run = ProcessRunner.RunOwnedAsync(script, [root], stop.Token, TimeSpan.FromMilliseconds(300));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(root, "ready"))) await Task.Delay(10, deadline.Token);
            Assert.False(run.IsCompleted);
            stop.Cancel();
            int exit = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(!ignoreTerm, File.Exists(Path.Combine(root, "done")));
            Assert.Equal(!ignoreTerm, exit == 0);
        }
        finally
        {
            stop.Cancel();
            if (run is not null) await run.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RestartInterruptsTheRetryDelay()
    {
        string root = Directory.CreateTempSubdirectory("openxlr-retry-").FullName;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            string path = Path.Combine(root, "events");
            string script = ExecutableScript.Write(Path.Combine(root, "child"),
                "#!/bin/sh\necho start >> '" + path + "'\nexit 1\n");
            using var session = new OpenXLR.UI.FlatpakSession(script, (_, token) =>
            {
                waiting.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.Restart());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (File.ReadAllLines(path).Length < 2) await Task.Delay(10, deadline.Token);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ALogFifoCannotBlockTheDaemon()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Directory.CreateTempSubdirectory("openxlr-log-fifo-").FullName;
        string path = Path.Combine(root, "daemon.log");
        Task? write = null;
        try
        {
            Assert.True(ProcessRunner.Run("mkfifo", [path]).Ok);
            using var log = new FlatpakLog(path);
            write = Task.Run(() => log.Write("must not block"));
            await write.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (write is { IsCompleted: false })
            {
                using var unblock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                await write.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DiagnosticLogsKeepPrivacyFilteringAndFileBounds()
    {
        string root = Directory.CreateTempSubdirectory("openxlr-log-copy-").FullName;
        string output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        try
        {
            string path = Path.Combine(root, "daemon.log");
            File.WriteAllText(path, "/home/test-login/plugin {\"application.process.id\":12345} token=secret-value");
            string Clean(string text) => OpenXLR.UI.Diagnostics.Redact(text, [], "test-login");
            OpenXLR.UI.Diagnostics.CopyDaemonLogs(Clean, root, output);
            string text = File.ReadAllText(Path.Combine(output, "daemon.log"));
            Assert.DoesNotContain("test-login", text);
            Assert.DoesNotContain("secret-value", text);
            Assert.DoesNotContain("12345", text);
            File.WriteAllText(path, new string('x', OpenXLR.UI.Diagnostics.DaemonLogCopyLimit + 1));
            OpenXLR.UI.Diagnostics.CopyDaemonLogs(Clean, root, output);
            Assert.Contains("File unavailable:", File.ReadAllText(Path.Combine(output, "daemon.log")));
            if (OperatingSystem.IsLinux())
            {
                File.Delete(path);
                Assert.True(ProcessRunner.Run("mkfifo", [path]).Ok);
                OpenXLR.UI.Diagnostics.CopyDaemonLogs(Clean, root, output);
                Assert.Contains("File unavailable:", File.ReadAllText(Path.Combine(output, "daemon.log")));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DiagnosticLogLinksCannotCollectAnotherPrivateFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Directory.CreateTempSubdirectory("openxlr-log-link-").FullName;
        string output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        try
        {
            string secret = "NEVER-COLLECT-PRIVATE-TOKEN";
            string target = Path.Combine(root, "token");
            File.WriteAllText(target, secret);
            File.CreateSymbolicLink(Path.Combine(root, "daemon.log"), target);
            OpenXLR.UI.Diagnostics.CopyDaemonLogs(text => text, root, output);
            Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(output, "daemon.log")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancelledOwnerNeverStartsItsChild()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunOwnedAsync(
            "/missing-openxlr-child", [], stop.Token, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void LogRotatesAndRefusesSymbolicLinks()
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-log-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "daemon.log");
            using var log = new FlatpakLog(path);
            for (int i = 0; i < 140; i++) log.Write(new string('a', 9000));
            Assert.True(File.Exists(path + ".old"));
            Assert.InRange(new FileInfo(path).Length, 1, FlatpakLog.Limit + 8193);
            Assert.InRange(new FileInfo(path + ".old").Length, 1, FlatpakLog.Limit + 8193);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            string target = Path.Combine(root, "private");
            File.WriteAllText(target, "keep");
            File.Delete(path); File.CreateSymbolicLink(path, target);
            log.Write("do not follow");
            Assert.Equal("keep", File.ReadAllText(target));
        }
        finally { Directory.Delete(root, true); }
    }
}
