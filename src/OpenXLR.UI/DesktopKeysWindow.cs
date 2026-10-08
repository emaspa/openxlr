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
        var outputControls = new CheckBox { Content = Localizer.Text("AddVolumeAndMuteKeys"), IsChecked = saved.OutputControls };
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
                    OutputControls = outputControls.IsChecked == true,
                    OutputDevice = outputDevice.SelectedItem is AudioDeviceItem { Name.Length: > 0 } selected ? selected.Name : null,
                    MainOutputs = mainChoices.Where(c => c.Box.IsChecked == true).Select(c => c.Name).ToList() });
            }
            finally { apply.IsEnabled = true; }
        };
    }
}
