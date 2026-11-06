using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class StartupIntegrationRestartTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void ExplicitRestartClearsOnlyTheDaemonStartLimitAndReportsFailures(int resetExit, int restartExit)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-restart-limit-").FullName;
        string log = Path.Combine(directory, "calls"), cleared = Path.Combine(directory, "cleared");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        string? previousFlatpak = Environment.GetEnvironmentVariable("FLATPAK_ID");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "systemctl"), $$"""
                #!/bin/sh
                printf '%s\n' "$*" >> {{InstallationDoctor.Quote(log)}}
                [ "$#" = 3 ] && [ "$1" = --user ] && [ "$3" = openxlr-daemon.service ] || exit 99
                case "$2" in
                  reset-failed)
                    [ {{resetExit}} = 0 ] || exit {{resetExit}}
                    : > {{InstallationDoctor.Quote(cleared)}}
                    ;;
                  restart)
                    [ -f {{InstallationDoctor.Quote(cleared)}} ] || exit 5
                    exit {{restartExit}}
                    ;;
                  *) exit 99 ;;
                esac
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            Environment.SetEnvironmentVariable("FLATPAK_ID", null);

            Assert.Equal(resetExit == 0 && restartExit == 0, StartupIntegration.RestartDaemon());
            string[] expected = resetExit == 0
                ? ["--user reset-failed openxlr-daemon.service", "--user restart openxlr-daemon.service"]
                : ["--user reset-failed openxlr-daemon.service"];
            Assert.Equal(expected, File.ReadAllLines(log));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("FLATPAK_ID", previousFlatpak);
            Directory.Delete(directory, recursive: true);
        }
    }
    [Theory]
    [InlineData("42", 0, true, true)]
    [InlineData("42", 1, true, false)]
    [InlineData("0", 0, true, null)]
    [InlineData("43", 0, true, null)]
    [InlineData("42", 0, false, null)]
    public async Task TheUpgradeQuestionRequiresTheSameDaemonAndClaimsOnlyOneAttempt(
        string servicePid, int restartExit, bool remainsConnected, bool? expected)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-restart-upgrade-").FullName;
        string log = Path.Combine(directory, "calls");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        string? previousRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "systemctl"), $$"""
                #!/bin/sh
                case "$*" in
                  '--user show --property=MainPID --value openxlr-daemon.service')
                    printf '%s\n' '{{servicePid}}'
                    ;;
                  '--user --job-mode=fail try-restart openxlr-daemon.service')
                    printf '%s\n' restart >> {{InstallationDoctor.Quote(log)}}
                    exit {{restartExit}}
                    ;;
                  *) exit 99 ;;
                esac
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", directory);
            OpenXlrPaths.EnsurePrivateDir(OpenXlrPaths.RuntimeDir);

            bool?[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                int checks = 0;
                return StartupIntegration.IsRunningDaemonService(42, () => ++checks == 1 || remainsConnected)
                    && StartupIntegration.ClaimDaemonUpgradePrompt()
                    ? StartupIntegration.RestartRunningDaemonService(42, () => remainsConnected) : (bool?)null;
            })));
            Assert.Equal(expected is null ? [] : new[] { expected }, results.Where(result => result is not null));
            if (expected is null)
                Assert.Empty(Directory.GetFiles(OpenXlrPaths.RuntimeDir, "upgrade-prompt-*"));
            else
                Assert.False(StartupIntegration.ClaimDaemonUpgradePrompt());
            if (expected is null) Assert.False(File.Exists(log));
            else Assert.Equal(["restart"], File.ReadAllLines(log));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousRuntime);
            Directory.Delete(directory, recursive: true);
        }
    }
    [Fact]
    public void AServiceReplacedWhileTheQuestionIsOpenIsNotRestarted()
    {
        string directory = Directory.CreateTempSubdirectory("ox-upgrade-pid-").FullName;
        string pidFile = Path.Combine(directory, "pid"), log = Path.Combine(directory, "restart");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        string? previousRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "systemctl"), $$"""
                #!/bin/sh
                case "$*" in
                  '--user show --property=MainPID --value openxlr-daemon.service')
                    read -r pid < {{InstallationDoctor.Quote(pidFile)}}
                    echo "$pid" ;;
                  '--user --job-mode=fail try-restart openxlr-daemon.service')
                    echo restart > {{InstallationDoctor.Quote(log)}} ;;
                  *) exit 99 ;;
                esac
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", directory);
            File.WriteAllText(pidFile, "42\n");
            Assert.True(StartupIntegration.IsRunningDaemonService(42, () => true));
            Assert.True(StartupIntegration.ClaimDaemonUpgradePrompt());
            File.WriteAllText(pidFile, "43\n");
            Assert.False(StartupIntegration.RestartRunningDaemonService(42, () => true));
            Assert.False(File.Exists(log));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousRuntime);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AClaimRemovesEarlierVersionsMarkersFromTheConfigurationFallback()
    {
        string directory = Directory.CreateTempSubdirectory("ox-upgrade-markers-").FullName;
        string? previousConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? previousRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
            Assert.Equal(OpenXlrPaths.ConfigDir, OpenXlrPaths.RuntimeDir);
            OpenXlrPaths.EnsurePrivateDir(OpenXlrPaths.RuntimeDir);
            string[] stale = ["upgrade-prompt-0000", "upgrade-prompt-1111"];
            foreach (string name in stale) File.WriteAllText(Path.Combine(OpenXlrPaths.RuntimeDir, name), "");
            string unrelated = Path.Combine(OpenXlrPaths.RuntimeDir, "ui.json");
            File.WriteAllText(unrelated, "{}");

            Assert.True(StartupIntegration.ClaimDaemonUpgradePrompt());
            string current = Assert.Single(Directory.GetFiles(OpenXlrPaths.RuntimeDir, "upgrade-prompt-*"));
            Assert.DoesNotContain(Path.GetFileName(current), stale);
            Assert.True(File.Exists(unrelated));

            // A second claim in the same version still finds its own marker.
            Assert.False(StartupIntegration.ClaimDaemonUpgradePrompt());
            Assert.Equal([current], Directory.GetFiles(OpenXlrPaths.RuntimeDir, "upgrade-prompt-*"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousConfig);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousRuntime);
            Directory.Delete(directory, true);
        }
    }
}
