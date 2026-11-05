using Avalonia.Controls;
using Avalonia.Interactivity;
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
            Assert.True(main.StartsHidden);
            Assert.Empty(main.OwnedWindows);
            main.ShowMixer();
            Settle(main);
            Assert.Single(main.OwnedWindows.OfType<WelcomeGuideWindow>());
            main.Quit();
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
                var reopen = options.FindControl<Button>("WelcomeGuideButton")!;
                Click(reopen);
                Assert.Same(guide, Assert.Single(main.OwnedWindows.OfType<WelcomeGuideWindow>()));
                Click(back);
                Assert.Equal(1, progress.Value);
                Assert.False(back.IsEnabled);

                for (int step = 1; step <= progress.Maximum; step++)
                {
                    Settle(guide);
                    Assert.Equal(step, progress.Value);
                    AssertInside(next, guide);
                    AssertInside(skip, guide);
                    Capture(guide, $"welcome-guide-{step}");
                    if (step < progress.Maximum) Click(next);
                }

                Assert.Equal(Localizer.Text("GuideFinish"), next.Content);
                Assert.Null((UiSettings.Load() with { ShowHiddenItems = true }).Save());
                Click(next);
                Assert.False(guide.IsVisible);
                Assert.True(UiSettings.Load().WelcomeGuideCompleted);
                Assert.True(UiSettings.Load().ShowHiddenItems);
                Assert.False(UiSettings.PrepareWelcomeGuide());

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

    private static MainWindow Start() => new(new DaemonClient("ws://127.0.0.1:1/ws"),
        showWelcomeGuide: UiSettings.PrepareWelcomeGuide());

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
