using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// The doctor's note that removing the yabridge companion would take Wine
/// with it. Every companion folder and pacman database here is this test's
/// own; nothing reads the machine's.
/// </summary>
public sealed class DoctorKeepWineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("openxlr-keep-wine-").FullName;

    private string Companion => Path.Combine(_root, "yabridge");
    private string Pacman => Path.Combine(_root, "pacman-local");

    public DoctorKeepWineTests()
    {
        Directory.CreateDirectory(Companion);
        File.WriteAllText(Path.Combine(Companion, "openxlr-yabridge.json"), "{\"formatVersion\": 1}\n");
        Directory.CreateDirectory(Pacman);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>An installed package as pacman records it; a null reason is an explicit install, as pacman writes it.</summary>
    private void Package(string name, string version, string? reason, params string[] provides)
    {
        string directory = Directory.CreateDirectory(Path.Combine(Pacman, $"{name}-{version}")).FullName;
        string text = $"%NAME%\n{name}\n\n%VERSION%\n{version}\n\n%DESC%\nA package\n\n"
            + (reason is null ? "" : $"%REASON%\n{reason}\n\n")
            + (provides.Length == 0 ? "" : "%PROVIDES%\n" + string.Join("\n", provides) + "\n\n")
            + "%LICENSE%\nLGPL-2.1-or-later\n\n";
        File.WriteAllText(Path.Combine(directory, "desc"), text);
    }

    private DoctorNote? Note(string? manager) => InstallationDoctor.KeepWineNote(manager, Companion, Pacman);

    [Fact]
    public void OnArchWineInstalledAsADependencyGetsTheCommandThatKeepsIt()
    {
        Package("wine-mono", "10.4.1-1", "1");
        Package("wine", "11.20-1", "1");

        DoctorNote note = Assert.IsType<DoctorNote>(Note("arch"));
        Assert.Equal("wine", note.Path);
        Assert.StartsWith("wine is installed only as a dependency, and the openxlr-yabridge companion requires it, so pacman -Rs openxlr-yabridge removes it too", note.Information);
        Assert.EndsWith("run: sudo pacman -D --asexplicit 'wine'", note.Information);
    }

    [Fact]
    public void OnArchAnExplicitWineOrNoneAtAllGetsNoNote()
    {
        Assert.Null(Note("arch"));
        // wine-mono is not Wine, whatever its install reason.
        Package("wine-mono", "10.4.1-1", "1");
        Assert.Null(Note("arch"));
        Package("wine", "11.20-1", null);
        Assert.Null(Note("arch"));
    }

    [Fact]
    public void OnArchAPackageThatProvidesWineIsTheOneNamed()
    {
        Package("wine-staging", "11.20-1", "1", "wine=11.20", "wine-wow64");

        Assert.EndsWith("run: sudo pacman -D --asexplicit 'wine-staging'", Assert.IsType<DoctorNote>(Note("arch")).Information);
    }

    [Fact]
    public void RpmAlwaysGetsTheNoteAndAptAndNixNever()
    {
        Assert.EndsWith("run: sudo dnf mark user wine", Assert.IsType<DoctorNote>(Note("rpm")).Information);
        Package("wine", "11.20-1", "1");
        Assert.Null(Note("debian"));
        Assert.Null(Note("nix"));
        Assert.Null(Note(null));
    }

    [Fact]
    public void WithoutTheCompanionThereIsNothingToKeepWineFrom()
    {
        Package("wine", "11.20-1", "1");
        File.Delete(Path.Combine(Companion, "openxlr-yabridge.json"));

        Assert.Null(Note("arch"));
        Assert.Null(Note("rpm"));
    }

    [Fact]
    public void AMissingPacmanDatabaseGetsNoNote()
    {
        Directory.Delete(Pacman);

        Assert.Null(Note("arch"));
    }
}
