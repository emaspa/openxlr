using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace OpenXLR.UI.Skinning;

/// <summary>
/// Holds the appearance the application is wearing and writes it into the
/// running resources.
///
/// Two mechanisms carry a skin to the screen, and both update a window that is
/// already open:
///
/// - every <c>Ox.*</c> token is an entry in <c>Application.Resources</c>, which
///   the views read with <c>DynamicResource</c>. Replacing the entry repaints
///   what reads it. The same write feeds the Fluent theme keys a token is
///   bridged to, which is how a skin reaches buttons, sliders and dropdowns
///   without the application owning their templates.
/// - the few tokens a value converter needs (the LEDs and the meters, which are
///   chosen per value in code rather than per control in markup) are held as one
///   live brush each. A skin sets the brush's colour instead of replacing the
///   object, so every binding that already handed out that brush repaints too.
///   Those tokens are flat colours by definition; the reader refuses a gradient
///   or an image for them.
///
/// A token a skin does not mention keeps its default, so a partial skin is an
/// overlay rather than a half-painted window. A token with no default at all is
/// removed when no skin supplies it, which leaves the stock Fluent value in
/// place: the unskinned application looks exactly as it did before skins.
/// </summary>
public static class SkinService
{
    /// <summary>Read at launch to force an appearance for one run, whatever ui.json says.</summary>
    public const string OverrideVariable = "OPENXLR_SKIN";

    /// <summary>
    /// The live indicator brushes, made when one is first asked for rather than
    /// with the class. A SolidColorBrush is an AvaloniaObject, so its colour may
    /// only be read or written on the UI thread; building them in a class
    /// initializer bound the whole service to whichever thread happened to touch
    /// it first, and every other thread then got a type initialization failure
    /// out of a plain property read. They are made and used on the UI thread,
    /// which is where <see cref="Apply"/> and the views run.
    /// </summary>
    private static readonly Dictionary<string, SolidColorBrush> LiveBrushes = new(StringComparer.Ordinal);

    private static readonly List<IDisposable> Images = [];

    /// <summary>The skin in force.</summary>
    public static SkinEntry Current { get; private set; } = new(SkinPackage.Default, []);

    /// <summary>
    /// What went wrong applying <see cref="Current"/>: the reader's complaints
    /// plus anything the images or the bridged keys could not take. Empty when
    /// the skin applied cleanly. Options shows these; the mixer window does not.
    /// </summary>
    public static IReadOnlyList<string> Errors { get; private set; } = [];

    /// <summary>True when the launch override decided the appearance, so Options can say so.</summary>
    public static bool Overridden { get; private set; }

    /// <summary>Raised on the UI thread after a skin is applied, for what resources cannot reach.</summary>
    public static event Action? Changed;

    /// <summary>True when a token is one of the flat indicator colours drawn in code.</summary>
    private static bool IsLive(SkinToken token) => token is { SolidOnly: true, Default: ISolidColorBrush };

    /// <summary>
    /// The one brush behind a flat indicator colour. The same object for the
    /// life of the process, so a converter may hand it to a binding and a later
    /// skin change still reaches that binding.
    /// </summary>
    public static SolidColorBrush LiveBrush(string token)
    {
        if (LiveBrushes.TryGetValue(token, out SolidColorBrush? brush)) return brush;
        if (SkinTokens.Find(token) is not { SolidOnly: true, Default: ISolidColorBrush solid })
            throw new KeyNotFoundException($"{token} is not an indicator colour.");
        brush = new SolidColorBrush(solid.Color);
        LiveBrushes[token] = brush;
        return brush;
    }

    /// <summary>
    /// Put the saved appearance on before the first window is built. The launch
    /// override wins: <c>OPENXLR_SKIN=default</c> starts the application in the
    /// appearance it ships with, which is the way back from a skin that made
    /// something unreadable.
    /// </summary>
    public static void Initialize()
    {
        string? id = Environment.GetEnvironmentVariable(OverrideVariable);
        Overridden = id is { Length: > 0 };
        if (!Overridden) id = UiSettings.Load().Skin;
        Apply(SkinCatalog.Find(id) ?? new SkinEntry(SkinPackage.Default, []));
    }

