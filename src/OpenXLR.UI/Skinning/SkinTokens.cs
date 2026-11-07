using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OpenXLR.UI.Skinning;

/// <summary>What a token holds, which decides how its JSON value is read.</summary>
public enum SkinTokenKind
{
    /// <summary>A colour, a gradient or a local image, realized as an Avalonia brush.</summary>
    Brush,
    /// <summary>A bounded number: a size, a height, a weight of pixels.</summary>
    Number,
    CornerRadius,
    Thickness,
    FontFamily,
    FontWeight,
}

/// <summary>
/// A Fluent theme resource fed from a token. Fluent keys are not all brushes:
/// a handful (the expander header, the scroll bar thumb, the system accent)
/// are typed <c>Color</c>, and writing a brush into one of those is silently
/// ignored by the theme. The kind here is what the theme expects.
/// </summary>
/// <param name="Key">The Fluent resource key.</param>
/// <param name="AsColor">True when the theme reads the key as a <c>Color</c>.</param>
public readonly record struct SkinBridge(string Key, bool AsColor = false);

/// <summary>
/// One semantic appearance value. <see cref="Default"/> null marks a token
/// that has no value of its own: it is written only while a skin supplies it,
/// and removed again when one does not, so the untouched application keeps
/// exactly the appearance it had before skins existed.
/// </summary>
public sealed record SkinToken(
    string Name,
    SkinTokenKind Kind,
    object? Default = null,
    IReadOnlyList<SkinBridge>? Bridges = null,
    bool SolidOnly = false,
    double Minimum = 0,
    double Maximum = 0)
{
    public IReadOnlyList<SkinBridge> Bridges { get; init; } = Bridges ?? [];
}

