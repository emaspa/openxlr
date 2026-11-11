using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace OpenXLR.UI;

/// <summary>
/// The row of colour swatches beside a colour field: no colour first, then
/// the built-in palette. A click writes the colour into the field, which
/// stays the one place the value lives, so a custom value can still be typed
/// and the swatch that matches the field, if any, wears a ring. The colours
/// are the palette's own, picked by the user, so they are brushes made here;
/// the size, corners, outline and ring are skin tokens.
/// </summary>
public static class ColourSwatches
{
    /// <summary>A swatch's value, empty for no colour, and the ring it wears while the field holds that value.</summary>
    private sealed record Entry(string Value, Border Ring);

    /// <summary>Fill <paramref name="row"/> with the swatches, each writing into <paramref name="field"/>.</summary>
    public static void Fill(Panel row, TextBox field) => Fill(row, field, null);

    /// <summary>
    /// Fill <paramref name="row"/> with <paramref name="colours"/> alone, as
    /// <c>#rrggbb</c> and the name a tooltip says, without the no-colour
    /// swatch: for a field that always holds a colour. Null for the palette.
    /// </summary>
    public static void Fill(Panel row, TextBox field, IReadOnlyList<(string Hex, string Name)>? colours)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(field);
        row.Children.Clear();
        var entries = new List<Entry>();
        Button Add(string value, string name, Control face, string automationName)
        {
            var ring = new Border { IsVisible = false, IsHitTestVisible = false };
            ring.Bind(Border.BorderBrushProperty, ring.GetResourceObservable("Ox.Text.Primary"));
            ring.Bind(Border.BorderThicknessProperty, ring.GetResourceObservable("Ox.Swatch.SelectedThickness"));
            ring.Bind(Border.CornerRadiusProperty, ring.GetResourceObservable("Ox.Swatch.CornerRadius"));
            var button = new Button
            {
                Name = name, Tag = value, Classes = { "ghost" }, Padding = new Thickness(0),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                Content = new Panel { Children = { face, ring } },
            };
            button.Bind(Avalonia.Layout.Layoutable.WidthProperty, button.GetResourceObservable("Ox.Swatch.Size"));
            button.Bind(Avalonia.Layout.Layoutable.HeightProperty, button.GetResourceObservable("Ox.Swatch.Size"));
            button.Bind(Avalonia.Layout.Layoutable.MinWidthProperty, button.GetResourceObservable("Ox.Mixer.SmallControlMinSize"));
            button.Bind(Avalonia.Layout.Layoutable.MinHeightProperty, button.GetResourceObservable("Ox.Mixer.SmallControlMinSize"));
            button.Bind(Button.CornerRadiusProperty, button.GetResourceObservable("Ox.Swatch.CornerRadius"));
            AutomationProperties.SetName(button, automationName);
            ToolTip.SetTip(button, automationName);
            button.Click += (_, _) => field.Text = value;
            entries.Add(new Entry(value, ring));
            row.Children.Add(button);
            return button;
        }

        // No colour: an empty outlined square struck through, the sign for "none".
        var strike = new Path
        {
            Data = Geometry.Parse("M0 21.5L21.5 0L24 2.5L2.5 24z"), Stretch = Stretch.Fill,
            Margin = new Thickness(5), IsHitTestVisible = false,
        };
        strike.Bind(Shape.FillProperty, strike.GetResourceObservable("Ox.Text.Secondary"));
        if (colours is not null)
            foreach ((string hex, string name) in colours)
                Add(hex, "ColourSwatch", Face(hex), name);
        else
        {
            Add("", "ColourSwatchNone", new Panel { Children = { Face(null), strike } }, Localizer.Text("ColourNone"));
            foreach (LayoutColourDefinition colour in LayoutColours.Palette)
                Add(colour.Hex, "ColourSwatch", Face(colour.Hex), Name(colour.Name));
        }

        void Update()
        {
            string current = (field.Text ?? "").Trim();
            foreach (Entry entry in entries)
                entry.Ring.IsVisible = string.Equals(entry.Value, current, StringComparison.OrdinalIgnoreCase);
        }
        // The property itself rather than TextChanged, which the box raises later: the ring follows at once.
        field.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Update(); };
        Update();
    }

    /// <summary>A palette colour's name, as a swatch's tooltip and a screen reader say it.</summary>
    public static string Name(string colour) => colour switch
    {
        "red" => Localizer.Text("ColourRed"),
        "orange" => Localizer.Text("ColourOrange"),
        "yellow" => Localizer.Text("ColourYellow"),
        "lime" => Localizer.Text("ColourLime"),
        "green" => Localizer.Text("ColourGreen"),
        "teal" => Localizer.Text("ColourTeal"),
        "cyan" => Localizer.Text("ColourCyan"),
        "blue" => Localizer.Text("ColourBlue"),
        "indigo" => Localizer.Text("ColourIndigo"),
        "purple" => Localizer.Text("ColourPurple"),
        "pink" => Localizer.Text("ColourPink"),
        "brown" => Localizer.Text("ColourBrown"),
        _ => colour,
    };

    /// <summary>The colour itself, inset so the ring and the button's own hover face show round it.</summary>
    private static Border Face(string? hex)
    {
        var face = new Border { Margin = new Thickness(4), IsHitTestVisible = false };
        if (hex is not null) face.Background = new SolidColorBrush(Color.Parse(hex));
        face.Bind(Border.BorderBrushProperty, face.GetResourceObservable("Ox.Text.Secondary"));
        face.Bind(Border.BorderThicknessProperty, face.GetResourceObservable("Ox.Swatch.BorderThickness"));
        face.Bind(Border.CornerRadiusProperty, face.GetResourceObservable("Ox.Swatch.CornerRadius"));
        return face;
    }
}
