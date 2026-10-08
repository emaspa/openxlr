using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// Material's light and dark modes on open windows. Runs on the UI thread
/// <see cref="SkinWindowTests"/> owns, inside its Xvfb run. Changing the
/// application's requested variant raises the same event the desktop's
/// preference does, so no desktop setting is touched.
/// </summary>
internal static class AppearanceModeWindowTests
{
    private static readonly Color Dark = Color.Parse("#16181d");
    private static readonly Color Light = Color.Parse("#f3f5f8");

    private static string UiJson => Path.Combine(UiSettings.ConfigDir, "ui.json");

    internal static void Check(MainWindow main, OptionsWindow options, FlowWindow flow)
    {
        string? savedText = File.Exists(UiJson) ? File.ReadAllText(UiJson) : null;
        string? overrideBefore = Environment.GetEnvironmentVariable(SkinService.OverrideVariable);
        ThemeVariant? requested = Application.Current!.RequestedThemeVariant;
        try
        {
            OpenXlrPaths.WriteAtomic(UiJson, """{ "futureChoice": { "density": "touch" } }""");
            SkinService.Choose("default", out _);
            ModesRepaintOpenWindowsAndKeepTheLiveBrushes(main, options, flow);
            SystemFollowsTheDesktopWithoutWritingAnything(main);
            OtherSkinsKeepTheirColoursAndDisableTheMode(main, options);
            AModeThatCannotBeSavedIsWornAndReported(main, options);
            AVanishedSkinFallsBackToMaterialInTheSavedMode(main);
            TheLaunchOverrideHoldsDarkUntilASkinIsChosen(main, options);
            Assert.Equal("touch", (string?)JsonNode.Parse(File.ReadAllText(UiJson))!["futureChoice"]!["density"]);
        }
        finally
        {
            if (savedText is null) File.Delete(UiJson);
            else OpenXlrPaths.WriteAtomic(UiJson, savedText);
            Environment.SetEnvironmentVariable(SkinService.OverrideVariable, overrideBefore);
            SkinService.Initialize();
            Application.Current!.RequestedThemeVariant = requested;
            Pump();
            ((OptionsViewModel)options.DataContext!).ReloadSkins();
        }
    }

    private static void ModesRepaintOpenWindowsAndKeepTheLiveBrushes(MainWindow main, OptionsWindow options, FlowWindow flow)
    {
        var vm = (OptionsViewModel)options.DataContext!;
        var picker = options.FindControl<ComboBox>("AppearanceModePicker")!;
        Assert.True(picker.IsEnabled);

        // From any starting mode, the second choice is a change.
        picker.SelectedItem = vm.AppearanceModeChoices.Single(c => c.Id == AppearanceModes.Light);
        Pump();
        picker.SelectedItem = vm.AppearanceModeChoices.Single(c => c.Id == AppearanceModes.Dark);
        Pump();
        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Dark, flow.ActualThemeVariant);
        Assert.Equal(Dark, Background(main));
        Assert.Equal(AppearanceModes.Dark, UiSettings.Load().AppearanceMode);

