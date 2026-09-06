using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class StartupIntegrationTests
{
    [Theory]
    [InlineData("/home/em/openxlr/OpenXLR.Daemon", "\"/home/em/openxlr/OpenXLR.Daemon\"")]
    [InlineData("/home/em/My Projects/OpenXLR.Daemon", "\"/home/em/My Projects/OpenXLR.Daemon\"")]
    [InlineData("/home/em/100%/OpenXLR.Daemon", "\"/home/em/100%%/OpenXLR.Daemon\"")]
    [InlineData("/home/em/a\"b/OpenXLR.Daemon", "\"/home/em/a\\\"b/OpenXLR.Daemon\"")]
    public void UnitPathsAreQuotedAndRoundTrip(string path, string quoted)
    {
        Assert.Equal(quoted, StartupIntegration.SystemdQuote(path));
        Assert.Equal(path, StartupIntegration.ExecStartBinary(quoted + " --flag"));
    }

    [Theory]
    [InlineData("/usr/bin/openxlr-daemon", "/usr/bin/openxlr-daemon")]
    [InlineData("  /usr/bin/openxlr-daemon --mixer", "/usr/bin/openxlr-daemon")]
    [InlineData("-/usr/bin/openxlr-daemon", "/usr/bin/openxlr-daemon")]
    [InlineData("'/opt/my tools/daemon' x", "/opt/my tools/daemon")]
    [InlineData("", null)]
    public void OlderUnitsWithPlainPathsStillParse(string value, string? expected)
        => Assert.Equal(expected, StartupIntegration.ExecStartBinary(value));

    [Theory]
    [InlineData("/home/em/openxlr/OpenXLR.UI", "\"/home/em/openxlr/OpenXLR.UI\"")]
    [InlineData("/home/em/My Projects/OpenXLR.UI", "\"/home/em/My Projects/OpenXLR.UI\"")]
    [InlineData("/home/em/100%/OpenXLR.UI", "\"/home/em/100%%/OpenXLR.UI\"")]
    [InlineData("/home/em/$x/OpenXLR.UI", "\"/home/em/\\\\$x/OpenXLR.UI\"")]
    public void DesktopExecValuesFollowTheSpec(string path, string exec)
        => Assert.Equal(exec, StartupIntegration.DesktopExec(path));
}
