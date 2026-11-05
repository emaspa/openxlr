using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// Skins switched around a detached mixer: the panel stays one panel in one
/// window through repeated pop out, switch and dock rounds, with the main
/// window hidden, and across a mix deleted and made again; a skin's pop-out
/// default opens the mixer's window once and the user's own docking wins
/// from then on; a change of colours opens and closes nothing; and plugin
/// windows opened from the detached panel come back under the main window
/// when it docks.
/// </summary>
internal static partial class MixerPopOutWindowTests
{
    private const string Mixer = """
        {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"stream","name":"Stream","kind":"virtualMic"},
                  {"id":"chat","name":"Chat","kind":"virtualMic"}],
         "channels":[{"id":"music","name":"Music","levels":{"monitor":0.5,"stream":0.4,"chat":0.3}},
                     {"id":"game","name":"Game","levels":{"monitor":1,"stream":1,"chat":1}}]}
        """;

    private static readonly Dictionary<string, string> Skins = new()
    {
        ["switch-row"] = """
            {"schema":1,"name":"Row","tokens":{"Ox.Card.Background":"#232730"},"layout":{"version":1,
             "main":{"kind":"stack","id":"row","orientation":"horizontal","children":[
               {"kind":"region","region":"submixer"},{"kind":"region","region":"monitor"},{"kind":"region","region":"inputs"},
               {"kind":"region","region":"headphones"},{"kind":"region","region":"applications"}]}}}
            """,
        ["switch-grid"] = """
            {"schema":1,"name":"Grid","tokens":{"Ox.Card.Background":"#262a33"},"layout":{"version":1,
             "main":{"kind":"grid","id":"desktop","columns":[{"star":1},{"star":1}],"rows":["auto","auto","auto"],"children":[
               {"kind":"region","region":"inputs","row":0,"column":0},{"kind":"region","region":"monitor","row":0,"column":1},
               {"kind":"region","region":"headphones","row":1,"column":0},{"kind":"region","region":"applications","row":1,"column":1},
               {"kind":"region","region":"submixer","row":2,"column":0,"columnSpan":2}]},
             "regions":{"monitor":{"template":"output-toolbar"}}}}
            """,
        ["switch-banks"] = """
            {"schema":1,"name":"Banks","tokens":{"Ox.Card.Background":"#22252b"},"layout":{"version":1,
             "regions":{"submixer":{"template":"mix-banks","visibleBanks":3,"selectionDefaults":["stream","monitor"],
               "master":{"template":"master-strip"}}}}}
            """,
        ["switch-matrix"] = """
            {"schema":1,"name":"Matrix","tokens":{"Ox.Card.Background":"#24282e"},"layout":{"version":1,
             "regions":{"submixer":{"template":"mix-matrix"}}}}
            """,
        ["switch-detached"] = """
            {"schema":1,"name":"Detached","tokens":{"Ox.Card.Background":"#2a262e"},"layout":{"version":1,
             "regions":{"applications":{"template":"application-list"},"submixer":{"popOut":{"default":"detached"}}}}}
            """,
    };