        SolidColorBrush led = SkinService.LiveBrush("Ox.Led.On");
        SolidColorBrush hot = SkinService.LiveBrush("Ox.Meter.Hot");
        picker.SelectedItem = vm.AppearanceModeChoices.Single(c => c.Id == AppearanceModes.Light);
        Pump();
        Assert.Equal(ThemeVariant.Light, Application.Current.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Light, flow.ActualThemeVariant);
        Assert.Equal(Light, Background(main));
        Assert.Equal(Light, Background(options));
        // The converters' brushes are recoloured, not replaced, so a meter
        // that already holds one repaints, and the light meter has a hot zone.
        Assert.Same(led, SkinService.LiveBrush("Ox.Led.On"));
        Assert.Equal(Color.Parse("#197b43"), led.Color);
        Assert.NotEqual(SkinService.LiveBrush("Ox.Meter.Fill").Color, hot.Color);
        Assert.Equal(AppearanceModes.Light, UiSettings.Load().AppearanceMode);
        Assert.Null(vm.PreferenceError);
    }

    private static void SystemFollowsTheDesktopWithoutWritingAnything(MainWindow main)
    {
        SkinService.ChooseMode(AppearanceModes.System, out string? saveError);
        Assert.Null(saveError);
        string before = File.ReadAllText(UiJson);

        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Pump();
        Assert.Equal(Dark, Background(main));
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        Pump();
        Assert.Equal(Light, Background(main));

        Assert.Equal(before, File.ReadAllText(UiJson));
        Assert.Equal(AppearanceModes.System, SkinService.Mode);
    }

    private static void OtherSkinsKeepTheirColoursAndDisableTheMode(MainWindow main, OptionsWindow options)
    {
        var picker = options.FindControl<ComboBox>("AppearanceModePicker")!;
        var vm = (OptionsViewModel)options.DataContext!;
        foreach (SkinEntry entry in SkinCatalog.BuiltIn())
        {
            vm.SelectedSkin = vm.SkinChoices.Single(c => c.Id == entry.Id);
            Pump();
            IBrush? own = main.Background;
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            Pump();
            Assert.Same(own, main.Background);
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            Pump();
            Assert.Same(own, main.Background);
            Assert.False(picker.IsEnabled, $"{entry.Id} leaves the mode picker enabled");
        }
        vm.SelectedSkin = vm.SkinChoices.Single(c => c.Id == SkinPackage.DefaultId);
        Pump();
        Assert.True(picker.IsEnabled);
    }

    private static void AModeThatCannotBeSavedIsWornAndReported(MainWindow main, OptionsWindow options)
    {
        var vm = (OptionsViewModel)options.DataContext!;
        var picker = options.FindControl<ComboBox>("AppearanceModePicker")!;
        string original = File.ReadAllText(UiJson);
        try
        {
            // A file the window cannot read is left exactly as it is.
            OpenXlrPaths.WriteAtomic(UiJson, "{ \"skin\": ");
            picker.SelectedItem = vm.AppearanceModeChoices.Single(c => c.Id == AppearanceModes.Dark);
            Pump();
            Assert.Equal(AppearanceModes.Dark, SkinService.Mode);
            Assert.Equal(Dark, Background(main));
            Assert.Contains("ui.json", vm.PreferenceError);
            Assert.Equal("{ \"skin\": ", File.ReadAllText(UiJson));

            // A file that cannot be written: the choice is still worn.
            File.Delete(UiJson);
            Directory.CreateDirectory(UiJson);
            picker.SelectedItem = vm.AppearanceModeChoices.Single(c => c.Id == AppearanceModes.Light);
            Pump();
            Assert.Equal(AppearanceModes.Light, SkinService.Mode);
            Assert.Equal(Light, Background(main));
            Assert.StartsWith("Could not save ui.json.", vm.PreferenceError);
        }
        finally
        {
            if (Directory.Exists(UiJson)) Directory.Delete(UiJson);
            OpenXlrPaths.WriteAtomic(UiJson, original);
        }

        picker.SelectedItem = vm.AppearanceModeChoices.Single(c => c.Id == AppearanceModes.Dark);
        Pump();
        Assert.Null(vm.PreferenceError);
        Assert.Equal(AppearanceModes.Dark, UiSettings.Load().AppearanceMode);
    }

    private static void AVanishedSkinFallsBackToMaterialInTheSavedMode(MainWindow main)
    {
        string root = Directory.CreateTempSubdirectory("openxlr-mode-reload-").FullName;
        string? oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", root);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(root, "empty"));
            string folder = Path.Combine(SkinCatalog.UserSkinDir, "temporary");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "skin.json");
            foreach (string mode in new[] { AppearanceModes.Light, AppearanceModes.Dark })
            {
                File.WriteAllText(file, """{"schema":1,"name":"Temporary","tokens":{"Ox.Window.Background":"#123456"}}""");
                SkinService.ChooseMode(mode, out _);
                SkinService.Choose("temporary", out _);
                Application.Current!.RequestedThemeVariant = mode == AppearanceModes.Light ? ThemeVariant.Dark : ThemeVariant.Light;
                File.Delete(file);
                SkinService.Reload();
                Pump();
                Assert.Equal(SkinPackage.DefaultId, SkinService.Current.Id);
                Assert.Equal(mode == AppearanceModes.Light ? ThemeVariant.Light : ThemeVariant.Dark,
                    Application.Current.RequestedThemeVariant);
                Assert.Equal(mode == AppearanceModes.Light ? Light : Dark, Background(main));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", oldDirs);
            Directory.Delete(root, true);
            SkinService.Choose(SkinPackage.DefaultId, out _);
        }
    }

    private static void TheLaunchOverrideHoldsDarkUntilASkinIsChosen(MainWindow main, OptionsWindow options)
    {
        SkinService.ChooseMode(AppearanceModes.Light, out _);
        Environment.SetEnvironmentVariable(SkinService.OverrideVariable, SkinPackage.DefaultId);
        SkinService.Initialize();
        ((OptionsViewModel)options.DataContext!).ReloadSkins();
        Pump();
        Assert.True(SkinService.Overridden);
        Assert.False(options.FindControl<ComboBox>("AppearanceModePicker")!.IsEnabled);
        Assert.Equal(Dark, Background(main));
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Pump();
        Assert.Equal(Dark, Background(main));
        Assert.Equal(AppearanceModes.Light, UiSettings.Load().AppearanceMode);

        // Choosing a skin, even one that cannot be saved, ends the override.
        string original = File.ReadAllText(UiJson);
        File.Delete(UiJson);
        Directory.CreateDirectory(UiJson);
        try
        {
            SkinService.Choose(SkinPackage.DefaultId, out string? saveError);
            Assert.NotNull(saveError);
        }
        finally
        {
            Directory.Delete(UiJson);
            OpenXlrPaths.WriteAtomic(UiJson, original);
        }
        Pump();
        Assert.False(SkinService.Overridden);
        Assert.Equal(Light, Background(main));
    }

    private static Color Background(Window window) => Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color;

    private static void Pump() => Dispatcher.UIThread.RunJobs();
}
