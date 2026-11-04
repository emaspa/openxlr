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
///
/// Broadcast is worn while ui.json holds no choice. Material, the token
/// defaults and the fallback for a skin that is missing, has a light and a
/// dark palette. The dark one is the token defaults; the light one is skin
/// data compiled into the window (Assets/Appearance/material-light.json).
/// The saved mode picks one, and in <c>system</c> the toolkit's theme
/// variant, which follows the desktop's preference through the settings
/// portal, picks it. Every other skin is one
/// set of values and keeps it whatever the mode says.
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

    private static readonly Lazy<SkinEntry> LightMaterial = new(ReadLightMaterial);
    private static Application? _application;
    private static bool _settingVariant;

    internal const int NormalTextSize = 100;

    /// <summary>The current UI text size, including an unsaved choice for this run.</summary>
    internal static int TextSizePercent { get; private set; } = NormalTextSize;

    internal static int NormalizeTextSize(int percent) => percent is 80 or 90 or 100 or 110 or 120
        ? percent : NormalTextSize;

    internal static IReadOnlyList<int> TextSizeBases { get; } = [10, 11, 12, 13, 16, 17, 18, 19, 20, 22, 24];
    internal const string CrossfadeReadoutWidth = "UiText.CrossfadeReadoutWidth";

    private static double ScaleText(double size) => size * TextSizePercent / NormalTextSize;

    private static bool IsFontSize(SkinToken token) => token.Name.EndsWith("FontSize", StringComparison.Ordinal);

    /// <summary>Save the text size and apply it to open windows, even when the preference cannot be saved.</summary>
    internal static string? ChooseTextSize(int percent)
    {
        percent = NormalizeTextSize(percent);
        string? error = (UiSettings.Load() with { TextSizePercent = percent }).Save();
        ApplyTextSize(percent);
        return error;
    }

    internal static void ApplyTextSize(int percent)
    {
        TextSizePercent = NormalizeTextSize(percent);
        if (Application.Current is { } application)
        {
            ApplyTextResources(application);
            foreach (SkinToken token in SkinTokens.All)
                if (IsFontSize(token) && token.Default is double basis)
                    Put(application.Resources, token.Name, ScaleText(
                        _palette.Tokens.GetValueOrDefault(token.Name) is SkinNumber size ? size.Value : basis));
        }
        Changed?.Invoke();
    }

    private static void ApplyTextResources(Application application)
    {
        var resources = application.Resources;
        foreach (int size in TextSizeBases)
            Put(resources, $"UiText.FontSize{size}", ScaleText(size));
        // Reserve the endpoint readout's space even in the middle of a drag.
        Put(resources, CrossfadeReadoutWidth, Math.Ceiling(ScaleText(74)));
        // Read the toolkit's unscaled sizes from its styles, outside our
        // overrides, so picking another size cannot scale them twice.
        foreach (string key in new[] { "ControlContentThemeFontSize", "ToolTipContentThemeFontSize", "TabItemHeaderFontSize" })
        {
            if (TextSizePercent == NormalTextSize) resources.Remove(key);
            else if (application.Styles.TryGetResource(key, application.ActualThemeVariant, out object? value) && value is double size)
                Put(resources, key, ScaleText(size));
        }
    }

    /// <summary>The saved Material mode: system, light or dark.</summary>
    public static string Mode { get; private set; } = AppearanceModes.System;

    /// <summary>True when the mode decides anything: Material is worn and the launch did not force it.</summary>
    public static bool CanChooseMode => !Overridden && Current.Id == SkinPackage.DefaultId;

    /// <summary>Material's light palette, read once from the window's own assembly.</summary>
    internal static SkinEntry MaterialLight => LightMaterial.Value;

    private static SkinEntry ReadLightMaterial()
    {
        using Stream? stream = typeof(SkinService).Assembly.GetManifestResourceStream(
            "OpenXLR.UI.Assets.Appearance.material-light.json");
        if (stream is null) return new(SkinPackage.Default, ["The Material light palette is missing from this build."]);
        using var reader = new StreamReader(stream);
        SkinReadResult result = SkinReader.Read("material-light", reader.ReadToEnd(), SkinOrigin.BuiltIn, null);
        return new(result.Package ?? SkinPackage.Default, result.Errors);
    }

    /// <summary>The skin in force.</summary>
    public static SkinEntry Current { get; private set; } = new(SkinPackage.Default, []);

    /// <summary>
    /// The desktop layout of the skin in force, or null for the classic one.
    /// Material's light palette is colours only, so in either mode Material
    /// has none. The window reads it when <see cref="Changed"/> fires and
    /// moves its sections only when the arrangement it resolves to differs.
    /// </summary>
    public static SkinLayout? Layout => Current.Package.Layout;

    /// <summary>
    /// What went wrong applying <see cref="Current"/>: the reader's complaints
    /// plus anything the images or the bridged keys could not take. Empty when
    /// the skin applied cleanly. Options shows these; the mixer window does not.
    /// </summary>
    public static IReadOnlyList<string> Errors { get; private set; } = [];

    /// <summary>True when the launch override decided the appearance, so Options can say so.</summary>
    public static bool Overridden { get; private set; }

    /// <summary>
    /// True while the mixer wears Touch sizing: the sizing tokens are raised
    /// to <see cref="SkinTokens.TouchMinimums"/>, and a skin value above a
    /// floor is kept.
    /// </summary>
    public static bool TouchControls { get; private set; }

    /// <summary>
    /// True when the main window's controls take enlarged targets: under Touch
    /// sizing, or when the skin in force sets its own control minimum.
    /// </summary>
    public static bool LargeTargets => TouchControls
        || _palette.Tokens.GetValueOrDefault("Ox.Mixer.ControlMinSize") is SkinNumber { Value: > 0 };

    /// <summary>The token values in force: the skin, or Material's light palette in its place.</summary>
    private static SkinPackage _palette = SkinPackage.Default;

    /// <summary>Raised on the UI thread after a skin is applied, for what resources cannot reach.</summary>
    public static event Action? Changed;

    /// <summary>How many handlers <see cref="Changed"/> holds now, for the tests that prove a switch leaves none behind.</summary>
    internal static int ChangedSubscribers => Changed?.GetInvocationList().Length ?? 0;

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
    /// override wins: <c>OPENXLR_SKIN=default</c> starts the application in
    /// dark Material, whatever the saved choice and mode, which is the way back
    /// from a skin that made something unreadable. With no override and no
    /// saved choice the window starts on Broadcast; nothing is written for
    /// that, so the file stays as the user left it.
    /// </summary>
    public static void Initialize()
    {
        UiSettings settings = UiSettings.Load();
        TouchControls = settings.TouchControls;
        TextSizePercent = settings.TextSizePercent;
        if (Application.Current is { } app) ApplyTextResources(app);
        string? id = Environment.GetEnvironmentVariable(OverrideVariable);
        Overridden = id is { Length: > 0 };
        if (!ReferenceEquals(_application, Application.Current))
        {
            if (_application is not null) _application.ActualThemeVariantChanged -= OnThemeVariantChanged;
            _application = Application.Current;
            if (_application is not null) _application.ActualThemeVariantChanged += OnThemeVariantChanged;
        }
        ApplyMode(settings.AppearanceMode, Resolve(Overridden ? id : settings.Skin, Overridden));
    }

    /// <summary>
    /// The skin a launch wears. A skin named by the override or saved in
    /// ui.json is found by its id, Material when the machine has no such skin.
    /// No choice at all, an absent or empty <c>skin</c>, is Broadcast, or
    /// Material when Broadcast is missing or its document cannot be read.
    /// </summary>
    internal static SkinEntry Resolve(string? id, bool overridden)
    {
        var material = new SkinEntry(SkinPackage.Default, []);
        if (overridden || id is { Length: > 0 }) return SkinCatalog.Find(id) ?? material;
        return SkinCatalog.Find(SkinPackage.StartingId) is { Usable: true } starting ? starting : material;
    }

    /// <summary>The desktop switched between light and dark; Material in system mode follows.</summary>
    private static void OnThemeVariantChanged(object? sender, EventArgs args)
    {
        if (!_settingVariant && CanChooseMode && Mode == AppearanceModes.System) Apply(Current);
    }

    /// <summary>
    /// Wear a skin in a mode without saving either. The toolkit's own controls
    /// are asked for the matching variant: Material in light or dark holds that
    /// variant, Material in system and every other skin leave it to the
    /// desktop, and the launch override holds dark.
    /// </summary>
    internal static IReadOnlyList<string> ApplyMode(string? mode, SkinEntry entry)
    {
        Mode = AppearanceModes.Normalize(mode);
        if (Application.Current is { } application)
        {
            _settingVariant = true;
            try
            {
                application.RequestedThemeVariant = entry.Id != SkinPackage.DefaultId ? ThemeVariant.Default
                    : Overridden || Mode == AppearanceModes.Dark ? ThemeVariant.Dark
                    : Mode == AppearanceModes.Light ? ThemeVariant.Light : ThemeVariant.Default;
            }
            finally { _settingVariant = false; }
        }
        return Apply(entry);
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
        LayoutIconRegistry.UseSkin(entry.Package.Icons);
        if (Application.Current is not { } application)
        {
            Errors = errors;
            return errors;
        }

        // The images the last skin decoded stay alive until the new values are
        // in the resources, so nothing paints from a disposed bitmap.
        List<IDisposable> previous = [.. Images];
        Images.Clear();

        // Material wears its light palette in light mode, and in system mode
        // while the desktop prefers light. The palette is an overlay like any
        // skin, so a token it leaves out keeps its default.
        SkinPackage palette = entry.Package;
        if (entry.Id == SkinPackage.DefaultId && !Overridden
            && (Mode == AppearanceModes.Light
                || Mode == AppearanceModes.System && application.ActualThemeVariant == ThemeVariant.Light))
        {
            palette = MaterialLight.Package;
            errors.AddRange(MaterialLight.Errors);
        }
        _palette = palette;

        var realizer = new Realizer(palette, errors);
        IResourceDictionary resources = application.Resources;
        foreach (SkinToken token in SkinTokens.All)
        {
            SkinValue? value = palette.Tokens.GetValueOrDefault(token.Name);
            object? realized = value is null ? token.Default : realizer.Realize(token, value);
            // A value the realizer refused falls back to the default, so one
            // bad image never leaves a hole in the window.
            realized ??= token.Default;
            realized = Sized(token.Name, realized);
            if (IsFontSize(token) && realized is double size) realized = ScaleText(size);

            if (IsLive(token))
            {
                SolidColorBrush live = LiveBrush(token.Name);
                live.Color = realized is ISolidColorBrush solid ? solid.Color : Colors.Transparent;
                Put(resources, token.Name, live);
            }
            else if (realized is null) resources.Remove(token.Name);
            else Put(resources, token.Name, realized);

            ApplyBridges(token, realized, resources, errors);
        }

        ApplyControls(palette, application, resources, errors);
        DeckPalette.Publish(entry.Id, resources, errors);
        // The reader checked each icon's grammar; this is where Avalonia
        // builds it, so an entry it still refuses is reported like a bad token.
        foreach (string icon in entry.Package.Icons.Keys)
            if (!LayoutIconRegistry.SkinIconDraws(icon))
                errors.Add($"icons.{icon}: this path could not be drawn; the item keeps the built-in icon or its default.");

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
                    Put(resources, resourceKey, theme);
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

    /// <summary>
    /// Write a resource only when its value changes. Every write to the
    /// application's resources makes every control of every open window look
    /// its resources up again, so a skin switch that rewrote the values that
    /// stay the same, a live brush whose colour changed in place or a size
    /// that did not move, paid for each of them across the whole window.
    /// </summary>
    private static void Put(IResourceDictionary resources, object key, object value)
    {
        if (resources.TryGetValue(key, out object? existing) && Same(existing, value)) return;
        resources[key] = value;
    }

    /// <summary>
    /// The same object, or a value that draws the same: a number, a colour, a
    /// radius, a thickness or a weight, or a flat colour brush of the same
    /// colour. An image or a gradient is written again, so a brush is never
    /// left holding a bitmap the last skin disposed.
    /// </summary>
    private static bool Same(object? existing, object value) => ReferenceEquals(existing, value)
        || value is double or Color or CornerRadius or Thickness or FontWeight && Equals(existing, value)
        || existing is ImmutableSolidColorBrush a && value is ImmutableSolidColorBrush b
            && a.Color == b.Color && a.Opacity == b.Opacity && a.Transform is null && b.Transform is null;

    private static void ApplyBridges(SkinToken token, object? realized,
        IResourceDictionary resources, List<string> errors)
    {
        foreach (SkinBridge bridge in token.Bridges)
        {
            if (realized is null) { resources.Remove(bridge.Key); continue; }
            if (!bridge.AsColor) { Put(resources, bridge.Key, realized); continue; }
            if (realized is ISolidColorBrush solid) Put(resources, bridge.Key, solid.Color);
            else errors.Add($"{token.Name}: the theme reads this as a flat colour, so a gradient or image is ignored.");
        }
    }

    /// <summary>
    /// Save this skin as the one to wear and put it on. The choice lives in
    /// ui.json alone: it is not part of the mixer layout, the daemon's
    /// preferences or any audio profile, so switching appearance never touches
    /// what is playing. Material is saved by its id like any other skin, since
    /// no saved choice now means Broadcast. The skin goes on even when the
    /// choice cannot be saved; <paramref name="saveError"/> then says why. A
    /// choice made here replaces the launch override for the rest of the run.
    /// </summary>
    public static IReadOnlyList<string> Choose(string id, out string? saveError)
    {
        SkinEntry entry = SkinCatalog.Find(id) ?? new SkinEntry(SkinPackage.Default, []);
        saveError = (UiSettings.Load() with { Skin = entry.Id }).Save();
        Overridden = false;
        return ApplyMode(Mode, entry);
    }

    /// <summary>
    /// Save Material's mode and put it on. Like the skin, it lives in ui.json
    /// alone and goes on even when it cannot be saved; <paramref name="saveError"/>
    /// then says why.
    /// </summary>
    public static IReadOnlyList<string> ChooseMode(string mode, out string? saveError)
    {
        if (!AppearanceModes.IsValid(mode)) throw new ArgumentException($"Unknown appearance mode \"{mode}\".", nameof(mode));
        saveError = (UiSettings.Load() with { AppearanceMode = mode }).Save();
        Overridden = false;
        return ApplyMode(mode, Current);
    }

    /// <summary>
    /// Save the mixer's control sizing and put it on. Like the skin, it lives
    /// in ui.json alone and goes on even when it cannot be saved;
    /// <paramref name="saveError"/> then says why.
    /// </summary>
    public static void ChooseControlSizing(bool touch, out string? saveError)
    {
        saveError = (UiSettings.Load() with { TouchControls = touch }).Save();
        ApplyControlSizing(touch);
    }

    /// <summary>
    /// Wear Standard or Touch sizing without saving it. Only the sizing
    /// resources are written again; colours, images and the controls stay
    /// the objects they were.
    /// </summary>
    internal static void ApplyControlSizing(bool touch)
    {
        TouchControls = touch;
        if (Application.Current is { } application)
            foreach (SkinToken token in SkinTokens.All)
            {
                if (!SkinTokens.TouchMinimums.ContainsKey(token.Name)) continue;
                object? value = _palette.Tokens.GetValueOrDefault(token.Name) is SkinNumber number
                    ? number.Value : token.Default;
                value = Sized(token.Name, value);
                if (value is null) application.Resources.Remove(token.Name);
                else Put(application.Resources, token.Name, value);
            }
        Changed?.Invoke();
    }

    /// <summary>
    /// A sizing token's value with the Touch floor under it. A slider is also
    /// kept at least as tall as the control minimum and the fader cap the
    /// skin draws, so an enlarged cap is never clipped by its own row.
    /// </summary>
    private static object? Sized(string name, object? value)
    {
        if (!SkinTokens.TouchMinimums.TryGetValue(name, out double floor)) return value;
        double minimum = TouchControls ? floor : 0;
        if (name is "Ox.Mixer.SliderMinHeight" or "Ox.Mixer.DeviceSliderHeight")
        {
            if (_palette.Tokens.GetValueOrDefault("Ox.Mixer.ControlMinSize") is SkinNumber control)
                minimum = Math.Max(minimum, control.Value);
            if (minimum > 0 && _palette.Tokens.GetValueOrDefault("Ox.Fader.Thumb.Height") is SkinNumber thumb)
                minimum = Math.Max(minimum, thumb.Value);
        }
        return minimum > 0 ? Math.Max(value is double size ? size : 0, minimum) : value;
    }

    /// <summary>Read the skin folders again and put the current choice back on.</summary>
    public static IReadOnlyList<string> Reload() =>
        ApplyMode(Mode, SkinCatalog.Find(Current.Id) ?? new SkinEntry(SkinPackage.Default, []));

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