    private static void CheckSkinSwitching(MainWindow outer)
    {
        // The application has one main window; the layout test's would open a
        // mixer window of its own for the skin's pop-out default and record it.
        outer.Hide();
        string data = Directory.CreateTempSubdirectory("openxlr-popout-skins-").FullName;
        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        SkinEntry worn = SkinService.Current;
        MainWindow? main = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(data, "none"));
            foreach ((string id, string json) in Skins)
            {
                string folder = Path.Combine(data, "openxlr", "skins", id);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "skin.json"), json);
            }
            Assert.Null(new UiSettings().Save());
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            int subscribers = SkinService.ChangedSubscribers;
            main = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            var vm = (MainViewModel)main.DataContext!;
            MethodInfo apply = typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            apply.Invoke(vm, [JsonNode.Parse(Mixer)]);
            main.Show();
            Layout(main, 1040, 900);
            var host = main.FindControl<ContentControl>("MixerHost")!;
            var panel = Assert.IsType<MixerPanel>(host.Content);
            ChannelViewModel[] channels = [.. vm.Channels];
            MixViewModel[] mixes = [.. vm.Mixes];
            SendViewModel[] sends = [.. vm.Channels.SelectMany(c => c.Sends)];

            // Pop out, switch through a row, a grid, banks and back to Material, dock; twice.
            foreach (int round in new[] { 1, 2 })
            {
                MixerWindow popped = main.PopOutMixer();
                Layout(popped, 800, 600);
                foreach (string id in new[] { "switch-row", "switch-grid", "switch-banks", "switch-matrix", SkinPackage.DefaultId })
                {
                    Wear(id);
                    Layout(main, 1040, 900);
                    Layout(popped, 800, 600);
                    string where = $"round {round}, {id}";
                    Assert.Same(popped, main.DetachedMixer);
                    Assert.True(popped.IsVisible, where);
                    Assert.Null(host.Content);
                    Assert.Same(panel, popped.Panel);
                    AssertOnePanel(main, panel);
                    Assert.Equal(id == "switch-banks", panel.Banks is not null);
                    Assert.Equal(id == "switch-matrix", panel.Matrix is not null);
                    Assert.True(panel.Bounds.Width >= panel.MinimumWidth() + 8 - 0.5, where);
                    // The panel in the detached window still writes to the window's sends.
                    Slider fader = id switch
                    {
                        "switch-banks" => SkinLayoutWindowTests.BankCells(popped)[0][0].GetVisualDescendants().OfType<Slider>().Single(),
                        "switch-matrix" => popped.GetVisualDescendants().OfType<Slider>().First(s => s.IsEffectivelyVisible && s.DataContext is MatrixCellViewModel),
                        _ => popped.GetVisualDescendants().OfType<Slider>().First(s => s.DataContext is SendViewModel),
                    };
                    SendViewModel send = fader.DataContext switch
                    {
                        BankCellViewModel cell => cell.Send!,
                        MatrixCellViewModel cell => cell.Send!,
                        var other => (SendViewModel)other!,
                    };
                    fader.Value = round == 1 ? 0.21 : 0.37;
                    Assert.Equal(fader.Value, send.Level);
                    Assert.Contains(send, sends);
                }
                popped.Close();
                Dispatcher.UIThread.RunJobs();
                Assert.Null(main.DetachedMixer);
                Assert.Same(panel, host.Content);
                AssertOnePanel(main, panel);
                Assert.True(UiSettings.Load().MixerDocked);
            }

            // The detached window widens for a presentation that needs more, and only then.
            MixerWindow sized = main.PopOutMixer();
            Layout(sized, 320, 600);
            Assert.Equal(320, sized.ClientSize.Width, 0.5);
            Wear("switch-banks");
            Layout(sized, sized.ClientSize.Width, 600);
            double banksNeed = panel.MinimumWidth() + 8 + 16;
            Assert.True(banksNeed > 320, $"the banks need {banksNeed}");
            Assert.Equal(banksNeed, sized.ClientSize.Width, 0.5);
            Assert.Equal(panel.MinimumWidth(), sized.FittedMinimum, 0.5);
            // The whole matrix needs more than the banks did: the window widens again, to the matrix.
            Wear("switch-matrix");
            Layout(sized, sized.ClientSize.Width, 600);
            double matrixNeed = panel.MinimumWidth() + 8 + 16;
            Assert.True(matrixNeed > banksNeed, $"the matrix needs {matrixNeed}, the banks {banksNeed}");
            Assert.Equal(matrixNeed, sized.ClientSize.Width, 0.5);
            // A presentation that needs less leaves the width as it was.
            Wear("switch-banks");
            Layout(sized, sized.ClientSize.Width, 600);
            Assert.Equal(matrixNeed, sized.ClientSize.Width, 0.5);
            Wear(SkinPackage.DefaultId);
            Layout(sized, sized.ClientSize.Width, 600);
            Assert.Equal(matrixNeed, sized.ClientSize.Width, 0.5);
            // A window the user narrows stays narrow through a switch whose need did not grow.
            Layout(sized, 320, 600);
            Wear("switch-row");
            Layout(sized, sized.ClientSize.Width, 600);
            Assert.Equal(320, sized.ClientSize.Width, 0.5);
            Assert.True(sized.FindControl<ScrollViewer>("MixerScroll")!.Extent.Width >= panel.MinimumWidth());
            sized.Close();
            Dispatcher.UIThread.RunJobs();
            Wear(SkinPackage.DefaultId);

            // At a custom size the window widens to what the enlarged presentation needs.
            string? savedScale = UiSettings.Load().MixerScale;
            Assert.Null((UiSettings.Load() with { MixerScale = "137" }).Save());
            MixerWindow enlarged = main.PopOutMixer();
            try
            {
                Layout(enlarged, 320, 600);
                Assert.True(enlarged.CustomScale);
                Assert.Equal(1.37, enlarged.Scale, 10);
                Wear("switch-banks");
                Layout(enlarged, enlarged.ClientSize.Width, 600);
                double enlargedNeed = Math.Ceiling((Math.Ceiling(panel.MinimumWidth() + 8) + 1) * 1.37) + 16;
                Assert.True(enlargedNeed > banksNeed, $"the enlarged banks need {enlargedNeed}, at 100% {banksNeed}");
                Assert.Equal(enlargedNeed, enlarged.ClientSize.Width, 0.5);
                ScrollViewer enlargedScroll = enlarged.FindControl<ScrollViewer>("MixerScroll")!;
                Assert.True(enlargedScroll.Extent.Width <= enlargedScroll.Viewport.Width,
                    $"the enlarged banks are {enlargedScroll.Extent.Width} wide in {enlargedScroll.Viewport.Width}");
            }
            finally
            {
                enlarged.Close();
                Dispatcher.UIThread.RunJobs();
                Wear(SkinPackage.DefaultId);
                Assert.Null((UiSettings.Load() with { MixerScale = savedScale }).Save());
            }

            CheckMatrixFit(main, panel);

            // A switch while the main window is hidden in the tray, with the mixer detached.
            MixerWindow tray = main.PopOutMixer();
            main.Hide();
            Wear("switch-grid");
            Wear("switch-banks");
            Assert.True(tray.IsVisible);
            Assert.Same(panel, tray.Panel);
            Assert.NotNull(panel.Banks);
            Layout(tray, 800, 600);
            Assert.Equal(3, SkinLayoutWindowTests.Banks(tray).Length);
            main.ShowMixer();
            Layout(main, 1040, 900);
            Assert.Same(tray, main.DetachedMixer);
            Assert.True(main.FindControl<StackPanel>("DetachedMixerNotice")!.IsVisible);
            AssertOnePanel(main, panel);

            // A mix deleted while one skin is on and made again under another:
            // the banks drop the old mix and show the new one, never both.
            apply.Invoke(vm, [JsonNode.Parse(Mixer.Replace("""{"id":"stream","name":"Stream","kind":"virtualMic"},""", ""))]);
            Assert.DoesNotContain(panel.Banks!.Banks, b => b.Mix?.Id == "stream");
            Wear("switch-grid");
            apply.Invoke(vm, [JsonNode.Parse(Mixer)]);
            Wear("switch-banks");
            Layout(tray, 800, 600);
            MixViewModel stream = vm.Mixes.Single(m => m.Id == "stream");
            Assert.NotSame(mixes.Single(m => m.Id == "stream"), stream);
            Assert.Same(stream, panel.Banks!.Banks.Single(b => b.Mix?.Id == "stream").Mix);
            foreach (MixBankViewModel bank in panel.Banks.Banks)
            {
                Assert.Contains(bank.Mix!, vm.Mixes);
                foreach (BankCellViewModel cell in bank.Cells)
                    Assert.Same(cell.Channel.Sends.Single(s => s.MixId == bank.Mix!.Id), cell.Send);
            }
            Assert.Equal(channels.Select(c => c.Id), vm.Channels.Select(c => c.Id));
            tray.Close();
            Dispatcher.UIThread.RunJobs();
            Wear(SkinPackage.DefaultId);

            CheckPopOutDefault(main, vm);
            CheckPluginWindowsFollowTheMixer(main, vm);

            main.Quit();
            Dispatcher.UIThread.RunJobs();
            // The windows each switch made are gone, and so are their handlers.
            // The layout test's window built its application lamps again while
            // hidden; they take their handlers back once it lays out.
            outer.Show();
            Layout(outer, 1040, 900);
            Assert.Equal(subscribers, SkinService.ChangedSubscribers);
        }
        finally
        {
            main?.Quit();
            Dispatcher.UIThread.RunJobs();
            SkinService.Apply(worn);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", dataDirs);
            Directory.Delete(data, true);
            outer.Show();
        }
    }

    private static void CheckMatrixFit(MainWindow main, MixerPanel panel)
    {
        string? savedScale = UiSettings.Load().MixerScale;
        bool touch = SkinService.TouchControls;
        Layout(main, 1040, 900);
        Wear("switch-matrix");
        SkinService.ApplyControlSizing(true);
        Layout(main, 1040, 900);
        MixerWindow popped = main.PopOutMixer();
        try
        {
            var picker = popped.FindControl<ComboBox>("MixerScalePicker")!;
            picker.SelectedIndex = 1;
            Layout(popped, 1400, 340);
            MixMatrixView matrix = panel.MatrixItems;
            var lines = matrix.FindControl<StackPanel>("MatrixLines")!;
            Slider master = matrix.GetVisualDescendants().OfType<Slider>().First(s => s.Name == "MatrixMaster");
            Slider send = matrix.GetVisualDescendants().OfType<Slider>().First(s => s.DataContext is MatrixCellViewModel);
            double normalWidth = lines.Bounds.Width;
            double normalMaster = master.Bounds.Width;
            double normalSend = send.Bounds.Width;
            Assert.True(matrix.Scroller.Viewport.Width - normalWidth > 100);
            Capture(popped, "popout-matrix-100-wide");

            picker.SelectedIndex = 0;
            Layout(popped, 1400, 340);
            double gap = matrix.Scroller.Viewport.Width - lines.Bounds.Width;
            Capture(popped, "popout-matrix-fit-wide");
            Assert.InRange(gap, -0.5, 6);
            Assert.True(master.Bounds.Width > normalMaster, "spare width should lengthen the master fader");
            Assert.True(send.Bounds.Width > normalSend, "spare width should lengthen the send fader");
            Assert.True(matrix.Scroller.Extent.Width <= matrix.Scroller.Viewport.Width + 0.5);
            Assert.True(popped.FindControl<ScrollViewer>("MixerScroll")!.Extent.Width
                <= popped.FindControl<ScrollViewer>("MixerScroll")!.Viewport.Width + 0.5);

            Layout(popped, 340, 280);
            Assert.Equal(1, popped.Scale);
            Assert.True(popped.FindControl<ScrollViewer>("MixerScroll")!.Extent.Width > 340);
            Assert.Equal(normalWidth, lines.Bounds.Width, 0.5);

            picker.SelectedIndex = 1;
            Layout(popped, 1400, 340);
            Assert.Equal(normalWidth, lines.Bounds.Width, 0.5);
            picker.SelectedIndex = 0;
            Layout(popped, 1400, 340);
            popped.Close();
            Dispatcher.UIThread.RunJobs();
            Layout(main, 1400, 900);
            Assert.Equal(normalWidth, lines.Bounds.Width, 0.5);
        }
        finally
        {
            popped.Close();
            Dispatcher.UIThread.RunJobs();
            Layout(main, 1040, 900);
            SkinService.ApplyControlSizing(touch);
            Wear(SkinPackage.DefaultId);
            Assert.Null((UiSettings.Load() with { MixerScale = savedScale }).Save());
        }
    }

    /// <summary>
    /// The skin's pop-out default against the user's choice: it opens the
    /// mixer's window once while there is no choice, a switch away keeps it
    /// open, the user docking it is a choice that keeps every later switch
    /// docked, Reset forgets the choice without opening anything, the
    /// default then applies again on the next switch to the skin, it waits
    /// while the main window is hidden, and neither a change of colours nor
    /// reading the same skin again opens or closes a window.
    /// </summary>
    private static void CheckPopOutDefault(MainWindow main, MainViewModel vm)
    {
        Assert.Null((UiSettings.Load() with { MixerDocked = null }).Save());
        Assert.Null(main.DetachedMixer);
        Wear("switch-detached");
        MixerWindow popped = Assert.IsType<MixerWindow>(main.DetachedMixer);
        Assert.True(popped.IsVisible);
        UiSettings settings = UiSettings.Load();
        Assert.True(settings.HasAppliedSkinPopOut("switch-detached", 1));
        Assert.Null(settings.MixerDocked);
        Layout(popped, 800, 600);

        // Colours, sizing and a reload open and close nothing.
        Assert.Empty(SkinService.Apply(SkinCatalog.Find("switch-detached")!));
        SkinService.ChooseControlSizing(!SkinService.TouchControls, out _);
        SkinService.ChooseControlSizing(!SkinService.TouchControls, out _);
        SkinService.Reload();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(popped, main.DetachedMixer);
        // Switching away keeps the mixer where it is.
        Wear("opendeck");
        Wear(SkinPackage.DefaultId);
        Assert.Same(popped, main.DetachedMixer);
        Assert.True(popped.IsVisible);

        // Back to the skin: it already opened once, and the mixer is open anyway.
        Wear("switch-detached");
        Assert.Same(popped, main.DetachedMixer);
        // The user docks: a choice, which no skin overrides afterwards.
        popped.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(UiSettings.Load().MixerDocked);
        Wear(SkinPackage.DefaultId);
        Wear("switch-detached");
        Assert.Null(main.DetachedMixer);
        // Without the choice the record still holds: once per skin layout.
        Assert.Null((UiSettings.Load() with { MixerDocked = null }).Save());
        Wear(SkinPackage.DefaultId);
        Wear("switch-detached");
        Assert.Null(main.DetachedMixer);

        // Options' reset forgets the choice and the record and opens nothing now.
        Assert.Null((UiSettings.Load() with { MixerDocked = true }).Save());
        var options = new OptionsViewModel(new DaemonClient("ws://127.0.0.1:1/ws"), vm) { ResetArrangement = main.ResetSkinArrangement };
        Assert.Equal(OpenXLR.UI.Localization.Localizer.Text("MixerDockingDocked"), options.MixerDockingNote);
        Assert.Contains(OpenXLR.UI.Localization.Localizer.Text("LayoutPartDetached"), options.SkinLayoutSummary, StringComparison.Ordinal);
        options.ResetSkinArrangement();
        Assert.Null(main.DetachedMixer);
        settings = UiSettings.Load();
        Assert.Null(settings.MixerDocked);
        Assert.False(settings.HasAppliedSkinPopOut("switch-detached", 1));
        Assert.Equal(OpenXLR.UI.Localization.Localizer.Text("MixerDockingSkinDetached"), options.MixerDockingNote);
        // Reading the same skin again is not putting it on.
        SkinService.Reload();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(main.DetachedMixer);

        // Put on while the main window is hidden, it waits for the window to show.
        Wear(SkinPackage.DefaultId);
        main.Hide();
        Wear("switch-detached");
        Assert.Null(main.DetachedMixer);
        main.ShowMixer();
        Layout(main, 1040, 900);
        popped = Assert.IsType<MixerWindow>(main.DetachedMixer);
        Assert.True(UiSettings.Load().HasAppliedSkinPopOut("switch-detached", 1));
        Assert.Null(UiSettings.Load().MixerDocked);

        // The user's own Pop out is a choice too.
        popped.Close();
        Dispatcher.UIThread.RunJobs();
        main.FindControl<Button>("PopOutMixerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(UiSettings.Load().MixerDocked);
        Assert.Equal(OpenXLR.UI.Localization.Localizer.Text("MixerDockingDetached"),
            new OptionsViewModel(new DaemonClient("ws://127.0.0.1:1/ws"), vm).MixerDockingNote);
        main.DetachedMixer!.Close();
        Dispatcher.UIThread.RunJobs();
        Wear(SkinPackage.DefaultId);
    }

    /// <summary>
    /// A chain and an insert's controls opened from the detached panel are
    /// owned by the mixer's window, stay so through a skin switch, and open
    /// again under the main window, on the same chain and insert, when the
    /// mixer docks.
    /// </summary>
    private static void CheckPluginWindowsFollowTheMixer(MainWindow main, MainViewModel vm)
    {
        MixerWindow popped = main.PopOutMixer();
        Layout(popped, 800, 600);
        MixViewModel mix = vm.Mixes.Single(m => m.Id == "monitor");
        var insert = new InsertViewModel(mix.Inserts, "handover-test", "urn:openxlr:test", "Handover effect");
        mix.Inserts.Items.Add(insert);
        InsertWindows.OpenChain(popped, mix.Inserts, "mix:monitor");
        InsertWindows.OpenControls(popped, insert);
        Dispatcher.UIThread.RunJobs();
        var chain = Assert.Single(popped.OwnedWindows.OfType<MixInsertsWindow>());
        var controls = Assert.Single(popped.OwnedWindows.OfType<InsertControlsWindow>());
        Assert.Empty(main.OwnedWindows.OfType<MixInsertsWindow>());

        Wear("switch-grid");
        Wear("switch-banks");
        Assert.Same(chain, Assert.Single(popped.OwnedWindows.OfType<MixInsertsWindow>()));
        Assert.True(chain.IsVisible);
        Assert.True(controls.IsVisible);

        popped.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(chain.IsVisible);
        Assert.False(controls.IsVisible);
        var movedChain = Assert.Single(main.OwnedWindows.OfType<MixInsertsWindow>());
        var movedControls = Assert.Single(main.OwnedWindows.OfType<InsertControlsWindow>());
        Assert.Same(mix.Inserts, movedChain.DataContext);
        Assert.Same(insert, movedControls.DataContext);
        Assert.True(movedChain.IsVisible);
        // Asking again from the main window raises the same window rather than another.
        InsertWindows.OpenChain(main, mix.Inserts, "mix:monitor");
        Assert.Same(movedChain, Assert.Single(main.OwnedWindows.OfType<MixInsertsWindow>()));
        movedChain.Close();
        movedControls.Close();
        mix.Inserts.Items.Remove(insert);
        Dispatcher.UIThread.RunJobs();
        Wear(SkinPackage.DefaultId);
    }

    /// <summary>One mixer panel in all the application's windows, the one given.</summary>
    private static void AssertOnePanel(MainWindow main, MixerPanel panel)
    {
        Window[] windows = [main, .. main.DetachedMixer is { } detached ? new Window[] { detached } : []];
        Assert.Same(panel, Assert.Single(windows.SelectMany(w => w.GetVisualDescendants().OfType<MixerPanel>())));
    }

    private static void Wear(string id)
    {
        SkinEntry entry = id == SkinPackage.DefaultId ? new SkinEntry(SkinPackage.Default, []) : SkinCatalog.Find(id)!;
        Assert.Empty(entry.Errors);
        Assert.Empty(SkinService.Apply(entry));
        Dispatcher.UIThread.RunJobs();
    }
}
