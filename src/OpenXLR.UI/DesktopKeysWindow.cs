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
        Title = "OpenXLR Desktop keys";
        Width = 540; Height = 540; MinWidth = 360; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("dialog");
        DesktopKeySettings saved = DesktopKeySettings.Load();
        var enabled = new CheckBox { Content = "Enable desktop integration", IsChecked = saved.Enabled };
        var choices = vm.Channels.Where(c => c.IsApplication).Select(c => (c.Id,
            Box: new CheckBox { Content = c.Name, IsChecked = saved.FocusChannels.Contains(c.Id) })).ToArray();
        var outputControls = new CheckBox { Content = "Add volume and mute keys", IsChecked = saved.OutputControls };
        var outputs = vm.Outputs.Where(d => DesktopKeySettings.ValidOutput(d.Name)
            && (!d.IsOwn || vm.Mixes.Any(m => m.IsMonitor && "OpenXLR_mix_" + m.Id == d.Name))).ToList();
        foreach (string missing in saved.MainOutputs.Append(saved.OutputDevice ?? "").Where(n => n != "@monitor" && DesktopKeySettings.ValidOutput(n)).Distinct())
            if (outputs.All(d => d.Name != missing)) outputs.Add(new AudioDeviceItem(missing, missing + " (unavailable)", false));
        var outputChoices = new[] { new AudioDeviceItem("", "Current system default output", false) }.Concat(outputs).ToArray();
        var outputDevice = new ComboBox { ItemsSource = outputChoices,
            ItemTemplate = new FuncDataTemplate<AudioDeviceItem>((item, _) => new TextBlock
                { Text = item?.Label, TextTrimming = TextTrimming.CharacterEllipsis }),
            SelectedItem = outputChoices.FirstOrDefault(d => d.Name == (saved.OutputDevice ?? "")),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        var mainChoices = new[] { new AudioDeviceItem("@monitor", "Follow selected monitor output", false) }.Concat(outputs)
            .Select(d => (d.Name, Box: new CheckBox { Content = new TextBlock { Text = "Switch system output: " + d.Label, TextWrapping = TextWrapping.Wrap },
                IsChecked = saved.MainOutputs.Contains(d.Name) })).ToArray();
        var status = new TextBlock { Text = keys.Status, TextWrapping = TextWrapping.Wrap, Classes = { "hint" } };
        var apply = new Button { Content = "Apply and configure keys", IsDefault = true };
        var close = new Button { Content = "Close", IsCancel = true };
        var manual = new Button { Content = "Manual" };
        ToolTip.SetTip(manual, "Open the manual section on desktop keys");
        var content = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        content.Children.Add(enabled);
        content.Children.Add(new TextBlock
        {
            Text = "Focused application routing uses KDE Plasma. Enable it for OpenDeck keys, then select channels below to assign PC shortcuts through the desktop's permission dialog. Keep OpenXLR running, including in the tray.",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var choice in choices) content.Children.Add(choice.Box);
        content.Children.Add(outputControls);
        content.Children.Add(outputDevice);
        content.Children.Add(new TextBlock { Text = "Volume keys move by 5%, up to 150%. System-output keys change the enforced desktop default; mixer feeds keep their own selections.", TextWrapping = TextWrapping.Wrap });
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
