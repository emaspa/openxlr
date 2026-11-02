namespace OpenXLR.Tui;

/// <summary>
/// The appearance and what the terminal knows about the connection. The skin
/// list is the same one the window shows, read from the same folders, and the
/// choice is written back to the same file, so picking Gruvbox here is
/// picking it in the window too. Material's mode is the same shared choice.
/// What feeds XLR 1 and XLR 2 is chosen on the Inputs tab. The block size
/// is the daemon's, the same one the window's Options set, and so is where
/// the Wave XLR Pro's jack mixes are built (hardware mixing), with the same
/// dry run before the switch to the unit.
/// </summary>
internal sealed class OptionsView : View
{
    private static readonly string[] Modes = [AppearanceModes.System, AppearanceModes.Light, AppearanceModes.Dark];
    private static readonly string[] ModeNames = ["System", "Light", "Dark"];
    private readonly RowList _list = new();
    private IReadOnlyList<SkinEntry> _skins = SkinCatalog.Scan();

    public override string Title => "Options";

    public override string Keys => "Left/Right change  Enter use skin  R reload skins";

    public override void Draw(Screen screen, Rect area, App app)
    {
        _list.Draw(screen, area, app.Theme, Build(app), labelWidth: 26);
    }

    public override bool Handle(KeyPress key, App app)
    {
        if (key.Key == Key.Char && key.Char == 'R')
        {
            _skins = SkinCatalog.Scan();
            app.Say($"{_skins.Count} skins");
            return true;
        }
        return _list.Handle(key, Build(app));
    }

    private List<Row> Build(App app)
    {
        List<Row> rows = [];
        if (State(app)?.Mixer is { BlockSizes.Count: > 0 } mixer)
        {
            string[] names = ["automatic", .. mixer.BlockSizes.Select(f => $"{f} samples ({f / 48.0:0.#} ms)")];
            int at = mixer.BlockSize is int forced ? mixer.BlockSizes.IndexOf(forced) + 1 : 0;
            rows.Add(new HeadingRow("Audio"));
            rows.Add(new ChoiceRow("Block size", names, at, index =>
            {
                int? frames = index == 0 ? null : mixer.BlockSizes[index - 1];
                app.Link.Send("setBlockSize", body => body["value"] = frames);
                app.Say(frames is int f ? $"Block size {f} samples; the device reopens" : "Block size left to PipeWire");
            }));
        }
        if (State(app) is { } current && (current.Can("hardwareMixing") || current.Mixer.HardwareMixing is { Wanted: true }))
            rows.AddRange(HardwareMixingRows(app, current.Mixer.HardwareMixing));
        rows.AddRange(
        [
            new HeadingRow("Appearance"),
            new ChoiceRow("Material mode", ModeNames, Array.IndexOf(Modes, app.AppearanceMode), index =>
            {
                app.UseAppearance(Modes[index]);
                string chosen = $"Material mode {ModeNames[index]}";
                if (!UiSettingsFile.WriteAppearanceMode(Modes[index]))
                    app.Say($"{chosen} is on for this run, the saved choice could not be written");
                else if (app.Theme.Id != "default")
                    app.Say($"{chosen} is saved; it applies when Material is the skin");
                else app.Say($"{chosen} is the appearance here and in the window");
            }),
        ]);
        foreach (SkinEntry skin in _skins) rows.Add(new SkinRow(skin, app));

        rows.Add(new HeadingRow("Daemon"));
        Snapshot? state = State(app);
        rows.Add(new TextRow("Connection", app.Link.Status));
        rows.Add(new TextRow("Version", state?.DaemonVersion is { Length: > 0 } version ? version : "unknown"));
        if (state?.Device is { } device)
            rows.Add(new TextRow("Interface", $"{device.Vendor} {device.Model} ({device.UsbId})"));
        if (state?.Warning is { Length: > 0 } warning) rows.Add(new TextRow("Warning", warning));
        if (state?.Mixer.LayoutWarning is { Length: > 0 } layout) rows.Add(new TextRow("Layout", layout));
        rows.Add(new ActionRow("Fresh state", "refresh", () =>
        {
            app.Link.Send("getState");
            app.Say("Asked for a state");
        }));

        return rows;
    }