    /// <summary>
    /// Wear this skin now. Every window open at the time follows; nothing about
    /// the mixer, the daemon or the audio graph is touched. Returns the errors,
    /// which are also kept in <see cref="Errors"/>.
    /// </summary>
    public static IReadOnlyList<string> Apply(SkinEntry entry)
    {
        var errors = new List<string>(entry.Errors);
        Current = entry;
        if (Application.Current is not { } application)
        {
            Errors = errors;
            return errors;
        }

        // The images the last skin decoded stay alive until the new values are
        // in the resources, so nothing paints from a disposed bitmap.
        List<IDisposable> previous = [.. Images];
        Images.Clear();

        var realizer = new Realizer(entry.Package, errors);
        IResourceDictionary resources = application.Resources;
        foreach (SkinToken token in SkinTokens.All)
        {
            SkinValue? value = entry.Package.Tokens.GetValueOrDefault(token.Name);
            object? realized = value is null ? token.Default : realizer.Realize(token, value);
            // A value the realizer refused falls back to the default, so one
            // bad image never leaves a hole in the window.
            realized ??= token.Default;

            if (IsLive(token))
            {
                SolidColorBrush live = LiveBrush(token.Name);
                live.Color = realized is ISolidColorBrush solid ? solid.Color : Colors.Transparent;
                resources[token.Name] = live;
            }
            else if (realized is null) resources.Remove(token.Name);
            else resources[token.Name] = realized;

            ApplyBridges(token, realized, resources, errors);
        }

        ApplyControls(entry.Package, application, resources, errors);

        Images.AddRange(realizer.Bitmaps);
        foreach (IDisposable image in previous) image.Dispose();
        Errors = errors;
        Changed?.Invoke();
        return errors;
    }

    /// <summary>
    /// Publish the control theme each slot's chosen variant uses. The built-in
    /// appearance publishes no resource at all, which leaves the style in
    /// App.axaml with nothing to set and the control with the theme it has
    /// always had.
    /// </summary>
    private static void ApplyControls(SkinPackage package, Application application,
        IResourceDictionary resources, List<string> errors)
    {
        foreach (SkinControlSlot slot in SkinControls.Slots)
        {
            string? variant = package.Controls.GetValueOrDefault(slot.Name);
            IReadOnlyList<string>? themeKeys = SkinControls.ThemeKeys(slot, variant);
            for (int i = 0; i < slot.ResourceKeys.Count; i++)
            {
                string resourceKey = slot.ResourceKeys[i];
                string? themeKey = themeKeys is null ? null : themeKeys[i];
                if (themeKey is not null
                    && application.TryGetResource(themeKey, null, out object? theme) && theme is ControlTheme)
                {
                    resources[resourceKey] = theme;
                    continue;
                }
                resources.Remove(resourceKey);
                if (themeKey is not null)
                    // The variant passed validation but its drawing is missing,
                    // which is a packaging fault rather than the skin's.
                    errors.Add($"controls.{slot.Name}: the \"{variant}\" appearance is not installed.");
            }
        }
    }

    private static void ApplyBridges(SkinToken token, object? realized,
        IResourceDictionary resources, List<string> errors)
    {
        foreach (SkinBridge bridge in token.Bridges)
        {
            if (realized is null) { resources.Remove(bridge.Key); continue; }
            if (!bridge.AsColor) { resources[bridge.Key] = realized; continue; }
            if (realized is ISolidColorBrush solid) resources[bridge.Key] = solid.Color;
            else errors.Add($"{token.Name}: the theme reads this as a flat colour, so a gradient or image is ignored.");
        }
    }

    /// <summary>
    /// Save this skin as the one to wear and put it on. The choice lives in
    /// ui.json alone: it is not part of the mixer layout, the daemon's
    /// preferences or any audio profile, so switching appearance never touches
    /// what is playing.
    /// </summary>
    public static IReadOnlyList<string> Choose(string id)
    {
        SkinEntry entry = SkinCatalog.Find(id) ?? new SkinEntry(SkinPackage.Default, []);
        (UiSettings.Load() with { Skin = entry.Id == SkinPackage.DefaultId ? null : entry.Id }).Save();
        return Apply(entry);
    }

