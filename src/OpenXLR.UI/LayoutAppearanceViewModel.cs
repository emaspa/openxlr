using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Media;

namespace OpenXLR.UI;

public sealed class LayoutAppearanceViewModel : ViewModelBase
{
    public static string[] Icons { get; } = ["", "●", "♪", "♫", "✦", "◆", "▶", "◉"];
    private string _icon = "";
    public string Icon { get => _icon; private set => Set(ref _icon, value); }
    private string? _colour;
    public string? Colour => _colour;
    private IBrush? _accent;
    public IBrush? Accent => _accent;
    public bool HasColour => _accent is not null;
    private bool _hidden;
    public bool Hidden { get => _hidden; private set => Set(ref _hidden, value); }

    public void Apply(JsonNode? value)
    {
        Icon = value?["icon"]?.GetValue<string>() ?? "";
        Hidden = value?["hidden"]?.GetValue<bool>() ?? false;
        string? colour = value?["colour"]?.GetValue<string>();
        if (_colour == colour) return;
        _colour = colour;
        _accent = Color.TryParse(colour, out Color parsed) ? new SolidColorBrush(parsed) : null;
        Raise(nameof(Colour));
        Raise(nameof(Accent));
        Raise(nameof(HasColour));
    }
}

public sealed partial class MainViewModel
{
    private bool _compactMixer = UiSettings.Load().CompactMixer;
    public bool CompactMixer
    {
        get => _compactMixer;
        set
        {
            if (!Set(ref _compactMixer, value)) return;
            (UiSettings.Load() with { CompactMixer = value }).Save();
            RefreshChannelPresentation();
        }
    }
    private ChannelViewModel? _selectedCompactChannel;
    private string? _compactChannelId = UiSettings.Load().CompactChannel;
    public ChannelViewModel? SelectedCompactChannel
    {
        get => _selectedCompactChannel;
        set
        {
            if (_applying || !Set(ref _selectedCompactChannel, value)) return;
            _compactChannelId = value?.Id;
            (UiSettings.Load() with { CompactChannel = _compactChannelId }).Save();
            RefreshChannelPresentation();
        }
    }

    private void RefreshChannelPresentation()
    {
        var selected = Channels.FirstOrDefault(c => c.Id == _compactChannelId && c.Visible) ?? Channels.FirstOrDefault(c => c.Visible && !c.Appearance.Hidden)
            ?? Channels.FirstOrDefault(c => c.Visible);
        // Device changes and removed channels must not persist an automatic fallback over the user's choice.
        if (!ReferenceEquals(selected, _selectedCompactChannel))
        {
            _selectedCompactChannel = selected;
            Raise(nameof(SelectedCompactChannel));
        }
        foreach (var channel in Channels)
            channel.DisplayVisible = channel.Visible && (CompactMixer ? ReferenceEquals(channel, selected) : !channel.Appearance.Hidden);
    }

    public Task<string?> SetLayoutAppearance(string id, bool mix, string icon, string? colour, bool hidden)
        => Edit(_client.SetLayoutAppearanceAsync(id, mix, icon, colour, hidden));
}
