# Localization

The UI can be translated. Every string it shows lives in one
English catalogue, and a language is a second catalogue with the same keys.
OpenXLR ships in English, German (Deutsch), Spanish (Español), French (Français), Italian (Italiano), Tagalog, Cebuano (Bisaya) and Serbian (Cyrillic). Other
languages come from contributors who read and write them, one pull request
per language.

## The catalogue

`src/OpenXLR.UI/Localization/Strings.resx` holds the UI's English text.
Each entry is a key and a value:

```xml
<data name="Close" xml:space="preserve">
  <value>Close</value>
</data>
<data name="InsertsTitle" xml:space="preserve">
  <value>{0} inserts</value>
</data>
```

Markup reads an entry through the `loc:` extension, declared on the
window's root element:

```xml
<Window xmlns:loc="using:OpenXLR.UI.Localization" ...>
  <Button Content="{loc:Text Key=Close}"/>
  <Window Title="{Binding Title, StringFormat={loc:Text Key=InsertsTitle}}"/>
```

Code reads it through `Localizer`:

```csharp
Status = Localizer.Text("Scanning");
Note = Localizer.Format("PluginsInChain", count);   // "{0} plugins in chain"
```

`Localizer.Format` fills `{0}`, `{1}` and so on, numbers in the user's own
format. The text goes straight into the control. It never passes through
the application's resources, which belong to the skin tokens.

Some text stays as it is in every language: product and format names
(OpenXLR, LV2, CLAP, VST3), versions and commands, names you
give channels, mixes and profiles, text a plugin supplies, messages the
daemon sends, the diagnostics archive, and the messages a skin author
reads when a skin does not load. The terminal mixer and the OpenDeck plugin
are not part of the catalogue.

Routing hints keep the mixer's default channel and mix names, such as
`System`, so they match the labels the user sees.

## Adding or changing a string

1. Add an entry to `Strings.resx`, or change the value of an existing one.
   Keys are letters and digits; values use straight quotes and
   apostrophes, and placeholders for anything that changes.
2. Read it by its literal key: `{loc:Text Key=MyKey}` in markup,
   `Localizer.Text("MyKey")` in code. Keep the key in the call, not in a
   variable, so the tests can see it.
3. Run the tests. `LocalizationTests` fails when the UI reads a key
   the catalogue lacks, when an entry is never read, when markup carries
   text of its own, or when a key is built at run time. Its line-break
   and literal-command checks run per language from `InlineData` rows
   that name `de`, `es`, `fr`, `it`, `tl`, `ceb` and `sr-Cyrl`; add a row for the new language. The
   layout tests measure the fixed-width captions of the main window and
   Options in every shipped language, whichever language the run uses.
   Keep literal English expectations in UI tests for English runs so a wrong
   catalogue value fails; use localized expectations for non-English runs.

A translation that has the old wording of a changed entry still shows it,
so a change of meaning gets a new key.

## The browser companion

The browser companion in `browser/chromium` has its own catalogue in Chrome's
format: `_locales/<locale>/messages.json`, one folder per language. The
English text also lives in `browser/chromium/text.mjs`, which answers where
Chrome's lookup is missing, and `browser/tests/text.test.mjs` keeps the two
equal and every language complete. Chrome's locale names differ from the
UI's: Tagalog is `fil` and Serbian is `sr`, which Chrome shows in Cyrillic.
Chrome has no Cebuano. Keep each `$1` as written.

## Adding a language

1. Copy `src/OpenXLR.UI/Localization/Strings.resx` to
   `Strings.<tag>.resx` in the same folder. The tag is the language's
   BCP 47 tag: `de`, `fr`, `pt-BR`, `zh-Hans`.
2. Translate each `<value>`. Keep every `name` as it is. Keep each
   placeholder, `{0}` or `{1:0.##}`, exactly as written; the order inside
   the sentence can change. Keep line breaks where the English has them.
   An entry you leave out shows in English, so a partial catalogue works.
3. Add the language to `Localizer.Languages` in
   `src/OpenXLR.UI/Localization/Localizer.cs`, with the tag and the
   language's name for itself:

   ```csharp
   new("en", "English"),
   new("de", "Deutsch"),
   new("es", "Español"),
   new("fr", "Français"),
   new("it", "Italiano"),
   ```

4. Build and test:

   ```sh
   dotnet build src/OpenXLR.slnx -c Release -warnaserror
   dotnet test src/OpenXLR.slnx -c Release --no-build
   ```

   The tests check that the language is listed, that the catalogue has no
   key English lacks, and that every placeholder of the English entry is
   in the translation.
5. Run the UI in the language, with **System language** chosen in
   Options, and go through each window at its smallest size: Options, the layout editor, the plugin windows and the
   dialogs. A longer caption should wrap, not clip.

   ```sh
   LANG=en_US.UTF-8 LANGUAGE=<tag> dotnet run --project src/OpenXLR.UI -c Release
   ```

6. Open a pull request with the one language. Say which windows you
   looked at, and whether a second speaker has read it.

## What the UI does when an entry is missing

An entry missing from a translation reads from the English catalogue. A
key missing from English shows as the key itself, so the mistake is
visible rather than a crash; the tests catch it before a release.

## How the language is chosen

The UI chooses its language once, when it starts:

1. The language picked in Options, APPEARANCE, **Language**, saved as
   `language` in `~/.config/openxlr/ui.json`.
2. With no saved language, or **System language**, the desktop's message
   language: the `LANGUAGE` list in order, then `LC_ALL`, `LC_MESSAGES`
   and `LANG`, as gettext reads them. `LANGUAGE` is ignored when the
   locale is `C` or `POSIX`, or when none of the three is set.
3. Filipino desktop locales (`fil`, including `fil_PH.UTF-8`) select
   the Tagalog catalogue (`tl`). Cebuano (`ceb`, including `ceb_PH.UTF-8`)
   is listed as Bisaya (Cebuano), distinguishing it from other Visayan languages.
4. A regional tag the UI does not ship reads its base language, so
   `de_AT.UTF-8` gets `de`. Chinese is matched by script: `zh_TW`,
   `zh_HK` and `zh_MO` mean Traditional (`zh-Hant`), other regions mean
   Simplified (`zh-Hans`), and the old .NET names `zh-CHT` and `zh-CHS`
   map to the same two.
5. Serbian without a script (`sr`, including `sr_RS.UTF-8`) selects
   Cyrillic (`sr-Cyrl`), including `sr_ME.UTF-8`. An explicit script is
   respected: `sr-Latn`, POSIX `sr_RS.UTF-8@latin` and the older spellings
   `sr@Latn` and `sr_RS@Latn` do not select Cyrillic. The `@latin` and
   `@Latn` modifiers are case-insensitive and also work with an encoding
   suffix, such as `sr_RS.UTF-8@Latn`. Latin Serbian is not shipped;
   unsupported choices follow the fallback rules below.
6. Anything else is English. Malformed tags, including empty subtags, are
   ignored; an invalid entry in `LANGUAGE` does not stop later entries
   from being tried.

A change in Options applies the next time the UI starts. Only the
catalogue changes: numbers, dates and the values the UI sends to the
daemon keep the format they had.
The picker shows the resolved language for a saved regional tag without
rewriting it. The system choice continues to follow the desktop.

## Scripts and layout

Text aligns by its own script: a right-to-left caption, or a channel named
in Arabic or Hebrew, starts at the right edge. The UI's layout stays
left to right, since channel order and the left and right of audio do not
change with the language. The UI draws with the Inter font and takes
other scripts from the desktop's fonts, so a language in another script
needs a font for it installed, such as Noto.