    private static readonly string[] MixingSides = ["This computer", "This unit"];

    /// <summary>
    /// Experimental hardware mixing on the Wave XLR Pro, as the window's
    /// Options offer it: the choice, why a wanted mode waits, and before the
    /// switch to the unit the dry run's list of what this layout loses, with
    /// the one row that switches. Back to this computer needs no check.
    /// </summary>
    private static IEnumerable<Row> HardwareMixingRows(App app, HardwareMixingEntry? mode)
    {
        bool onUnit = mode?.Wanted == true;
        yield return new HeadingRow("Hardware mixing (experimental)");
        yield return new ChoiceRow("Pro output mixes", MixingSides, onUnit ? 1 : 0, index =>
        {
            if (index == 1 && !onUnit)
            {
                app.Link.CheckMixing();
                app.Say("Checking this layout; the list below says what changes, and Mix on the unit switches");
            }
            else if (index == 0 && onUnit)
            {
                app.Link.Send("setMixing", body => body["value"] = "software");
                app.Link.ForgetMixingPlan();
                app.Say("This computer builds the mixes again, and the unit is put back as it was");
            }
        });
        if (mode is { Active: true })
            yield return new TextRow("Running", "the Wave XLR Pro builds the mixes its jacks play");
        else if (onUnit)
            yield return new TextRow("Waiting", mode?.Note ?? "the Wave XLR Pro builds the mixes once it is ready");
        if (onUnit) yield break;

        if (app.Link.CheckingMixing)
        {
            yield return new TextRow("Check", "asking the daemon");
            yield break;
        }
        if (app.Link.MixingPlan is not { } plan) yield break;
        foreach ((_, string sentence) in plan.Disabled.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            yield return new TextRow("Changes", sentence);
        foreach (string jack in new[] { "hp1", "hp2", "lineout" })
            if (plan.EntryOffsetsDb.TryGetValue(jack, out double db) && db > 0)
                yield return new TextRow(JackName(jack), $"turned down {db:0.#} dB first, so nothing gets louder");
        if (plan.Reason == "budget")
        {
            yield return new TextRow("Refused", plan.Note ?? "this layout has more channels than USB pairs");
            yield break;
        }
        if (!plan.Active)
            yield return new TextRow("Not yet", plan.Note ?? "the Wave XLR Pro is not ready");
        yield return new ActionRow("Mix on the unit", "switch", () =>
        {
            app.Link.Send("setMixing", body => body["value"] = "hardware");
            app.Link.ForgetMixingPlan();
            app.Say("The Wave XLR Pro builds the mixes its jacks play; This computer switches back");
        });
    }

    private static string JackName(string jack) => jack switch
    {
        "hp1" => "Headphones 1",
        "hp2" => "Headphones 2",
        _ => "Line Out",
    };

    /// <summary>One skin in the picker.</summary>
    private sealed class SkinRow(SkinEntry skin, App app) : Row(skin.Name)
    {
        public override void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused)
        {
            bool chosen = app.Theme.Id == skin.Id;
            Widgets.Lamp(screen, x, y, chosen, theme, back);
            string note = skin.Description is { Length: > 0 } text ? text : skin.Id;
            screen.Text(x + 2, y, note, chosen ? theme.TextDetail : theme.TextMuted, back, bold: focused,
                maxWidth: Math.Max(4, width - 4));
        }

        public override bool Handle(KeyPress key)
        {
            if (key.Key != Key.Enter && !(key.Key == Key.Char && key.Char == ' ')) return false;
            Theme theme = skin.Id == "default"
                ? Theme.Material
                : skin.Read() is { } json ? Theme.FromJson(json, skin.Id, skin.Name) : Theme.Material;
            app.UseTheme(theme);
            app.Say(UiSettingsFile.WriteSkin(skin.Id)
                ? $"{skin.Name} is the appearance here and in the window"
                : $"{skin.Name} is on for this run, the saved choice could not be written");
            return true;
        }
    }
}
