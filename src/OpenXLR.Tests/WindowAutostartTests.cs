using Avalonia.Controls;
using Avalonia.Data;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class WindowAutostartTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "openxlr-autostart-" + Guid.NewGuid());
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private string AutostartDir => Path.Combine(_home, "autostart");
    private string Entry => Path.Combine(AutostartDir, "openxlr.desktop");
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

    /// <summary>Settings that ask for the window at login, with no entry written yet.</summary>
    private static UiSettings Enabled(string? writtenFor = null)
        => new() { OpenWindowAtLogin = true, AutostartExecutable = writtenFor };

    /// <summary>The lines of the [Desktop Entry] group, in file order.</summary>
    private string[] DesktopGroup()
    {
        string[] lines = File.ReadAllLines(Entry);
        int start = Array.FindIndex(lines, l => l.Trim() == "[Desktop Entry]");
        Assert.True(start >= 0, "The file has no [Desktop Entry] group.");
        int end = Array.FindIndex(lines, start + 1, l => l.TrimStart().StartsWith('['));
        return lines[(start + 1)..(end < 0 ? lines.Length : end)];
    }

    private string[] ExecValues()
        => [.. DesktopGroup().Where(l => StartupIntegration.DesktopKey(l) == "Exec")
            .Select(StartupIntegration.DesktopValue)];

    [Fact]
    public void EnablingCreatesAQuotedDesktopEntryAndDisablingRemovesIt()
    {
        Assert.True(StartupIntegration.SetWindowAtLogin(true, Executable));
        string entry = File.ReadAllText(Entry);
        Assert.Contains("[Desktop Entry]", entry);
        Assert.Contains("Type=Application", entry);
        Assert.Equal([StartupIntegration.DesktopExec(Executable)], ExecValues());
        Assert.DoesNotContain("OnlyShowIn", entry);
        Assert.DoesNotContain("Hidden=true", entry);
        Assert.True(StartupIntegration.SetWindowAtLogin(false, Executable));
        Assert.False(File.Exists(Entry));
        Assert.True(StartupIntegration.SetWindowAtLogin(false, Executable));
    }

    /// <summary>
    /// Turning the option off with no ~/.config/autostart at all: File.Delete
    /// reports a missing directory, and that is the state the caller asked for.
    /// </summary>
    [Fact]
    public void DisablingSucceedsWithNoAutostartDirectory()
    {
        Assert.False(Directory.Exists(AutostartDir));
        Assert.True(StartupIntegration.SetWindowAtLogin(false, Executable));
        Assert.False(Directory.Exists(AutostartDir));
    }

    /// <summary>
    /// ~/.config/autostart belongs to the desktop. Writing the entry must not
    /// make the directory private, and the entry itself gets the mode the
    /// umask gives any other file written there, not 0600.
    /// </summary>
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]   // file modes are a Unix matter
    public void WritingTheEntryLeavesTheSharedDirectoryAndFileModesAlone()
    {
        Directory.CreateDirectory(AutostartDir);
        UnixFileMode directoryBefore = File.GetUnixFileMode(AutostartDir);
        string reference = Path.Combine(AutostartDir, "reference.desktop");
        File.WriteAllText(reference, "reference\n");
        UnixFileMode umaskMode = File.GetUnixFileMode(reference);

        Assert.True(StartupIntegration.SetWindowAtLogin(true, Executable));

        Assert.Equal(directoryBefore, File.GetUnixFileMode(AutostartDir));
        Assert.Equal(umaskMode, File.GetUnixFileMode(Entry));
        Assert.Empty(Directory.GetFiles(AutostartDir, "*.openxlr-tmp"));
    }

    /// <summary>
    /// A symlinked entry points at a file somebody else manages. Replacing it
    /// with a regular file, or writing through it, is not OpenXLR's call.
    /// </summary>
    [Fact]
    public void ASymlinkedEntryIsRefusedRatherThanReplaced()
    {
        Directory.CreateDirectory(AutostartDir);
        string managed = Path.Combine(_home, "managed.desktop");
        File.WriteAllText(managed, "[Desktop Entry]\nType=Application\nExec=managed\n");
        File.CreateSymbolicLink(Entry, managed);

        Assert.False(StartupIntegration.SetWindowAtLogin(true, Executable));
        Assert.Equal(managed, new FileInfo(Entry).LinkTarget);
        Assert.Equal("[Desktop Entry]\nType=Application\nExec=managed\n", File.ReadAllText(managed));
    }

    [Fact]
    public void EnabledPreferenceRepairsAMissingEntryAndIsIdempotent()
    {
        var settings = Enabled(writtenFor: Path.Combine(_home, "Older Build", "OpenXLR.UI"));
        Assert.Equal(StartupIntegration.AutostartRepair.Repaired,
            StartupIntegration.RepairWindowAutostart(settings, Executable));
        string entry = File.ReadAllText(Entry);
        var marker = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Entry, marker);
        Assert.Equal(StartupIntegration.AutostartRepair.NotNeeded,
            StartupIntegration.RepairWindowAutostart(settings, Executable));
        Assert.Equal(entry, File.ReadAllText(Entry));
        Assert.Equal(marker, File.GetLastWriteTimeUtc(Entry));
    }

    /// <summary>
    /// The entry is gone and the recorded launch path is still this one, so
    /// somebody removed it on purpose with a desktop tool. Putting it back
    /// every launch would make that removal impossible.
    /// </summary>
    [Fact]
    public void AnEntryRemovedOutsideOpenXlrIsNotRecreated()
    {
        Assert.Equal(StartupIntegration.AutostartRepair.RemovedOutside,
            StartupIntegration.RepairWindowAutostart(Enabled(writtenFor: Executable), Executable));
        Assert.False(File.Exists(Entry));
        Assert.Contains("removed outside OpenXLR",
            OptionsViewModel.RepairNote(StartupIntegration.AutostartRepair.RemovedOutside));
    }

    [Fact]
    public void DaemonAndMinimizedPreferencesDoNotEnableWindowAutostart()
    {
        var settings = new UiSettings { StartDaemonAtLogin = true, StartMinimized = true };
        settings.Save();
        Assert.Equal(StartupIntegration.AutostartRepair.NotNeeded,
            StartupIntegration.RepairWindowAutostart(settings, Executable));
        Assert.False(File.Exists(Entry));
        var client = new DaemonClient();
        var options = new OptionsViewModel(client, new MainViewModel(client));
        Assert.True(options.StartDaemonAtLogin);
        Assert.True(options.StartMinimized);
        Assert.False(options.OpenWindowAtLogin);
        Assert.Contains("does not enable autostart", options.StartupHint);
    }

    /// <summary>
    /// An Exec whose program is gone is replaced in place, inside the entry's
    /// own group, leaving every other key and every other group alone.
    /// </summary>
    [Theory]
    [InlineData("Exec=\"/old/build/OpenXLR.UI\"\n")]
    [InlineData("Exec = \"/old/build/OpenXLR.UI\"\n")]   // the spec allows space around '='
    [InlineData("")]
    public void RepairsLaunchPathWithoutRemovingDesktopPreferences(string exec)
    {
        Directory.CreateDirectory(AutostartDir);
        File.WriteAllText(Entry, "[Desktop Entry]\nType=Application\n" + exec +
            "Hidden=true\nX-KDE-autostart-after=panel\n[Desktop Action Manual]\nExec=manual-command\n");
        Assert.Equal(StartupIntegration.AutostartRepair.Repaired,
            StartupIntegration.RepairWindowAutostart(Enabled(), Executable));

        // Exactly one Exec key in [Desktop Entry], naming this build:
        // desktop-file-validate rejects a duplicated key.
        Assert.Equal([StartupIntegration.DesktopExec(Executable)], ExecValues());
        string entry = File.ReadAllText(Entry);
        Assert.DoesNotContain("/old/build/OpenXLR.UI", entry);
        Assert.Contains("Hidden=true", entry);
        Assert.Contains("X-KDE-autostart-after=panel", entry);
        Assert.Contains("[Desktop Action Manual]\nExec=manual-command", entry);
    }

    /// <summary>A duplicated Exec key, however it got there, leaves one behind.</summary>
    [Fact]
    public void RepairLeavesOneExecKeyWhenTheEntryAlreadyHadTwo()
    {
        Directory.CreateDirectory(AutostartDir);
        File.WriteAllText(Entry, "[Desktop Entry]\nType=Application\nExec=/gone/one\nExec = /gone/two\nName=OpenXLR\n");
        Assert.Equal(StartupIntegration.AutostartRepair.Repaired,
            StartupIntegration.RepairWindowAutostart(Enabled(), Executable));
        Assert.Equal([StartupIntegration.DesktopExec(Executable)], ExecValues());
        Assert.Contains("Name=OpenXLR", File.ReadAllText(Entry));
    }

    /// <summary>
    /// A command that still runs is the user's, whatever it is: a wrapper
    /// script, a bare command on PATH, an Exec they edited by hand.
    /// </summary>
    [Theory]
    [InlineData("Exec={0}\n")]
    [InlineData("Exec = {0} --minimized\n")]
    [InlineData("Exec=\"{0}\"\n")]
    public void AnExecWhoseProgramStillExistsIsNeverRewritten(string template)
    {
        string theirs = Path.Combine(_home, "their-launcher");
        File.WriteAllText(theirs, "#!/bin/sh\n");
        Directory.CreateDirectory(AutostartDir);
        string text = "[Desktop Entry]\nType=Application\n" + string.Format(template, theirs) + "Name=OpenXLR\n";
        File.WriteAllText(Entry, text);
        Assert.Equal(StartupIntegration.AutostartRepair.NotNeeded,
            StartupIntegration.RepairWindowAutostart(Enabled(), Executable));
        Assert.Equal(text, File.ReadAllText(Entry));
    }

    /// <summary>The same for a bare command the desktop resolves on PATH.</summary>
    [Fact]
    public void AnExecNamingACommandOnPathIsNeverRewritten()
    {
        string bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "openxlr"), "#!/bin/sh\n");
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", bin);
        try
        {
            Directory.CreateDirectory(AutostartDir);
            string text = "[Desktop Entry]\nType=Application\nExec=openxlr %U\n";
            File.WriteAllText(Entry, text);
            Assert.Equal(StartupIntegration.AutostartRepair.NotNeeded,
                StartupIntegration.RepairWindowAutostart(Enabled(), Executable));
            Assert.Equal(text, File.ReadAllText(Entry));
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
    }

    /// <summary>
    /// What this window wrote reads back as the path it was given, through
    /// both escaping layers: spaces, quotes, backslashes, '$' and '%'.
    /// </summary>
    [Theory]
    [InlineData("/home/u/My Build/OpenXLR.UI")]
    [InlineData("/home/u/100% mine/OpenXLR.UI")]
    [InlineData("/home/u/$HOME `x`/OpenXLR.UI")]
    [InlineData("/home/u/back\\slash/OpenXLR.UI")]
    [InlineData("/home/u/quo\"te/OpenXLR.UI")]
    public void AnExecThisWindowWroteReadsBackAsThePath(string path)
        => Assert.Equal(path, StartupIntegration.DesktopExecBinary(StartupIntegration.DesktopExec(path)));

    [Fact]
    public void MissingBinaryDoesNotOverwriteAnExistingEntry()
    {
        Assert.True(StartupIntegration.SetWindowAtLogin(true, Executable));
        string before = File.ReadAllText(Entry);
        Assert.Equal(StartupIntegration.AutostartRepair.Failed,
            StartupIntegration.RepairWindowAutostart(Enabled(), Executable + ".missing"));
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

    /// <summary>
    /// The check box itself has to go back, not just the view model: it has
    /// already drawn the state the user asked for. This binds a real
    /// CheckBox, which needs no windowing platform, and drives it the way the
    /// control does when it is clicked.
    /// </summary>
    [Fact]
    public void ARejectedToggleSnapsTheCheckBoxBack()
    {
        new UiSettings { OpenWindowAtLogin = true }.Save();
        Directory.CreateDirectory(Entry); // Disabling cannot remove a directory.
        var client = new DaemonClient();
        var options = new OptionsViewModel(client, new MainViewModel(client));
        var box = new CheckBox { DataContext = options };
        box.Bind(CheckBox.IsCheckedProperty,
            new Binding { Path = nameof(OptionsViewModel.OpenWindowAtLogin), Mode = BindingMode.TwoWay });
        Assert.True(box.IsChecked);

        box.IsChecked = false;   // what a click does before the binding writes back

        Assert.True(options.OpenWindowAtLogin);
        Assert.True(box.IsChecked);
        Assert.NotNull(options.StartupError);
    }

    [Fact]
    public void WriteFailureReturnsFalseInsteadOfCrashing()
    {
        File.WriteAllText(AutostartDir, "not a directory");
        Assert.False(StartupIntegration.SetWindowAtLogin(true, Executable));
        Assert.Equal(StartupIntegration.AutostartRepair.Failed,
            StartupIntegration.RepairWindowAutostart(Enabled(), Executable));
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
        Directory.CreateDirectory(AutostartDir);
        File.WriteAllText(Entry, "incomplete file");
        Assert.Equal(StartupIntegration.AutostartRepair.Repaired,
            StartupIntegration.RepairWindowAutostart(Enabled(), Executable));
        Assert.Contains("[Desktop Entry]", File.ReadAllText(Entry));
    }
}
