namespace OpenXLR.Tui;

/// <summary>One tab: what it is called, what it draws, and what its keys do.</summary>
internal abstract class View
{
    public abstract string Title { get; }

    /// <summary>The keys this tab adds, for the help line at the bottom.</summary>
    public abstract string Keys { get; }

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
    private readonly List<string> _helpLines = [];
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
            new MixerView(), new InputsView(), new OutputsView(), new AppsView(),
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

        if (Prompting) { Typing(key); return; }

        if (_help)
        {
            // Any key closes the help, which is the only thing it does.
            _help = false;
            return;
        }

        switch (key.Key)
        {
            case Key.Tab: Tab = (Tab + 1) % _views.Count; return;
            case Key.BackTab: Tab = (Tab + _views.Count - 1) % _views.Count; return;
            case Key.F1: _help = true; return;
        }

        // Ctrl and C ends the program wherever it is pressed, so it is taken
        // before a tab can read it as one of its own letters.
        if (key.Ctrl && key.Is('c')) { Stop(); return; }

        if (key.Key == Key.Char && key.Char is >= '1' and <= '9' && !key.Ctrl)
        {
            int index = key.Char - '1';
            if (index < _views.Count) { Tab = index; return; }
        }

        if (Current.Handle(key, this)) return;

        if (key.Is('q')) { Stop(); return; }
        if (key.Is('?')) { _help = true; return; }
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
        Tabs(screen);

        Rect body = new(0, 3, screen.Width, Math.Max(0, screen.Height - 4));
        if (body.Height > 0) Current.Draw(screen, body, this);

        Bottom(screen);
        if (_help) Help(screen);
    }

    private void Header(Screen screen)
    {
        Theme theme = Theme;
        Snapshot? state = Link.State;
        screen.Fill(0, 0, screen.Width, 1, theme.Card);

        int x = 1;
        x += screen.Text(x, 0, "OpenXLR", theme.TextPrimary, theme.Card, bold: true);
        x += screen.Text(x, 0, "  ", theme.TextPrimary, theme.Card);

        string device = state?.Device is { } descriptor && state.Connected
            ? $"{descriptor.Vendor} {descriptor.Model}"
            : state is null ? "waiting for the daemon" : "no interface";
        Widgets.Lamp(screen, x, 0, state?.Connected == true, theme, theme.Card,
            alert: state is not null && !state.Connected);
        x += 2;
        x += screen.Text(x, 0, device, theme.TextDetail, theme.Card);

        if (state?.Device?.Note is { Length: > 0 } note)
            x += screen.Text(x, 0, $"  ({note})", theme.TextSecondary, theme.Card);

        string right = state?.DaemonVersion is { Length: > 0 } version ? $"v{version} " : $"{Link.Status} ";
        screen.Text(Math.Max(x + 1, screen.Width - right.Length - 1), 0, right, theme.TextSecondary, theme.Card);

        if (state?.Warning is { Length: > 0 } warning)
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
            string label = $" {index + 1} {_views[index].Title} ";
            Rgb back = chosen ? theme.Accent : theme.Tile;
            Rgb fore = chosen
                ? (theme.Accent.Luminance() > 0.5 ? theme.Window : theme.TextPrimary)
                : theme.TextSecondary;
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

        string keys = $" {Current.Keys}  tab next  F1 help  q quit";
        screen.TextPad(0, y, keys, screen.Width, theme.TextMuted, theme.Card);
    }

    private void Help(Screen screen)
    {
        Theme theme = Theme;
        if (_helpLines.Count == 0)
        {
            _helpLines.Add("Tabs        1 to 7, or tab and shift tab");
            _helpLines.Add("Move        arrow keys, page up and page down");
            _helpLines.Add("Change      left and right, or - and +");
            _helpLines.Add("Toggle      space");
            _helpLines.Add("Fine step   ctrl and left or right");
            _helpLines.Add("Confirm     enter, cancel escape");
            _helpLines.Add("Quit        q");
            _helpLines.Add(string.Empty);
            _helpLines.Add("Each tab lists its own keys on the bottom line.");
            _helpLines.Add("The appearance comes from the skin chosen in Options,");
            _helpLines.Add("which is the same choice the window uses.");
        }

        int width = Math.Min(60, screen.Width - 4);
        int height = Math.Min(_helpLines.Count + 4, screen.Height - 4);
        int x = (screen.Width - width) / 2;
        int y = (screen.Height - height) / 2;
        screen.Panel(x, y, width, height, theme.Accent, theme.Dialog, "Keys", theme.TextPrimary);
        for (int line = 0; line < _helpLines.Count && line < height - 3; line++)
            screen.Text(x + 2, y + 2 + line, _helpLines[line], theme.TextDetail, theme.Dialog, maxWidth: width - 4);
        screen.Text(x + 2, y + height - 1, " any key closes ", theme.TextMuted, theme.Dialog);
    }
}
