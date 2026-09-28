using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenXLR.UI;

public sealed record ExclusiveGroupItem(string? Id, string Name, IReadOnlyList<string> Channels)
{
    public override string ToString() => Name;
}

/// <summary>Edit membership; the ordinary send mute buttons select a member per mix.</summary>
public sealed class ExclusiveGroupsWindow : Window
{
    public ExclusiveGroupsWindow(MainViewModel vm)
    {
        Title = "Exclusive channel groups";
        Width = 500;
        Height = 620;
        MinWidth = 360;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("dialog");
        var groups = new ComboBox { Name = "Groups", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { new ExclusiveGroupItem(null, "New group", []) }.Concat(vm.ExclusiveGroups).ToArray() };
        var name = new TextBox { Name = "GroupName", MaxLength = 60, PlaceholderText = "Group name" };
        var members = vm.Channels.Select(ch => new CheckBox { Name = "Member_" + ch.Id, Tag = ch.Id, Content = new TextBlock { Text = ch.Name, TextWrapping = TextWrapping.Wrap } }).ToArray();
        var choices = new StackPanel { Spacing = 4 };
        foreach (CheckBox member in members) choices.Children.Add(member);
        var error = new TextBlock { Name = "GroupError", TextWrapping = TextWrapping.Wrap, Classes = { "hint" } };
        var save = new Button { Name = "SaveGroup", Content = "Save group", IsDefault = true };
        var delete = new Button { Name = "DeleteGroup", Content = "Delete group" };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (Button button in new[] { save, delete, cancel }) { button.Margin = new Thickness(0, 0, 8, 8); buttons.Children.Add(button); }
        var form = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children =
        {
            new TextBlock { Text = "Opening one member's send closes the others in that mix. Other mixes and send levels stay unchanged. Use the send mute buttons in the submixer to choose a member.", TextWrapping = TextWrapping.Wrap, Classes = { "hint" } },
            groups, name, choices,
            new TextBlock { Text = "Choose at least two channels. Each channel belongs to at most one group. If several sends are open when you save, all members close in that mix. Deleting a group keeps its current mutes.", TextWrapping = TextWrapping.Wrap, Classes = { "hint" } },
            error, buttons,
        } };
        Content = new ScrollViewer { Content = form };
        groups.SelectionChanged += (_, _) =>
        {
            if (groups.SelectedItem is not ExclusiveGroupItem group) return;
            name.Text = group.Id is null ? "" : group.Name;
            foreach (CheckBox member in members) member.IsChecked = group.Channels.Contains((string)member.Tag!);
            delete.IsEnabled = group.Id is not null;
            error.Text = "";
        };
        groups.SelectedIndex = 0;
        bool pending = false;
        async Task Submit(bool remove)
        {
            if (pending || groups.SelectedItem is not ExclusiveGroupItem group) return;
            string[] selected = members.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToArray();
            if (!remove && (string.IsNullOrWhiteSpace(name.Text) || selected.Length < 2))
            { error.Text = "Enter a name and choose at least two channels."; return; }
            if (remove && group.Id is null) return;
            pending = true;
            form.IsEnabled = false;
            try
            {
                string? failure = remove ? await vm.DeleteExclusiveGroup(group.Id!)
                    : await vm.SetExclusiveGroup(group.Id, name.Text!.Trim(), selected);
                if (failure is null) Close();
                else error.Text = failure;
            }
            finally { pending = false; form.IsEnabled = true; }
        }
        save.Click += async (_, _) => await Submit(false);
        delete.Click += async (_, _) => await Submit(true);
        cancel.Click += (_, _) => Close();
    }
}
