using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Media;

namespace OpenXLR.UI;

/// <summary>
/// A channel's or mix's icon, colour and hidden flag, as the daemon publishes
/// them. The colour is the user's own choice for this one item, so it is a
/// brush made here rather than a skin token; without one the item keeps the
/// skin's colours.
/// </summary>
public sealed class LayoutAppearanceViewModel : ViewModelBase
{
    /// <summary>The icons the daemon accepts, with "no icon" first.</summary>
    public static string[] Icons { get; } = ["", "●", "♪", "♫", "✦", "◆", "▶", "◉"];

    private string _icon = "";
    public string Icon { get => _icon; private set { if (Set(ref _icon, value)) Raise(nameof(HasIcon)); } }
    public bool HasIcon => _icon.Length > 0;

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
    public Task<string?> SetLayoutAppearance(string id, bool mix, string icon, string? colour, bool hidden)
        => Edit(_client.SetLayoutAppearanceAsync(id, mix, icon, colour, hidden));

    /// <summary>
    /// Which channel strips the mixer draws: those the device can feed,
    /// less the ones hidden in the layout editor. A hidden channel keeps
    /// its sends, its meter and its applications.
    /// </summary>
    private void RefreshChannelPresentation()
    {
        foreach (ChannelViewModel channel in Channels)
            channel.DisplayVisible = channel.Visible && !channel.Appearance.Hidden;
    }
}
