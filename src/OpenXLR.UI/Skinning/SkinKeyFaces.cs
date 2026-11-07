using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace OpenXLR.UI.Skinning;

/// <summary>
/// The faces and rings a skin may give the keys the window letters in a lamp
/// colour, and the rings of a muted key and a destructive one.
///
/// These tokens have no default. A style that read an unset token would
/// paint nothing at all rather than leave the key as it was, so the styles
/// are not written in App.axaml: they are made here, from the values the
/// skin sets, each time a skin is applied. A token the skin leaves out adds
/// no style, and the key keeps exactly the face and ring it has always had.
/// </summary>
internal static class SkinKeyFaces
{
    /// <summary>The styles made for the skin in force, kept last in the application's styles so they win.</summary>
    private static readonly Styles Faces = [];

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

    /// <summary>
    /// Make the styles for the tokens the skin sets, from the values now in
    /// <paramref name="resources"/>, and put them last in the application's
    /// styles. A face under the pointer that the skin leaves out takes the
    /// face at rest, so a key never loses its face when the pointer reaches it.
    /// </summary>
    public static void Apply(Application application, IResourceDictionary resources)
    {
        Faces.Clear();
        Family(resources, "Ox.Alert.Background", "Ox.Alert.BackgroundPointerOver", "Ox.Alert.BorderBrush", AlertRest, AlertActive);
        Family(resources, "Ox.Lit.Background", "Ox.Lit.BackgroundPointerOver", "Ox.Lit.BorderBrush", LitRest, LitActive);
        if (Brush(resources, "Ox.Mute.BorderBrushChecked") is { } muted)
            Add(Muted, ContentPresenter.BorderBrushProperty, muted);
        if (Brush(resources, "Ox.Danger.BorderBrush") is { } danger)
            Add(Danger, ContentPresenter.BorderBrushProperty, danger);

        Styles styles = application.Styles;
        if (Faces.Count == 0)
        {
            styles.Remove(Faces);
            return;
        }
        if (styles.Count > 0 && ReferenceEquals(styles[^1], Faces)) return;
        styles.Remove(Faces);
        styles.Add(Faces);
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

    private static void Add(IEnumerable<Func<Selector?, Selector>> keys, AvaloniaProperty property, IBrush value)
    {
        foreach (Func<Selector?, Selector> key in keys)
            Faces.Add(new Style(key) { Setters = { new Setter(property, value) } });
    }

    private static IBrush? Brush(IResourceDictionary resources, string token) =>
        resources.TryGetValue(token, out object? value) ? value as IBrush : null;
}
