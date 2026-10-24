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
}
