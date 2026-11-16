using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using OpenXLR.UI;
using OpenXLR.UI.Views;

namespace OpenXLR.Tests;

/// <summary>
/// The main window while the daemon goes and comes back: the input,
/// headphone and monitor sections and the mixer grey out through the
/// controls' own disabled look and take input again with the next
/// connection, and a refusal shows as the notice above the sections. Runs
/// inside WindowLayoutTests, which owns the UI thread.
/// </summary>
internal static class DaemonGoneWindowTests
{
    private sealed class NoHold : IDisposable { public void Dispose() { } }

    internal static void Check()
    {
        var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var window = new MainWindow(client);
        try
        {
            // The fixture says when the daemon comes and goes, not a retry loop.
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();
            var model = (MainViewModel)window.DataContext!;
            model.StartNoticeHold = (_, _) => new NoHold();
            window.Show();

            Control[] sections =
            [
                window.FindControl<Panel>("InputsPanel")!,
                window.FindControl<StackPanel>("HeadphonesPanel")!,
                window.FindControl<StackPanel>("MonitorPanel")!,
                window.FindControl<MixerPanel>("MixerPanel")!,
                window.FindControl<ToggleButton>("MonitorMute")!,
            ];
            Slider headphones = window.FindControl<Grid>("HeadphoneControls")!.Children.OfType<Slider>().First();
            Slider monitor = window.FindControl<Slider>("OutputVolumeSlider")!;

            void Expect(bool enabled, string when)
            {
                Dispatcher.UIThread.RunJobs();
                foreach (Control section in sections)
                    Assert.True(section.IsEffectivelyEnabled == enabled, $"{section.Name} {when}");
                Assert.True(headphones.IsEffectivelyEnabled == enabled, $"a headphone fader {when}");
                Assert.True(monitor.IsEffectivelyEnabled == enabled, $"the monitor fader {when}");
                Assert.Equal(!enabled, headphones.Classes.Contains(":disabled"));
            }

            Invoke(model, "OnConnectionChanged", true);
            Apply(model);
            Expect(true, "with the daemon connected");

            Invoke(model, "OnConnectionChanged", false);
            Expect(false, "with the daemon gone");
            Assert.All(model.Channels, channel => Assert.Equal(0, channel.MeterL));

            Invoke(model, "OnConnectionChanged", true);
            Apply(model);
            Expect(true, "once the daemon is back");

            Invoke(model, "OnDaemonError", "setLevel: unknown channel 'gone'");
            Dispatcher.UIThread.RunJobs();
            TextBlock notice = window.FindControl<TextBlock>("MainNotice")!;
            Assert.True(notice.IsEffectivelyVisible);
            Assert.Equal("setLevel: unknown channel 'gone'", notice.Text);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Apply(MainViewModel model)
    {
        JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState(
            InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1)), withMix: true))!;
        Invoke(model, "Apply", state);
    }

    private static void Invoke(MainViewModel model, string method, params object?[] args)
        => typeof(MainViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, args);
}
