using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace OpenXLR.UI.Skinning;

/// <summary>
/// The styles for appearance values that have no default: the faces and
/// rings a skin may give the keys the UI letters in a lamp colour, the
/// rings of a muted key and a destructive one, the readouts' lettering and
/// the source chips drawn as badges.
///
/// A style that read an unset token would paint nothing at all rather than
/// leave the control as it was, and a chip whose theme were read from an
/// unset key would lose the skin's button for the framework's. So the styles
/// are not written in App.axaml: they are made here, from the values the
/// skin sets, each time a skin is applied. A value the skin leaves out adds
/// no style, and the control keeps exactly the appearance it has always had.
/// </summary>
internal static class SkinOptionalStyles
{
    /// <summary>The styles made for the skin in force, kept last in the application's styles so they win.</summary>
    private static readonly Styles Made = [];

    private const string Presenter = "PART_ContentPresenter";

    private static Func<Selector?, Selector> Face<T>(params string[] classes) where T : Control => x =>
    {
        Selector selector = x.OfType<T>();
        foreach (string c in classes) selector = selector.Class(c);
        return selector.Template().OfType<ContentPresenter>().Name(Presenter);
    };

    // The keys each family covers, at rest and under the pointer or held down.
    private static readonly Func<Selector?, Selector>[] AlertRest =
        [Face<Button>("fx", "fxOff"), Face<ToggleButton>("bypass", ":checked")];
    private static readonly Func<Selector?, Selector>[] AlertActive =
    [
        Face<Button>("fx", "fxOff", ":pointerover"), Face<Button>("fx", "fxOff", ":pressed"),
        Face<ToggleButton>("bypass", ":checked", ":pointerover"), Face<ToggleButton>("bypass", ":checked", ":pressed"),
    ];
    private static readonly Func<Selector?, Selector>[] LitRest = [Face<Button>("fx", "fxOn")];
    private static readonly Func<Selector?, Selector>[] LitActive =
        [Face<Button>("fx", "fxOn", ":pointerover"), Face<Button>("fx", "fxOn", ":pressed")];
    private static readonly Func<Selector?, Selector>[] Muted =
    [
        Face<ToggleButton>("mute", ":checked"), Face<ToggleButton>("mute", ":checked", ":pointerover"),
        Face<ToggleButton>("mute", ":checked", ":pressed"),
    ];
    private static readonly Func<Selector?, Selector>[] Danger =
    [
        Face<Button>("danger"), Face<Button>("danger", ":pointerover"), Face<Button>("danger", ":pressed"),
    ];
    private static readonly Func<Selector?, Selector>[] Readouts = [x => x.OfType<TextBlock>().Class("val")];
    // XLR 1's and XLR 2's binding chips and a capture input's source chip.
    private static readonly Func<Selector?, Selector>[] Chips =
        [x => x.OfType<Button>().Class("bindingChip"), x => x.OfType<Button>().Class("captureChip")];
    private static readonly Func<Selector?, Selector>[] ChipLettering = ChipPart<TextBlock>(false, "ChipText");
    private static readonly Func<Selector?, Selector>[] UnlinkedLettering = ChipPart<TextBlock>(true, "ChipText");
    private static readonly Func<Selector?, Selector>[] ChipLamps = ChipPart<StatusLed>(false, null);

    private static readonly Func<Selector?, Selector>[] InsertChips = [x => x.OfType<Border>().Class("insertChip")];
    private static readonly Func<Selector?, Selector>[] InsertLamps = [x => x.OfType<StatusLed>().Class("insertLamp")];
    private static readonly Func<Selector?, Selector>[] InsertLettering =
        [x => x.OfType<Border>().Class("insertChip").Child().OfType<TextBlock>().Class("insertName")];
    private static readonly Func<Selector?, Selector>[] InsertOffLettering =
        [x => x.OfType<Border>().Class("insertChip").Class("off").Child().OfType<TextBlock>().Class("insertName")];

    private static readonly Func<Selector?, Selector>[] DeviceLamp = [x => x.OfType<StatusLed>().Class("deviceLamp")];
    private static readonly Func<Selector?, Selector>[] DisconnectedLettering =
    [
        x => x.OfType<Grid>().Name("DeviceLine").Class("disconnected").Child().OfType<TextBlock>(),
        x => x.OfType<Grid>().Name("DeviceLine").Class("disconnected").Descendant().OfType<DropDownButton>().Descendant().OfType<TextBlock>(),
    ];

    private static Func<Selector?, Selector>[] ChipPart<T>(bool unlinked, string? name) where T : Control =>
    [
        .. new[] { "bindingChip", "captureChip" }.Select(chip => (Func<Selector?, Selector>)(x =>
        {
            Selector button = x.OfType<Button>().Class(chip);
            if (unlinked) button = button.Class("unlinked");
            Selector part = button.Descendant().OfType<T>();
            return name is null ? part : part.Name(name);
        })),
    ];

