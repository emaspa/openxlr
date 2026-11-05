using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>A modeless guide so the mixer stays usable while the user follows a step.</summary>
public partial class WelcomeGuideWindow : Window
{
    private const string Manual = "https://github.com/emaspa/openxlr/blob/main/docs/manual.md#";

    private sealed record Page(string Title, string Body, string ManualAnchor, string? ActionTitle = null, Action<MainWindow>? Open = null);

    private readonly Page[] _pages;
    private int _step;

    public WelcomeGuideWindow()
    {
        InitializeComponent();
        _pages =
        [
            new(Localizer.Text("GuideWelcomeTitle"), Localizer.Text("GuideWelcomeBody"), "first-run"),
            new(Localizer.Text("GuideMicTitle"), Localizer.Text("GuideMicBody"), "input-bindings",
                Localizer.Text("GuideOpenAudio"), owner => owner.OpenOptions().ShowGuideSection("GuideAudio")),
            new(Localizer.Text("GuideMonitorTitle"), Localizer.Text("GuideMonitorBody"), "monitor",
                Localizer.Text("GuideOpenMonitor"), owner => owner.ShowGuideMonitor()),
            new(Localizer.Text("GuideMixerTitle"), Localizer.Text("GuideMixerBody"), "concepts",
                Localizer.Text("GuideOpenMixer"), owner => owner.ShowGuideMixer()),
            new(Localizer.Text("GuideVirtualTitle"), Localizer.Text("GuideVirtualBody"), "default-devices",
                Localizer.Text("GuideOpenDefaults"), owner => owner.OpenOptions().ShowGuideSection("GuideDefaults")),
            new(Localizer.Text("GuideEffectsTitle"), Localizer.Text("GuideEffectsBody"), "plugins"),
            new(Localizer.Text("GuideAppearanceTitle"), Localizer.Text("GuideAppearanceBody"), "skins",
                Localizer.Text("GuideOpenAppearance"), owner => owner.OpenOptions().ShowGuideSection("GuideAppearance")),
            new(Localizer.Text("GuideHelpTitle"), Localizer.Text("GuideHelpBody"), "troubleshooting",
                Localizer.Text("GuideOpenSupport"), owner => owner.OpenOptions().ShowGuideSection("GuideSupport")),
        ];
        GuideProgress.Maximum = _pages.Length;
        ShowStep();
    }

    internal WelcomeGuideWindow(MainWindow owner) : this()
    {
        if (owner.Screens.ScreenFromWindow(owner) is { } screen)
        {
            Size size = FlowWindow.FitClientSize(new(Width, Height), screen.WorkingArea.Size, screen.Scaling, new(32, 48));
            MinWidth = Math.Min(MinWidth, size.Width);
            MinHeight = Math.Min(MinHeight, size.Height);
            Width = size.Width;
            Height = size.Height;
        }
    }

    private Page Current => _pages[_step];

    private void ShowStep()
    {
        GuideProgressText.Text = Localizer.Format("GuideStep", _step + 1, _pages.Length);
        GuideProgress.Value = _step + 1;
        GuideHeading.Text = Current.Title;
        GuideBody.Text = Current.Body;
        GuideAction.IsVisible = Current.Open is not null;
        GuideActionText.Text = Current.ActionTitle;
        GuideBack.IsEnabled = _step > 0;
        GuideNext.Content = _step == _pages.Length - 1 ? Localizer.Text("GuideFinish") : Localizer.Text("GuideNext");
        GuideScroll.Offset = default;
    }

    private void OnBack(object? sender, RoutedEventArgs e)
    {
        if (_step == 0) return;
        _step--;
        ShowStep();
    }

    private void OnNext(object? sender, RoutedEventArgs e)
    {
        if (_step == _pages.Length - 1) { Close(); return; }
        _step++;
        ShowStep();
    }

    private void OnAction(object? sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow owner) Current.Open?.Invoke(owner);
    }
    private void OnManual(object? sender, RoutedEventArgs e) => ExternalLink.Open(Manual + Current.ManualAnchor);
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