    /// <summary>Read the skin folders again and put the current choice back on.</summary>
    public static IReadOnlyList<string> Reload() => Apply(SkinCatalog.Find(Current.Id) ?? new SkinEntry(SkinPackage.Default, []));

    /// <summary>Turns validated values into the Avalonia objects the resources hold.</summary>
    private sealed class Realizer(SkinPackage package, List<string> errors)
    {
        private long _pixels;

        public List<IDisposable> Bitmaps { get; } = [];

        public object? Realize(SkinToken token, SkinValue value)
        {
            switch (value)
            {
                case SkinSolid solid:
                    return new ImmutableSolidColorBrush(solid.Color);
                case SkinGradient gradient:
                    return Gradient(gradient);
                case SkinImage image:
                    return Image(token, image);
                case SkinNumber number:
                    return number.Value;
                case SkinCornerRadius radius:
                    return radius.Value;
                case SkinThickness thickness:
                    return thickness.Value;
                case SkinFontFamily family:
                    return new FontFamily(family.Value);
                case SkinFontWeight weight:
                    return weight.Value;
                default:
                    return null;
            }
        }

        private static IBrush Gradient(SkinGradient gradient)
        {
            var stops = new GradientStops();
            foreach (SkinStop stop in gradient.Stops) stops.Add(new GradientStop(stop.Color, stop.Offset));
            if (!gradient.Radial)
                return new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(gradient.Start, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(gradient.End, RelativeUnit.Relative),
                    GradientStops = stops,
                };
            return new RadialGradientBrush
            {
                Center = new RelativePoint(gradient.End, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(gradient.Start, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(gradient.Radius, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(gradient.Radius, RelativeUnit.Relative),
                GradientStops = stops,
            };
        }

        private object? Image(SkinToken token, SkinImage image)
        {
            if (package.Directory is not { } directory)
            {
                errors.Add($"{token.Name}: a built-in skin carries no files.");
                return null;
            }
            if (SkinReader.ResolveAsset(directory, image.RelativePath) is not { } file)
            {
                errors.Add($"{token.Name}: \"{image.RelativePath}\" is missing, or it is a link out of the skin's folder.");
                return null;
            }
            if (file.Length > SkinFormat.MaxImageBytes)
            {
                errors.Add($"{token.Name}: \"{image.RelativePath}\" is larger than the "
                    + $"{SkinFormat.MaxImageBytes / (1024 * 1024)} MB image limit.");
                return null;
            }
            if (SkinImageHeader.Read(file) is not { } size)
            {
                errors.Add($"{token.Name}: \"{image.RelativePath}\" is not a PNG or JPEG image.");
                return null;
            }
            if (size.Width > SkinFormat.MaxImagePixels || size.Height > SkinFormat.MaxImagePixels)
            {
                errors.Add($"{token.Name}: \"{image.RelativePath}\" is {size.Width} by {size.Height}; "
                    + $"a skin image is at most {SkinFormat.MaxImagePixels} pixels a side.");
                return null;
            }
            long pixels = (long)size.Width * size.Height;
            if (_pixels + pixels > SkinFormat.MaxDecodedPixels)
            {
                errors.Add($"{token.Name}: the skin's images together are over the "
                    + $"{SkinFormat.MaxDecodedPixels / (1024 * 1024)} megapixel budget.");
                return null;
            }

            try
            {
                var bitmap = new Bitmap(file.FullName);
                _pixels += pixels;
                Bitmaps.Add(bitmap);
                return new ImageBrush(bitmap) { Stretch = image.Stretch, Opacity = image.Opacity };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException)
            {
                errors.Add($"{token.Name}: \"{image.RelativePath}\" could not be decoded: {ex.Message}");
                return null;
            }
        }
    }
}
