using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenXLR.UI.Skinning;

/// <summary>
/// One control a skin may restyle, and the appearances the application owns
/// for it.
/// </summary>
/// <param name="Name">What the skin's "controls" section calls it.</param>
/// <param name="ResourceKeys">
/// Where the chosen control themes are published. A style in App.axaml reads
/// each key, so setting them restyles every such control in every open window,
/// and removing them hands the controls back to their built-in appearance. One
/// slot has more than one key when the same appearance covers several control
/// types, which is how a plain button and a toggle wear the same face.
/// </param>
/// <param name="Default">The variant the window has always used.</param>
/// <param name="Variants">
/// Variant name to the control themes that draw it, one per resource key, or
/// null for the built-in appearance, which is published as no resource at all.
/// </param>
public sealed record SkinControlSlot(
    string Name,
    IReadOnlyList<string> ResourceKeys,
    string Default,
    IReadOnlyDictionary<string, IReadOnlyList<string>?> Variants)
{
    /// <summary>The variant names, for a message that says what was expected.</summary>
    public string VariantList => string.Join(", ", Variants.Keys.Select(v => $"\"{v}\""));
}

/// <summary>
/// The controls a skin may choose an appearance for. A skin names a variant;
/// it never supplies a template, a style or any markup, so the set of shapes
/// the window can take is fixed here and reviewed like any other code.
///
/// Each variant is drawn by <c>Skinning/Controls.axaml</c> and tinted by the
/// <c>Ox.*</c> tokens, so a skin picks a shape and colours it.
/// </summary>
public static class SkinControls
{
    public static readonly IReadOnlyList<SkinControlSlot> Slots =
    [
        // The console fader keeps the framework's Track, repeat buttons and
        // Thumb, so only its drawing changes.
        new("fader", ["Ox.Slider.Theme"], "flat",
            new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal)
            {
                ["flat"] = null,
                ["console"] = ["Ox.Slider.Console"],
            }),
        // The raised key face, shared by the plain button and the toggle so a
        // skin cannot make one of them look like a key and leave the other flat.
        new("button", ["Ox.Button.Theme", "Ox.ToggleButton.Theme", "Ox.DropDownButton.Theme"], "flat",
            new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal)
            {
                ["flat"] = null,
                ["cap"] = ["Ox.Button.Cap", "Ox.ToggleButton.Cap", "Ox.DropDownButton.Cap"],
            }),
        // One bar that grows, or a ladder of cells that light in turn.
        new("meter", ["Ox.Meter.Theme"], "continuous",
            new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal)
            {
                ["continuous"] = null,
                ["segmented"] = ["Ox.Meter.Segmented"],
            }),
        // A flat dot, or a lamp in a bezel with a halo when lit.
        new("led", ["Ox.Led.Theme"], "flat",
            new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal)
            {
                ["flat"] = null,
                ["lamp"] = ["Ox.Led.Lamp"],
            }),
        // The framework's button, or a cap with a bevel laid over it.
        new("mute", ["Ox.MuteButton.Theme"], "flat",
            new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal)
            {
                ["flat"] = null,
                ["cap"] = ["Ox.MuteButton.Cap"],
            }),
    ];

    private static readonly Dictionary<string, SkinControlSlot> ByName =
        Slots.ToDictionary(s => s.Name, StringComparer.Ordinal);

    /// <summary>The slot with this name, or null when a skin names a control we do not have.</summary>
    public static SkinControlSlot? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>The control theme keys for a slot's chosen variant, or null for the built-in appearance.</summary>
    public static IReadOnlyList<string>? ThemeKeys(SkinControlSlot slot, string? variant) =>
        slot.Variants.GetValueOrDefault(variant ?? slot.Default);
}
