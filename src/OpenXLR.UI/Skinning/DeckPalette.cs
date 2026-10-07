using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpenXLR.UI.Skinning;

/// <summary>The Deck reads the realised colours, including the active desktop palette.</summary>
internal static class DeckPalette
{
    internal static readonly string[] Names =
    [
        "Ox.Card.Background", "Ox.Text.Primary", "Ox.Text.Muted", "Ox.Led.On",
        "Ox.Led.Off", "Ox.Led.Alert", "Ox.Meter.Track", "Ox.Meter.Fill",
        "Ox.Meter.Warning", "Ox.Meter.Hot", "Ox.Meter.WarningLevel", "Ox.Meter.HotLevel",
    ];

    internal static Dictionary<string, object> Read(IResourceDictionary resources)
    {
        var colours = new Dictionary<string, object>();
        foreach (string name in Names)
        {
            resources.TryGetValue(name, out object? value);
            value ??= SkinTokens.Find(name)!.Default;
            object? flat = value switch
            {
                ISolidColorBrush solid => solid.Color.ToString(),
                GradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops[0].Color.ToString(),
                double number => number,
                _ => null,
            };
            if (flat is not null) colours[name] = flat;
        }
        return colours;
    }

    internal static void Publish(string id, IResourceDictionary resources, List<string> errors)
    {
        try
        {
            OpenXlrPaths.WriteAtomicJson(OpenXlrPaths.ConfigFile("deck-palette.json"),
                new { schema = 1, skin = id, pid = Environment.ProcessId, tokens = Read(resources) },
                new System.Text.Json.JsonSerializerOptions());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            errors.Add("Stream Deck palette: " + error.Message);
        }
    }
}
