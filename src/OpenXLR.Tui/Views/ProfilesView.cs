namespace OpenXLR.Tui;

/// <summary>
/// Saved scenes and the device they belong to: load one, save the current
/// settings under a name, choose what is recalled when the interface
/// connects, switch to another attached interface, and write the recorded
/// defaults back to the hardware.
/// </summary>
internal sealed class ProfilesView : View
{
    private readonly RowList _list = new();

    public override string Title => "Profiles";

    public override string Keys => "enter load  s save  d delete  r recall";

    public override void Draw(Screen screen, Rect area, App app)
    {
        Snapshot? state = State(app);
        if (state is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "Waiting for the daemon", app.Theme.TextMuted, app.Theme.Window);
            return;
        }
        _list.Draw(screen, area, app.Theme, Build(app, state), labelWidth: 30);
    }

    public override bool Handle(KeyPress key, App app)
    {
        Snapshot? state = State(app);
        return state is not null && _list.Handle(key, Build(app, state));
    }

    private static List<Row> Build(App app, Snapshot state)
    {
        DaemonLink link = app.Link;
        List<Row> rows = [new HeadingRow("Profiles")];

        if (state.Profiles.Count == 0) rows.Add(new TextRow("None saved", "press s to save the current settings"));
        foreach (string profile in state.Profiles)
            rows.Add(new ProfileRow(profile, state, app));

        rows.Add(new ActionRow("Save settings as", "save as", () =>
            app.Ask("Profile name", state.ActiveProfile ?? string.Empty, name =>
            {
                link.Send("saveProfile", body => body["name"] = name);
                app.Say($"Saved {name}");
            })));

        rows.Add(new HeadingRow("On connect"));
        List<string> names = ["nothing", .. state.Profiles];
        int at = Math.Max(0, names.IndexOf(state.RecallOnConnect ?? "nothing"));
        rows.Add(new ChoiceRow("Recall", names, at, index =>
            link.Send("setRecallOnConnect", body => body["name"] = index == 0 ? string.Empty : names[index])));

        rows.Add(new HeadingRow("Device"));
        rows.Add(new TextRow("Active profile", state.ActiveProfile ?? "none"));
        if (state.Detected.Count > 0)
        {
            List<string> devices = state.Detected.Select(device => device.Name).ToList();
            int active = Math.Max(0, state.Detected.FindIndex(device => device.Active));
            rows.Add(new ChoiceRow("Interface", devices, active, index =>
                link.Send("setActiveDevice", body => body["device"] = state.Detected[index].UsbId))
            { Enabled = state.Detected.Count > 1 });
        }
        rows.Add(new ActionRow("Recorded defaults", "reset device", () =>
            app.Ask("Type yes to reset the device", string.Empty, answer =>
            {
                if (!answer.Equals("yes", StringComparison.OrdinalIgnoreCase)) return;
                link.Send("resetDevice");
                app.Say("Asked the daemon to reset the device");
            }))
        { Destructive = true, Enabled = state.Can("builtInDefaults") });

        return rows;
    }

    /// <summary>One saved profile.</summary>
    private sealed class ProfileRow(string name, Snapshot state, App app) : Row(name)
    {
        public override void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused)
        {
            bool active = state.ActiveProfile == Label;
            bool recalled = state.RecallOnConnect == Label;
            Widgets.Lamp(screen, x, y, active, theme, back);
            string note = active && recalled ? "active, recalled on connect"
                : active ? "active"
                : recalled ? "recalled on connect"
                : "enter to load";
            screen.Text(x + 2, y, note, focused ? theme.TextPrimary : theme.TextMuted, back, bold: focused,
                maxWidth: Math.Max(4, width - 4));
        }

        public override bool Handle(KeyPress key)
        {
            switch (key.Key)
            {
                case Key.Enter:
                    app.Link.Send("loadProfile", body => body["name"] = Label);
                    app.Say($"Loaded {Label}");
                    return true;
                case Key.Char when key.Char == 'r':
                    app.Link.Send("setRecallOnConnect", body => body["name"] = Label);
                    app.Say($"{Label} is recalled when the device connects");
                    return true;
                case Key.Char when key.Char == 's':
                    app.Link.Send("saveProfile", body => body["name"] = Label);
                    app.Say($"Saved over {Label}");
                    return true;
                case Key.Char when key.Char == 'd':
                    app.Ask($"Type yes to delete {Label}", string.Empty, answer =>
                    {
                        if (answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
                            app.Link.Send("deleteProfile", body => body["name"] = Label);
                    });
                    return true;
                default: return false;
            }
        }
    }
}
