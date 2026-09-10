using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class WindowAutostartTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "openxlr-autostart-" + Guid.NewGuid());
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private string Entry => Path.Combine(_home, "autostart", "openxlr.desktop");
    private string Executable => Path.Combine(_home, "My Build", "OpenXLR.UI");

    public WindowAutostartTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _home);
        Directory.CreateDirectory(Path.GetDirectoryName(Executable)!);
        File.WriteAllText(Executable, "#!/bin/sh\nexit 0\n");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
        Directory.Delete(_home, true);
    }

    [Fact]
    public void EnablingCreatesAQuotedDesktopEntryAndDisablingRemovesIt()
    {
        Assert.True(StartupIntegration.SetWindowAtLogin(true, Executable));
        string entry = File.ReadAllText(Entry);
        Assert.Contains("[Desktop Entry]", entry);
        Assert.Contains("Type=Application", entry);
        Assert.Contains("Exec=" + StartupIntegration.DesktopExec(Executable), entry);
        Assert.DoesNotContain("OnlyShowIn", entry);
        Assert.DoesNotContain("Hidden=true", entry);
        Assert.True(StartupIntegration.SetWindowAtLogin(false, Executable));
        Assert.False(File.Exists(Entry));
        Assert.True(StartupIntegration.SetWindowAtLogin(false, Executable));
    }

    [Fact]
    public void EnabledPreferenceRepairsAMissingEntryAndIsIdempotent()
    {
        var settings = new UiSettings { OpenWindowAtLogin = true };
        Assert.True(StartupIntegration.RepairWindowAutostart(settings, Executable));
        string entry = File.ReadAllText(Entry);
        var marker = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Entry, marker);
        Assert.True(StartupIntegration.RepairWindowAutostart(settings, Executable));
        Assert.Equal(entry, File.ReadAllText(Entry));
        Assert.Equal(marker, File.GetLastWriteTimeUtc(Entry));
    }

    [Fact]
    public void DaemonAndMinimizedPreferencesDoNotEnableWindowAutostart()
    {
        var settings = new UiSettings { StartDaemonAtLogin = true, StartMinimized = true };
        settings.Save();
        Assert.True(StartupIntegration.RepairWindowAutostart(settings, Executable));
        Assert.False(File.Exists(Entry));
        var client = new DaemonClient();
        var options = new OptionsViewModel(client, new MainViewModel(client));
        Assert.True(options.StartDaemonAtLogin);
        Assert.True(options.StartMinimized);
        Assert.False(options.OpenWindowAtLogin);
        Assert.Contains("does not enable autostart", options.StartupHint);
    }

    [Theory]
    [InlineData("Exec=\"/old/build/OpenXLR.UI\"\n")]
    [InlineData("")]
    public void RepairsLaunchPathWithoutRemovingDesktopPreferences(string exec)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "[Desktop Entry]\nType=Application\n" + exec +
            "Hidden=true\nX-KDE-autostart-after=panel\n[Desktop Action Manual]\nExec=manual-command\n");
        Assert.True(StartupIntegration.RepairWindowAutostart(new() { OpenWindowAtLogin = true }, Executable));
        string entry = File.ReadAllText(Entry);
        Assert.Contains("Exec=" + StartupIntegration.DesktopExec(Executable), entry);
        Assert.Contains("Hidden=true", entry);
        Assert.Contains("X-KDE-autostart-after=panel", entry);
        Assert.Contains("[Desktop Action Manual]\nExec=manual-command", entry);
    }

    [Fact]
    public void MissingBinaryDoesNotOverwriteAnExistingEntry()
    {
        Assert.True(StartupIntegration.SetWindowAtLogin(true, Executable));
        string before = File.ReadAllText(Entry);
        Assert.False(StartupIntegration.RepairWindowAutostart(new() { OpenWindowAtLogin = true }, Executable + ".missing"));
        Assert.False(StartupIntegration.SetWindowAtLogin(true, Executable + ".missing"));
        Assert.Equal(before, File.ReadAllText(Entry));
    }

    [Fact]
    public void FailedDisableKeepsTheSavedPreferenceAndReportsTheFailure()
    {
        new UiSettings { OpenWindowAtLogin = true }.Save();
        Directory.CreateDirectory(Entry); // A directory cannot be removed as a desktop file.
        var client = new DaemonClient();
        var options = new OptionsViewModel(client, new MainViewModel(client));
        options.OpenWindowAtLogin = false;
        Assert.True(options.OpenWindowAtLogin);
        Assert.True(UiSettings.Load().OpenWindowAtLogin);
        Assert.NotNull(options.StartupError);

        Directory.Delete(Entry);
        options.OpenWindowAtLogin = false;
        Assert.False(options.OpenWindowAtLogin);
        Assert.False(UiSettings.Load().OpenWindowAtLogin);
        Assert.Null(options.StartupError);
    }

    [Fact]
    public void WriteFailureReturnsFalseInsteadOfCrashing()
    {
        File.WriteAllText(Path.Combine(_home, "autostart"), "not a directory");
        Assert.False(StartupIntegration.SetWindowAtLogin(true, Executable));
        Assert.False(StartupIntegration.RepairWindowAutostart(new() { OpenWindowAtLogin = true }, Executable));
    }

    [Theory]
    [InlineData("lib/openxlr/ui")]
    [InlineData("lib/openxlr")]
    public void PackagedInstallsUseTheStableWrapper(string relativeBase)
    {
        string prefix = Path.Combine(_home, "prefix");
        string wrapper = Path.Combine(prefix, "bin", "openxlr");
        Directory.CreateDirectory(Path.GetDirectoryName(wrapper)!);
        File.WriteAllText(wrapper, "#!/bin/sh\n");
        Assert.Equal(wrapper, StartupIntegration.ResolveUiBinary(Path.Combine(prefix, relativeBase)));
    }

    [Fact]
    public void SourceBuildUsesItsOwnAppHost()
        => Assert.Equal(Executable, StartupIntegration.ResolveUiBinary(Path.GetDirectoryName(Executable)!));

    [Fact]
    public void SourceBuildDoesNotPickAnUnrelatedAncestorWrapper()
    {
        string source = Path.Combine(_home, "src", "build");
        Directory.CreateDirectory(Path.Combine(_home, "bin"));
        File.WriteAllText(Path.Combine(_home, "bin", "openxlr"), "#!/bin/sh\n");
        Assert.Equal(Path.Combine(source, "OpenXLR.UI"), StartupIntegration.ResolveUiBinary(source));
    }

    [Fact]
    public void EnabledPreferenceReplacesAnEntryWithoutADesktopGroup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "incomplete file");
        Assert.True(StartupIntegration.RepairWindowAutostart(new() { OpenWindowAtLogin = true }, Executable));
        Assert.Contains("[Desktop Entry]", File.ReadAllText(Entry));
    }
}
