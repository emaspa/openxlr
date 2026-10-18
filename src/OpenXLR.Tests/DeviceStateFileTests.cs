using System.Text;
using OpenXLR.Core;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class DeviceStateFileTests : IDisposable
{
    private const string Device = "0fd9:007d";
    private const int MaximumBytes = 64 * 1024;
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-device-file-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private string Folder => Path.Combine(_directory, "openxlr", "devices", "0fd9-007d");

    public DeviceStateFileTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
        Directory.CreateDirectory(Folder);
    }

    private static object? Load(bool defaults)
        => defaults ? DeviceStateStore.LoadDefaults(Device) : DeviceStateStore.LoadLast(Device);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedHardwareSnapshotsAreRefusedWithoutChangingThem(bool defaults)
    {
        string path = Path.Combine(Folder, defaults ? "defaults.json" : "last-state.json");
        string text = "{\"gainDb\":42}" + new string(' ', MaximumBytes);
        File.WriteAllText(path, text);
        Assert.Throws<IOException>(() => Load(defaults));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheReadLimitIsInclusiveAndLegacyEncodingsStillLoad(bool defaults)
    {
        string path = Path.Combine(Folder, defaults ? "defaults.json" : "last-state.json");
        Assert.Null(Load(defaults));
        string text = "{\"gainDb\":42}";
        File.WriteAllText(path, text.PadRight(MaximumBytes));
        Assert.NotNull(Load(defaults));
        foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode })
        {
            File.WriteAllText(path, text, encoding);
            Assert.Equal(42, (defaults ? DeviceStateStore.LoadDefaults(Device) : DeviceStateStore.LoadLast(Device))!.GainDb);
        }
        File.WriteAllText(path, "{broken");
        Assert.Null(Load(defaults));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PipesCannotBlockHardwareRestoration(bool defaults)
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = Path.Combine(Folder, defaults ? "defaults.json" : "last-state.json");
        var created = ProcessRunner.Run("mkfifo", [path]);
        Assert.True(created.Ok, created.Stderr);
        using var keeper = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var read = Task.Run(() => Record.Exception(() => Load(defaults)));
        try { Assert.IsType<IOException>(await read.WaitAsync(TimeSpan.FromSeconds(3))); }
        finally { keeper.Dispose(); await read.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.True(File.Exists(path));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, recursive: true);
    }
}
