using System.Text.Json;
using OpenXLR.Core;
using Backup = OpenXLR.UI.SettingsBackup;
using Commands = OpenXLR.UI.SettingsBackupCommands;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class SettingsBackupTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-backup-").FullName;
    private readonly string? _config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private readonly string? _runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

    private string Archive => Path.Combine(_directory, "test.openxlr-backup");
    private string Root => OpenXlrPaths.ConfigDir;

    public SettingsBackupTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_directory, "source"));
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _directory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoresNamedProfilesAndTheirDeviceScopesIntoACleanConfig(bool profilesOnly)
    {
        ProfileStore.Save("0fd9:00a6", "Voice", new Profile { Device = new() { GainDb = 40 } });
        ProfileStore.Save("generic", "Voice", new Profile { Mixer = new() });
        ProfileStore.SetRecallOnConnect("0fd9:00a6", "Voice");
        Write("mixer.json", """{"appRoutes":{"Firefox":"browser"}}""");
        Write("eq-profiles.json", """{"version":1,"profiles":[]}""");
        Write("devices/0fd9-00a6@0123456789abcdef/last-state.json", "{}");
        Write("devices/0fd9-00a6/defaults.json", "{}");
        Write("installation.json", "{}");
        Write("soundboard.json", "{}");
        Write("token", "private");

        Assert.Equal(profilesOnly ? 3 : 6, Backup.Export(Archive, profilesOnly));
        if (OperatingSystem.IsLinux())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Archive));

        FreshConfig();
        var preview = Backup.Preview(Archive);
        Assert.All(preview, change => Assert.False(change.Conflict));
        Assert.Null(Backup.Apply(preview, replace: false));
        Assert.Equal(40, ProfileStore.Load("0fd9:00a6", "Voice")!.Device!.GainDb);
        Assert.NotNull(ProfileStore.Load("generic", "Voice")!.Mixer);
        Assert.Equal("Voice", ProfileStore.RecallOnConnect("0fd9:00a6"));
        Assert.Equal(!profilesOnly, File.Exists(Path.Combine(Root, "mixer.json")));
        Assert.Equal(!profilesOnly, File.Exists(Path.Combine(Root, "devices/0fd9-00a6@0123456789abcdef/last-state.json")));
        Assert.False(File.Exists(Path.Combine(Root, "installation.json")));
        Assert.False(File.Exists(Path.Combine(Root, "devices/0fd9-00a6/defaults.json")));
        Assert.False(File.Exists(Path.Combine(Root, "soundboard.json")));
        Assert.False(File.Exists(Path.Combine(Root, "token")));
        Assert.Empty(Backup.Preview(Archive));
    }

    [Fact]
    public void ConflictsRequireConsentAndKeepAReusableCopyOfPreviousSettings()
    {
        Write("mixer.json", """{"volume":50}""");
        Write("ui.json", """{"language":"de"}""");
        Backup.Export(Archive, profilesOnly: false);
        FreshConfig();
        Write("mixer.json", """{"volume":75}""");
        Write("daemon.json", "{}");

        var changes = Backup.Preview(Archive);
        Assert.Single(changes, change => change.Conflict && change.Name == "mixer.json");
        Assert.Throws<IOException>(() => Backup.Apply(changes, replace: false));
        Assert.False(File.Exists(Path.Combine(Root, "ui.json")));

        string safety = Assert.IsType<string>(Backup.Apply(changes, replace: true));
        Assert.Equal("""{"volume":50}""", File.ReadAllText(Path.Combine(Root, "mixer.json")));
        Assert.True(File.Exists(Path.Combine(Root, "daemon.json")));
        var original = Assert.Single(Backup.Preview(safety));
        Assert.Equal("mixer.json", original.Name);
        Backup.Apply([original], replace: true);
        Assert.Equal("""{"volume":75}""", File.ReadAllText(Path.Combine(Root, "mixer.json")));
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("/tmp/file.json")]
    [InlineData("profiles/generic/../../ui.json")]
    [InlineData("profiles/other/name.json")]
    [InlineData("installation.json")]
    [InlineData("bridge/yabridgectl/config.toml")]
    public void ArchivePathsMustNameExistingTypesOfSettings(string name)
    {
        WriteArchive(new Dictionary<string, byte[]> { [name] = "{}"u8.ToArray(), ["mixer.json"] = "{}"u8.ToArray() });
        Assert.Throws<InvalidDataException>(() => Backup.Preview(Archive));
        Assert.False(Directory.Exists(Root));
    }

    [Theory]
    [InlineData("""{"version":2,"files":{"mixer.json":"e30="}}""")]
    [InlineData("""{"version":1,"files":{"mixer.json":"e30=","mixer.json":"e30="}}""")]
    [InlineData("""{"version":1,"files":{"mixer.json":"eA=="}}""")]
    [InlineData("""{"version":1,"files":{"mixer.json":"W10="}}""")]
    [InlineData("""{"version":1,"files":{"mixer.json":null}}""")]
    public void InvalidContentIsRejectedBeforeAnyWrite(string json)
    {
        File.WriteAllText(Archive, json);
        var error = new StringWriter();
        Assert.Equal(1, Commands.Run(["import", Archive], TextWriter.Null, error));
        Assert.NotEmpty(error.ToString());
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void BackupRefusesSymlinkedProfileDirectoriesAndDoesNotOverwriteExports()
    {
        Write("mixer.json", "{}");
        Backup.Export(Archive, profilesOnly: false);
        byte[] before = File.ReadAllBytes(Archive);
        Write("mixer.json", """{"updated":true}""");
        Assert.Throws<IOException>(() => Backup.Export(Archive, profilesOnly: false));
        Assert.Equal(before, File.ReadAllBytes(Archive));

        string elsewhere = Path.Combine(_directory, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateSymbolicLink(Path.Combine(Root, "profiles"), elsewhere);
        Assert.Throws<IOException>(() => Backup.Export(Path.Combine(_directory, "second.openxlr-backup"), profilesOnly: true));

        File.Delete(Archive);
        WriteArchive(new Dictionary<string, byte[]> { ["profiles/generic/Voice.json"] = "{}"u8.ToArray() });
        Assert.Throws<IOException>(() => Backup.Preview(Archive));
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public void FailedPublicationRollsBackAlreadyAppliedFiles()
    {
        Write("mixer.json", "{}");
        Write("ui.json", "{}");
        Backup.Export(Archive, profilesOnly: false);
        FreshConfig();
        var changes = Backup.Preview(Archive);
        // Another writer put a directory at the final path after planning.
        Directory.CreateDirectory(Path.Combine(Root, changes[^1].Name));
        Assert.Throws<IOException>(() => Backup.Apply(changes, replace: false));
        Assert.False(File.Exists(Path.Combine(Root, changes[0].Name)));
    }

    [Fact]
    public void ImportRequiresBothTheWindowAndDaemonToBeStopped()
    {
        Write("mixer.json", "{}");
        Backup.Export(Archive, profilesOnly: false);
        FreshConfig();

        using (var window = OpenXLR.UI.SingleInstance.TryBecomePrimary())
        {
            Assert.NotNull(window);
            var error = new StringWriter();
            Assert.Equal(1, Commands.Run(["import", Archive], TextWriter.Null, error));
            Assert.Contains("Quit the OpenXLR window", error.ToString());
        }
        using (var daemon = DaemonLock.TryAcquire())
        {
            Assert.NotNull(daemon);
            var error = new StringWriter();
            Assert.Equal(1, Commands.Run(["import", Archive], TextWriter.Null, error));
            Assert.Contains("Stop the OpenXLR daemon", error.ToString());
        }
        Assert.False(File.Exists(Path.Combine(Root, "mixer.json")));
        Assert.Equal(0, Commands.Run(["import", Archive], TextWriter.Null, TextWriter.Null));
        Assert.True(File.Exists(Path.Combine(Root, "mixer.json")));
    }

    private void Write(string name, string value) => OpenXlrPaths.WriteAtomic(Path.Combine(Root, name), value);

    private void FreshConfig() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_directory, "target"));

    private void WriteArchive(Dictionary<string, byte[]> files)
        => File.WriteAllText(Archive, JsonSerializer.Serialize(new { version = 1, files }));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config);
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _runtime);
        Directory.Delete(_directory, recursive: true);
    }
}
