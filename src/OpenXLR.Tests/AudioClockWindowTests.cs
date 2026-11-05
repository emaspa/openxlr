using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenXLR.Core.Mixing;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// The audio clock in the open windows, run inside the skin test's process
/// (Avalonia sets its platform up once per process): Flow names the clock
/// and the resampled microphone under its title, and the main window's
/// warning offers the manual section beside the daemon's sentence. It runs
/// last, since it replaces the fixture's state with a daemon's.
/// </summary>
internal static class AudioClockWindowTests
{
    private const string Mic = "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_0123456789-00.mono-fallback";
    private const string Card = "alsa_input.usb-Elgato_Elgato_4K_X_ABCDEF-02.analog-stereo";

    internal static void Check(MainWindow main, FlowWindow flow)
    {
        var vm = (MainViewModel)main.DataContext!;
        var clock = new MixerClock(new ClockDriver(Card, "Elgato 4K X", 2109) { Role = "capture" },
            [new ClockFollower(Mic, "Wave XLR", "unitInput", ["xlr1"], true, 2100, true)]);
        string warning = clock.Warning()!;
        Apply(vm, State(clock, warning));

        TextBlock line = flow.FindControl<TextBlock>("FlowClock")!, alert = flow.FindControl<TextBlock>("FlowClockWarning")!;
        Assert.True(line.IsVisible);
        Assert.Equal(Localizer.Format("FlowClock", "Elgato 4K X"), line.Text);
        Assert.True(alert.IsVisible);
        Assert.Equal(Localizer.Format("FlowClockResampled", "Wave XLR"), alert.Text);
        Button manual = main.FindControl<Button>("ClockManual")!;
        Assert.True(manual.IsEffectivelyVisible);
        Assert.False(main.FindControl<Button>("CardProfileManual")!.IsVisible);
        Assert.Contains(main.GetVisualDescendantsOfType<TextBlock>(), t => t.Text == warning && t.IsEffectivelyVisible);

        // The unit as the clock: the line stays, the warning and the button go.
        Apply(vm, State(new MixerClock(new ClockDriver(Mic, "Wave XLR", 2200) { Role = "unitInput" },
            [new ClockFollower(Card, "Elgato 4K X", "capture", ["console"], false, 2109, true)]), ""));
        Assert.Equal(Localizer.Format("FlowClock", "Wave XLR"), line.Text);
        Assert.False(alert.IsVisible);
        Assert.False(manual.IsVisible);

        // An older daemon reports no clock, and nothing is drawn for it.
        Apply(vm, State(null, ""));
        Assert.False(line.IsVisible);
        Assert.False(alert.IsVisible);
    }

    private static string State(MixerClock? clock, string warning)
    {
        string extra = clock is null ? "" : ",\"clock\":" + JsonSerializer.Serialize(clock, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        JsonNode node = JsonNode.Parse(InputBindingModelTests.ProState(
            InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1)), mixerExtra: extra))!;
        if (warning.Length > 0) node["warning"] = warning;
        return node.ToJsonString();
    }

    private static void Apply(MainViewModel vm, string state)
    {
        typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [JsonNode.Parse(state)!]);
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<T> GetVisualDescendantsOfType<T>(this Avalonia.Visual root) where T : Avalonia.Visual
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}
