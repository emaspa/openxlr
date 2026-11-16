using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// Every arrangement reaches every mix and input control. The classic mix
/// tiles and the matrix's column heads each draw the mix EQ, the monitor
/// mix's Dry switch, the Wave XLR Pro's maximizer and the USB Aux port
/// switch, exactly where the mix has each, inside the tile or the head and
/// bound to the mix. The matrix's row heads draw Line In's Raw Capture
/// switch and XLR 1's gain lock where the unit has them, and each writes
/// the same setting the INPUTS panel does.
/// </summary>
internal static class MixControlParityWindowTests
{
    private const string Mixer = """
        {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor","monitorDry":false,"hardwareSection":0,"maximizer":30,
                   "eq":{"enabled":false,"bands":[{"enabled":true,"type":"peak","frequency":1000,"gain":0,"q":0.707}],"available":true,"processing":"software"}},
                  {"id":"monitor2","name":"Monitor B","kind":"monitor","monitorDry":true},
                  {"id":"stream","name":"Stream","kind":"virtualMic",
                   "eq":{"enabled":true,"bands":[{"enabled":true,"type":"peak","frequency":1000,"gain":3,"q":0.707}],"available":true,"processing":"software"}},
                  {"id":"chat","name":"Chat","kind":"virtualMic"},
                  {"id":"auxout","name":"Aux","kind":"auxPort"}],
         "channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"levels":{"monitor":1,"stream":1},"volume":1},
                     {"id":"line","name":"Line In","hardware":true,"levels":{"monitor":1,"stream":1},"volume":0.6},
                     {"id":"music","name":"Music","levels":{"monitor":1,"stream":1},"volume":1}],
         "auxPortEnabled":true,"inserts":{}}
        """;

