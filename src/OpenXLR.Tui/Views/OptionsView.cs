namespace OpenXLR.Tui;

/// <summary>
/// The appearance and what the terminal knows about the connection. The skin
/// list is the same one the window shows, read from the same folders, and the
/// choice is written back to the same file, so picking Gruvbox here is
/// picking it in the window too.
/// </summary>
internal sealed class OptionsView : View
{
    private readonly RowList _list = new();
    private IReadOnlyList<SkinEntry> _skins = SkinCatalog.Scan();

    public override string Title => "Options";

    public override string Keys => "Enter use skin  R reload skins";

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
        List<Row> rows = [new HeadingRow("Appearance")];
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
