using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

internal static class WelcomeGuideWindowTests
{
    internal static void Check()
    {
        UiSettings initial = UiSettings.Load();
        Assert.Null((initial with { WelcomeGuideCompleted = false, StartMinimized = true }).Save());
        try
        {
            var main = Start();
            try
            {
                Assert.True(main.StartsHidden);
                Assert.Empty(main.OwnedWindows);
                main.ShowMixer();
                Settle(main);
                var firstGuide = Assert.Single(main.OwnedWindows.OfType<WelcomeGuideWindow>());
                Click(firstGuide.FindControl<Button>("GuideNext")!);
                main.Close();
                Dispatcher.UIThread.RunJobs();
                Assert.False(main.IsVisible);
                Assert.False(firstGuide.IsVisible);
                main.ShowMixer();
                Settle(main);
                Assert.Same(firstGuide, Assert.Single(main.OwnedWindows.OfType<WelcomeGuideWindow>()));
                Assert.True(firstGuide.IsVisible);
                Assert.Equal(2, firstGuide.FindControl<ProgressBar>("GuideProgress")!.Value);
            }
            finally
            {
                main.Quit();
            }
            Assert.False(UiSettings.Load().WelcomeGuideCompleted);

            main = Start();
            try
            {
                main.ShowMixer();
                Settle(main);
                var guide = Assert.Single(main.OwnedWindows.OfType<WelcomeGuideWindow>());
                Assert.True(main.IsEffectivelyEnabled);
                Layout(guide, Math.Min(360, guide.Width), Math.Min(280, guide.Height));
                var next = guide.FindControl<Button>("GuideNext")!;
                var back = guide.FindControl<Button>("GuideBack")!;
                var skip = guide.FindControl<Button>("GuideSkip")!;
                var action = guide.FindControl<Button>("GuideAction")!;
                var progress = guide.FindControl<ProgressBar>("GuideProgress")!;
                Assert.False(back.IsEnabled);
                Assert.Equal(1, progress.Value);
                Assert.False(action.IsVisible);
                Click(next);
                Assert.Equal(2, progress.Value);
                Assert.True(action.IsVisible);
                Click(action);
                var options = Assert.Single(main.OwnedWindows.OfType<OptionsWindow>());
                Settle(options);
                Assert.True(options.IsVisible);
                // The microphone step opens Options on the tab with its card.
                Assert.Equal(OptionsTab.Audio, options.SelectedTab);
                Assert.True(options.FindControl<TextBlock>("GuideAudio")!.IsEffectivelyVisible);
                var reopen = options.FindControl<Button>("WelcomeGuideButton")!;
                Click(reopen);
                Assert.Same(guide, Assert.Single(main.OwnedWindows.OfType<WelcomeGuideWindow>()));

                typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(main.DataContext, [JsonNode.Parse("""{"mixes":[],"channels":[]}""")]);
                var monitor = main.FindControl<Expander>("MonitorTile")!;
                var submixer = main.FindControl<Expander>("SubmixerTile")!;
                var applications = main.FindControl<Expander>("ApplicationsTile")!;
                monitor.IsExpanded = false;
                applications.IsExpanded = false;
                submixer.IsExpanded = false;
                Settle(main);

                Click(next);
                Click(action);
                Assert.True(monitor.IsExpanded);
                Click(next);
                Click(action);
                Assert.True(applications.IsExpanded);
                Click(next);
                Click(action);
                Assert.True(submixer.IsExpanded);
                while (progress.Value > 1) Click(back);
                Assert.Equal(1, progress.Value);
                Assert.False(back.IsEnabled);

                var discord = guide.FindControl<Button>("GuideDiscord")!;
                Assert.Equal(11, progress.Maximum);
                for (int step = 1; step <= progress.Maximum; step++)
                {
                    Settle(guide);
                    Assert.Equal(step, progress.Value);
                    Assert.Equal(step == progress.Maximum, discord.IsVisible);
                    AssertInside(next, guide);
                    AssertInside(skip, guide);
                    Capture(guide, $"welcome-guide-{step}");
                    // A step that opens Options shows the tab its card is on,
                    // whichever tab Options had on show.
                    if (OptionsSteps.TryGetValue(step, out var shortcut))
                    {
                        options.SelectedTab = shortcut.Tab == OptionsTab.Plugins ? OptionsTab.General : OptionsTab.Plugins;
                        Click(action);
                        Settle(options);
                        Assert.Equal(shortcut.Tab, options.SelectedTab);
                        Assert.True(options.FindControl<TextBlock>(shortcut.Section)!.IsEffectivelyVisible, $"step {step}");
                    }
                    if (step < progress.Maximum) Click(next);
                }

                Assert.Equal(Localizer.Text("GuideFinish"), next.Content);
                Assert.Null((UiSettings.Load() with { ShowHiddenItems = true }).Save());
                Click(next);
                Assert.False(guide.IsVisible);
                Assert.True(UiSettings.Load().WelcomeGuideCompleted);
                Assert.True(UiSettings.Load().ShowHiddenItems);
                Assert.False(UiSettings.PrepareWelcomeGuide());

                Assert.Equal(Localizer.Text("DiscordCommunity"), discord.Content);
                Click(reopen);
                var replay = Assert.Single(main.OwnedWindows.OfType<WelcomeGuideWindow>());
                Assert.NotSame(guide, replay);
                Assert.Equal(1, replay.FindControl<ProgressBar>("GuideProgress")!.Value);
                Click(replay.FindControl<Button>("GuideSkip")!);
                Assert.Empty(main.OwnedWindows.OfType<WelcomeGuideWindow>());
            }
            finally
            {
                main.Quit();
            }

            // Skipping on a first launch also suppresses the automatic prompt.
            Assert.Null((UiSettings.Load() with { WelcomeGuideCompleted = false }).Save());
            main = Start();
            try
            {
                main.ShowMixer();
                Settle(main);
                var guide = Assert.Single(main.OwnedWindows.OfType<WelcomeGuideWindow>());
                Click(guide.FindControl<Button>("GuideSkip")!);
                Assert.True(UiSettings.Load().WelcomeGuideCompleted);
                main.ShowMixer();
                Assert.Empty(main.OwnedWindows.OfType<WelcomeGuideWindow>());
            }
            finally
            {
                main.Quit();
            }
        }
        finally
        {
            Assert.Null(initial.Save());
        }
    }

    /// <summary>The guide's steps that open Options, the card each names and the tab that card is on.</summary>
    private static readonly Dictionary<int, (string Section, OptionsTab Tab)> OptionsSteps = new()
    {
        [2] = ("GuideAudio", OptionsTab.Audio),
        [6] = ("GuideDefaults", OptionsTab.Audio),
        [9] = ("GuideAppearance", OptionsTab.Appearance),
        [10] = ("GuideStartup", OptionsTab.General),
        [11] = ("GuideSupport", OptionsTab.Support),
    };

    private static MainWindow Start() => new(new DaemonClient("ws://127.0.0.1:1/ws"),
        showWelcomeGuide: UiSettings.PrepareWelcomeGuide());

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
