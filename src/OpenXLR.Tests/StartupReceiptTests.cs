using System.Text.Json;
using OpenXLR.UI;
using Receipt = OpenXLR.UI.InstallationReceipt;
using Owned = OpenXLR.UI.InstalledFile;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class StartupReceiptTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "openxlr-receipt-" + Guid.NewGuid());
    private readonly string? _old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private string Entry => Path.Combine(_home, "autostart/openxlr.desktop");
    private string Manifest => Path.Combine(_home, "share/openxlr/installations/openxlr.json");
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    public StartupReceiptTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _old);
        Directory.Delete(_home, true);
    }

    private Receipt Package(params Owned[] files) => new()
    { Package = "openxlr", Manager = "debian", Version = "1.2.3", Commit = Commit, Prefix = _home, Files = [.. files] };

    private string WriteReceipt(Receipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Manifest)!);
        receipt = receipt with { Files = [.. receipt.Files, new() { Path = Manifest, Kind = "receipt" }] };
        File.WriteAllText(Manifest, JsonSerializer.Serialize(receipt, Receipt.Json));
        return Manifest;
    }

    [Fact]
    public void SpecialPermissionChangesAreReportedAndPreventRemoval()
    {
        if (OperatingSystem.IsWindows()) return;
        StartupReceipt.Write(Entry, "installed");
        Owned entry = StartupReceipt.Load().Files.Single(f => f.Path == Entry);
        File.SetUnixFileMode(Entry, (UnixFileMode)entry.Mode! | UnixFileMode.SetUser);
        Assert.Equal("permissions were modified", Receipt.Problem(entry, _home));
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        Assert.Equal("installed", File.ReadAllText(Entry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUpgradeRefusesADamagedOriginalBackupBeforeChangingTheReceiptOrStartupFile(bool missing)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "original");
        StartupReceipt.Write(Entry, "installed");
        Owned backup = StartupReceipt.Load().Files.Single(f => f.Path.Contains("/installation-backups/"));
        if (missing) File.Delete(backup.Path);
        else File.WriteAllText(backup.Path, "damaged");
        byte[] before = File.ReadAllBytes(StartupReceipt.Path);
        Assert.Throws<IOException>(() => StartupReceipt.Write(Entry, "upgrade"));
        Assert.Equal(before, File.ReadAllBytes(StartupReceipt.Path));
        Assert.Equal("installed", File.ReadAllText(Entry));
    }

    [Fact]
    public void ReplacementRecordsTheOriginalAndRemovalRestoresItsBytesAndMode()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "user original");
        int? originalMode = null;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Entry, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            originalMode = (int)File.GetUnixFileMode(Entry);
        }
        string originalHash = Receipt.Digest(Entry);
        StartupReceipt.Write(Entry, "installed");
        Owned entry = StartupReceipt.Load().Files.Single(f => f.Path == Entry);
        Assert.Equal(originalHash, entry.ReplacedSha256);
        Assert.Equal(originalMode, entry.ReplacedMode);
        Assert.Contains(StartupReceipt.Load().Files, f => f.Sha256 == originalHash);
        StartupReceipt.Write(Entry, "upgrade");
        Assert.Equal(originalHash, StartupReceipt.Load().Files.Single(f => f.Path == Entry).ReplacedSha256);
        StartupReceipt.Remove(Entry);
        Assert.Equal("user original", File.ReadAllText(Entry));
        if (!OperatingSystem.IsWindows()) Assert.Equal(originalMode, (int)File.GetUnixFileMode(Entry));
        Assert.True(StartupReceipt.Load().Files.Single(f => f.Path == Entry).Removed);
        // Repeating removal recognizes the restored original without deleting it.
        StartupReceipt.Remove(Entry);
        Assert.Equal("user original", File.ReadAllText(Entry));
    }

    [Fact]
    public void RemovalRefusesUnownedModifiedAndRedirectedPaths()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "foreign");
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        Assert.Equal("foreign", File.ReadAllText(Entry));
        StartupReceipt.Write(Entry, "installed");
        File.WriteAllText(Entry, "modified");
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        File.Delete(Entry);
        File.CreateSymbolicLink(Entry, Path.Combine(_home, "unrelated"));
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        Assert.NotNull(new FileInfo(Entry).LinkTarget);
        Assert.Throws<IOException>(() => StartupReceipt.Write(Path.Combine(_home, "other"), "not allowed"));
    }

    [Fact]
    public void CorruptReceiptOrOriginalBackupPreventsDestructiveChanges()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "original");
        StartupReceipt.Write(Entry, "installed");
        Owned backup = StartupReceipt.Load().Files.Single(f => f.Path.Contains("/installation-backups/"));
        File.WriteAllText(backup.Path, "corrupt");
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        Assert.Equal("installed", File.ReadAllText(Entry));
        File.WriteAllText(StartupReceipt.Path, "{ broken");
        Assert.Throws<IOException>(() => StartupReceipt.Write(Entry, "next version"));
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        Assert.Equal("installed", File.ReadAllText(Entry));
    }

    [Fact]
    public void StartupDirectoriesAndForeignLinksAreNeverDeletedOrTraversed()
    {
        Directory.CreateDirectory(Entry);
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        Directory.Delete(Entry);
        Directory.Delete(Path.GetDirectoryName(Entry)!);
        string elsewhere = Path.Combine(_home, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateSymbolicLink(Path.GetDirectoryName(Entry)!, elsewhere);
        Assert.Throws<IOException>(() => StartupReceipt.Write(Entry, "installed"));
        Assert.Empty(Directory.GetFiles(elsewhere));
    }

    [Fact]
    public void AnEnabledUnitLinkIsRecordedAndRemovedExactly()
    {
        string link = StartupReceipt.StartupPaths.Single(p => p.Contains("default.target.wants"));
        StartupReceipt.EnableLink(link, "/usr/lib/systemd/user/openxlr-daemon.service");
        StartupReceipt.EnableLink(link, "/usr/lib/systemd/user/openxlr-daemon.service");
        Assert.Equal("link", StartupReceipt.Load().Files.Single(f => f.Path == link).Kind);
        StartupReceipt.Remove(link);
        Assert.Null(new FileInfo(link).LinkTarget);
        File.CreateSymbolicLink(link, "/foreign/unit");
        Assert.Throws<IOException>(() => StartupReceipt.EnableLink(link, "/usr/lib/systemd/user/openxlr-daemon.service"));
        Assert.Equal("/foreign/unit", new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void EnablingAdoptsARelativeLinkToTheSameUnitWithoutChangingItsSpelling()
    {
        string link = StartupReceipt.StartupPaths[2];
        string unit = StartupReceipt.StartupPaths[1];
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.WriteAllText(unit, "unit");
        File.CreateSymbolicLink(link, "../openxlr-daemon.service");
        StartupReceipt.EnableLink(link, unit);
        Assert.Equal("../openxlr-daemon.service", StartupReceipt.Load().Files.Single(f => f.Path == link).LinkTarget);
        StartupReceipt.Remove(link);
        Assert.True(File.Exists(unit));
    }

    [Fact]
    public async Task CommandSupportsHeadlessJsonAndRefusesUnownedRemovalBeforeDeletingAnything()
    {
        var output = new StringWriter();
        Assert.Equal(0, await InstallationCommands.Run(["doctor", "--receipt", WriteReceipt(Package()), "--no-runtime", "--json"], output));
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        Assert.Empty(json.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Equal(2, await InstallationCommands.Run(["doctor", "--unknown"], new StringWriter()));
        Assert.Equal(2, await InstallationCommands.Run(["doctor", "--root", "/"], new StringWriter()));
        StartupReceipt.Write(Entry, "owned");
        string unit = StartupReceipt.StartupPaths.Single(p => p.EndsWith("/user/openxlr-daemon.service", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        File.WriteAllText(unit, "foreign");
        Assert.Equal(2, await InstallationCommands.Run(["installation", "remove-startup"], new StringWriter()));
        Assert.Equal("owned", File.ReadAllText(Entry));
        Assert.Equal("foreign", File.ReadAllText(unit));
    }

    [Fact]
    public void DisablingAnAlreadyMissingOwnedFileRecordsItsRemoval()
    {
        StartupReceipt.Write(Entry, "installed");
        File.Delete(Entry);
        StartupReceipt.Remove(Entry);
        Assert.True(StartupReceipt.Load().Files.Single(f => f.Path == Entry).Removed);
        Assert.Null(Receipt.Problem(StartupReceipt.Load().Files.Single(f => f.Path == Entry), _home));
    }

    [Fact]
    public void AConcurrentReceiptWriterIsRefusedBeforeTouchingStartupFiles()
    {
        string directory = Path.Combine(_home, "openxlr");
        Directory.CreateDirectory(directory);
        using (var lease = new FileStream(Path.Combine(directory, "installation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => StartupReceipt.Write(Entry, "installed"));
            Assert.False(File.Exists(Entry));
        }
        StartupReceipt.Write(Entry, "installed");
        Assert.Equal("installed", File.ReadAllText(Entry));
    }

    [Fact]
    public void PackageDiscoveryResolvesProfileReceiptLinksWithoutChangingStartupOwnership()
    {
        string manifest = WriteReceipt(Package());
        string share = Path.Combine(_home, "profile/share");
        string alias = Path.Combine(share, "openxlr/installations/openxlr.json");
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        File.CreateSymbolicLink(alias, manifest);
        string? previous = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", share);
            Assert.Contains(manifest, InstallationHistory.PackageReceipts(Path.Combine(_home, "source-build")));
            Assert.DoesNotContain(alias, InstallationHistory.PackageReceipts(Path.Combine(_home, "source-build")));
        }
        finally { Environment.SetEnvironmentVariable("XDG_DATA_DIRS", previous); }
        Directory.CreateDirectory(Path.GetDirectoryName(StartupReceipt.Path)!);
        File.CreateSymbolicLink(StartupReceipt.Path, manifest);
        Assert.Throws<IOException>(() => StartupReceipt.Write(Entry, "installed"));
    }

    [Fact]
    public void ANewInstallationDoesNotRestoreAnOriginalRemovedAfterThePreviousUninstall()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "original");
        StartupReceipt.Write(Entry, "installed");
        StartupReceipt.Remove(Entry);
        File.Delete(Entry);
        StartupReceipt.Write(Entry, "new install");
        StartupReceipt.Remove(Entry);
        Assert.False(File.Exists(Entry));
    }

    [Fact]
    public void AFailedReceiptWriteCannotReplaceTheOriginal()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "original");
        Directory.CreateDirectory(StartupReceipt.Path);
        Assert.Throws<IOException>(() => StartupReceipt.Write(Entry, "installed"));
        Assert.Equal("original", File.ReadAllText(Entry));
        // A private, content-addressed backup may remain, but the original was never replaced.
    }

    [Fact]
    public void RestoringAnOriginalUnitCannotSilentlyShadowThePackagedDaemon()
    {
        string unit = StartupReceipt.StartupPaths[1];
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        File.WriteAllText(unit, "original custom unit");
        StartupReceipt.Write(unit, "generated source unit");
        StartupReceipt.EnableLink(StartupReceipt.StartupPaths[2], unit);
        string packaged = Path.Combine(_home, "packaged.service");
        File.WriteAllText(packaged, "packaged unit");
        string bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        ExecutableScript.Write(Path.Combine(bin, "systemctl"), "#!/bin/sh\nexit 0\n");
        string? previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + previous);
            Assert.False(StartupIntegration.SetDaemonAtLogin(true, packaged));
            Assert.Equal("original custom unit", File.ReadAllText(unit));
            Assert.Null(new FileInfo(StartupReceipt.StartupPaths[2]).LinkTarget);
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }
    }

    [Fact]
    public void ARefusedUnitRepairNeverStartsTheUnownedUnit()
    {
        string foreign = Path.Combine(_home, "foreign-units/user");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "openxlr-daemon.service"), "[Service]\nExecStart=/missing/openxlr\n");
        Directory.CreateSymbolicLink(Path.Combine(_home, "systemd"), Path.Combine(_home, "foreign-units"));
        string bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        string log = Path.Combine(_home, "systemctl.log");
        ExecutableScript.Write(Path.Combine(bin, "systemctl"), "#!/bin/sh\nprintf '%s\\n' \"$*\" >> " + InstallationDoctor.Quote(log) + "\nexit 0\n");
        string? previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + previous);
            Assert.True(StartupIntegration.HasStaleUserUnit());
            StartupIntegration.RepairDaemonUnit();
            Assert.DoesNotContain(File.Exists(log) ? File.ReadAllLines(log) : Array.Empty<string>(),
                line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("start", StringComparer.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }
    }

    [Fact]
    public async Task AForeignStartupEntryIsNamedAndRestoringItDoesNotPretendStartupIsDisabled()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "foreign startup entry");
        var output = new StringWriter();
        Assert.Equal(1, await InstallationCommands.Run(["doctor", "--no-runtime", "--json"], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Contains(json.RootElement.GetProperty("findings").EnumerateArray(), f => f.GetProperty("path").GetString() == Entry);
        string executable = Path.Combine(_home, "OpenXLR.UI");
        File.WriteAllText(executable, "fixture");
        Assert.True(StartupIntegration.SetWindowAtLogin(true, executable));
        Assert.False(StartupIntegration.SetWindowAtLogin(false, executable));
        Assert.Equal("foreign startup entry", File.ReadAllText(Entry));
    }

    [Fact]
    public void LegacyGeneratedAutostartCanBeDisabledWithoutEnablingItFirst()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        string binary = Path.Combine(_home, "old source/My Projects/OpenXLR.UI");
        File.WriteAllText(Entry, StartupIntegration.AutostartEntry(binary));
        Assert.True(StartupIntegration.SetWindowAtLogin(false, binary));
        Assert.False(File.Exists(Entry));
        Assert.True(StartupReceipt.Load().Files.Single(f => f.Path == Entry).Removed);
    }

    [Fact]
    public async Task LegacySourceUnitAndItsWantsLinkAreRemovedTogether()
    {
        string unit = StartupReceipt.StartupPaths[1];
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        File.WriteAllText(unit, StartupIntegration.SourceUnit("/old missing/OpenXLR.Daemon"));
        Directory.CreateDirectory(Path.GetDirectoryName(StartupReceipt.StartupPaths[2])!);
        File.CreateSymbolicLink(StartupReceipt.StartupPaths[2], "../openxlr-daemon.service");
        Assert.Equal(0, await InstallationCommands.Run(["installation", "remove-startup"], new StringWriter()));
        Assert.False(File.Exists(unit));
        Assert.Null(new FileInfo(StartupReceipt.StartupPaths[2]).LinkTarget);
    }

    [Fact]
    public void LegacyPackagedEnableLinkCanBeDisabledButForeignLinksRemain()
    {
        string packaged = Path.Combine(_home, "package/openxlr-daemon.service");
        Directory.CreateDirectory(Path.GetDirectoryName(packaged)!);
        File.WriteAllText(packaged, "package unit");
        string wants = StartupReceipt.StartupPaths[2];
        Directory.CreateDirectory(Path.GetDirectoryName(wants)!);
        File.CreateSymbolicLink(wants, packaged);
        StartupReceipt.PrepareRemoval(packaged);
        StartupReceipt.Remove(wants, packaged);
        Assert.Null(new FileInfo(wants).LinkTarget);
        Assert.Equal("package unit", File.ReadAllText(packaged));
        File.CreateSymbolicLink(wants, "/foreign.service");
        Assert.Throws<IOException>(() => StartupReceipt.Remove(wants, packaged));
        Assert.Equal("/foreign.service", new FileInfo(wants).LinkTarget);
    }

    [Fact]
    public void AStaleGeneratedUnitAndItsEnableLinkAreReplacedByThePackagedUnit()
    {
        string unit = StartupReceipt.StartupPaths[1];
        string wants = StartupReceipt.StartupPaths[2];
        Directory.CreateDirectory(Path.GetDirectoryName(wants)!);
        File.WriteAllText(unit, StartupIntegration.SourceUnit("/does-not-exist/OpenXLR.Daemon"));
        // systemctl --user enable wrote the link with the unit's absolute path.
        File.CreateSymbolicLink(wants, unit);
        string packaged = Path.Combine(_home, "packaged.service");
        File.WriteAllText(packaged, "package unit");
        string log = Path.Combine(_home, "systemctl.log");
        using (new FakeSystemctl(_home, log))
        {
            Assert.True(StartupIntegration.SetDaemonAtLogin(true, packaged));
            Assert.False(File.Exists(unit));
            Assert.Equal(packaged, new FileInfo(wants).LinkTarget);
            Assert.Contains("--user daemon-reload", File.ReadAllLines(log));
        }
        Receipt receipt = StartupReceipt.Load();
        Assert.True(receipt.Files.Single(f => f.Path == unit).Removed);
        Owned link = receipt.Files.Single(f => f.Path == wants);
        Assert.False(link.Removed);
        Assert.Equal(packaged, link.LinkTarget);
        Assert.Null(Receipt.Problem(link, _home));
    }

    [Fact]
    public void ADanglingEnableLinkWithoutItsUnitCanBeDisabled()
    {
        string unit = StartupReceipt.StartupPaths[1];
        string wants = StartupReceipt.StartupPaths[2];
        Directory.CreateDirectory(Path.GetDirectoryName(wants)!);
        File.CreateSymbolicLink(wants, unit);
        using (new FakeSystemctl(_home, Path.Combine(_home, "systemctl.log")))
            Assert.True(StartupIntegration.SetDaemonAtLogin(false, null));
        Assert.Null(new FileInfo(wants).LinkTarget);
        Assert.True(StartupReceipt.Load().Files.Single(f => f.Path == wants).Removed);
    }

    [Fact]
    public void DisablingWithAForeignUnitBehindTheEnableLinkChangesNothing()
    {
        string unit = StartupReceipt.StartupPaths[1];
        string wants = StartupReceipt.StartupPaths[2];
        Directory.CreateDirectory(Path.GetDirectoryName(wants)!);
        File.WriteAllText(unit, "hand-written unit");
        File.CreateSymbolicLink(wants, unit);
        using (new FakeSystemctl(_home, Path.Combine(_home, "systemctl.log")))
            Assert.False(StartupIntegration.SetDaemonAtLogin(false, null));
        Assert.Equal(unit, new FileInfo(wants).LinkTarget);
        Assert.Equal("hand-written unit", File.ReadAllText(unit));
    }

    [Fact]
    public void AForeignEnableLinkIsRefusedBeforeASourceUnitIsWritten()
    {
        string unit = StartupReceipt.StartupPaths[1];
        string wants = StartupReceipt.StartupPaths[2];
        Directory.CreateDirectory(Path.GetDirectoryName(wants)!);
        File.CreateSymbolicLink(wants, "/foreign/openxlr-daemon.service");
        using (new FakeSystemctl(_home, Path.Combine(_home, "systemctl.log")))
            Assert.False(StartupIntegration.SetDaemonAtLogin(true, null));
        Assert.False(File.Exists(unit));
        Assert.Equal("/foreign/openxlr-daemon.service", new FileInfo(wants).LinkTarget);
        Assert.DoesNotContain(StartupReceipt.Load().Files, f => f.Path == unit);
    }

    [Fact]
    public void AConfigHomeWithATrailingSeparatorStillOwnsStartupFiles()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _home + Path.DirectorySeparatorChar);
        string executable = Path.Combine(_home, "OpenXLR.UI");
        File.WriteAllText(executable, "fixture");
        Assert.True(StartupIntegration.SetWindowAtLogin(true, executable));
        Assert.Equal(_home, StartupReceipt.Load().Prefix);
        Assert.Equal(StartupIntegration.AutostartEntry(executable), File.ReadAllText(Entry));
        Assert.True(StartupIntegration.SetWindowAtLogin(false, executable));
        Assert.False(File.Exists(Entry));
    }

    public static TheoryData<string, string> HistoricalTemplates => new()
    {
        { "entry v0.1.0", """
            [Desktop Entry]
            Type=Application
            Name=OpenXLR
            Comment=OpenXLR mixer window
            Exec=/old/OpenXLR.UI
            Icon=openxlr
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """ },
        { "entry v0.1.23", """
            [Desktop Entry]
            Type=Application
            Name=OpenXLR
            Comment=OpenXLR mixer window
            Exec="/old/OpenXLR.UI"
            Icon=openxlr
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """ },
        { "entry v0.1.33", """
            [Desktop Entry]
            Type=Application
            Name=OpenXLR
            Comment=OpenXLR mixer window
            Exec="/old/OpenXLR.UI"
            Icon=openxlr
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """ },
        { "unit v0.1.0", """
            [Unit]
            Description=OpenXLR audio daemon
            After=pipewire-pulse.service wireplumber.service

            [Service]
            ExecStart=/old/OpenXLR.Daemon
            Environment=OPENXLR_BUILD_MIXER=1
            Restart=on-failure
            RestartSec=3

            [Install]
            WantedBy=default.target
            """ },
        { "unit v0.1.9", """
            [Unit]
            Description=OpenXLR audio daemon
            After=pipewire-pulse.service wireplumber.service

            [Service]
            ExecStart=/old/OpenXLR.Daemon
            Environment=OPENXLR_BUILD_MIXER=1
            TimeoutStopSec=45
            Restart=on-failure
            RestartSec=3

            [Install]
            WantedBy=default.target
            """ },
        { "unit v0.1.13", """
            [Unit]
            Description=OpenXLR audio daemon
            After=pipewire-pulse.service wireplumber.service

            [Service]
            ExecStart=/old/OpenXLR.Daemon
            Environment=OPENXLR_BUILD_MIXER=1
            TimeoutStopSec=45
            Restart=on-failure
            RestartSec=3
            NoNewPrivileges=true
            PrivateTmp=true
            ProtectSystem=strict
            ProtectControlGroups=true
            ProtectKernelTunables=true
            RestrictSUIDSGID=true

            [Install]
            WantedBy=default.target
            """ },
        { "unit v0.1.22", """
            [Unit]
            Description=OpenXLR audio daemon
            After=pipewire-pulse.service wireplumber.service
            StartLimitIntervalSec=300
            StartLimitBurst=3

            [Service]
            Type=notify
            NotifyAccess=main
            WatchdogSec=60
            WatchdogSignal=SIGTERM
            TimeoutStartSec=120
            ExecStart=/old/OpenXLR.Daemon
            Environment=OPENXLR_BUILD_MIXER=1
            TimeoutStopSec=45
            Restart=on-failure
            RestartSec=3
            NoNewPrivileges=true
            PrivateTmp=true
            ProtectSystem=strict
            ProtectControlGroups=true
            ProtectKernelTunables=true
            RestrictSUIDSGID=true

            [Install]
            WantedBy=default.target
            """ },
        { "unit v0.1.23", """
            # Written by the OpenXLR window for a source build (Options, Start at login).
            [Unit]
            Description=OpenXLR audio daemon
            After=pipewire-pulse.service wireplumber.service
            StartLimitIntervalSec=300
            StartLimitBurst=3

            [Service]
            Type=notify
            NotifyAccess=main
            WatchdogSec=60
            WatchdogSignal=SIGTERM
            TimeoutStartSec=120
            ExecStart="/old/OpenXLR.Daemon"
            Environment=OPENXLR_BUILD_MIXER=1
            TimeoutStopSec=45
            Restart=on-failure
            RestartSec=3
            NoNewPrivileges=true
            PrivateTmp=true
            ProtectSystem=strict
            ProtectControlGroups=true
            ProtectKernelTunables=true
            RestrictSUIDSGID=true
            UMask=0077

            [Install]
            WantedBy=default.target
            """ },
        { "unit v0.1.24", """
            # Written by the OpenXLR window for a source build (Options, Start at login).
            [Unit]
            Description=OpenXLR audio daemon
            After=pipewire-pulse.service wireplumber.service
            StartLimitIntervalSec=300
            StartLimitBurst=3

            [Service]
            Type=notify
            NotifyAccess=main
            WatchdogSec=60
            WatchdogSignal=SIGTERM
            TimeoutStartSec=120
            ExecStart="/old/OpenXLR.Daemon"
            Environment=OPENXLR_BUILD_MIXER=1
            TimeoutStopSec=45
            Restart=on-failure
            RestartSec=3
            NoNewPrivileges=true
            PrivateTmp=true
            ProtectSystem=strict
            ProtectControlGroups=true
            ProtectKernelTunables=true
            RestrictSUIDSGID=true
            UMask=0077
            KeyringMode=private
            ProtectClock=true
            ProtectHostname=true
            ProtectKernelLogs=true
            ProtectKernelModules=true
            LockPersonality=true
            RestrictNamespaces=true
            CapabilityBoundingSet=

            [Install]
            WantedBy=default.target
            """ },
        { "unit v0.1.33", """
            # Written by the OpenXLR window for a source build (Options, Start at login).
            [Unit]
            Description=OpenXLR audio daemon
            After=pipewire-pulse.service wireplumber.service
            StartLimitIntervalSec=300
            StartLimitBurst=3

            [Service]
            Type=notify
            NotifyAccess=main
            WatchdogSec=60
            WatchdogSignal=SIGTERM
            TimeoutStartSec=120
            ExecStart="/old/OpenXLR.Daemon"
            Environment=OPENXLR_BUILD_MIXER=1
            TimeoutStopSec=45
            KillMode=mixed
            Restart=on-failure
            RestartSec=3
            NoNewPrivileges=true
            PrivateTmp=true
            ProtectSystem=strict
            ProtectControlGroups=true
            ProtectKernelTunables=true
            RestrictSUIDSGID=true
            UMask=0077
            KeyringMode=private
            ProtectClock=true
            ProtectHostname=true
            ProtectKernelLogs=true
            ProtectKernelModules=true
            LockPersonality=true
            RestrictNamespaces=true
            CapabilityBoundingSet=

            [Install]
            WantedBy=default.target
            """ },
        { "unit v0.1.37", """
            # Written by the OpenXLR window for a source build (Options, Start at login).
            [Unit]
            Description=OpenXLR audio daemon
            After=pipewire-pulse.service wireplumber.service
            StartLimitIntervalSec=300
            StartLimitBurst=3

            [Service]
            Type=notify
            NotifyAccess=main
            WatchdogSec=60
            WatchdogSignal=SIGTERM
            TimeoutStartSec=120
            ExecStart="/old/OpenXLR.Daemon"
            Environment=OPENXLR_BUILD_MIXER=1
            TimeoutStopSec=45
            KillMode=mixed
            LimitMEMLOCK=infinity
            Restart=on-failure
            RestartSec=3
            NoNewPrivileges=true
            PrivateTmp=true
            ProtectSystem=strict
            ProtectControlGroups=true
            ProtectKernelTunables=true
            RestrictSUIDSGID=true
            UMask=0077
            KeyringMode=private
            ProtectClock=true
            ProtectHostname=true
            ProtectKernelLogs=true
            ProtectKernelModules=true
            LockPersonality=true
            RestrictNamespaces=true
            CapabilityBoundingSet=

            [Install]
            WantedBy=default.target
            """ },
    };

    // One sample per template a release wrote, copied from that release's
    // source. Each is recognized from its own launch path and removed.
    [Theory]
    [MemberData(nameof(HistoricalTemplates))]
    public void FilesGeneratedByEveryEarlierReleaseAreRecognizedAndRemoved(string release, string text)
    {
        string path = release.StartsWith("entry", StringComparison.Ordinal) ? Entry : StartupReceipt.StartupPaths[1];
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        Assert.True(StartupIntegration.IsGeneratedStartupFile(path, text));
        Assert.True(StartupReceipt.Recognized(path, null));
        Assert.False(StartupIntegration.IsGeneratedStartupFile(path, text + "Nice=5"));
        StartupReceipt.Remove(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ThePreviousUnitGenerationRemainsRecognisedAfterCapabilityRestrictionsChange()
    {
        const string daemon = "/My Projects/%/OpenXLR.Daemon";
        Assert.NotEqual(StartupIntegration.SourceUnit(daemon), StartupIntegration.SourceUnit(daemon, 7));
        string[] legacy = [.. StartupIntegration.LegacySourceUnits(daemon)];
        Assert.Equal(9, legacy.Distinct().Count());
        Assert.Contains(StartupIntegration.SourceUnit(daemon, 7), legacy);
        Assert.Contains(StartupIntegration.SourceUnit(daemon, 8), legacy);
        Assert.DoesNotContain(StartupIntegration.SourceUnit(daemon), legacy);
        Assert.Equal(2, StartupIntegration.LegacyAutostartEntries(daemon).Distinct().Count());
    }

    [Fact]
    public void UserUnitsNeverRequireDroppingTheManagersCapabilities()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "../.."));
        string[] units = [StartupIntegration.SourceUnit("/test/OpenXLR.Daemon"),
            File.ReadAllText(Path.Combine(root, "packaging/openxlr-daemon.service")),
            File.ReadAllText(Path.Combine(root, "packaging/nix/module.nix"))];
        foreach (string unit in units)
        {
            foreach (string name in new[] { "CapabilityBoundingSet", "ProtectClock", "ProtectKernelModules", "ProtectKernelLogs" })
                Assert.DoesNotContain(name + "=", unit.Replace(" ", ""));
            Assert.Contains("NoNewPrivileges", unit);
            Assert.Contains("KillMode", unit);
            Assert.Contains("LimitMEMLOCK", unit);
        }
    }

    // The unit's own restarts (an explicit Restart daemon, a watchdog stop)
    // share the start limit; three in five minutes left the unit failed.
    [Fact]
    public void UserUnitsAllowTenStartsInFiveMinutesAndTheirThreeStartBodiesStayRecognised()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "../.."));
        const string daemon = "/My Projects/%/OpenXLR.Daemon";
        string reference = File.ReadAllText(Path.Combine(root, "packaging/openxlr-daemon.service"));
        string[] units = [StartupIntegration.SourceUnit(daemon), reference];
        foreach (string unit in units)
        {
            Assert.Contains("\nStartLimitIntervalSec=300\n", unit);
            Assert.Contains("\nStartLimitBurst=10\n", unit);
        }
        string nix = File.ReadAllText(Path.Combine(root, "packaging/nix/module.nix"));
        Assert.Contains("StartLimitIntervalSec = 300;", nix);
        Assert.Contains("StartLimitBurst = 10;", nix);

        // The bodies written and shipped before keep being adopted.
        static string ThreeStarts(string unit) => unit.Replace("StartLimitBurst=10\n", "StartLimitBurst=3\n", StringComparison.Ordinal);
        Assert.Equal(ThreeStarts(StartupIntegration.SourceUnit(daemon)), StartupIntegration.SourceUnit(daemon, 8));
        string path = StartupReceipt.StartupPaths[1];
        Assert.True(StartupIntegration.IsGeneratedStartupFile(path, StartupIntegration.SourceUnit(daemon, 8)));
        Assert.True(StartupIntegration.IsGeneratedStartupFile(path, ThreeStarts(reference)));
        Assert.True(StartupIntegration.IsGeneratedStartupFile(path, ThreeStarts(reference).Replace(
            "ExecStart=%h/openxlr/src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon\n",
            "ExecStart=%h/src/My Clone/src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon\n", StringComparison.Ordinal)));
        Assert.True(StartupIntegration.IsGeneratedStartupFile(path, reference));
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public async Task DoctorReportsRecognizedStartupFilesAsInformationAndForeignOnesAsFindings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, StartupIntegration.AutostartEntry("/old/OpenXLR.UI"));
        string wants = StartupReceipt.StartupPaths[2];
        Directory.CreateDirectory(Path.GetDirectoryName(wants)!);
        File.CreateSymbolicLink(wants, StartupReceipt.StartupPaths[1]);
        var output = new StringWriter();
        // The machine's own yabridge companion, and its Wine, stay out of the notes.
        string? yabridge = Environment.GetEnvironmentVariable("OPENXLR_YABRIDGE");
        Environment.SetEnvironmentVariable("OPENXLR_YABRIDGE", "system");
        try { await InstallationCommands.Run(["doctor", "--no-runtime", "--json"], output); }
        finally { Environment.SetEnvironmentVariable("OPENXLR_YABRIDGE", yabridge); }
        using (var json = JsonDocument.Parse(output.ToString()))
        {
            string[] findings = [.. json.RootElement.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)];
            string[] notes = [.. json.RootElement.GetProperty("notes").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)];
            Assert.DoesNotContain(Entry, findings);
            Assert.DoesNotContain(wants, findings);
            Assert.Equal(new[] { Entry, wants }, notes);
        }
        output = new StringWriter();
        await InstallationCommands.Run(["doctor", "--no-runtime"], output);
        Assert.Contains(Entry + ": Information: recognized startup path", output.ToString());
        File.WriteAllText(Entry, "foreign startup entry");
        output = new StringWriter();
        Assert.Equal(1, await InstallationCommands.Run(["doctor", "--no-runtime", "--json"], output));
        using (var json = JsonDocument.Parse(output.ToString()))
        {
            Assert.Contains(json.RootElement.GetProperty("findings").EnumerateArray(), f => f.GetProperty("path").GetString() == Entry);
            Assert.DoesNotContain(json.RootElement.GetProperty("notes").EnumerateArray(), f => f.GetProperty("path").GetString() == Entry);
        }
    }

    /// <summary>A systemctl on PATH that records its arguments and succeeds.</summary>
    private sealed class FakeSystemctl : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("PATH");

        public FakeSystemctl(string home, string log)
        {
            string bin = Path.Combine(home, "bin");
            Directory.CreateDirectory(bin);
            ExecutableScript.Write(Path.Combine(bin, "systemctl"), "#!/bin/sh\nprintf '%s\\n' \"$*\" >> " + InstallationDoctor.Quote(log) + "\nexit 0\n");
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + _previous);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("PATH", _previous);
    }

    [Fact]
    public void MigrationMatchesTheManualUnitExactlyAndRejectsAdditionalDirectives()
    {
        using var stream = typeof(StartupIntegration).Assembly.GetManifestResourceStream("OpenXLR.StartupUnit")!;
        using var reader = new StreamReader(stream);
        string reference = reader.ReadToEnd();
        string unit = StartupReceipt.StartupPaths[1];
        Assert.True(StartupIntegration.IsGeneratedStartupFile(unit, reference));
        Assert.False(StartupIntegration.IsGeneratedStartupFile(unit, reference + "Environment=USER_CUSTOM=1\n"));
        Assert.False(StartupIntegration.IsGeneratedStartupFile(Entry, StartupIntegration.AutostartEntry("/old/OpenXLR.UI") + "Exec=/foreign\n"));
    }

    // packaging/openxlr-daemon.service as v0.1.37 to v0.1.70 shipped it. The
    // source install guide had users copy it and edit only ExecStart.
    private const string ReferenceUnitBeforeCapabilityChange = """
        [Unit]
        Description=OpenXLR audio daemon
        After=pipewire-pulse.service wireplumber.service
        StartLimitIntervalSec=300
        StartLimitBurst=3

        [Service]
        Type=notify
        NotifyAccess=main
        WatchdogSec=60
        WatchdogSignal=SIGTERM
        TimeoutStartSec=120
        # %h is your home directory; adjust the path to where you cloned the repo.
        ExecStart=%h/openxlr/src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon
        Environment=OPENXLR_BUILD_MIXER=1
        TimeoutStopSec=45
        # Bridged Windows plugins lock their shared audio buffers into memory, and
        # they inherit this allowance from the daemon. The 8 MiB most sessions
        # default to is below what yabridge asks for, which costs a warning on every
        # plugin load and dropouts while one runs.
        LimitMEMLOCK=infinity
        # SIGTERM goes to the daemon alone, so it can end the Wine session a bridged
        # plugin leaves behind before the rest of the group is killed. Sending it to
        # the whole group would take wineserver down first and leave winedevice.exe
        # to the timeout above.
        KillMode=mixed
        Restart=on-failure
        RestartSec=3
        NoNewPrivileges=true
        PrivateTmp=true
        ProtectSystem=strict
        ProtectControlGroups=true
        ProtectKernelTunables=true
        RestrictSUIDSGID=true
        UMask=0077
        # Verified on hardware with the device, the submix graph and both clients:
        # none of these touches USB, PipeWire or the .NET runtime.
        KeyringMode=private
        ProtectClock=true
        ProtectHostname=true
        ProtectKernelLogs=true
        ProtectKernelModules=true
        LockPersonality=true
        RestrictNamespaces=true
        CapabilityBoundingSet=

        [Install]
        WantedBy=default.target
        """ + "\n";

    private static string ReferenceUnitCopy(string execStart) => ReferenceUnitBeforeCapabilityChange.Replace(
        "ExecStart=%h/openxlr/src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon\n", "ExecStart=" + execStart + "\n", StringComparison.Ordinal);

    [Fact]
    public void ACopyOfTheEarlierReferenceUnitIsRecognisedOnlyWhenJustItsExecStartChanged()
    {
        using var stream = typeof(StartupIntegration).Assembly.GetManifestResourceStream("OpenXLR.StartupUnit")!;
        Assert.NotEqual(ReferenceUnitBeforeCapabilityChange, new StreamReader(stream).ReadToEnd());
        string unit = StartupReceipt.StartupPaths[1];
        Assert.True(StartupIntegration.IsGeneratedStartupFile(unit, ReferenceUnitBeforeCapabilityChange));
        string copy = ReferenceUnitCopy("%h/src/My Clone/src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon");
        Assert.NotEqual(ReferenceUnitBeforeCapabilityChange, copy);
        Assert.True(StartupIntegration.IsGeneratedStartupFile(unit, copy));
        Assert.False(StartupIntegration.IsGeneratedStartupFile(unit, copy + "Environment=USER_CUSTOM=1\n"));
        Assert.False(StartupIntegration.IsGeneratedStartupFile(unit, copy.Replace("CapabilityBoundingSet=\n", "", StringComparison.Ordinal)));
        Assert.False(StartupIntegration.IsGeneratedStartupFile(unit, ReferenceUnitCopy("")));
        Assert.False(StartupIntegration.IsGeneratedStartupFile(Entry, copy));
    }

    // The unit fails with 218/CAPABILITIES where the user manager cannot drop
    // capabilities. Turning Start audio at login off takes the copy and its
    // link down, so turning it on again writes the current unit; a packaged
    // install replaces it with the package's unit.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHandCopiedEarlierReferenceUnitIsReplacedFromOptions(bool packagedInstall)
    {
        string unit = StartupReceipt.StartupPaths[1];
        string wants = StartupReceipt.StartupPaths[2];
        Directory.CreateDirectory(Path.GetDirectoryName(wants)!);
        File.WriteAllText(unit, ReferenceUnitCopy("%h/src/openxlr/src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon"));
        File.CreateSymbolicLink(wants, unit);
        string packaged = Path.Combine(_home, "packaged.service");
        File.WriteAllText(packaged, "package unit");
        string log = Path.Combine(_home, "systemctl.log");
        using (new FakeSystemctl(_home, log))
        {
            if (packagedInstall)
            {
                Assert.True(StartupIntegration.SetDaemonAtLogin(true, packaged));
                Assert.Equal(packaged, new FileInfo(wants).LinkTarget);
            }
            else
            {
                Assert.True(StartupIntegration.SetDaemonAtLogin(false, null));
                Assert.Null(new FileInfo(wants).LinkTarget);
            }
            Assert.False(File.Exists(unit));
            Assert.Contains("--user daemon-reload", File.ReadAllLines(log));
        }
        Assert.True(StartupReceipt.Load().Files.Single(f => f.Path == unit).Removed);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"formatVersion\":99}")]
    public async Task InvalidStartupReceiptsHaveUserRecoveryRatherThanPackageRepair(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StartupReceipt.Path)!);
        File.WriteAllText(StartupReceipt.Path, contents);
        Assert.False(StartupIntegration.SetWindowAtLogin(false, "/missing"));
        DoctorReport report = await InstallationDoctor.Inspect([StartupReceipt.Path], null, false);
        Assert.Contains(report.Findings, f => f.Repair == "openxlr installation reset-startup-receipt");
        var output = new StringWriter();
        Assert.Equal(0, await InstallationCommands.Run(["installation", "reset-startup-receipt"], output));
        string saved = Directory.GetFiles(Path.GetDirectoryName(StartupReceipt.Path)!, "installation.json.before-recovery-*").Single();
        Assert.Equal(contents, File.ReadAllText(saved));
        Assert.False(File.Exists(StartupReceipt.Path));
        Assert.True(StartupIntegration.SetWindowAtLogin(false, "/missing"));
    }

    [Fact]
    public async Task RepairWithNoEnabledPreferencesDoesNotClaimToReapplyAnything()
    {
        var output = new StringWriter();
        Assert.Equal(0, await InstallationCommands.Run(["installation", "repair-startup"], output));
        Assert.Contains("No startup preferences are enabled", output.ToString());
        Assert.False(File.Exists(StartupReceipt.Path));
    }
    [Fact]
    public void MigrationDoesNotNormalizeInvalidUtf8IntoAGeneratedFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(StartupIntegration.AutostartEntry("/old/\ufffd/OpenXLR.UI"));
        int index = Array.IndexOf(bytes, (byte)0xef);
        bytes[index] = 0xff;
        File.WriteAllBytes(Entry, bytes);
        Assert.False(StartupIntegration.SetWindowAtLogin(false, "/unused"));
        Assert.Equal(bytes, File.ReadAllBytes(Entry));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AnExistingDirectoryIsRejectedWithoutClaimingIt(int index)
    {
        string path = StartupReceipt.StartupPaths[index];
        Directory.CreateDirectory(path);
        if (index == 2) Assert.Throws<IOException>(() => StartupReceipt.EnableLink(path, StartupReceipt.StartupPaths[1]));
        else Assert.Throws<IOException>(() => StartupReceipt.Write(path, "generated"));
        Assert.True(Directory.Exists(path));
        Assert.False(File.Exists(StartupReceipt.Path));
    }

}
