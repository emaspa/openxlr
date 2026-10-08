using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// The window's text catalogue: every key the window reads is in the English
/// catalogue and every entry is read, markup carries no text of its own, a
/// translation keeps the English keys and placeholders, a missing entry reads
/// English, and the language comes from the saved choice, then the desktop.
/// </summary>
public sealed class LocalizationTests
{
    private static readonly string[] Shipped = ["en", "de", "pt", "zh-Hans", "zh-Hant"];

    private static string Root
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "localization.md")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            return directory.FullName;
        }
    }

    private static string Folder => Path.Combine(Root, "src", "OpenXLR.UI", "Localization");

    private static IEnumerable<string> Sources => Directory
        .EnumerateFiles(Path.Combine(Root, "src", "OpenXLR.UI"), "*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static Dictionary<string, string> Read(string file)
    {
        XElement[] data = [.. XDocument.Load(Path.Combine(Folder, file)).Root!.Elements("data")];
        Assert.Equal(data.Length, data.Select(d => d.Attribute("name")!.Value).Distinct(StringComparer.Ordinal).Count());
        return data.ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> English => Read("Strings.resx");

    [Fact]
    public void EveryKeyTheWindowReadsIsInTheCatalogueAndEveryEntryIsRead()
    {
        var english = English;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var lookup = new Regex("loc:Text Key=([A-Za-z0-9]+)|Localizer\\.(?:Text|Format)\\(\"([A-Za-z0-9]+)\"");
        foreach (string file in Sources)
        {
            string source = File.ReadAllText(file);
            foreach (Match match in lookup.Matches(source))
            {
                string key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                Assert.True(english.ContainsKey(key), $"{Path.GetFileName(file)} reads {key}, which Strings.resx lacks.");
                used.Add(key);
            }
            // A key built at run time cannot be checked here, so every lookup names its key.
            if (!file.EndsWith("Localizer.cs", StringComparison.Ordinal))
                Assert.DoesNotMatch("Localizer\\.(?:Text|Format)\\((?!\")", source);
        }
        Assert.Empty(english.Keys.Except(used).Order());
    }

    [Fact]
    public void MarkupHoldsNoTextOfItsOwn()
    {
        // Names, versions and commands that read the same in every language.
        string[] literal = ["OpenXLR", "v0.0.0", "github.com/emaspa/openxlr", "systemctl --user restart openxlr-daemon", "LV2", "CLAP", "VST3"];
        string[] properties = ["Text", "Content", "Title", "ToolTip.Tip", "PlaceholderText", "Watermark", "Header", "AutomationProperties.Name"];
        foreach (string file in Sources.Where(f => f.EndsWith(".axaml", StringComparison.Ordinal)))
            foreach (XAttribute attribute in XDocument.Load(file).Descendants().Attributes())
            {
                string value = attribute.Value;
                string where = $"{Path.GetFileName(file)}: {attribute.Name.LocalName}=\"{value}\"";
                if (properties.Contains(attribute.Name.LocalName) && !value.StartsWith('{') && !literal.Contains(value))
                    Assert.False(Regex.IsMatch(value, "[A-Za-z]{2,}"), $"{where} is text; read it from the catalogue.");
                // A binding's StringFormat holds no words: StringFormat={loc:Text Key=...} does.
                Match format = Regex.Match(value, "StringFormat='([^']*)'");
                if (format.Success)
                    Assert.False(Regex.IsMatch(format.Groups[1].Value, "[A-Za-z]{2,}"), $"{where} formats with text.");
            }
    }

    [Fact]
    public void EnglishEntriesAreFormatsWithPlainPunctuation()
    {
        foreach ((string key, string text) in English)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), key);
            Assert.Equal(text.Trim(), text);
            Assert.False(key.StartsWith("Ox.", StringComparison.Ordinal), key);
            // Straight quotes and apostrophes, and no dashes used as dashes.
            Assert.DoesNotMatch("[\\u2018\\u2019\\u201C\\u201D\\u2013\\u2014]", text);
            _ = CompositeFormat.Parse(text);
        }
    }

    [Fact]
    public void EveryTranslationIsListedAndKeepsTheEnglishKeysAndPlaceholders()
    {
        var english = English;
        string[] files = [.. Directory.EnumerateFiles(Folder, "Strings.*.resx").Select(Path.GetFileName).OfType<string>()];
        string[] tags = [.. files.Select(f => f["Strings.".Length..^".resx".Length])];
        Assert.Equal(["en", .. tags.Order(StringComparer.Ordinal)],
            [Localizer.Languages[0].Id!, .. Localizer.Languages.Skip(1).Select(c => c.Id!).Order(StringComparer.Ordinal)]);
        Assert.All(Localizer.Languages, c => Assert.False(string.IsNullOrWhiteSpace(c.Label)));
        static string Holes(string text) => string.Join(",", Regex.Matches(text, @"\{(\d+)[,:}]")
            .Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal));
        foreach (string file in files)
            foreach ((string key, string text) in Read(file))
            {
                Assert.True(english.ContainsKey(key), $"{file} has {key}, which English does not.");
                Assert.False(string.IsNullOrWhiteSpace(text), $"{file}: {key}");
                Assert.Equal(Holes(english[key]), Holes(text));
                _ = CompositeFormat.Parse(text);
            }
    }

    [Fact]
    public void AMissingEntryReadsEnglishAndAnUnknownKeyReadsItself()
    {
        // No catalogue ships for German or Brazilian Portuguese: every entry falls back.
        Assert.Equal("Close", Localizer.Get("Close", CultureInfo.GetCultureInfo("de")));
        Assert.Equal("Close", Localizer.Get("Close", CultureInfo.GetCultureInfo("pt-BR")));
        Assert.Equal("NoSuchKey", Localizer.Text("NoSuchKey"));
        Assert.Equal("Low Cut 80", Localizer.Format("LowCutFrequency", 80));
        Assert.Equal("Close", new TextExtension { Key = "Close" }.ProvideValue(null!));
    }

    [Theory]
    // The saved choice wins over the desktop, and a regional tag falls back to its language.
    [InlineData("de", "LANG=pt_BR.UTF-8", "de")]
    [InlineData("de-AT", "LANG=pt_BR.UTF-8", "de")]
    [InlineData("pt-BR", "", "pt")]
    [InlineData("it", "LANG=de_DE.UTF-8", "en")]
    [InlineData("../../de", "LANG=de_DE.UTF-8", "en")]
    // No saved choice, or system, follows the desktop.
    [InlineData(null, "LANG=de_DE.UTF-8", "de")]
    [InlineData("system", "LANG=de_AT.UTF-8@euro", "de")]
    [InlineData("", "LANG=it_IT.UTF-8", "en")]
    [InlineData(null, "", "en")]
    // LC_ALL, then LC_MESSAGES, then LANG.
    [InlineData(null, "LANG=de_DE.UTF-8 LC_MESSAGES=pt_PT.UTF-8", "pt")]
    [InlineData(null, "LANG=it_IT.UTF-8 LC_MESSAGES=pt_PT.UTF-8 LC_ALL=de_CH.UTF-8", "de")]
    // LANGUAGE is a list tried in order, ignored under the C locale.
    [InlineData(null, "LANG=en_US.UTF-8 LANGUAGE=it:de:pt", "de")]
    [InlineData(null, "LANG=de_DE.UTF-8 LANGUAGE=it_IT", "de")]
    [InlineData(null, "LANG=C.UTF-8 LANGUAGE=de", "en")]
    [InlineData(null, "LANGUAGE=de", "en")]
    // Chinese is chosen by script; a region implies one, and the old .NET names map to theirs.
    [InlineData(null, "LANG=zh_CN.UTF-8", "zh-Hans")]
    [InlineData(null, "LANG=zh_TW.UTF-8", "zh-Hant")]
    [InlineData(null, "LANG=zh_HK.UTF-8", "zh-Hant")]
    [InlineData("zh", "", "zh-Hans")]
    [InlineData("zh-Hant-CN", "", "zh-Hant")]
    [InlineData("zh-CHT", "", "zh-Hant")]
    [InlineData("ZH-chs", "", "zh-Hans")]
    public void TheLanguageComesFromTheSavedChoiceThenTheDesktop(string? saved, string environment, string expected)
    {
        Dictionary<string, string> variables = environment.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Split('=', 2)).ToDictionary(v => v[0], v => v[1]);
        Assert.Equal(expected, Localizer.Resolve(saved, n => variables.GetValueOrDefault(n), Shipped));
        // The window ships English alone today, so every case reads English.
        Assert.Equal("en", Localizer.Resolve(saved, n => variables.GetValueOrDefault(n),
            [.. Localizer.Languages.Select(c => c.Id!)]));
    }

    [Theory]
    [InlineData("zh_TW.UTF-8", "zh-Hant-TW")]
    [InlineData("zh-CN", "zh-Hans-CN")]
    [InlineData("zh-CHT", "zh-Hant")]
    [InlineData("zh-CHS", "zh-Hans")]
    [InlineData("de_DE.UTF-8@euro", "de-DE")]
    [InlineData("../de", "")]
    public void LocaleNamesBecomeTagsWithChineseScripts(string name, string tag)
        => Assert.Equal(tag, Localizer.Canonical(name));
}