    /// <summary>
    /// Make the styles for the tokens the skin sets, from the values now in
    /// <paramref name="resources"/>, and put them last in the application's
    /// styles. A face under the pointer that the skin leaves out takes the
    /// face at rest, so a key never loses its face when the pointer reaches it.
    /// </summary>
    public static void Apply(Application application, IResourceDictionary resources)
    {
        Made.Clear();
        Family(resources, "Ox.Alert.Background", "Ox.Alert.BackgroundPointerOver", "Ox.Alert.BorderBrush", AlertRest, AlertActive);
        Family(resources, "Ox.Lit.Background", "Ox.Lit.BackgroundPointerOver", "Ox.Lit.BorderBrush", LitRest, LitActive);
        if (Brush(resources, "Ox.Mute.BorderBrushChecked") is { } muted)
            Add(Muted, ContentPresenter.BorderBrushProperty, muted);
        if (Brush(resources, "Ox.Danger.BorderBrush") is { } danger)
            Add(Danger, ContentPresenter.BorderBrushProperty, danger);
        if (Brush(resources, "Ox.Value.Foreground") is { } value)
            Add(Readouts, TextBlock.ForegroundProperty, value);
        if (resources.TryGetValue("Ox.Chip.Theme", out object? chip) && chip is ControlTheme badge)
        {
            Add(Chips, StyledElement.ThemeProperty, badge);
            if (Brush(resources, "Ox.Text.Info") is { } info)
                Add(ChipLettering, TextBlock.ForegroundProperty, info);
            // A badge has no lamp: a source that is not linked is lettered in
            // the alert colour instead. The gap is the lamp's margin, so it goes too.
            Add(ChipLamps, Visual.IsVisibleProperty, false);
            if (Brush(resources, "Ox.Led.Alert") is { } alert)
                Add(UnlinkedLettering, TextBlock.ForegroundProperty, alert);

            // A plugin's name in an insert list is the same badge, with no
            // lamp: lettered in the alert colour while bypassed or failing.
            if (Brush(resources, "Ox.Badge.Background") is { } face)
                Add(InsertChips, Border.BackgroundProperty, face);
            Add(InsertChips, Decorator.PaddingProperty, new Thickness(6, 1));
            if (resources.TryGetValue("Ox.Eq.CornerRadius", out object? corners) && corners is CornerRadius radius)
                Add(InsertChips, Border.CornerRadiusProperty, radius);
            Add(InsertLamps, Visual.IsVisibleProperty, false);
            if (Brush(resources, "Ox.Text.Info") is { } insertInfo)
                Add(InsertLettering, TextBlock.ForegroundProperty, insertInfo);
            if (Brush(resources, "Ox.Led.Alert") is { } insertAlert)
                Add(InsertOffLettering, TextBlock.ForegroundProperty, insertAlert);

            // The interface's lamp in the header goes the same way: its line is
            // lettered in the alert colour while the interface is disconnected.
            Add(DeviceLamp, Visual.IsVisibleProperty, false);
            if (Brush(resources, "Ox.Led.Alert") is { } deviceAlert)
                Add(DisconnectedLettering, TextBlock.ForegroundProperty, deviceAlert);
        }

        Styles styles = application.Styles;
        if (Made.Count == 0)
        {
            styles.Remove(Made);
            return;
        }
        if (styles.Count > 0 && ReferenceEquals(styles[^1], Made)) return;
        styles.Remove(Made);
        styles.Add(Made);
    }

    private static void Family(IResourceDictionary resources, string rest, string active, string ring,
        Func<Selector?, Selector>[] restKeys, Func<Selector?, Selector>[] activeKeys)
    {
        IBrush? face = Brush(resources, rest);
        IBrush? under = Brush(resources, active) ?? face;
        IBrush? border = Brush(resources, ring);
        if (face is not null) Add(restKeys, ContentPresenter.BackgroundProperty, face);
        if (under is not null) Add(activeKeys, ContentPresenter.BackgroundProperty, under);
        if (border is not null)
        {
            Add(restKeys, ContentPresenter.BorderBrushProperty, border);
            Add(activeKeys, ContentPresenter.BorderBrushProperty, border);
        }
    }

    private static void Add(IEnumerable<Func<Selector?, Selector>> keys, AvaloniaProperty property, object value)
    {
        foreach (Func<Selector?, Selector> key in keys)
            Made.Add(new Style(key) { Setters = { new Setter(property, value) } });
    }

    private static IBrush? Brush(IResourceDictionary resources, string token) =>
        resources.TryGetValue(token, out object? value) ? value as IBrush : null;
}
