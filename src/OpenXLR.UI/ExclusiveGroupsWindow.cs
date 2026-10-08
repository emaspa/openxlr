using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenXLR.UI;

/// <summary>An exclusive group as the state lists it; a null id is the "new group" entry.</summary>
public sealed record ExclusiveGroupItem(string? Id, string Name, IReadOnlyList<string> Channels)
{
    public override string ToString() => Name;
}

/// <summary>
/// Create, edit and delete exclusive channel groups. Membership only: which
/// member is heard is chosen with the ordinary send mutes, and the daemon
/// mutes the other members when one opens.
/// </summary>
public sealed class ExclusiveGroupsWindow : Window
{
    public ExclusiveGroupsWindow(MainViewModel vm)
    {
        Title = "Exclusive channel groups";
        Width = 500;
        Height = 600;
        MinWidth = 360;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("dialog");

        var groups = new ComboBox
        {
            Name = "Groups", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { new ExclusiveGroupItem(null, "New group", []) }.Concat(vm.ExclusiveGroups).ToArray(),
        };
        var name = new TextBox { Name = "GroupName", MaxLength = 60, PlaceholderText = "Group name, e.g. Microphones" };
        CheckBox[] members = [.. vm.Channels.Select(ch => new CheckBox
        {
            Name = "Member_" + ch.Id, Tag = ch.Id,
            Content = new TextBlock { Text = ch.Name, TextWrapping = TextWrapping.Wrap },
        })];
        var choices = new StackPanel { Spacing = 4 };
        foreach (CheckBox member in members) choices.Children.Add(member);
        var error = new TextBlock { Name = "GroupError", TextWrapping = TextWrapping.Wrap, Classes = { "hint" } };
        var save = new Button { Name = "SaveGroup", Content = "Save group", IsDefault = true };
        var delete = new Button { Name = "DeleteGroup", Content = "Delete group" };
        var close = new Button { Content = "Close", IsCancel = true };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (Button button in new[] { save, delete, close })
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            buttons.Children.Add(button);
        }
        var form = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Only one channel of a group is heard. Unmuting a member in any mix mutes the other members in every mix; their levels stay where they are.", TextWrapping = TextWrapping.Wrap, Classes = { "hint" } },
                groups, name, choices,
                new TextBlock { Text = "Choose at least two channels. A channel belongs to one group at most. If more than one member is unmuted when you save, every member is muted and you unmute the one you want. Deleting a group leaves the mutes as they are.", TextWrapping = TextWrapping.Wrap, Classes = { "hint" } },
                new TextBlock { Text = "On a Wave XLR Pro, XLR 1 in a group reaches the headphone jacks through the mixer instead of the interface's zero-latency path.", TextWrapping = TextWrapping.Wrap, Classes = { "hint" } },
                error, buttons,
            },
        };
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
            if (remove && group.Id is null) return;
            string[] selected = [.. members.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!)];
            if (!remove && (string.IsNullOrWhiteSpace(name.Text) || selected.Length < 2))
            {
                error.Text = "Enter a name and choose at least two channels.";
                return;
            }
            pending = true;
            form.IsEnabled = false;
            try
            {
                string? failure = remove ? await vm.DeleteExclusiveGroup(group.Id!)
                    : await vm.SetExclusiveGroup(group.Id, name.Text!.Trim(), selected);
                if (failure is null) Close();
                else error.Text = failure;
            }
            finally
            {
                pending = false;
                form.IsEnabled = true;
            }
        }
        save.Click += async (_, _) => await Submit(remove: false);
        delete.Click += async (_, _) => await Submit(remove: true);
        close.Click += (_, _) => Close();
    }
}