/// <summary>The language choice in Options: saved in ui.json, read at the next start.</summary>
[Collection("xdg-config")]
public sealed class LanguageSettingsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "openxlr-language-" + Guid.NewGuid());
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public LanguageSettingsTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _home);
        Directory.CreateDirectory(UiSettings.ConfigDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
        Directory.Delete(_home, true);
    }

    private static string UiJson => Path.Combine(UiSettings.ConfigDir, "ui.json");

    private static OptionsViewModel Options()
    {
        var client = new DaemonClient();
        return new OptionsViewModel(client, new MainViewModel(client));
    }

    [Fact]
    public void TheChoiceIsSavedWithTheOtherPreferencesAndReadAtStartup()
    {
        File.WriteAllText(UiJson, """{ "skin": "nord", "futureChoice": 3 }""");
        OptionsViewModel options = Options();
        Assert.Null(options.SelectedLanguage!.Id);
        Assert.Equal(["System language", "English"], options.LanguageChoices.Select(c => c.Label));

        options.SelectedLanguage = options.LanguageChoices.Single(c => c.Id == "en");

        Assert.Null(options.PreferenceError);
        JsonObject saved = JsonNode.Parse(File.ReadAllText(UiJson))!.AsObject();
        Assert.Equal("en", (string?)saved["language"]);
        Assert.Equal("nord", (string?)saved["skin"]);
        Assert.Equal(3, (int?)saved["futureChoice"]);
        Assert.Equal("en", Options().SelectedLanguage!.Id);
        Localizer.Initialize();
        Assert.Equal("en", Localizer.Language);

        options.SelectedLanguage = options.LanguageChoices[0];
        Assert.Null(UiSettings.Load().Language);
    }

    [Fact]
    public void AFailedSaveShowsInThePreferenceLine()
    {
        Directory.CreateDirectory(UiJson);
        OptionsViewModel options = Options();

        options.SelectedLanguage = options.LanguageChoices.Single(c => c.Id == "en");

        Assert.Equal("en", options.SelectedLanguage!.Id);
        Assert.StartsWith("Could not save ui.json.", options.PreferenceError);
    }
}
