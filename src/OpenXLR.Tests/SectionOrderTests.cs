using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>The saved section order as the window reads and changes it.</summary>
[Collection("xdg-config")]
public sealed class SectionOrderTests
{
    private static readonly string[] Usual = ["InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile", "SubmixerTile"];

    [Fact]
    public void ASavedOrderIsMadeWholeAndKeepsOnlyKnownSections()
    {
        Assert.Equal(Usual, MainWindow.CompleteSectionOrder(null));
        Assert.Equal(Usual, MainWindow.CompleteSectionOrder([]));
        Assert.Equal(["SubmixerTile", "MonitorTile", "InputsTile", "HeadphonesTile", "ApplicationsTile"],
            MainWindow.CompleteSectionOrder(["SubmixerTile", "Gone", "MonitorTile", "SubmixerTile"]));
    }

    [Fact]
    public void ADropLandsBeforeOrAfterItsTarget()
    {
        Assert.Equal(["SubmixerTile", "InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile"],
            MainWindow.PlaceSection(Usual, "SubmixerTile", "InputsTile", after: false));
        Assert.Equal(["HeadphonesTile", "MonitorTile", "InputsTile", "ApplicationsTile", "SubmixerTile"],
            MainWindow.PlaceSection(Usual, "InputsTile", "MonitorTile", after: true));
        Assert.Equal(Usual, MainWindow.PlaceSection(Usual, "InputsTile", "InputsTile", after: true));
        Assert.Equal(Usual, MainWindow.PlaceSection(Usual, "Gone", "InputsTile", after: true));
    }

    [Fact]
    public void ANullOrderInTheFileReadsAsNone()
    {
        string home = Directory.CreateTempSubdirectory("openxlr-sections-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", home);
            Directory.CreateDirectory(Path.Combine(home, "openxlr"));
            File.WriteAllText(Path.Combine(home, "openxlr", "ui.json"), """{"sectionOrder":null,"collapsedSections":null}""");
            UiSettings settings = UiSettings.Load();
            Assert.Empty(settings.SectionOrder);
            Assert.Empty(settings.CollapsedSections);
            Assert.Null((settings with { SectionOrder = ["SubmixerTile"] }).Save());
            Assert.Equal(["SubmixerTile"], UiSettings.Load().SectionOrder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(home, true);
        }
    }
}