/// <summary>
/// The complete set of semantic <c>Ox.*</c> resources, their default values
/// and the Fluent keys each one feeds. This is the single place the default
/// appearance is written down: the views reference token names only, and a
/// skin overlays values on top of these.
/// </summary>
public static class SkinTokens
{
    // A default is data, and this table is built by the class initializer on
    // whichever thread first reads it. A SolidColorBrush is an AvaloniaObject,
    // and reading its colour is allowed on the UI thread only, so a mutable
    // brush here would make the defaults unreadable from anywhere else.
    private static IBrush Solid(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    private static SkinToken Brush(string name, string? hex, params SkinBridge[] bridges) =>
        new(name, SkinTokenKind.Brush, hex is null ? null : Solid(hex), bridges);

    private static SkinToken SolidBrush(string name, string hex, params SkinBridge[] bridges) =>
        new(name, SkinTokenKind.Brush, Solid(hex), bridges, SolidOnly: true);

    private static SkinToken Number(string name, double? value, double min, double max, params SkinBridge[] bridges) =>
        new(name, SkinTokenKind.Number, value, bridges, Minimum: min, Maximum: max);

    private static SkinToken Radius(string name, double? value, params SkinBridge[] bridges) =>
        new(name, SkinTokenKind.CornerRadius, value is null ? null : new CornerRadius(value.Value), bridges,
            Minimum: 0, Maximum: MaxCornerRadius);

    private static SkinToken Thick(string name, double? value, params SkinBridge[] bridges) =>
        new(name, SkinTokenKind.Thickness, value is null ? null : new Thickness(value.Value), bridges,
            Minimum: 0, Maximum: MaxThickness);

    private static SkinToken Weight(string name, FontWeight? value) =>
        new(name, SkinTokenKind.FontWeight, value);

    private static SkinToken Family(string name, string? value, params SkinBridge[] bridges) =>
        new(name, SkinTokenKind.FontFamily, value is null ? null : new FontFamily(value), bridges);

    /// <summary>The smallest target Touch sizing gives a mixer control, in device-independent units.</summary>
    public const double TouchTarget = 44;

    /// <summary>
    /// The floors Touch sizing puts under the mixer sizing tokens. A skin value
    /// above a floor is kept; Standard sizing applies no floor at all.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> TouchMinimums = new Dictionary<string, double>
    {
        ["Ox.Mixer.ControlMinSize"] = TouchTarget,
        ["Ox.Mixer.SliderMinHeight"] = TouchTarget,
        ["Ox.Mixer.DeviceSliderHeight"] = TouchTarget,
        ["Ox.Mixer.SmallControlMinSize"] = TouchTarget,
        ["Ox.Mixer.InsertControlMinSize"] = TouchTarget,
        ["Ox.Mixer.ChannelWidth"] = 180,
        ["Ox.Mixer.MixWidth"] = 280,
        ["Ox.Mixer.StripWidth"] = 96,
        ["Ox.Matrix.NameWidth"] = 132,
        ["Ox.Matrix.CellWidth"] = 200,
        ["Ox.Matrix.RowHeight"] = 52,
        ["Ox.Soundboard.PadWidth"] = 176,
        ["Ox.Soundboard.PadHeight"] = 120,
    };

    /// <summary>Corner radii and border widths are bounded so a skin cannot swallow a control.</summary>
    public const double MaxCornerRadius = 48;
    public const double MaxThickness = 16;

    /// <summary>Every token, in the order the documentation lists them.</summary>
    public static readonly IReadOnlyList<SkinToken> All =
    [
        // Surfaces.
        Brush("Ox.Window.Background", "#16181d"),
        // Read only while the skin's window section asks for the rounded
        // outline and the platform can draw it (OpenXLR.UI.WindowFrame).
        Radius("Ox.Window.CornerRadius", 12),
        Brush("Ox.Window.BorderBrush", "#00000000"),
        Thick("Ox.Window.BorderThickness", 0),
        Brush("Ox.Card.Background", "#1d2027"),
        Brush("Ox.Card.BorderBrush", "#00000000"),
        Thick("Ox.Card.BorderThickness", 0),
        Radius("Ox.Card.CornerRadius", 8),
        Brush("Ox.Tile.Background", "#23262f"),
        Brush("Ox.Tile.BorderBrush", "#00000000"),
        Thick("Ox.Tile.BorderThickness", 0),
        Radius("Ox.Tile.CornerRadius", 6),
        Brush("Ox.Dialog.Background", "#1d2027"),
        Brush("Ox.Danger.Background", "#a03434"),
        Brush("Ox.Danger.Foreground", "#f6dede"),
        // The ring of that button. Unset, it keeps the ordinary button's ring.
        Brush("Ox.Danger.BorderBrush", null),

        // Text.
        Brush("Ox.Text.Primary", "#e6e9f0"),
        Brush("Ox.Text.Secondary", "#8b93a7"),
        Brush("Ox.Text.Muted", "#6b7285"),
        Brush("Ox.Text.Warning", "#e0a05a"),
        Brush("Ox.Text.Alert", "#e0a030"),
        Brush("Ox.Text.Info", "#5ba8f5"),
        // The lighter grey of a paragraph: the About text, a plugin group
        // heading, the release notes.
        Brush("Ox.Text.Detail", "#b9bfcc"),
        Brush("Ox.Link.Foreground", "#5ba8f5"),
        Brush("Ox.Link.ForegroundPointerOver", "#8cc4ff"),
        Brush("Ox.Badge.Background", "#2a2e38"),
        Brush("Ox.Divider", "#2d313c"),

        // Typography.
        Number("Ox.Label.FontSize", 12, 8, 24),
        Weight("Ox.Label.FontWeight", FontWeight.SemiBold),
        Number("Ox.Title.FontSize", 20, 10, 40),
        Weight("Ox.Title.FontWeight", FontWeight.Bold),
        Brush("Ox.Title.Foreground", null),
        Number("Ox.Title.Glow", 0, 0, 1),
        Weight("Ox.Strip.FontWeight", FontWeight.SemiBold),
        Brush("Ox.Strip.Foreground", null),
        Brush("Ox.Strip.IconMuted", null),
        Number("Ox.Strip.Glow", 0, 0, 1),
        Family("Ox.Value.FontFamily", "monospace"),
        Brush("Ox.Value.Foreground", null),
        Family("Ox.Font.Family", null, new SkinBridge("ContentControlThemeFontFamily")),

        // Indicators. These reach the views through value converters as well as
        // through resources, so they are held as one live brush each and must
        // stay solid colours; see SkinService.
        SolidBrush("Ox.Led.On", "#3ecf7a"),
        SolidBrush("Ox.Led.Off", "#4a4f5c"),
        SolidBrush("Ox.Led.Alert", "#ff3c4e"),
        // A meter is coloured by where each part of it sits on the scale, not
        // by how loud the whole bar is. The three colours default to the same
        // green, so the shipped appearance shows no zones at all.
        SolidBrush("Ox.Meter.Fill", "#3ecf7a"),
        SolidBrush("Ox.Meter.Warning", "#3ecf7a"),
        SolidBrush("Ox.Meter.Hot", "#3ecf7a"),
        // The meter reads 0 at -60 dBFS RMS and 1 at 0 dBFS (MeterReader), so
        // these are -18 dBFS, the usual alignment level, and -6 dBFS, where an
        // RMS reading this high means the peaks have run out of headroom.
        Number("Ox.Meter.WarningLevel", (-18.0 + 60) / 60, 0, 1),
        Number("Ox.Meter.HotLevel", (-6.0 + 60) / 60, 0, 1),
        Brush("Ox.Meter.Track", "#14161b"),
        Radius("Ox.Meter.CornerRadius", 2),
        Number("Ox.Meter.Height", 4, 2, 24),
        // An upright meter's thickness, where a strip template stands its meter
        // up; its length is the length of what it stands beside.
        Number("Ox.Meter.VerticalWidth", 4, 2, 24),
        // Read by the segmented meter only; the continuous one ignores them.
        Number("Ox.Meter.Segments", 16, 2, 64),
        Number("Ox.Meter.SegmentGap", 2, 0, 8),
        Number("Ox.Meter.Glow", 0, 0, 1),
        // Read by the lamp indicator only. The defaults draw no ring and no
        // halo, which is the flat dot the window has always shown.
        Number("Ox.Led.Size", 9, 4, 40),
        Brush("Ox.Led.Bezel", "#00000000"),
        Number("Ox.Led.BezelThickness", 0, 0, 6),
        Number("Ox.Led.Glow", 0, 0, 1),
        // How much of the lamp's box the lit core fills. The rest is the bezel
        // and the halo, which is why the lamp never draws outside its box.
        Number("Ox.Led.CoreScale", 1, 0.1, 1),

        // Mute buttons: green while audio passes, red when muted.
        Brush("Ox.Mute.Background", "#1e5c3a"),
        Brush("Ox.Mute.Foreground", "#d9efe2"),
        Brush("Ox.Mute.BackgroundPointerOver", "#27754b"),
        Brush("Ox.Mute.BackgroundChecked", "#a03434"),
        Brush("Ox.Mute.ForegroundChecked", "#f6dede"),
        Brush("Ox.Mute.BorderBrush", "#00000000"),
        // The ring while muted. Unset, the ring stays Ox.Mute.BorderBrush.
        Brush("Ox.Mute.BorderBrushChecked", null),
        Thick("Ox.Mute.BorderThickness", 0),


        // Faders. The window has always sized the Fluent thumb through these
        // three theme keys; the rest is applied only when a skin asks for it,
        // so an unskinned slider is the stock Fluent one.
        Number("Ox.Main.FaderMaxWidth", null, 150, 1600),
        Number("Ox.Fader.Thumb.Width", 22, 4, 64,
            new SkinBridge("SliderHorizontalThumbWidth"), new SkinBridge("SliderVerticalThumbHeight")),
        Number("Ox.Fader.Thumb.Height", 10, 4, 64,
            new SkinBridge("SliderHorizontalThumbHeight"), new SkinBridge("SliderVerticalThumbWidth")),
        Radius("Ox.Fader.Thumb.CornerRadius", 2, new SkinBridge("SliderThumbCornerRadius")),
        Brush("Ox.Fader.Thumb.Background", null,
            new SkinBridge("SliderThumbBackground"),
            new SkinBridge("SliderThumbBackgroundPointerOver"),
            new SkinBridge("SliderThumbBackgroundPressed")),
        Brush("Ox.Fader.Thumb.BorderBrush", "#00000000"),
        Thick("Ox.Fader.Thumb.BorderThickness", 0),
        Brush("Ox.Fader.Track", null,
            new SkinBridge("SliderTrackFill"),
            new SkinBridge("SliderTrackFillPointerOver"),
            new SkinBridge("SliderTrackFillPressed")),
        Brush("Ox.Fader.Fill", null,
            new SkinBridge("SliderTrackValueFill"),
            new SkinBridge("SliderTrackValueFillPointerOver"),
            new SkinBridge("SliderTrackValueFillPressed")),
        // The framework's own groove is two pixels high, so writing that value
        // back through its key changes nothing until a skin says otherwise.
        Number("Ox.Fader.TrackHeight", 2, 1, 24, new SkinBridge("SliderTrackThemeHeight")),
        Radius("Ox.Fader.TrackCornerRadius", 1),
        Brush("Ox.Fader.Thumb.Grip", "#00000000"),
        Number("Ox.Fader.Thumb.GripLength", 6, 0, 64),

        // Buttons, toggles and dropdowns, through the Fluent theme's own keys.
        Brush("Ox.Control.Background", null,
            new SkinBridge("ButtonBackground"), new SkinBridge("ToggleButtonBackground"),
            new SkinBridge("ComboBoxBackground")),
        Brush("Ox.Control.BackgroundPointerOver", null,
            new SkinBridge("ButtonBackgroundPointerOver"), new SkinBridge("ToggleButtonBackgroundPointerOver"),
            new SkinBridge("ComboBoxBackgroundPointerOver")),
        Brush("Ox.Control.BackgroundPressed", null,
            new SkinBridge("ButtonBackgroundPressed"), new SkinBridge("ToggleButtonBackgroundPressed")),
        Brush("Ox.Control.BackgroundChecked", null,
            new SkinBridge("ToggleButtonBackgroundChecked"),
            new SkinBridge("ToggleButtonBackgroundCheckedPointerOver"),
            new SkinBridge("ToggleButtonBackgroundCheckedPressed")),
        Brush("Ox.Control.Foreground", null,
            new SkinBridge("ButtonForeground"), new SkinBridge("ButtonForegroundPointerOver"),
            new SkinBridge("ButtonForegroundPressed"), new SkinBridge("ToggleButtonForeground"),
            new SkinBridge("ComboBoxForeground"),
            new SkinBridge("CheckBoxForegroundUnchecked"), new SkinBridge("CheckBoxForegroundChecked")),
        Brush("Ox.Control.ForegroundChecked", null, new SkinBridge("ToggleButtonForegroundChecked")),
        Brush("Ox.Control.ForegroundDisabled", null,
            new SkinBridge("ButtonForegroundDisabled"), new SkinBridge("ComboBoxForegroundDisabled")),
        Brush("Ox.Control.BackgroundDisabled", null,
            new SkinBridge("ButtonBackgroundDisabled"), new SkinBridge("ToggleButtonBackgroundDisabled"),
            new SkinBridge("ComboBoxBackgroundDisabled")),
        Brush("Ox.Control.BorderBrush", null,
            new SkinBridge("ButtonBorderBrush"), new SkinBridge("ToggleButtonBorderBrush"),
            new SkinBridge("ComboBoxBorderBrush")),
        Brush("Ox.Control.BorderBrushPointerOver", null,
            new SkinBridge("ButtonBorderBrushPointerOver"),
            new SkinBridge("ToggleButtonBorderBrushPointerOver"),
            new SkinBridge("ComboBoxBorderBrushPointerOver")),
        // The face behind a plain button that the view asks to stay out of the
        // way: the header actions, a flyout's rows. Transparent by default,
        // which is what those views used to say inline.
        Brush("Ox.Ghost.Background", "#00000000"),
        // The gloss laid over every raised key face: the buttons, the toggles,
        // the mute and the dropdown's inner surface all take this one brush,
        // so a skin cannot give one of them a cap and leave the rest flat.
        // Transparent draws nothing, which is the flat appearance.
        Brush("Ox.Cap.Bevel", "#00000000"),

        // The lettering on a toggle carries its state, so it is separate from a
        // plain button's. These come after Ox.Control.Foreground and win over
        // it for toggles, which keeps a skin that only sets the older token
        // working exactly as it did.
        Brush("Ox.Toggle.Foreground", null,
            new SkinBridge("ToggleButtonForeground"),
            new SkinBridge("ToggleButtonForegroundPointerOver"),
            new SkinBridge("ToggleButtonForegroundPressed")),
        Brush("Ox.Toggle.ForegroundChecked", null,
            new SkinBridge("ToggleButtonForegroundChecked"),
            new SkinBridge("ToggleButtonForegroundCheckedPointerOver"),
            new SkinBridge("ToggleButtonForegroundCheckedPressed")),
        Brush("Ox.Toggle.ForegroundDisabled", null, new SkinBridge("ToggleButtonForegroundDisabled")),
        Brush("Ox.Toggle.BorderBrushChecked", null,
            new SkinBridge("ToggleButtonBorderBrushChecked"),
            new SkinBridge("ToggleButtonBorderBrushCheckedPointerOver")),
        // The plugin bypass key, whose checked state takes the plugin out of
        // the path. It is lettered by what the audio is doing, so the two
        // colours are its own rather than the ordinary toggle's. Both default
        // to the lettering the framework's toggle uses, which keeps the key
        // readable in an appearance that does not colour by state.
        Brush("Ox.Bypass.Foreground", "#ffffff"),
        Brush("Ox.Bypass.ForegroundBypassed", "#ffffff"),
        // The face of a key the window letters in the lamp colour of something
        // running: a plugin chain that is processing, a microphone EQ, noise
        // removal or dynamics that is on. Unset, those keys keep the ordinary
        // face and only their lettering lights.
        Brush("Ox.Lit.Background", null),
        Brush("Ox.Lit.BackgroundPointerOver", null),
        Brush("Ox.Lit.BorderBrush", null),
        // The ring of the small keys beside an input's binding chip, and of the
        // chip itself. None by default, which is how they have always looked.
        Thick("Ox.Chip.BorderThickness", 0),
        // The face of a key the window letters in the alert colour: a plugin
        // chain with nothing processing, a microphone EQ that cannot run and a
        // bypassed plugin. Unset, those keys keep the ordinary face and only
        // their lettering turns, as it always has.
        Brush("Ox.Alert.Background", null),
        Brush("Ox.Alert.BackgroundPointerOver", null),
        Brush("Ox.Alert.BorderBrush", null),
        // The label beside a checkbox is running text, not a lit legend, so it
        // defaults to the colour the rest of the running text has.
        Brush("Ox.Check.Foreground", "#e6e9f0",
            new SkinBridge("CheckBoxForegroundUnchecked"), new SkinBridge("CheckBoxForegroundChecked")),
        Radius("Ox.Control.CornerRadius", null, new SkinBridge("ControlCornerRadius")),
        // The ring around a raised key face. Read by the cap appearance only,
        // so the default is the width the framework's own button uses.
        Thick("Ox.Control.BorderThickness", 1),
        // A key that cannot be used keeps its colours and fades instead, which
        // is the cue that does not depend on hue: an unlit toggle and an
        // unavailable action can both be red without being confused. 1 is no
        // fade at all, which is what the shipped appearance does.
        Number("Ox.Control.DisabledOpacity", 1, 0.2, 1),
        Brush("Ox.Control.BorderBrushDisabled", null,
            new SkinBridge("ButtonBorderBrushDisabled"), new SkinBridge("ToggleButtonBorderBrushDisabled")),
        Brush("Ox.Input.Background", null,
            new SkinBridge("TextControlBackground"), new SkinBridge("TextControlBackgroundFocused")),
        Brush("Ox.Input.Foreground", null, new SkinBridge("TextControlForeground")),
        Brush("Ox.Input.BorderBrush", null, new SkinBridge("TextControlBorderBrush")),
        Brush("Ox.Flyout.Background", null,
            new SkinBridge("FlyoutPresenterBackground"), new SkinBridge("ComboBoxDropDownBackground")),
        Brush("Ox.Tooltip.Background", null, new SkinBridge("ToolTipBackground")),
        Brush("Ox.Tooltip.Foreground", null, new SkinBridge("ToolTipForeground")),
        Brush("Ox.Tooltip.BorderBrush", null, new SkinBridge("ToolTipBorderBrush")),
        // The accent paints the tick in a checkbox and the selection in a
        // dropdown. Fluent reads it as a Color, not as a brush.
        new SkinToken("Ox.Accent", SkinTokenKind.Brush, null,
        [
            new SkinBridge("SystemAccentColor", AsColor: true),
            new SkinBridge("SystemAccentColorLight1", AsColor: true),
            new SkinBridge("CheckBoxCheckBackgroundFillChecked"),
        ], SolidOnly: true),

        // The tabs Options is split into. Their labels take the heading and
        // readout colours, Ox.Text.Secondary and Ox.Text.Primary, so a skin
        // that colours its text has coloured its tabs. The bar under the
        // chosen tab is the framework's pipe, which follows the accent until
        // a skin names a brush of its own.
        Number("Ox.Tab.FontSize", 14, 8, 32),
        Brush("Ox.Tab.Indicator", null, new SkinBridge("TabItemHeaderSelectedPipeFill")),

        // An application's icon in APPLICATIONS: the size it is drawn at,
        // and the badge with the name's initial that stands in for an icon
        // the desktop does not have, at the same size so the rows line up.
        Number("Ox.AppIcon.Size", 18, 12, 48),
        Brush("Ox.AppIcon.Background", "#353a47"),
        Brush("Ox.AppIcon.Foreground", "#b9bfcc"),
        Radius("Ox.AppIcon.CornerRadius", 4),
        Number("Ox.AppIcon.FontSize", 11, 6, 32),
        // The name's width in a tile of APPLICATIONS' columns, the same in
        // every tile so the tiles of a column line up. 128 holds about sixteen
        // characters of the body text, "Telegram Desktop" or "VLC media
        // player"; a longer name scrolls while the pointer is on its tile.
        Number("Ox.AppColumns.NameWidth", 128, 64, 400),
        Number("Ox.AppColumns.TileMaxWidth", 0, 0, 1000),

        // The audio flow window.
        Brush("Ox.Flow.Background", "#242624"),
        Brush("Ox.Flow.Card", "#333633"),
        Brush("Ox.Flow.CardSelected", "#414541"),
        Brush("Ox.Flow.Text", "#f0f2ef"),
        Brush("Ox.Flow.TextDim", "#a3aaa3"),
        Brush("Ox.Flow.Input", "#20c4d1"),
        Brush("Ox.Flow.Channel", "#c958e5"),
        Brush("Ox.Flow.Output", "#20d49a"),
        Brush("Ox.Flow.Muted", "#757d75"),
        Brush("Ox.Flow.NodeHoverBackground", "#414541"),
        Brush("Ox.Flow.NodeHoverBorderBrush", "#717971"),

        // The microphone EQ's response graph. It draws in code, so its colours
        // are held as live brushes, as the indicators are, and must be flat.
        // The zones shade the six regions in turn over the background.
        SolidBrush("Ox.Eq.Background", "#14161b"),
        SolidBrush("Ox.Eq.Grid", "#2d313c"),
        SolidBrush("Ox.Eq.GridLabel", "#8b93a7"),
        SolidBrush("Ox.Eq.ZoneA", "#00000000"),
        SolidBrush("Ox.Eq.ZoneB", "#0affffff"),
        SolidBrush("Ox.Eq.ZoneLabel", "#6b7285"),
        SolidBrush("Ox.Eq.Curve", "#e6e9f0"),
        SolidBrush("Ox.Eq.CurveFill", "#335ba8f5"),
        SolidBrush("Ox.Eq.Band1", "#5ba8f5"),
        SolidBrush("Ox.Eq.Band2", "#3ecf7a"),
        SolidBrush("Ox.Eq.Band3", "#e0a05a"),
        SolidBrush("Ox.Eq.Band4", "#c958e5"),
        SolidBrush("Ox.Eq.HandleOff", "#6b7285"),
        SolidBrush("Ox.Eq.HandleRing", "#e6e9f0"),
        Number("Ox.Eq.CurveThickness", 2.5, 1, 8),
        Number("Ox.Eq.BandCurveThickness", 1.25, 0.5, 6),
        Number("Ox.Eq.HandleSize", 18, 12, 48),
        Number("Ox.Eq.LabelFontSize", 10, 8, 24),
        Radius("Ox.Eq.CornerRadius", 6),

        // The compressor's and expander's windows. Their graphs draw in code
        // on the EQ graph's ground, grid, labels and corners, so these are
        // flat too; the reduction meters take theirs through markup.
        SolidBrush("Ox.Dynamics.Level", "#5ba8f5"),
        SolidBrush("Ox.Dynamics.LevelFill", "#b35ba8f5"),
        SolidBrush("Ox.Dynamics.Threshold", "#e6e9f0"),
        SolidBrush("Ox.Dynamics.Reduction", "#e0a05a"),
        Number("Ox.Dynamics.LineThickness", 1.5, 0.5, 6),

        // Mixer control sizing. Touch sizing raises these to TouchMinimums.
        Number("Ox.Mixer.ControlMinSize", null, 0, 64),
        Number("Ox.Mixer.SliderMinHeight", null, 0, 96),
        Number("Ox.Mixer.DeviceSliderHeight", 30, 30, 96),
        Number("Ox.Mixer.SmallControlMinSize", 0, 0, 64),
        Number("Ox.Mixer.InsertControlMinSize", 24, 16, 64),
        Number("Ox.Mixer.ChannelWidth", 132, 132, 400),
        Number("Ox.Mixer.MixWidth", 232, 232, 500),
        // The strip templates of a skin's layout: an upright strip in a mix
        // bank, the length of an upright fader, and the gap between banks. An
        // input rack and a submixer of banks reckon their narrowest width
        // from the strip width.
        Number("Ox.Mixer.TileMaxWidth", 0, 0, 1000),
        Number("Ox.Mixer.StripWidth", 76, 56, 200),
        Number("Ox.Mixer.VerticalFaderLength", 160, 80, 480),
        Number("Ox.Mixer.BankSpacing", 12, 0, 64),
        // A channel or mix hidden in the layout editor, drawn in its place
        // while the submixer's Show hidden is on. It fades even where the
        // skin does not fade unusable keys, since it is fully usable.
        Number("Ox.Mixer.HiddenOpacity", 0.5, 0.2, 1),

        // The mix matrix: the name column of a row head, the width of a cell
        // and of a row head's source controls, and the height of a row. Every
        // cell is the same size, so the columns line up down the matrix and the
        // rows across it. Touch sizing raises the three to TouchMinimums.
        Number("Ox.Matrix.NameWidth", 140, 56, 400),
        Number("Ox.Matrix.CellWidth", 168, 120, 400),
        Number("Ox.Matrix.CellMaxWidth", 0, 0, 1000),
        Number("Ox.Matrix.RowHeight", 40, 28, 120),
        // A muted send's cell: its background, and the lettering of its mute.
        // The background is translucent by default, so the cell's own surface
        // shows through as a red tint.
        Brush("Ox.Matrix.MutedBackground", "#40a03434"),
        Brush("Ox.Matrix.MutedForeground", "#f6dede"),
        // How far a muted send's fader and level fade where the matrix dims
        // muted cells instead of tinting them. The mute keeps its own colour.
        Number("Ox.Matrix.MutedOpacity", 0.5, 0.1, 1),

        // The Soundboard window's pads. A pad is a key in the skin's button
        // face; these size it and draw what it shows while its clip plays: a
        // ring round the face and a bar for how far along the clip is.
        Number("Ox.Soundboard.PadWidth", 152, 96, 400),
        Number("Ox.Soundboard.PadHeight", 100, 72, 400),
        Radius("Ox.Soundboard.PadCornerRadius", 8),
        Brush("Ox.Soundboard.Playing", "#3ecf7a"),
        Thick("Ox.Soundboard.PlayingThickness", 2),
        Brush("Ox.Soundboard.Progress", "#3ecf7a"),
        Number("Ox.Soundboard.ProgressHeight", 4, 2, 16),
        // A pad whose clip has a colour is filled with it while it can play.
        // Its lettering is dark on a light colour and light on a dark one, so
        // it reads on any colour the user picks. Translucent layers over the
        // fill answer the pointer and a press, since the fill hides the
        // face's own.
        Brush("Ox.Soundboard.ForegroundOnLight", "#16181d"),
        Brush("Ox.Soundboard.ForegroundOnDark", "#ffffff"),
        Brush("Ox.Soundboard.FillPointerOver", "#1fffffff"),
        Brush("Ox.Soundboard.FillPressed", "#33000000"),
        Brush("Ox.Soundboard.MenuBackground", "#00000000"),

        // The colour swatches beside a colour field: the clip editor's and
        // the Appearance dialog's. The colours are the palette's; these size
        // a swatch and draw its outline and the ring round the chosen one,
        // which take the secondary and primary text colours.
        Number("Ox.Swatch.Size", 28, 16, 64),
        Radius("Ox.Swatch.CornerRadius", 4),
        Thick("Ox.Swatch.BorderThickness", 1),
        Thick("Ox.Swatch.SelectedThickness", 2),
    ];

    private static readonly Dictionary<string, SkinToken> ByName =
        All.ToDictionary(t => t.Name, System.StringComparer.Ordinal);

    /// <summary>The token with this name, or null when a skin names one we do not have.</summary>
    public static SkinToken? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>True when the name is a token this version knows.</summary>
    public static bool Exists(string name) => ByName.ContainsKey(name);
}
