using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
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
    /// <summary>The styles made for the skin in force, kept last in the application's styles so they win; null while it sets none.</summary>
    private static Styles? _current;

    /// <summary>What those styles set and where, to tell a skin that needs other styles from one that needs the same.</summary>
    private static string _signature = "";

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
    /// Make the styles for the tokens the skin sets and put them last in the
    /// application's styles. A face under the pointer that the skin leaves
    /// out takes the face at rest, so a key never loses its face when the
    /// pointer reaches it.
    ///
    /// Each setter reads its token as a dynamic resource rather than holding
    /// the value, so the styles depend only on which tokens the skin sets.
    /// Reloading a skin, or moving to another that sets the same tokens,
    /// leaves the application's styles untouched and the new values arrive
    /// through the resources. Only a change in which tokens are set swaps the
    /// styles, and then the new set goes in before the old one comes out:
    /// clearing a set already in the application restyled every control in
    /// place, and a hidden part briefly counted as shown, which left strips
    /// measured taller than what they hold.
    /// </summary>
    public static void Apply(Application application, IResourceDictionary resources)
    {
        var next = new Styles();
        var signature = new List<string>();
        void Add(IEnumerable<Func<Selector?, Selector>> keys, AvaloniaProperty property, object value)
        {
            foreach (Func<Selector?, Selector> key in keys)
            {
                Selector selector = key(null);
                object setterValue = value is Token token ? new DynamicResourceExtension(token.Key) : value;
                next.Add(new Style(_ => selector) { Setters = { new Setter(property, setterValue) } });
                signature.Add($"{selector} {property.Name} {value}");
            }
        }
        Token? Set(string key) => resources.ContainsKey(key) ? new Token(key) : null;
        void Family(string rest, string active, string ring,
            Func<Selector?, Selector>[] restKeys, Func<Selector?, Selector>[] activeKeys)
        {
            Token? face = Set(rest);
            Token? under = Set(active) ?? face;
            Token? border = Set(ring);
            if (face is not null) Add(restKeys, ContentPresenter.BackgroundProperty, face);
            if (under is not null) Add(activeKeys, ContentPresenter.BackgroundProperty, under);
            if (border is not null)
            {
                Add(restKeys, ContentPresenter.BorderBrushProperty, border);
                Add(activeKeys, ContentPresenter.BorderBrushProperty, border);
            }
        }

        Family("Ox.Alert.Background", "Ox.Alert.BackgroundPointerOver", "Ox.Alert.BorderBrush", AlertRest, AlertActive);
        Family("Ox.Lit.Background", "Ox.Lit.BackgroundPointerOver", "Ox.Lit.BorderBrush", LitRest, LitActive);
        if (Set("Ox.Mute.BorderBrushChecked") is { } muted) Add(Muted, ContentPresenter.BorderBrushProperty, muted);
        if (Set("Ox.Danger.BorderBrush") is { } danger) Add(Danger, ContentPresenter.BorderBrushProperty, danger);
        if (Set("Ox.Value.Foreground") is { } value) Add(Readouts, TextBlock.ForegroundProperty, value);
        if (resources.TryGetValue("Ox.Chip.Theme", out object? chip) && chip is ControlTheme)
        {
            var info = new Token("Ox.Text.Info");
            var alert = new Token("Ox.Led.Alert");
            Add(Chips, StyledElement.ThemeProperty, new Token("Ox.Chip.Theme"));
            Add(ChipLettering, TextBlock.ForegroundProperty, info);
            // A badge has no lamp: a source that is not linked is lettered in
            // the alert colour instead. The gap is the lamp's margin, so it goes too.
            Add(ChipLamps, Visual.IsVisibleProperty, false);
            Add(UnlinkedLettering, TextBlock.ForegroundProperty, alert);

            // A plugin's name in an insert list is the same badge, with no
            // lamp: lettered in the alert colour while bypassed or failing.
            Add(InsertChips, Border.BackgroundProperty, new Token("Ox.Badge.Background"));
            Add(InsertChips, Decorator.PaddingProperty, new Thickness(6, 1));
            Add(InsertChips, Border.CornerRadiusProperty, new Token("Ox.Eq.CornerRadius"));
            Add(InsertLamps, Visual.IsVisibleProperty, false);
            Add(InsertLettering, TextBlock.ForegroundProperty, info);
            Add(InsertOffLettering, TextBlock.ForegroundProperty, alert);

            // The interface's lamp in the header goes the same way: its line is
            // lettered in the alert colour while the interface is disconnected.
            Add(DeviceLamp, Visual.IsVisibleProperty, false);
            Add(DisconnectedLettering, TextBlock.ForegroundProperty, alert);
        }

        string made = string.Join("\n", signature);
        Styles styles = application.Styles;
        bool inPlace = _current is not null && styles.Count > 0 && ReferenceEquals(styles[^1], _current);
        if (made == _signature && (inPlace || _current is null)) return;
        if (next.Count > 0) styles.Add(next);
        if (_current is not null) styles.Remove(_current);
        _current = next.Count > 0 ? next : null;
        _signature = made;
    }

    /// <summary>A setter's value read from a token as a dynamic resource.</summary>
    private sealed record Token(string Key)
    {
        public override string ToString() => "{" + Key + "}";
    }
}
