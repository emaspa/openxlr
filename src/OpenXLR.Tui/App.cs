namespace OpenXLR.Tui;

/// <summary>One tab: what it is called, what it draws, and what its keys do.</summary>
internal abstract class View
{
    public abstract string Title { get; }

    /// <summary>The keys this tab adds, for the help line at the bottom.</summary>
    public abstract string Keys { get; }

    /// <summary>
    /// True while the tab is taking typed text, such as a filter, so a letter
    /// or a digit is its own rather than a quit or a section switch. Ctrl+C,
    /// Tab and F1 stay the frame's.
    /// </summary>
    public virtual bool WantsText => false;

    public abstract void Draw(Screen screen, Rect area, App app);

    /// <summary>True when the key was this tab's to handle.</summary>
    public abstract bool Handle(KeyPress key, App app);

    /// <summary>The state, or null while the daemon has not sent one.</summary>
    protected static Snapshot? State(App app) => app.Link.State;

    /// <summary>Moves a selection inside a list, stopping at both ends.</summary>
    protected static int Move(int index, int by, int count) =>
        count <= 0 ? 0 : Math.Clamp(index + by, 0, count - 1);
}

/// <summary>
/// The terminal mixer: the frame around the tabs, the keys that are the same
/// everywhere, the line at the bottom, and the one place a view asks for
/// something to be typed.
/// </summary>
internal sealed class App
{
    private readonly List<View> _views;
    private int _helpScroll;
    private string _message = string.Empty;
    private DateTime _messageUntil = DateTime.MinValue;
    private bool _help;
    private string? _promptTitle;
    private string _promptText = string.Empty;
    private Action<string>? _promptDone;

    public App(DaemonLink link, Theme theme)
    {
        Link = link;
        Theme = theme;
        _views =
        [
            new MixerView(), new MatrixView(), new InputsView(), new OutputsView(), new AppsView(),
            new InsertsView(), new ProfilesView(), new OptionsView(),
        ];
    }

    public DaemonLink Link { get; }

    public Theme Theme { get; private set; }

    public bool Running { get; private set; } = true;

    public int Tab { get; private set; }

    public View Current => _views[Tab];

    public IReadOnlyList<View> Views => _views;

    /// <summary>True while something is being typed, which takes every key.</summary>
    public bool Prompting => _promptTitle is not null;

    public void Stop() => Running = false;

    /// <summary>A sentence on the bottom line for a few seconds.</summary>
    public void Say(string message)
    {
        _message = message;
        _messageUntil = DateTime.UtcNow.AddSeconds(4);
    }

    /// <summary>Asks for a line of text, and calls back when it is typed rather than cancelled.</summary>
    public void Ask(string title, string initial, Action<string> done)
    {
        _promptTitle = title;
        _promptText = initial;
        _promptDone = done;
    }

    public void UseTheme(Theme theme)
    {
        Theme = theme;
    }

    public void ShowTab(int index)
    {
        if (index >= 0 && index < _views.Count) Tab = index;
    }

    /// <summary>Handles a key: the prompt first, then the tab, then everything that is global.</summary>
    public void Handle(KeyPress key)
    {
        if (key.Key == Key.None) return;
        Link.ClearError();

        if (key.Ctrl && key.Is('c')) { Stop(); return; }
        if (Prompting) { Typing(key); return; }
        bool typing = Current.WantsText;
        if (!typing && key.Is('q')) { Stop(); return; }

        if (_help)
        {
            if (key.Key is Key.Down or Key.PageDown) { _helpScroll++; return; }
            if (key.Key is Key.Up or Key.PageUp) { _helpScroll = Math.Max(0, _helpScroll - 1); return; }
            _help = false;
            return;
        }

        switch (key.Key)
        {
            case Key.Tab: Tab = (Tab + 1) % _views.Count; return;
            case Key.BackTab: Tab = (Tab + _views.Count - 1) % _views.Count; return;
            case Key.F1: _help = true; return;
        }

        if (!typing && key.Key == Key.Char && key.Char is >= '1' and <= '9' && !key.Ctrl)
        {
            int index = key.Char - '1';
            if (index < _views.Count) { Tab = index; return; }
        }

        if (Current.Handle(key, this)) return;

        if (key.Is('q')) { Stop(); return; }
        if (!typing && key.Is('?')) { _help = true; return; }
    }

    private void Typing(KeyPress key)
    {
        switch (key.Key)
        {
            case Key.Enter:
                Action<string>? done = _promptDone;
                string text = _promptText.Trim();
                _promptTitle = null;
                _promptDone = null;
                if (text.Length > 0) done?.Invoke(text);
                return;
            case Key.Escape:
                _promptTitle = null;
                _promptDone = null;
                return;
            case Key.Backspace:
                if (_promptText.Length > 0) _promptText = _promptText[..^1];
                return;
            case Key.Char when !key.Ctrl && _promptText.Length < 64:
                _promptText += key.Char;
                return;
        }
    }

