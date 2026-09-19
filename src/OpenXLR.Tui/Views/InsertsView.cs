using System.Text.Json.Nodes;

namespace OpenXLR.Tui;

/// <summary>
/// The plugin chains: one target at a time, its inserts in order, and what the
/// daemon says about each of them. A chain can be bypassed, reordered and
/// thinned out here; choosing a new plugin and editing its controls stays in
/// the window, which has the catalogue and the plugin's own editor.
/// </summary>
internal sealed class InsertsView : View
{
    private readonly RowList _list = new();
    private int _target;

    public override string Title => "Inserts";

    public override string Keys => "Space bypass  Ctrl+Up/Down move  d remove  e editor";

    public override void Draw(Screen screen, Rect area, App app)
    {
        Snapshot? state = State(app);
        if (state is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "Waiting for the daemon", app.Theme.TextMuted, app.Theme.Window);
            return;
        }
        _list.Draw(screen, area, app.Theme, Build(app, state), labelWidth: 34);
    }

    public override bool Handle(KeyPress key, App app)
    {
        Snapshot? state = State(app);
        return state is not null && _list.Handle(key, Build(app, state));
    }

    /// <summary>The chains the daemon reports, in the order the window lists them.</summary>
    private static List<(string Key, string Name)> Targets(Snapshot state)
    {
        List<(string, string)> targets = [];
        int inputs = Math.Max(1, state.Count("xlrInputs"));
        for (int input = 1; input <= inputs; input++) targets.Add(($"xlr{input}", $"XLR {input}"));
        foreach (MixEntry mix in state.Mixer.Mixes) targets.Add(($"mix:{mix.Id}", mix.Name));
        return targets;
    }

    private List<Row> Build(App app, Snapshot state)
    {
        List<(string Key, string Name)> targets = Targets(state);
        if (targets.Count == 0) return [new TextRow("No chains", "the mixer is not built")];
        _target = Math.Clamp(_target, 0, targets.Count - 1);

        List<Row> rows =
        [
            new ChoiceRow("Chain", targets.Select(target => target.Name).ToList(), _target, index => _target = index),
            new HeadingRow(targets[_target].Name),
        ];

        List<InsertEntry> chain = state.Mixer.Inserts.TryGetValue(targets[_target].Key, out List<InsertEntry>? entries)
            ? entries
            : [];
        if (chain.Count == 0)
            rows.Add(new TextRow("No plugins", "add them from the window"));
        for (int index = 0; index < chain.Count; index++)
            rows.Add(new InsertRow(chain[index], index, chain, targets[_target].Key, app));

        return rows;
    }

    /// <summary>One plugin in a chain.</summary>
    private sealed class InsertRow(InsertEntry entry, int index, List<InsertEntry> chain, string target, App app)
        : Row($"{index + 1}. {entry.Insert.Label ?? entry.Insert.Plugin}")
    {
        public override void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused)
        {
            bool failed = entry.Error is { Length: > 0 };
            Widgets.Lamp(screen, x, y, !entry.Insert.Bypass && !failed, theme, back, alert: failed || entry.Insert.Bypass);
            string status = failed ? entry.Error! : entry.Insert.Bypass ? "bypassed" : "processing";
            screen.Text(x + 2, y, status, failed ? theme.TextWarning : theme.TextDetail, back,
                bold: focused, maxWidth: Math.Max(4, width - 10));
            // The format sits on a badge, as it does beside a plugin's name in
            // the window.
            screen.Text(x + Math.Max(6, width - 6), y, $" {entry.Insert.Kind} ", theme.TextSecondary,
                theme.Badge, maxWidth: 6);
        }

        public override bool Handle(KeyPress key)
        {
            switch (key.Key)
            {
                case Key.Char when key.Char == ' ':
                    app.Link.Send("setInsertBypass", body =>
                    {
                        body["channel"] = target;
                        body["insertId"] = entry.Insert.Id;
                        body["value"] = !entry.Insert.Bypass;
                    });
                    return true;

                case Key.Up when key.Ctrl: Move(-1); return true;
                case Key.Down when key.Ctrl: Move(1); return true;

                case Key.Char when key.Char == 'd':
                    app.Ask($"Type yes to remove {Label}", string.Empty, answer =>
                    {
                        if (!answer.Equals("yes", StringComparison.OrdinalIgnoreCase)) return;
                        Write(chain.Where((_, at) => at != index));
                    });
                    return true;

                case Key.Char when key.Char == 'e':
                    app.Link.Send("showInsertUi", body =>
                    {
                        body["channel"] = target;
                        body["insertId"] = entry.Insert.Id;
                    });
                    app.Say("Asked the daemon to open the plugin's own editor");
                    return true;

                default: return false;
            }
        }

        private void Move(int by)
        {
            int to = Math.Clamp(index + by, 0, chain.Count - 1);
            if (to == index) return;
            List<InsertEntry> moved = [.. chain];
            moved.RemoveAt(index);
            moved.Insert(to, entry);
            Write(moved);
        }

        /// <summary>
        /// Writes a chain back. The whole chain goes at once, which is the one
        /// command the daemon has for it, so every insert is sent with the
        /// fields it was read with.
        /// </summary>
        private void Write(IEnumerable<InsertEntry> inserts)
        {
            JsonArray array = [];
            foreach (InsertEntry item in inserts)
            {
                JsonObject values = [];
                foreach ((string symbol, double value) in item.Insert.Params) values[symbol] = value;
                array.Add(new JsonObject
                {
                    ["id"] = item.Insert.Id,
                    ["kind"] = item.Insert.Kind,
                    ["plugin"] = item.Insert.Plugin,
                    ["label"] = item.Insert.Label,
                    ["bypass"] = item.Insert.Bypass,
                    ["params"] = values,
                });
            }
            app.Link.Send("setInserts", body =>
            {
                body["channel"] = target;
                body["inserts"] = array;
            });
        }
    }
}
