using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Resources;
using Avalonia.Markup.Xaml;

[assembly: NeutralResourcesLanguage("en")]

namespace OpenXLR.UI.Localization;

/// <summary>One entry in the language picker; a null id follows the desktop.</summary>
public sealed record LanguageChoice(string? Id, string Label);

/// <summary>
/// The window's text. Every string the window shows is a key in
/// Strings.resx, the English catalogue; a translation is a
/// Strings.&lt;tag&gt;.resx next to it with the same keys. The language is
/// chosen once, at startup, and only changes which catalogue is read: the
/// process culture, numbers sent to the daemon, saved names and text a plugin
/// supplies are left alone.
/// </summary>
public static class Localizer
{
    /// <summary>
    /// The languages the window ships, by catalogue tag, with the name each
    /// gives itself so a user can find their own language in any other one.
    /// English is the catalogue every other one falls back to. A translation
    /// adds its line here next to its Strings.&lt;tag&gt;.resx.
    /// </summary>
    internal static IReadOnlyList<LanguageChoice> Languages { get; } = Array.AsReadOnly<LanguageChoice>(
    [
        new("en", "English"),
    ]);

    internal static readonly ResourceManager Resources =
        new("OpenXLR.UI.Localization.Strings", typeof(Localizer).Assembly);

    private static CultureInfo _culture = CultureInfo.GetCultureInfo("en");

    /// <summary>The catalogue tag the window reads, such as <c>en</c>.</summary>
    public static string Language => _culture.Name;

    /// <summary>True when the tag names a catalogue the window ships.</summary>
    internal static bool IsSupported(string? language)
        => language is not null && Languages.Any(c => c.Id == language);

    /// <summary>
    /// Pick the catalogue for this run: the language saved in Options, else
    /// the desktop's message language. Call before the first window is built.
    /// </summary>
    internal static void Initialize()
        => _culture = CultureInfo.GetCultureInfo(
            Resolve(UiSettings.Load().Language, Environment.GetEnvironmentVariable,
                Languages.Select(c => c.Id!).ToArray()));

    /// <summary>
    /// The catalogue tag for a saved choice and the desktop's environment.
    /// A saved tag wins; a missing one, or <c>system</c>, follows
    /// <c>LANGUAGE</c> (a colon-separated list, first match wins), then
    /// <c>LC_ALL</c>, <c>LC_MESSAGES</c> and <c>LANG</c>, as gettext reads
    /// them. A regional tag the window does not ship falls back to its base
    /// language, and anything else to English. Only a tag in
    /// <paramref name="shipped"/> is ever returned, so no file or assembly
    /// name comes from the preference or the environment.
    /// </summary>
    internal static string Resolve(string? saved, Func<string, string?> environment, IReadOnlyList<string> shipped)
    {
        if (!string.IsNullOrWhiteSpace(saved) && saved != "system")
            return Match(saved, shipped) ?? "en";
        // gettext ignores LANGUAGE when the locale is C or POSIX.
        string? locale = FirstSet(environment, "LC_ALL", "LC_MESSAGES", "LANG");
        if (locale is not null && !IsCLocale(locale) && environment("LANGUAGE") is { Length: > 0 } list)
            foreach (string entry in list.Split(':', StringSplitOptions.RemoveEmptyEntries))
                if (Match(entry, shipped) is { } listed) return listed;
        return locale is null || IsCLocale(locale) ? "en" : Match(locale, shipped) ?? "en";
    }

    private static string? FirstSet(Func<string, string?> environment, params string[] names)
        => names.Select(environment).FirstOrDefault(value => !string.IsNullOrEmpty(value));

    private static bool IsCLocale(string locale)
        => Canonical(locale).ToUpperInvariant() is "C" or "POSIX";

    /// <summary>
    /// The shipped tag for one locale name, trying the full tag and then each
    /// shorter one: <c>de_AT.UTF-8</c> reads <c>de-AT</c>, then <c>de</c>.
    /// </summary>
    internal static string? Match(string name, IReadOnlyList<string> shipped)
    {
        string tag = Canonical(name);
        while (tag.Length > 0)
        {
            string? found = shipped.FirstOrDefault(s => string.Equals(s, tag, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;
            int cut = tag.LastIndexOf('-');
            tag = cut < 0 ? "" : tag[..cut];
        }
        return null;
    }

    /// <summary>
    /// A POSIX locale or BCP 47 name as a BCP 47 tag with Chinese given its
    /// script: <c>zh_TW.UTF-8</c> becomes <c>zh-Hant-TW</c>, <c>zh-CN</c>
    /// becomes <c>zh-Hans-CN</c>, and the legacy .NET names <c>zh-CHT</c> and
    /// <c>zh-CHS</c> become <c>zh-Hant</c> and <c>zh-Hans</c>. Names that are
    /// not tags come back empty.
    /// </summary>
    internal static string Canonical(string name)
    {
        string tag = name.Trim();
        int cut = tag.IndexOfAny(['.', '@']);
        if (cut >= 0) tag = tag[..cut];
        tag = tag.Replace('_', '-');
        if (tag.Length == 0 || !tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return "";
        string[] parts = tag.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (!parts[0].Equals("zh", StringComparison.OrdinalIgnoreCase)) return string.Join('-', parts);
        string[] rest = parts.Skip(1).ToArray();
        if (rest.Any(p => p.Equals("Hant", StringComparison.OrdinalIgnoreCase) || p.Equals("Hans", StringComparison.OrdinalIgnoreCase)))
            return string.Join('-', parts);
        if (rest is [var legacy] && legacy.Equals("CHT", StringComparison.OrdinalIgnoreCase)) return "zh-Hant";
        if (rest is [var legacy2] && legacy2.Equals("CHS", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
        bool traditional = rest.Any(p => p.ToUpperInvariant() is "TW" or "HK" or "MO");
        return string.Join('-', new[] { "zh", traditional ? "Hant" : "Hans" }.Concat(rest));
    }

    /// <summary>
    /// The text for a key in the window's language. A key the translation
    /// lacks reads from the English catalogue; a key English lacks too comes
    /// back as the key itself, so a mistake shows instead of failing.
    /// </summary>
    public static string Text(string key) => Get(key, _culture);

    /// <summary><see cref="Text"/> with its {0} placeholders filled, numbers in the user's own format.</summary>
    public static string Format(string key, params object?[] arguments)
        => string.Format(CultureInfo.CurrentCulture, Text(key), arguments);

    internal static string Get(string key, CultureInfo culture) => Resources.GetString(key, culture) ?? key;
}

/// <summary>
/// Window text in markup: <c>Text="{loc:Text Key=Close}"</c>. The string is
/// read once when the control is built, in the language chosen at startup.
/// It never goes through the application's resources, which belong to the
/// skin tokens.
/// </summary>
public sealed class TextExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Localizer.Text(Key);
}
