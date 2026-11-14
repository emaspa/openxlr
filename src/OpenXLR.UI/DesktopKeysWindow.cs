using OpenXLR.UI.Localization;
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenXLR.UI;

internal sealed class DesktopKeysWindow : Window
{
    private const string Manual = "https://github.com/emaspa/openxlr/blob/main/docs/manual.md#desktop-keys";

    internal DesktopKeysWindow(DesktopKeys keys, MainViewModel vm)
    {
        Title = Localizer.Text("DesktopKeysTitle");
        Width = 540; Height = 540; MinWidth = 360; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("dialog");
        DesktopKeySettings saved = DesktopKeySettings.Load();
        var enabled = new CheckBox { Content = Localizer.Text("EnableDesktopIntegration"), IsChecked = saved.Enabled };
        var choices = vm.Channels.Where(c => c.IsApplication).Select(c => (c.Id,
            Box: new CheckBox { Content = c.Name, IsChecked = saved.FocusChannels.Contains(c.Id) })).ToArray();
        // One key per clip the user ticks, named after the clip. A ticked
        // clip the daemon does not report now keeps its tick unless it was
        // seen deleted, so an Apply while the daemon is down or cannot read
        // the library drops no binding.
        SoundboardViewModel board = vm.Soundboard;
        var clipChoices = keys.ClipChoices(saved, board.Clips.Select(c => (c.Id, c.Name))).Select(c => (c.Id, Box: new CheckBox
            { Content = new TextBlock { Text = c.Label, TextWrapping = TextWrapping.Wrap }, IsChecked = c.Ticked })).ToList();
        var clipsStop = new CheckBox { Content = Localizer.Text("AddStopClipsKey"), IsChecked = saved.ClipsStop };
        bool showsClips = board.IsReported || clipChoices.Count > 0 || saved.ClipsStop;
        // The microphone strips: XLR 1 and XLR 2 where the unit has them, and
        // every capture input, an additional interface's included. A covered
        // one the mixer does not show now stays ticked, as a clip does.
        var micChoices = keys.MicrophoneChoices(saved, vm.Channels
            .Where(c => c.IsHardware ? c.Id is "xlr1" or "xlr2" && c.Present : c.CaptureSource is not null)
            .Select(c => (c.Id, c.Name))).Select(c => (c.Id, Box: new CheckBox
            { Content = new TextBlock { Text = c.Label, TextWrapping = TextWrapping.Wrap }, IsChecked = c.Ticked })).ToList();
        var micMute = new CheckBox { Content = new TextBlock { Text = Localizer.Text("AddMicMuteKey"), TextWrapping = TextWrapping.Wrap },
            IsChecked = saved.MicMute };
        // Push to talk is offered where the desktop reports keys coming up;
        // one already set up stays, so it can be turned off.
        bool talkOffered = keys.DeliversRelease || saved.PushToTalk;
        var pushToTalk = new CheckBox { Content = new TextBlock { Text = Localizer.Text("AddPushToTalkKey"), TextWrapping = TextWrapping.Wrap },
            IsChecked = saved.PushToTalk, IsEnabled = talkOffered };
        var outputControls = new CheckBox { Content = Localizer.Text("AddVolumeAndMuteKeys"), IsChecked = saved.OutputControls };
        var monitorControls = new CheckBox { Content = Localizer.Text("AddMonitoringPauseKey"), IsChecked = saved.MonitorControls };
        var outputs = vm.Outputs.Where(d => DesktopKeySettings.ValidOutput(d.Name)
            && (!d.IsOwn || vm.Mixes.Any(m => m.IsMonitor && "OpenXLR_mix_" + m.Id == d.Name))).ToList();
        foreach (string missing in saved.MainOutputs.Append(saved.OutputDevice ?? "").Where(n => n != "@monitor" && DesktopKeySettings.ValidOutput(n)).Distinct())
            if (outputs.All(d => d.Name != missing)) outputs.Add(new AudioDeviceItem(missing, Localizer.Format("OutputUnavailable", missing), false));
        var outputChoices = new[] { new AudioDeviceItem("", Localizer.Text("CurrentSystemDefaultOutput"), false) }.Concat(outputs).ToArray();
        var outputDevice = new ComboBox { ItemsSource = outputChoices,
            ItemTemplate = new FuncDataTemplate<AudioDeviceItem>((item, _) => new TextBlock
                { Text = item?.Label, TextTrimming = TextTrimming.CharacterEllipsis }),
            SelectedItem = outputChoices.FirstOrDefault(d => d.Name == (saved.OutputDevice ?? "")),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        var mainChoices = new[] { new AudioDeviceItem("@monitor", Localizer.Text("FollowSelectedMonitorOutput"), false) }.Concat(outputs)
            .Select(d => (d.Name, Box: new CheckBox { Content = new TextBlock { Text = Localizer.Format("SwitchSystemOutput", d.Label), TextWrapping = TextWrapping.Wrap },
                IsChecked = saved.MainOutputs.Contains(d.Name) })).ToArray();
        var status = new TextBlock { Text = keys.Status, TextWrapping = TextWrapping.Wrap, Classes = { "hint" } };
        var apply = new Button { Content = Localizer.Text("ApplyAndConfigureKeys"), IsDefault = true };
        var close = new Button { Content = Localizer.Text("Close"), IsCancel = true };
        var manual = new Button { Content = Localizer.Text("Manual") };
        ToolTip.SetTip(manual, Localizer.Text("OpenManualDesktopKeys"));
        var content = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        content.Children.Add(enabled);
        content.Children.Add(new TextBlock
        {
            Text = Localizer.Text("DesktopKeysIntro"),
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var choice in choices) content.Children.Add(choice.Box);
        if (showsClips)
        {
            foreach (var choice in clipChoices) content.Children.Add(choice.Box);
            content.Children.Add(clipsStop);
            content.Children.Add(new TextBlock { Text = Localizer.Text("SoundboardKeysNote"), TextWrapping = TextWrapping.Wrap });
        }
        content.Children.Add(micMute);
        content.Children.Add(pushToTalk);
        content.Children.Add(new TextBlock { Text = keys.DeliversRelease ? Localizer.Text("PushToTalkNote") : Localizer.Text("PushToTalkUnavailableNote"),
            TextWrapping = TextWrapping.Wrap });
        foreach (var choice in micChoices) content.Children.Add(choice.Box);
        content.Children.Add(new TextBlock { Text = Localizer.Text("MicKeysNote"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(monitorControls);
        content.Children.Add(new TextBlock { Text = Localizer.Text("MonitoringPauseKeysNote"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(outputControls);
        content.Children.Add(outputDevice);
        content.Children.Add(new TextBlock { Text = Localizer.Text("DesktopKeysVolumeNote"), TextWrapping = TextWrapping.Wrap });
        foreach (var choice in mainChoices) content.Children.Add(choice.Box);
        content.Children.Add(status);
        content.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { apply, close, manual } });
        Content = new ScrollViewer { Content = content };
        void Update() => Dispatcher.UIThread.Post(() => status.Text = keys.Status);
        keys.Changed += Update;
        Closed += (_, _) => keys.Changed -= Update;
        close.Click += (_, _) => Close();
        manual.Click += (_, _) => ExternalLink.Open(Manual);
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try
            {
                await keys.ConfigureAsync(new DesktopKeySettings { Enabled = enabled.IsChecked == true,
                    FocusChannels = choices.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToList(),
                    MonitorControls = monitorControls.IsChecked == true,
                    OutputControls = outputControls.IsChecked == true,
                    OutputDevice = outputDevice.SelectedItem is AudioDeviceItem { Name.Length: > 0 } selected ? selected.Name : null,
                    MainOutputs = mainChoices.Where(c => c.Box.IsChecked == true).Select(c => c.Name).ToList(),
                    Clips = clipChoices.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToList(),
                    ClipsStop = clipsStop.IsChecked == true,
                    MicMute = micMute.IsChecked == true,
                    PushToTalk = pushToTalk.IsChecked == true,
                    Microphones = micChoices.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToList() });
            }
            finally { apply.IsEnabled = true; }
        };
    }
}
