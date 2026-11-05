using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

internal static class MonitorMicrophonePauseWindowTests
{
    internal static void Check()
    {
        var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainWindow(client);
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        var model = (MainViewModel)main.DataContext!;
        var options = new OptionsWindow { DataContext = new OptionsViewModel(client, model) };
        bool touch = SkinService.TouchControls;
        try
        {
            main.Show();
            options.Show();
            OptionsTabsWindowTests.Show(options, OptionsTab.Audio);
            var checkbox = options.FindControl<CheckBox>("MonitorMuteMicrophones")!;
            var caption = (TextBlock)checkbox.Content!;
            var hint = options.FindControl<TextBlock>("MonitorMuteMicrophonesHint")!;
            var optionWarning = options.FindControl<TextBlock>("MicrophonePausePendingNote")!;
            var monitorWarning = main.FindControl<TextBlock>("MonitorMicrophonePausePendingNote")!;
            Report(model, null);
            Assert.False(checkbox.IsEnabled);
            Assert.False(checkbox.IsChecked);
            Assert.False(optionWarning.IsVisible);
            Assert.False(monitorWarning.IsVisible);
            Report(model, State(option: true, pending: true));
            main.FindControl<Expander>("MonitorTile")!.IsExpanded = true;
            Assert.True(checkbox.IsEnabled);
            Assert.True(checkbox.IsChecked);
            Assert.True(optionWarning.IsVisible);
            Assert.True(monitorWarning.IsVisible);
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.MonitorMicrophoneError))!
                .SetValue(model, "The microphone option could not be saved.");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("The microphone option could not be saved.", optionWarning.Text);
            Assert.True(optionWarning.IsVisible);
            // Losing the connection is not confirmation that a failed audio
            // pause or resume completed. Disable editing, keep the warning.
            ((Action<bool>)typeof(DaemonClient).GetField(nameof(DaemonClient.ConnectionChanged),
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!).Invoke(false);
            Dispatcher.UIThread.RunJobs();
            Assert.False(checkbox.IsEnabled);
            Assert.True(model.MicrophonePausePending);
            Assert.Null(model.MonitorMicrophoneError);
            Assert.True(optionWarning.IsVisible);
            Assert.True(monitorWarning.IsVisible);
            Report(model, State(option: true, pending: true));

            foreach (bool large in new[] { false, true })
            {
                SkinService.ApplyControlSizing(large);
                foreach (string language in new[] { "en", "de", "es", "it", Localizer.Language }.Distinct(StringComparer.Ordinal))
                {
                    var culture = CultureInfo.GetCultureInfo(language);
                    caption.Text = Localizer.Get("MuteMicrophoneSendsWithMonitoring", culture);
                    hint.Text = Localizer.Get("MuteMicrophoneSendsWithMonitoringHint", culture);
                    optionWarning.Text = monitorWarning.Text = Localizer.Get("MicrophonePausePendingNote", culture);
                    WindowLayoutTests.Layout(main, 640, 900);
                    options.UpdateLayout();
                    optionWarning.BringIntoView();
                    monitorWarning.BringIntoView();
                    Dispatcher.UIThread.RunJobs();
                    options.UpdateLayout();
                    Assert.True(caption.Bounds.Height > 0);
                    Assert.True(caption.TextLayout.Width <= caption.Bounds.Width + 1);
                    Assert.True(hint.TextLayout.Width <= hint.Bounds.Width + 1);
                    Assert.True(optionWarning.TextLayout.Width <= optionWarning.Bounds.Width + 1);
                    Assert.True(monitorWarning.TextLayout.Width <= monitorWarning.Bounds.Width + 1);
                    Assert.True(optionWarning.IsEffectivelyVisible);
                    Assert.True(monitorWarning.IsEffectivelyVisible);
                    if (large) Assert.True(main.FindControl<Avalonia.Controls.Primitives.ToggleButton>("MonitorMute")!.Bounds.Height >= SkinTokens.TouchTarget);
                    string size = large ? "touch" : "standard";
                    WindowLayoutTests.Capture(options, $"microphone-pause-options-{language}-{size}");
                    WindowLayoutTests.Capture(main, $"microphone-pause-monitor-640-{language}-{size}");
                }
            }
            Report(model, State(option: false, pending: false));
            Assert.False(checkbox.IsChecked);
            Assert.False(optionWarning.IsVisible);
            Assert.False(monitorWarning.IsVisible);
        }
        finally
        {
            SkinService.ApplyControlSizing(touch);
            options.Close();
            main.Quit();
        }
    }

    private static JsonNode State(bool option, bool pending) => new JsonObject
    {
        ["monitorMuteMicrophones"] = option, ["microphonePausePending"] = pending,
        ["channels"] = new JsonArray(), ["mixes"] = new JsonArray(), ["inserts"] = new JsonObject(),
    };

    private static void Report(MainViewModel model, JsonNode? mixer)
    {
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.DaemonConnected))!.SetValue(model, true);
        typeof(MainViewModel).GetField("_applying", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, true);
        try { typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [mixer]); }
        finally { typeof(MainViewModel).GetField("_applying", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, false); }
        Dispatcher.UIThread.RunJobs();
    }
}