    /// <summary>Draws the whole screen: the header, the tabs, the current view and the bottom line.</summary>
    public void Draw(Screen screen)
    {
        Theme theme = Theme;
        screen.Clear(theme.Window);

        Header(screen);
        if (screen.Width < 80 || screen.Height < 24)
        {
            screen.Text(1, 3, "Resize to at least 80x24", theme.TextPrimary, theme.Window,
                maxWidth: screen.Width - 2);
            Bottom(screen);
            return;
        }
        bool wide = screen.Width >= 120 && screen.Height >= 30;
        Rect body;
        if (wide)
        {
            Sidebar(screen);
            body = new Rect(18, 2, screen.Width - 19, screen.Height - 3);
        }
        else
        {
            Tabs(screen);
            body = new Rect(1, 4, screen.Width - 2, Math.Max(0, screen.Height - 5));
        }

        if (body.Height > 0)
        {
            if (Tab == 0) Current.Draw(screen, body, this);
            else
            {
                screen.Panel(body.X, body.Y, body.Width, body.Height, theme.Rule, theme.Card,
                    Current.Title, theme.TextPrimary);
                Current.Draw(screen, body.Inset(1), this);
            }
        }

        Bottom(screen);
        if (_help) Help(screen);
    }

    private void Sidebar(Screen screen)
    {
        Theme theme = Theme;
        int height = screen.Height - 3;
        screen.Fill(1, 2, 15, height, theme.Card);
        screen.Text(3, 3, "CONSOLE", theme.TextMuted, theme.Card, bold: true);
        // Eight entries a row apart fit a short terminal; taller ones space them out.
        int step = screen.Height >= 34 ? 2 : 1;
        for (int index = 0; index < _views.Count; index++)
        {
            bool chosen = index == Tab;
            int y = 5 + index * step;
            Rgb back = chosen ? theme.Selection : theme.Card;
            screen.TextPad(2, y, $" {index + 1} {_views[index].Title}", 13,
                chosen ? theme.TextPrimary : theme.TextSecondary, back, chosen);
            if (chosen) screen.Set(1, y, '┃', theme.Accent, back);
        }
        int bottom = screen.Height - 2;
        screen.Text(3, bottom - 9, "INTERFACE", theme.TextMuted, theme.Card);
        Snapshot? state = Link.State;
        if (state is not null && state.Connected)
        {
            int inputs = Math.Min(2, Math.Max(1, state.Count("xlrInputs")));
            for (int i = 0; i < inputs; i++)
            {
                string suffix = i == 0 ? string.Empty : "2";
                screen.Text(3, bottom - 7 + i * 2, $"XLR {i + 1} {state.Number($"gain{suffix}Db"),3:0} dB",
                    theme.TextDetail, theme.Card, maxWidth: 12);
                Widgets.Meter(screen, 3, bottom - 6 + i * 2, 11, Link.Meter("ch", $"xlr{i + 1}"), theme, theme.Card);
            }
        }
        else screen.Text(3, bottom - 7, "Offline", theme.TextMuted, theme.Card);
        screen.Text(3, bottom - 2, "SKIN", theme.TextMuted, theme.Card);
        screen.Text(3, bottom - 1, theme.Name, theme.TextDetail, theme.Card, maxWidth: 12);
    }

    private void Header(Screen screen)
    {
        Theme theme = Theme;
        Snapshot? state = Link.State;
        screen.Fill(0, 0, screen.Width, 1, theme.Card);

        screen.Text(1, 0, "OpenXLR", theme.TextPrimary, theme.Card, bold: true);
        string device = state?.Device is { } descriptor && state.Connected
            ? $"{descriptor.Vendor} {descriptor.Model}"
            : state is null ? "waiting for the daemon" : "no interface";
        Widgets.Lamp(screen, 10, 0, state?.Connected == true, theme, theme.Card,
            alert: state is not null && !state.Connected);
        string right = state?.DaemonVersion is { Length: > 0 } version ? $"v{version}" : Link.Status;
        int rightX = Math.Max(12, screen.Width - right.Length - 2);
        screen.Text(12, 0, device, theme.TextDetail, theme.Card, maxWidth: Math.Max(0, rightX - 14));
        screen.Text(rightX, 0, right, theme.TextSecondary, theme.Card);
        string? warning = state?.Warning ?? state?.Mixer.LayoutWarning ?? state?.Device?.Note;
        if (warning is { Length: > 0 })
            screen.TextPad(1, 1, warning, screen.Width - 2, theme.TextWarning, theme.Window);
    }

    private void Tabs(Screen screen)
    {
        Theme theme = Theme;
        int y = screen.Height > 6 ? 2 : 1;
        screen.Fill(0, y, screen.Width, 1, theme.Window);
        int x = 1;
        for (int index = 0; index < _views.Count; index++)
        {
            bool chosen = index == Tab;
            string[] shortNames = ["Mix", "Matrix", "In", "Out", "Apps", "FX", "Profiles", "Options"];
            string title = screen.Width < 100 ? shortNames[index] : _views[index].Title;
            string label = $" {index + 1} {title} ";
            Rgb back = chosen ? theme.Selection : theme.Card;
            Rgb fore = chosen ? theme.TextPrimary : theme.TextSecondary;
            screen.Text(x, y, label, fore, back, bold: chosen);
            x += label.Length + 1;
            if (x >= screen.Width) break;
        }
    }

