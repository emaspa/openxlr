using OpenXLR.Core.Mixing;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// The daemon's half of application icons, against a fake XDG tree: which
/// desktop entry an application's PipeWire properties lead to, in which
/// order the properties are tried, and which directories are searched.
/// </summary>
[Collection("xdg-config")]
public sealed class ApplicationIconTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("openxlr-app-icons-").FullName;
    private string Home => Path.Combine(_root, "home/applications");
    private string System => Path.Combine(_root, "usr/applications");
    private string Flatpak => Path.Combine(_root, "flatpak/applications");

    public ApplicationIconTests()
    {
        Entry(Home, "steam.desktop", "Steam", "steam", "env MANGOHUD=1 /usr/bin/steam %U");
        Entry(System, "steam.desktop", "Steam", "steam-system", "/usr/bin/steam %U");
        // Steam's shortcut for a game runs Steam too, and sorts before it.
        Entry(Home, "Balatro.desktop", "Balatro", "steam_icon_2379780", "steam steam://rungameid/2379780");
        Entry(Home, "Cyberpunk 2077.desktop", "Cyberpunk 2077", "steam_icon_1091500", "steam steam://rungameid/1091500");
        Entry(System, "discord.desktop", "Discord", "discord", "/usr/bin/discord --url -- %u", "discord");
        Entry(System, "signal.desktop", "Signal", "signal-desktop", "signal-desktop -- %u", "signal");
        Entry(System, "com.google.Chrome.desktop", "Google Chrome", "google-chrome", "/usr/bin/google-chrome-stable %U",
            "google-chrome", extra: "NoDisplay=true");
        Entry(System, "remote-viewer.desktop", "Remote Viewer", "virt-viewer", "remote-viewer %u");
        Entry(Flatpak, "io.github.tobagin.karere.desktop", "Karere", "io.github.tobagin.karere",
            "/usr/bin/flatpak run --branch=stable --arch=x86_64 --command=karere --file-forwarding io.github.tobagin.karere @@u %U @@",
            "karere");
        // An entry hidden in the user's directory hides the system one.
        Entry(Home, "hidden.desktop", "Hidden", "hidden-user", "hidden", extra: "Hidden=true");
        Entry(System, "hidden.desktop", "Hidden", "hidden-system", "hidden");
        // A subdirectory's entries take the directory into their id.
        Entry(Path.Combine(System, "kde"), "tool.desktop", "Tool", "kde-tool", "tool");
        // Not an application, and an application without an icon.
        Entry(System, "link.desktop", "Link", "link", "link", type: "Link");
        File.WriteAllText(Path.Combine(System, "noicon.desktop"), "[Desktop Entry]\nType=Application\nName=NoIcon\nExec=noicon\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Entry(string dir, string file, string name, string icon, string exec,
        string? wmClass = null, string type = "Application", string? extra = null)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), $"""
            [Desktop Entry]
            Type={type}
            Name={name}
            Name[de]=Lokal {name}
            Exec={exec}
            Icon={icon}
            {(wmClass is null ? "" : "StartupWMClass=" + wmClass)}
            {extra}

            [Desktop Action Other]
            Name=Other
            Exec=other
            Icon=wrong
            """);
    }

    private DesktopEntryIndex Index() => DesktopEntryIndex.Scan([Home, Flatpak, System]);

    private IconMatch? Resolve(string? iconName = null, string? appId = null, string? binary = null, string? appName = null)
        => ApplicationIconResolver.Resolve(Index(), new ApplicationIconHints(iconName, appId, binary, appName));

    [Fact]
    public void ANameIsSpelledAsItsDesktopEntrySpellsIt()
    {
        DesktopEntryIndex index = Index();
        Assert.Equal("Google Chrome", ApplicationIconResolver.DisplayName(index, "google chrome"));
        Assert.Equal("Google Chrome", ApplicationIconResolver.DisplayName(index, "Google Chrome"));
        Assert.Equal("chrome", ApplicationIconResolver.DisplayName(index, "chrome"));
        Assert.Equal("nothing installed", ApplicationIconResolver.DisplayName(index, "nothing installed"));
    }

    [Fact]
    public void EntriesAreReadInSearchOrderWithTheirMainGroupOnly()
    {
        DesktopEntryIndex index = Index();
        Assert.Equal("steam", index.ById("steam")!.Icon);
        Assert.Equal("steam", index.ById("steam")!.Program);
        Assert.Null(index.ById("hidden"));
        Assert.Equal("kde-tool", index.ById("kde-tool")!.Icon);
        Assert.Null(index.ById("link"));
        Assert.Null(index.ById("noicon"));
        Assert.Equal("karere", index.ById("io.github.tobagin.karere")!.Program);
        Assert.Equal("Google Chrome", index.ById("com.google.Chrome")!.Name);
        Assert.DoesNotContain(index.Entries, e => e.Icon == "wrong");
    }

    [Theory]
    [InlineData("/usr/bin/discord --url -- %u", "discord")]
    [InlineData("env MANGOHUD=1 /usr/bin/steam %U", "steam")]
    [InlineData("\"/opt/My App/app\" %f", "app")]
    [InlineData("\"/opt/odd \\\"name\\\"/run\"", "run")]
    [InlineData("/usr/bin/flatpak run --branch=stable --command=karere io.github.tobagin.karere", "karere")]
    [InlineData("flatpak run com.example.App", null)]
    [InlineData("%U", null)]
    public void TheProgramIsWhatTheExecLineRuns(string exec, string? program)
        => Assert.Equal(program, DesktopEntryIndex.ProgramOf(exec));

    [Fact]
    public void APublishedIconNameComesFirst()
    {
        Assert.Equal(new IconMatch("custom", IconSource.IconName), Resolve("custom", "io.github.tobagin.karere", "discord", "Discord"));
        // A path is not an icon name, and a client is not trusted to point at files.
        Assert.Equal(IconSource.Binary, Resolve("/etc/passwd", binary: "discord")!.Source);
    }

    [Fact]
    public void TheApplicationIdFindsItsEntryAheadOfTheBinary()
    {
        Assert.Equal(new IconMatch("io.github.tobagin.karere", IconSource.AppId),
            Resolve(appId: "io.github.tobagin.karere", binary: "discord"));
        // An id without an entry, like Steam's org.valvesoftware.steam, falls through.
        Assert.Equal(new IconMatch("steam", IconSource.Binary), Resolve(appId: "org.valvesoftware.steam", binary: "steam"));
    }

    [Theory]
    [InlineData("Discord", "discord")]               // the entry's id, in another case
    [InlineData("steam", "steam")]                   // the id before a game shortcut's program
    [InlineData("karere", "io.github.tobagin.karere")] // the window class
    [InlineData("chrome", "google-chrome")]          // the last part of a reverse-DNS id
    [InlineData("signal-desktop", "signal-desktop")] // the program the entry runs
    [InlineData("remote-viewer", "virt-viewer")]
    public void TheBinaryFindsItsEntry(string binary, string icon)
        => Assert.Equal(new IconMatch(icon, IconSource.Binary), Resolve(binary: binary));

    [Fact]
    public void TheNameFindsAnEntryTheBinaryDoesNot()
    {
        Assert.Equal(new IconMatch("google-chrome", IconSource.AppName), Resolve(appName: "Google Chrome"));
        // A remembered Windows game is known by its identity, or by its label
        // spelt the way its shortcut spells it.
        Assert.Equal(new IconMatch("steam_icon_2379780", IconSource.Binary),
            ApplicationIconResolver.Resolve(Index(), ApplicationIconHints.Remembered("balatro", "Balatro")));
        Assert.Equal(new IconMatch("steam_icon_1091500", IconSource.AppName),
            ApplicationIconResolver.Resolve(Index(), ApplicationIconHints.Remembered("cyberpunk2077", "Cyberpunk2077")));
    }

    [Fact]
    public void WithoutAnEntryTheBinaryIsTheGuess()
    {
        Assert.Equal(new IconMatch("virt-viewer", IconSource.BinaryName), Resolve(binary: "Virt-Viewer", appName: "Virt Viewer"));
        Assert.Null(Resolve(appName: "Nothing at all"));
    }

    [Fact]
    public void StreamsGiveTheirHintsAndWineGivesNoBinary()
    {
        var steam = new AudioStream(1, "Steam", "steamwebhelper", null) { AppId = "org.valvesoftware.steam", IconName = "steam-x" };
        Assert.Equal(new ApplicationIconHints("steam-x", "org.valvesoftware.steam", "steam", "Steam"), ApplicationIconHints.From(steam));
        var game = new AudioStream(2, "Balatro.exe", "wine64-preloader", null);
        Assert.Equal(new ApplicationIconHints(null, null, null, "Balatro"), ApplicationIconHints.From(game));
        Assert.Equal(new IconMatch("steam_icon_2379780", IconSource.AppName),
            ApplicationIconResolver.Resolve(Index(), ApplicationIconHints.From(game)));
        Assert.Null(ApplicationIconHints.Remembered("wine|format test stream", "Wine").Binary);
    }

    [Fact]
    public void TheSearchFollowsXdgAndAddsFlatpakAndSnap()
    {
        string? home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", $"{Path.Combine(_root, "flatpak")}:relative:{Path.Combine(_root, "usr")}/");
            IReadOnlyList<string> found = DesktopEntryIndex.ApplicationDirectories();
            Assert.Equal([Home, Flatpak, System], found.Take(3));
            Assert.DoesNotContain(found, d => d.Contains("relative"));
            Assert.Contains("/var/lib/flatpak/exports/share/applications", found);
            Assert.Contains("/var/lib/snapd/desktop/applications", found);
            Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local/share/flatpak/exports/share/applications"), found);

            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", null);
            Assert.Contains("/usr/share/applications", DesktopEntryIndex.ApplicationDirectories());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", dirs);
        }
    }

    [Fact]
    public void LookupsAreKeptUntilADirectoryChanges()
    {
        string extra = Path.Combine(_root, "extra/applications");
        Directory.CreateDirectory(extra);
        var resolver = new ApplicationIconResolver(() => [extra, System], TimeSpan.Zero);
        var hints = new ApplicationIconHints(null, null, "newapp", null);
        Assert.Equal(IconSource.BinaryName, resolver.Resolve(hints)!.Source);   // nothing read yet
        resolver.Refresh();
        Assert.Equal(IconSource.BinaryName, resolver.Resolve(hints)!.Source);

        Entry(extra, "newapp.desktop", "New", "new-icon", "newapp");
        Directory.SetLastWriteTimeUtc(extra, DateTime.UtcNow.AddMinutes(1));
        resolver.Refresh();
        Assert.Equal(new IconMatch("new-icon", IconSource.Binary), resolver.Resolve(hints));

        // Within the interval nothing is read again.
        var slow = new ApplicationIconResolver(() => [extra], TimeSpan.FromHours(1));
        slow.Refresh();
        File.Delete(Path.Combine(extra, "newapp.desktop"));
        Directory.SetLastWriteTimeUtc(extra, DateTime.UtcNow.AddMinutes(2));
        slow.Refresh();
        Assert.Equal("new-icon", slow.Resolve(hints)!.Icon);
    }
}