    internal static void Check()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            var model = (MainViewModel)window.DataContext!;
            StopConnecting(window);
            model.CapLineInput = true;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse(Mixer)]);
            model.RefreshBindings();
            PropertyInfo gainLock = typeof(MainViewModel).GetProperty(nameof(MainViewModel.ShowGainLock))!;
            PropertyInfo rawCapture = typeof(MainViewModel).GetProperty(nameof(MainViewModel.CapLineInRawCapture))!;
            gainLock.SetValue(model, true);
            rawCapture.SetValue(model, true);
            // The fixture has every mix control somewhere and leaves each out somewhere.
            Assert.Equal(["monitor", "stream"], model.Mixes.Where(m => m.HasEq).Select(m => m.Id));
            Assert.Equal(["monitor", "monitor2"], model.Mixes.Where(m => m.HasMonitorDry).Select(m => m.Id));
            Assert.Equal(["monitor"], model.Mixes.Where(m => m.ShowMaximizer).Select(m => m.Id));
            Assert.Equal(["auxout"], model.Mixes.Where(m => m.ShowAuxPortToggle).Select(m => m.Id));
            window.Show();

            SkinLayoutWindowTests.Wear(SkinPackage.DefaultId);
            // An earlier check may have folded the submixer; the tiles are drawn only while it is open.
            window.FindControl<Expander>("SubmixerTile")!.IsExpanded = true;
            foreach (double width in new[] { 640d, 1040, 2560 })
            {
                Layout(window, width, 1400);
                CheckClassicTiles(window, model, $"in the classic tiles at {width}");
            }
            CheckClassicWrites(window, model);

            SkinLayoutWindowTests.Wear("broadcast");
            try
            {
                window.FindControl<Expander>("SubmixerTile")!.IsExpanded = true;
                foreach (double width in new[] { 640d, 1040, 2560 })
                {
                    Layout(window, width, 1400);
                    CheckColumnHeads(window, model, $"in the matrix at {width}");
                    CheckRowHeads(window, model, $"in the matrix at {width}");
                }
                CheckMatrixWrites(window, model);
                // Without the capabilities the row heads leave both out.
                gainLock.SetValue(model, false);
                rawCapture.SetValue(model, false);
                Layout(window, 1040, 1400);
                CheckRowHeads(window, model, "without raw capture or a gain lock");
            }
            finally { SkinLayoutWindowTests.Wear(SkinPackage.DefaultId); }
        }
        finally
        {
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    private static Control? Shown(Visual root, params string[] names) =>
        root.GetVisualDescendants().OfType<Control>().SingleOrDefault(c => names.Contains(c.Name) && c.IsEffectivelyVisible);

    /// <summary>Each mix's control shows inside the tile or head exactly when the mix has it.</summary>
    private static void CheckMix(Border tile, MixViewModel mix, (string[] Names, bool Has)[] controls, string where)
    {
        var drawn = new List<Control>();
        foreach ((string[] names, bool has) in controls)
        {
            Control? control = Shown(tile, names);
            Assert.True(has == control is not null, $"{mix.Id}: {names[0]} {(has ? "missing" : "drawn")} {where}");
            if (control is null) continue;
            AssertInside(control, tile);
            drawn.Add(control);
        }
        if (drawn.Count > 1) AssertNoOverlap(drawn);
    }

    private static Border[] ClassicTiles(MainWindow window) =>
        [.. window.FindControl<ItemsControl>("MixMasterTiles")!.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Name == "ClassicMasterTile" && b.IsEffectivelyVisible)];

    private static Border ClassicTile(MainWindow window, string mix) =>
        ClassicTiles(window).Single(b => b.DataContext is MixViewModel m && m.Id == mix);

    private static void CheckClassicTiles(MainWindow window, MainViewModel model, string where)
    {
        Border[] tiles = ClassicTiles(window);
        Assert.Equal(model.Mixes.Select(m => m.Id), tiles.Select(t => ((MixViewModel)t.DataContext!).Id));
        foreach (Border tile in tiles)
        {
            var mix = (MixViewModel)tile.DataContext!;
            CheckMix(tile, mix,
            [
                (["ClassicMixEq"], mix.HasEq),
                (["ClassicMixDry"], mix.HasMonitorDry),
                (["ClassicMixMaximizer"], mix.ShowMaximizer),
                (["ClassicMixAuxPort"], mix.ShowAuxPortToggle),
            ], where);
        }
        // The EQ stands over the chain, as the signal meets them, the maximizer between them.
        Border monitor = ClassicTile(window, "monitor");
        Control eq = Shown(monitor, "ClassicMixEq")!, max = Shown(monitor, "ClassicMixMaximizer")!;
        Button chain = monitor.GetVisualDescendants().OfType<Button>()
            .Single(b => b.IsEffectivelyVisible && b.Classes.Contains("fx") && b.Name is null);
        double Top(Control c) => c.TranslatePoint(default, monitor)!.Value.Y;
        Assert.True(Top(eq) < Top(max) && Top(max) < Top(chain), $"EQ {Top(eq)}, Max {Top(max)}, chain {Top(chain)} {where}");
    }

    private static void CheckClassicWrites(MainWindow window, MainViewModel model)
    {
        Layout(window, 1040, 1400);
        MixViewModel monitor = model.Mixes.Single(m => m.Id == "monitor"), aux = model.Mixes.Single(m => m.Id == "auxout");
        var dry = (ToggleButton)Shown(ClassicTile(window, "monitor"), "ClassicMixDry")!;
        Assert.Equal(Localizer.Format("MonitorDryName", "Monitor A"), AutomationProperties.GetName(dry));
        Assert.Equal(Localizer.Text("MonitorDryTip"), ToolTip.GetTip(dry));
        Assert.Contains("dryChip", dry.Classes);
        dry.IsChecked = true;
        Assert.True(monitor.MonitorDry);
        dry.IsChecked = false;
        Assert.False(monitor.MonitorDry);
        var eq = (Button)Shown(ClassicTile(window, "stream"), "ClassicMixEq")!;
        MicEqViewModel streamEq = model.Mixes.Single(m => m.Id == "stream").Eq!;
        Assert.Equal(streamEq.IsLit, eq.Classes.Contains("fxOn"));
        Assert.Equal(streamEq.ButtonTip, ToolTip.GetTip(eq));
        var max = (Button)Shown(ClassicTile(window, "monitor"), "ClassicMixMaximizer")!;
        Assert.Equal(monitor.MaximizerOn, max.Classes.Contains("fxOn"));
        Assert.Equal(Localizer.Format("MaximizerName", "Monitor A"), AutomationProperties.GetName(max));
        Assert.NotNull(max.Flyout);
        var port = (ToggleButton)Shown(ClassicTile(window, "auxout"), "ClassicMixAuxPort")!;
        Assert.True(port.IsChecked);
        port.IsChecked = false;
        Assert.False(aux.AuxPortEnabled);
        port.IsChecked = true;
        Assert.True(aux.AuxPortEnabled);
    }

    private static Border ColumnHead(MainWindow window, string mix) =>
        window.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "MatrixColumn" && b.IsEffectivelyVisible && b.DataContext is MatrixColumnViewModel c && c.Mix.Id == mix);

    private static void CheckColumnHeads(MainWindow window, MainViewModel model, string where)
    {
        MixMatrixViewModel matrix = window.MixMatrix!;
        Assert.Equal(model.Mixes.Select(m => m.Id), matrix.Columns.Select(c => c.Mix.Id));
        foreach (MatrixColumnViewModel column in matrix.Columns)
        {
            MixViewModel mix = column.Mix;
            CheckMix(ColumnHead(window, mix.Id), mix,
            [
                (["MatrixMixEq"], mix.HasEq),
                (["MatrixDryChip", "MatrixDryChipLead"], mix.IsMonitor && mix.HasMonitorDry),
                (["MatrixMixMaximizer"], mix.ShowMaximizer),
                (["MatrixAuxPortChip"], mix.ShowAuxPortToggle),
            ], where);
        }
        // The port's chip shares the name line with the name, clear of it.
        Border aux = ColumnHead(window, "auxout");
        Control chip = Shown(aux, "MatrixAuxPortChip")!;
        TextBlock name = aux.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Aux" && t.IsEffectivelyVisible);
        AssertNoOverlap([chip, name]);
        double Middle(Control c) => c.TranslatePoint(new Point(0, c.Bounds.Height / 2), aux)!.Value.Y;
        Assert.True(Math.Abs(Middle(chip) - Middle(name)) <= 1, $"the port's chip is off the name line {where}");
        SkinLayoutWindowTests.CheckMatrixCells(window, matrix, where);
    }

    private static void CheckRowHeads(MainWindow window, MainViewModel model, string where)
    {
        Border xlr1 = SkinLayoutWindowTests.RowHead(window, "xlr1"), line = SkinLayoutWindowTests.RowHead(window, "line");
        Control? gainLock = Shown(xlr1, "MatrixGainLock");
        Assert.True(model.ShowGainLock == gainLock is not null, $"gain lock {where}");
        Control? raw = Shown(line, "MatrixLineInRaw");
        Assert.True(model.CapLineInRawCapture == raw is not null, $"raw capture {where}");
        MixMatrixViewModel matrix = window.MixMatrix!;
        SkinLayoutWindowTests.CheckProcessingPlace(line, matrix.Rows.Single(r => r.Channel.Id == "line"), where);
        if (gainLock is not null)
        {
            AssertInside(gainLock, xlr1);
            Slider gain = xlr1.GetVisualDescendants().OfType<Slider>().First(s => s.IsEffectivelyVisible && s.Name is null);
            AssertNoOverlap([gainLock, gain]);
        }
    }

    private static void CheckMatrixWrites(MainWindow window, MainViewModel model)
    {
        Layout(window, 1040, 1400);
        MixViewModel aux = model.Mixes.Single(m => m.Id == "auxout");
        var chip = (ToggleButton)Shown(ColumnHead(window, "auxout"), "MatrixAuxPortChip")!;
        Assert.Equal(Localizer.Text("MatrixAuxPortChip"), chip.Content);
        Assert.Equal(Localizer.Text("ToUSBAuxPort"), AutomationProperties.GetName(chip));
        Assert.Equal(Localizer.Text("SendThisMixToTheSecondComputerOn"), ToolTip.GetTip(chip));
        Assert.True(chip.IsChecked);
        chip.IsChecked = false;
        Assert.False(aux.AuxPortEnabled);
        chip.IsChecked = true;
        Assert.True(aux.AuxPortEnabled);

        // Line In's Raw Capture: a press is the INPUTS toggle's write, the button lit while it is on.
        var raw = (Button)Shown(SkinLayoutWindowTests.RowHead(window, "line"), "MatrixLineInRaw")!;
        Assert.Equal(Localizer.Text("RawCapture"), raw.Content);
        Assert.Equal(Localizer.Text("LineInRawCaptureTip"), ToolTip.GetTip(raw));
        Assert.DoesNotContain("fxOn", raw.Classes);
        raw.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(model.LineInRawCapture);
        Assert.Contains("fxOn", raw.Classes);
        Assert.True(window.FindControl<ToggleButton>("LineInRawToggle")!.IsChecked);
        raw.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(model.LineInRawCapture);

        // XLR 1's gain lock: the hardware panel's setting, and it greys the gain beside it.
        Border xlr1 = SkinLayoutWindowTests.RowHead(window, "xlr1");
        var gainLock = (ToggleButton)Shown(xlr1, "MatrixGainLock")!;
        Assert.Equal(Localizer.Text("GainLockTheDaemonRejectsEveryGainChange"), ToolTip.GetTip(gainLock));
        Slider gain = xlr1.GetVisualDescendants().OfType<Slider>().First(s => s.IsEffectivelyVisible && s.Name is null);
        Assert.True(gain.IsEnabled);
        gainLock.IsChecked = true;
        Assert.True(model.GainLocked);
        Assert.False(gain.IsEnabled);
        gainLock.IsChecked = false;
        Assert.False(model.GainLocked);
        Assert.True(gain.IsEnabled);
    }
}