    private void Bottom(Screen screen)
    {
        Theme theme = Theme;
        int y = screen.Height - 1;
        screen.Fill(0, y, screen.Width, 1, theme.Card);

        if (Prompting)
        {
            string prompt = $" {_promptTitle}: {_promptText}";
            screen.TextPad(0, y, prompt, screen.Width, theme.TextPrimary, theme.Card, bold: true);
            screen.Set(Math.Min(prompt.Length, screen.Width - 1), y, '▁', theme.Accent, theme.Card);
            return;
        }

        if (Link.LastError is { Length: > 0 } error)
        {
            screen.TextPad(0, y, $" {error}", screen.Width, theme.DangerFore, theme.DangerBack);
            return;
        }

        if (DateTime.UtcNow < _messageUntil)
        {
            screen.TextPad(0, y, $" {_message}", screen.Width, theme.TextPrimary, theme.Card);
            return;
        }

        // One line, the whole width: the section's keys on the left and the
        // two that work everywhere at the right end. A narrow terminal loses
        // the section's last hints, cut at a gap, rather than help and quit.
        string everywhere = "F1/? help  q/Ctrl+C quit ";
        string keys = $" {Current.Keys}";
        int room = screen.Width - everywhere.Length - 2;
        if (keys.Length > room)
        {
            int cut = keys.LastIndexOf("  ", Math.Max(0, room), StringComparison.Ordinal);
            keys = keys[..(cut > 0 ? cut : Math.Max(0, room))];
        }
        screen.TextPad(0, y, keys, screen.Width, theme.TextDetail, theme.Card);
        screen.Text(screen.Width - everywhere.Length, y, everywhere, theme.TextSecondary, theme.Card);
    }

    private void Help(Screen screen)
    {
        Theme theme = Theme;
        string[] lines =
        [
            "Sections  1-8 or Tab / Shift+Tab. F1 or ? opens this table.",
            "Quit      q or Ctrl+C. In a text prompt use Ctrl+C to quit.",
            "Mixer     Up/Down channel; Home masters; End last channel.",
            "          Left/Right mix; PgUp/PgDn jump ten channels.",
            "          Space mute; -/+ five points; [/] one point.",
            "          r rename; n new channel; N new mic; c capture input.",
            "          d delete; Ctrl+Left/Right reorder channel or mic.",
            "Matrix    Up/Down channel; Left/Right mix; Space mute; -/+ [/] level.",
            "Inputs    Up/Down control; Left/Right or -/+ level; [/] fine.",
            "          Ctrl+Left/Right fine; Space toggle; Home/End limits.",
            "Outputs   Space select; Left/Right feed; Enter main output.",
            "          -/+ sink volume; m sink mute. Arrows set defaults.",
            "Apps      Left/Right channel; i desktop routing; f forget.",
            "Inserts   e open editor, or controls if blocked or refused.",
            "          Space bypass; Ctrl+Up/Down reorder; d remove.",
            "Controls  Esc back; r defaults; -/+ value; [/] fine; e editor.",
            "          Left/Right value/choice; Space/Enter toggle/choice.",
            "          Ctrl+Left/Right fine; Home/End limits.",
            "Profiles  Enter load; s overwrite; r recall; d delete.",
            "          Save settings as creates a profile. Arrows set recall.",
            "Options   Enter use skin; R reload skins; arrows move.",
            "Lists     Up/Down or PgUp/PgDn move; Enter or Space acts.",
            "Confirm   Enter accepts; Escape cancels. Deletion needs yes.",
            "Meters    RMS -60 to 0 dBFS; hold 1 s, fall 18 dB/s.",
        ];
        int width = Math.Max(4, Math.Min(78, screen.Width - 2));
        int height = Math.Max(4, Math.Min(lines.Length + 2, screen.Height - 2));
        int x = (screen.Width - width) / 2;
        int y = (screen.Height - height) / 2;
        int count = height - 2;
        _helpScroll = Math.Clamp(_helpScroll, 0, Math.Max(0, lines.Length - count));
        screen.Panel(x, y, width, height, theme.Accent, theme.Dialog, "Keys", theme.TextPrimary);
        for (int line = 0; line < count && line + _helpScroll < lines.Length; line++)
            screen.Text(x + 2, y + 1 + line, lines[line + _helpScroll], theme.TextDetail, theme.Dialog, maxWidth: width - 4);
        screen.Text(x + 2, y + height - 1, " Up/Down scroll, other keys close ", theme.TextSecondary, theme.Dialog,
            maxWidth: width - 4);
    }
}
