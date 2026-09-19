using System.Text.Json.Nodes;

namespace OpenXLR.Tui;

/// <summary>
/// The plugin chains: one target at a time, its inserts in order, and what the
/// daemon says about each of them. Editing asks for the native editor first,
/// with generated controls when it is blocked or cannot open.
/// </summary>
internal sealed class InsertsView : View
{
    private readonly RowList _list = new();
    private readonly RowList _header = new();
    private RowList _controls = new();
    private int _target;
    private EditTarget? _editing;
    private Task<string?>? _opening;
    private bool _showControls;
    private bool _blocked;
    private string _reason = string.Empty;
    private Snapshot? _controlState;
    private PluginEntry? _controlPlugin;
    private List<Row> _controlRows = [];

    private sealed record EditTarget(string Channel, string Id, string Kind, string Plugin);

    public override string Title => "Inserts";

    public override string Keys => _showControls
        ? "Esc back  r defaults  -/+ [/] value  Space toggle  Left/Right choose  e editor"
        : _opening is not null ? "Waiting for the native editor  Esc cancel"
        : "e editor  Space bypass  Ctrl+Up/Down move  d remove";

    public override void Draw(Screen screen, Rect area, App app)
    {
        Snapshot? state = State(app);
        if (state is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "Waiting for the daemon", app.Theme.TextMuted, app.Theme.Window);
            return;
        }
        Update(app, state);
        if (_showControls && Find(state) is { } entry)
        {
            PluginEntry? plugin = Plugin(app, entry);
            string name = plugin?.Name is { Length: > 0 } title ? title : entry.Insert.Label ?? entry.Insert.Plugin;
            string chain = Targets(state).FirstOrDefault(target => target.Key == _editing!.Channel).Name
                ?? _editing!.Channel;
            _header.Draw(screen, area with { Height = 2 }, app.Theme,
                [new HeadingRow($"{name} / {chain}"), new TextRow("Generated controls", _reason)],
                labelWidth: 22, focus: false);
            _controls.Draw(screen, new Rect(area.X, area.Y + 3, area.Width, Math.Max(0, area.Height - 3)),
                app.Theme, ControlRows(app, state, entry, plugin), labelWidth: 34);
        }
        else _list.Draw(screen, area, app.Theme, Build(app, state), labelWidth: 34);
    }

    public override bool Handle(KeyPress key, App app)
    {
        Snapshot? state = State(app);
        if (state is null) return false;
        Update(app, state);
        if (_editing is not null && key.Key == Key.Escape) { CloseControls(); return true; }
        if (_showControls && Find(state) is { } entry)
        {
            PluginEntry? plugin = Plugin(app, entry);
            if (key.Is('r'))
            {
                if (plugin is not null)
                    foreach (PluginControl control in plugin.Params) SetParam(app, control.Symbol, control.Default);
                return true;
            }
            if (key.Is('e')) { Edit(app, _editing!.Channel, entry); return true; }
            return _controls.Handle(key, ControlRows(app, state, entry, plugin));
        }
        return _list.Handle(key, Build(app, state));
    }

    private static PluginEntry? Plugin(App app, InsertEntry entry) =>
        app.Link.Plugins.GetValueOrDefault((entry.Insert.Kind, entry.Insert.Plugin));

    private InsertEntry? Find(Snapshot state) => _editing is { } edit &&
        state.Mixer.Inserts.TryGetValue(edit.Channel, out var chain)
            ? chain.FirstOrDefault(entry => entry.Insert.Id == edit.Id && entry.Insert.Kind == edit.Kind &&
                entry.Insert.Plugin == edit.Plugin) : null;

    private void Update(App app, Snapshot state)
    {
        app.Link.EnsurePlugins();
        if (_editing is null) return;
        InsertEntry? entry = Find(state);
        if (entry is null)
        {
            CloseControls();
            app.Say("The insert is no longer in this chain");
        }
        else if (_opening is { IsCompletedSuccessfully: true })
        {
            string? error = _opening.Result;
            _opening = null;
            if (error is { Length: > 0 })
            {
                _reason = error;
                _showControls = true;
            }
            else
            {
                CloseControls();
                app.Say("Opened the plugin's own editor");
            }
        }
        if (_showControls && entry is not null)
        {
            PluginEntry? plugin = Plugin(app, entry);
            if (entry.NativeUiBlocked || plugin?.NativeUiBlocked == true)
            {
                _reason = BlockReason(entry, plugin);
                _blocked = true;
            }
            else if (_blocked)
            {
                _reason = "Native editor is allowed; e opens it";
                _blocked = false;
            }
        }
    }

    private void CloseControls()
    {
        _editing = null;
        _opening = null;
        _showControls = false;
        _blocked = false;
        _controlState = null;
        _controlPlugin = null;
        _controlRows = [];
    }

    private void Edit(App app, string target, InsertEntry entry)
    {
        if (_opening is not null) return;
        _editing = new(target, entry.Insert.Id, entry.Insert.Kind, entry.Insert.Plugin);
        _controls = new RowList();
        _controlState = null;
        PluginEntry? plugin = Plugin(app, entry);
        if (entry.NativeUiBlocked || plugin?.NativeUiBlocked == true)
        {
            _showControls = true;
            _blocked = true;
            _reason = BlockReason(entry, plugin);
            return;
        }
        _opening = app.Link.Request("showInsertUi", body =>
        {
            body["channel"] = target;
            body["insertId"] = entry.Insert.Id;
        });
    }

    private static string BlockReason(InsertEntry entry, PluginEntry? plugin) =>
        (entry.NativeUiBlocked ? entry.NativeUiBlockReason : null) ?? plugin?.NativeUiBlockReason
        ?? "Native editor blocked by compatibility rules";

    private List<Row> ControlRows(App app, Snapshot state, InsertEntry entry, PluginEntry? plugin)
    {
        if (plugin is null)
            return [new TextRow("Controls", app.Link.PluginsLoaded ? "Plugin not found in the catalogue" : "Waiting for the plugin catalogue")];
        if (ReferenceEquals(_controlState, state) && ReferenceEquals(_controlPlugin, plugin)) return _controlRows;
        _controlState = state;
        _controlPlugin = plugin;
        _controlRows = [];
        foreach (PluginControl control in plugin.Params)
        {
            double value = entry.Insert.Params.GetValueOrDefault(control.Symbol, control.Default);
            string name = control.Name.Length > 0 ? control.Name : control.Symbol;
            void Set(double next) => SetParam(app, control.Symbol, next);
            if (control.Toggled)
                _controlRows.Add(new ToggleRow(name, value >= 0.5, on => Set(on ? 1 : 0)));
            else if (control.Enumeration && control.ScalePoints.Count > 0)
            {
                int at = Enumerable.Range(0, control.ScalePoints.Count)
                    .MinBy(index => Math.Abs(control.ScalePoints[index].Value - value));
                _controlRows.Add(new ChoiceRow(name, control.ScalePoints.Select(point => point.Label).ToArray(), at,
                    index => Set(control.ScalePoints[index].Value)));
            }
            else
                _controlRows.Add(new NumberRow(name, value, control.Min, control.Max,
                    control.Integer ? 1 : (control.Max - control.Min) / 20, control.Format, Set)
                {
                    FineStep = control.Integer ? 1 : (control.Max - control.Min) / 100,
                    Integer = control.Integer,
                    Logarithmic = control.Logarithmic && !control.Integer,
                });
        }
        if (_controlRows.Count == 0) _controlRows.Add(new TextRow("No declared controls", "This plugin exposes no parameters"));
        return _controlRows;
    }

    private void SetParam(App app, string symbol, double value)
    {
        if (_editing is not { } edit) return;
        app.Link.Send("setInsertParam", body =>
        {
            body["channel"] = edit.Channel;
            body["insertId"] = edit.Id;
            body["symbol"] = symbol;
            body["value"] = value;
        });
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
            rows.Add(new InsertRow(chain[index], index, chain, targets[_target].Key, app, this));

        return rows;
    }

    /// <summary>One plugin in a chain.</summary>
    private sealed class InsertRow(InsertEntry entry, int index, List<InsertEntry> chain, string target, App app, InsertsView view)
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
                    view.Edit(app, target, entry);
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
                    ["nativeHost"] = item.Insert.NativeHost,
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
