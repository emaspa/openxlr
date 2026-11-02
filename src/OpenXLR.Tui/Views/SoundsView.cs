namespace OpenXLR.Tui;

/// <summary>
/// The soundboard: the clips the daemon's player holds, played into the SFX
/// channel. One row a clip of the page shown, in library order, with its
/// length, press mode, loop, gain and status, and a bar for how far a playing
/// clip is. The board's switch and SFX's own volume, the board's master, head
/// the list, and the pages stand as tabs over it once there is more than the
/// default one; the daemon's sentence says why the board or a clip cannot play.
///
/// A terminal sends no key-up, so a clip in hold mode is let go by pressing
/// it again: the first press holds it, the next one releases it. A key held
/// down repeats, so presses of the same Hold or Toggle clip closer together
/// than <see cref="RepeatWindow"/> are taken as the repeat and ignored.
/// </summary>
internal sealed class SoundsView : View
{
    private const double GainStep = 0.05;
    private const double FineGainStep = 0.01;

    // The selection is a clip id, so it follows the clip through a reorder;
    // the index picks a neighbour when the selected clip is deleted.
    private string? _selected;
    private int _lastIndex;
    private int _scroll;

    // The page shown, empty for the default page. It follows a clip an
    // import added; once it has no clip, the first page is shown.
    private string _page = string.Empty;

    // A page asked for by a move or a rename, shown once the daemon's state
    // has it; in another case too, since the daemon keeps the spelling of a
    // page it already has.
    private string? _pendingPage;

    /// <summary>What a prompt takes for the default page, which has no name to type.</summary>
    internal const string DefaultPageWord = "-";

    /// <summary>The default page's name on its tab.</summary>
    internal const string DefaultPageTitle = "Main";

    /// <summary>How close a second press of a Hold or Toggle clip must follow to be the key repeating.</summary>
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromMilliseconds(750);

    /// <summary>How long an accepted hold may wait for its clip to start before it is let go.</summary>
    internal static readonly TimeSpan StartWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A hold press of this terminal: waiting for the daemon (<see cref="Press"/>
    /// set), or accepted and held until it is let go.
    /// </summary>
    private sealed class Hold
    {
        public Task<string?>? Press;
        public long AcceptedAt;
        public bool Started;
        public bool LetGoWhenAccepted;
    }

    // The daemon ties a hold to the connection, so a new connection holds nothing.
    private readonly Dictionary<string, Hold> _holds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastPress = new(StringComparer.Ordinal);
    private long _session = -1;

    private long _importSeen;
    private string? _importName;
    private string? _importNote;

    public override string Title => "Sounds";

    public override string Keys =>
        "Enter play  s stop  S stop all  i import  R starter sounds  a audition  b board  -/+ [/] gain  m mode  l loop  r rename  d delete  Ctrl+Up/Down move  </> ,/. SFX volume  p/P page  g move to page  G rename page";

    public override void Draw(Screen screen, Rect area, App app)
    {
        Theme theme = app.Theme;
        Snapshot? state = State(app);
        if (state is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "Waiting for the daemon", theme.TextMuted, theme.Card);
            return;
        }
        SoundboardEntry? board = state.Mixer.Soundboard;
        if (board is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "This daemon has no soundboard. Update OpenXLR to use it.",
                theme.TextMuted, theme.Card, maxWidth: area.Width - 4);
            return;
        }
        Update(app, board);
        if (area.Height < 6 || area.Width < 40) return;

        Header(screen, area, app, state, board);

        List<ClipEntry> clips = Shown(board);
        int top = area.Y + 2;
        if (ShowsPages(board))
        {
            Tabs(screen, new Rect(area.X, top, area.Width, 1), app.Theme, board.PageList);
            top++;
        }
        int listY = top + 1;
        int listHeight = Math.Max(1, area.Bottom - 1 - listY);
        if (board.Clips.Count == 0)
        {
            screen.Text(area.X + 2, listY, "No clips yet. Press i and type a file's path to import one.",
                theme.TextMuted, theme.Card, maxWidth: area.Width - 4);
        }
        else
        {
            Columns(screen, area, theme, top);
            int index = Index(clips);
            if (index < _scroll) _scroll = index;
            if (index >= _scroll + listHeight) _scroll = index - listHeight + 1;
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, clips.Count - listHeight));
            for (int line = 0; line < listHeight && _scroll + line < clips.Count; line++)
            {
                int at = _scroll + line;
                ClipRow(screen, new Rect(area.X, listY + line, area.Width, 1), app, state, clips[at], at == index);
            }
            if (_scroll > 0) screen.Set(area.Right - 1, listY, '▲', theme.TextMuted, theme.Card);
            if (_scroll + listHeight < clips.Count) screen.Set(area.Right - 1, listY + listHeight - 1, '▼', theme.TextMuted, theme.Card);
        }

        Detail(screen, area, app, board);
    }

    /// <summary>Settles the selection and the holds, and selects a clip an import added.</summary>
    private void Update(App app, SoundboardEntry board)
    {
        DaemonLink link = app.Link;
        if (link.SessionRevision != _session)
        {
            _session = link.SessionRevision;
            _holds.Clear();
            _lastPress.Clear();
        }
        Holds(app, board);

        if (link.LastImport is { } outcome && outcome.Serial != _importSeen)
        {
            _importSeen = outcome.Serial;
            if (outcome.Clip is { } added && board.Clips.FirstOrDefault(clip => clip.Id == added) is { } clip)
            {
                _selected = added;
                _page = clip.Page;
            }
        }
        List<string> pages = board.PageList;
        if (_pendingPage is not null && pages.FirstOrDefault(page => string.Equals(page, _pendingPage, StringComparison.OrdinalIgnoreCase)) is { } arrived)
        {
            _page = arrived;
            _pendingPage = null;
        }
        if (!pages.Contains(_page, StringComparer.Ordinal))
            _page = pages.FirstOrDefault(page => string.Equals(page, _page, StringComparison.OrdinalIgnoreCase))
                ?? (pages.Count > 0 ? pages[0] : string.Empty);
        List<ClipEntry> shown = Shown(board);
        if (_selected is null || !shown.Any(clip => clip.Id == _selected))
            _selected = shown.Count == 0 ? null : shown[Math.Clamp(_lastIndex, 0, shown.Count - 1)].Id;
    }

    /// <summary>The clips of the page shown, in library order.</summary>
    private List<ClipEntry> Shown(SoundboardEntry board) => [.. board.Clips.Where(clip => clip.Page == _page)];

    /// <summary>The tabs show once a page other than the default one has a clip.</summary>
    private static bool ShowsPages(SoundboardEntry board) => board.PageList is { Count: > 1 } or [{ Length: > 0 }];

    internal static string PageTitle(string page) => page.Length == 0 ? DefaultPageTitle : page;

    /// <summary>
    /// The pages as tabs, the shown one in the selection's colours. When they
    /// do not all fit, the row starts at the shown page, with a mark for the
    /// pages before it and after the last one drawn.
    /// </summary>
    private void Tabs(Screen screen, Rect row, Theme theme, List<string> pages)
    {
        int shown = Math.Max(0, pages.IndexOf(_page));
        int width = pages.Sum(page => PageTitle(page).Length + 3);
        int first = width <= row.Width - 2 ? 0 : shown;
        int x = row.X + 1;
        if (first > 0) screen.Set(row.X, row.Y, '‹', theme.TextMuted, theme.Card);
        for (int at = first; at < pages.Count; at++)
        {
            string label = $" {PageTitle(pages[at])} ";
            int room = row.Right - 2 - x;
            if (room < 4)
            {
                screen.Set(row.Right - 1, row.Y, '›', theme.TextMuted, theme.Card);
                break;
            }
            bool current = at == shown;
            x += screen.Text(x, row.Y, label, current ? theme.TextPrimary : theme.TextMuted, current ? theme.Selection : theme.Card,
                bold: current, maxWidth: room) + 1;
        }
    }

    /// <summary>
    /// Takes the daemon's answer to each hold press, and lets go of a hold
    /// that has nothing left to hold: the clip was deleted, another client
    /// moved it out of hold mode, or it stopped, by itself or by another
    /// client's stop, or never started.
    /// </summary>
    private void Holds(App app, SoundboardEntry board)
    {
        DaemonLink link = app.Link;
        foreach ((string id, Hold hold) in _holds.ToArray())
        {
            ClipEntry? clip = board.Clips.FirstOrDefault(entry => entry.Id == id);
            if (hold.Press is { IsCompleted: true } press)
            {
                string? error = press.IsCompletedSuccessfully ? press.Result : "The press did not reach the daemon";
                if (error is not null)
                {
                    _holds.Remove(id);
                    app.Say(error);
                    continue;
                }
                hold.Press = null;
                hold.AcceptedAt = app.Time.GetTimestamp();
                if (hold.LetGoWhenAccepted) { LetGo(link, id); continue; }
            }
            if (hold.Press is not null) continue;
            if (clip is null) { _holds.Remove(id); continue; }
            if (!clip.Hold) { LetGo(link, id); continue; }
            if (clip.Playing) { hold.Started = true; continue; }
            if (clip.Status == "loading") continue;
            if (hold.Started || app.Time.GetElapsedTime(hold.AcceptedAt) > StartWindow) LetGo(link, id);
        }
    }

    /// <summary>True while this terminal holds the clip, accepted by the daemon.</summary>
    private bool Held(string id) => _holds.TryGetValue(id, out Hold? hold) && hold.Press is null;

    private int Index(List<ClipEntry> clips)
    {
        int index = Math.Max(0, clips.FindIndex(clip => clip.Id == _selected));
        _lastIndex = index;
        return index;
    }

    private static void Header(Screen screen, Rect area, App app, Snapshot state, SoundboardEntry board)
    {
        Theme theme = app.Theme;
        int x = area.X + 1;
        int y = area.Y;
        bool plays = board.Error is null;
        Widgets.Lamp(screen, x, y, board.Enabled && plays, theme, theme.Card, alert: board.Enabled && !plays);
        x += 2;
        x += screen.Text(x, y, board.Enabled ? "Board on " : "Board off", theme.TextPrimary, theme.Card, bold: true);
        x += 3;

        ChannelEntry? sfx = Sfx(state, board);
        if (sfx is not null)
        {
            string name = sfx.Name.Length > 10 ? sfx.Name[..10] : sfx.Name;
            x += screen.Text(x, y, name, sfx.Appearance.Accent ?? theme.TextDetail, theme.Card);
            x += 1;
            if (sfx.Volume is double volume)
            {
                Widgets.Fader(screen, x, y, 12, volume, 1, theme, theme.Card, false);
                x += 13;
                x += screen.Text(x, y, Widgets.Percent(volume).PadLeft(4), theme.TextDetail, theme.Card);
            }
        }
        else
        {
            x += screen.Text(x, y, "no SFX channel", theme.TextWarning, theme.Card);
        }

        int playing = board.Clips.Count(clip => clip.Playing);
        string count = $"{board.Clips.Count} {(board.Clips.Count == 1 ? "clip" : "clips")}"
            + (playing > 0 ? $", {playing} playing" : string.Empty);
        int countX = Math.Max(x + 3, area.Right - count.Length - 1);
        screen.Text(countX, y, count, theme.TextSecondary, theme.Card, maxWidth: Math.Max(0, area.Right - countX));

        // The daemon's sentence when the board cannot play; what it plays when it can.
        string line = board.Error ?? (board.Formats.Count > 0
            ? $"Plays {Join(board.Formats)} files of up to 30 s into {(sfx?.Name ?? "SFX")}"
            : "Ready");
        screen.TextPad(area.X + 1, y + 1, line, area.Width - 2, board.Error is null ? theme.TextMuted : theme.TextWarning, theme.Card);
    }

    private static string Join(List<string> formats) =>
        formats.Count == 1 ? formats[0] : $"{string.Join(", ", formats.Take(formats.Count - 1))} and {formats[^1]}";

    /// <summary>The widths a clip row splits into: the name takes what the fixed columns leave.</summary>
    private static (int Name, int Status) Widths(int width)
    {
        // Marker 4, length 7, mode 9, loop 6, gain 6, the gaps between.
        int fixedCells = 4 + 1 + 7 + 9 + 6 + 6;
        int name = Math.Clamp((width - fixedCells) * 2 / 5, 10, 28);
        int status = Math.Max(8, width - fixedCells - name - 1);
        return (name, status);
    }

    private static void Columns(Screen screen, Rect area, Theme theme, int y)
    {
        (int name, _) = Widths(area.Width);
        int x = area.X + 4;
        screen.Text(x, y, "CLIP", theme.TextMuted, theme.Card, bold: true);
        x += name + 1;
        screen.Text(x, y, " LENGTH", theme.TextMuted, theme.Card, bold: true);
        x += 7;
        screen.Text(x + 1, y, "MODE", theme.TextMuted, theme.Card, bold: true);
        x += 9;
        screen.Text(x + 1, y, "LOOP", theme.TextMuted, theme.Card, bold: true);
        x += 6;
        screen.Text(x + 1, y, "GAIN", theme.TextMuted, theme.Card, bold: true);
        x += 6;
        screen.Text(x + 1, y, "STATUS", theme.TextMuted, theme.Card, bold: true);
    }

    private void ClipRow(Screen screen, Rect row, App app, Snapshot state, ClipEntry clip, bool focused)
    {
        Theme theme = app.Theme;
        Rgb back = focused ? theme.Selection : theme.Card;
        screen.Fill(row.X, row.Y, row.Width, 1, back);
        if (focused) screen.Set(row.X, row.Y, '┃', theme.Accent, back);
        (int nameWidth, int statusWidth) = Widths(row.Width);

        (double Position, int Voices)? progress = clip.Playing ? app.Link.ClipProgress(clip.Id) : null;
        if (clip.Playing) screen.Set(row.X + 2, row.Y, '▶', theme.LedOn, back);

        int x = row.X + 4;
        string icon = LayoutIcons.TextFor(clip.Icon, Sfx(state, state.Mixer.Soundboard!)?.Appearance.DefaultIcon ?? string.Empty);
        string label = icon.Length > 0 ? $"{icon} {clip.Name}" : clip.Name;
        Rgb nameFore = Rgb.TryParse(clip.Colour) ?? (focused ? theme.TextPrimary : theme.TextDetail);
        screen.Text(x, row.Y, label, nameFore, back, bold: focused, maxWidth: nameWidth);
        x += nameWidth + 1;
        screen.Text(x, row.Y, $"{clip.Seconds:0.0} s".PadLeft(7), theme.TextDetail, back);
        x += 7;
        screen.Text(x + 1, row.Y, clip.Mode, clip.Hold ? theme.TextInfo : theme.TextDetail, back, maxWidth: 8);
        x += 9;
        screen.Text(x + 1, row.Y, clip.Loop ? "loop" : "-", clip.Loop ? theme.TextInfo : theme.TextMuted, back);
        x += 6;
        screen.Text(x + 1, row.Y, Widgets.Percent(clip.Gain).PadLeft(4), theme.TextDetail, back);
        x += 6;

        int statusX = x + 1;
        int room = Math.Min(statusWidth, row.Right - statusX - 1);
        if (room <= 0) return;
        bool auditioned = state.Mixer.Soundboard?.Auditioning == clip.Id;
        if (clip.Playing)
        {
            string tail = (progress is { Voices: > 1 } many ? $" x{many.Voices}" : string.Empty)
                + (Held(clip.Id) ? " held" : string.Empty)
                + (auditioned ? " audition" : string.Empty);
            int bar = Math.Max(4, room - tail.Length - 1);
            Progress(screen, statusX, row.Y, bar, progress?.Position ?? 0, theme, back);
            screen.Text(statusX + bar, row.Y, tail, theme.TextInfo, back, maxWidth: Math.Max(0, room - bar));
            return;
        }
        (string word, Rgb fore) = auditioned ? ("auditioning", theme.TextInfo) : clip.Status switch
        {
            "ready" => ("ready", theme.TextSecondary),
            "loading" => ("loading", theme.TextInfo),
            "error" => (clip.Error is { Length: > 0 } error ? $"error: {error}" : "error", theme.TextWarning),
            _ => ("off", theme.TextMuted),
        };
        screen.Text(statusX, row.Y, word, fore, back, maxWidth: room);
    }

    /// <summary>How far a clip is: a solid bar on the meter's track, in half cells.</summary>
    private static void Progress(Screen screen, int x, int y, int width, double position, Theme theme, Rgb back)
    {
        double filled = Math.Clamp(position, 0, 1) * width;
        for (int cell = 0; cell < width; cell++)
        {
            double within = filled - cell;
            char ch = within >= 1 ? '█' : within >= 0.5 ? '▌' : '─';
            screen.Set(x + cell, y, ch, within >= 0.5 ? theme.MeterFill : theme.MeterTrack, back);
        }
    }

    /// <summary>The line under the list: the selected clip's reason, the hold rule, or the last import.</summary>
    private void Detail(Screen screen, Rect area, App app, SoundboardEntry board)
    {
        Theme theme = app.Theme;
        int y = area.Bottom - 1;
        ClipEntry? clip = board.Clips.FirstOrDefault(entry => entry.Id == _selected);
        ClipImportOutcome? outcome = app.Link.LastImport;
        (string text, Rgb fore) =
            app.Link.Importing ? ($"Importing {_importName}", theme.TextInfo)
            : clip is { Status: "error", Error.Length: > 0 } ? (clip.Error!, theme.TextWarning)
            : _importNote is { Length: > 0 } note ? (note, theme.TextWarning)
            : outcome is { Ok: false } failed ? (failed.Message, theme.TextWarning)
            : clip is not null && board.Auditioning == clip.Id
                ? ("Auditioning in the monitor mixes only, never on the stream: a stops it", theme.TextInfo)
            : clip is { Hold: true } ? (Held(clip.Id)
                ? "Held: press Enter or Space again to let it go"
                : _holds.ContainsKey(clip.Id) ? "Starting"
                : "Hold: the first press plays it, the next press lets it go", theme.TextInfo)
            : outcome is { Ok: true } done ? (done.Message, theme.TextSecondary)
            : (string.Empty, theme.TextMuted);
        screen.TextPad(area.X + 1, y, text, area.Width - 2, fore, theme.Card);
    }

    private static ChannelEntry? Sfx(Snapshot state, SoundboardEntry board) =>
        board.Channel is { } id ? state.Mixer.Channels.FirstOrDefault(channel => channel.Id == id) : null;

    public override bool Handle(KeyPress key, App app)
    {
        Snapshot? state = State(app);
        if (state?.Mixer.Soundboard is not { } board) return false;
        Update(app, board);
        DaemonLink link = app.Link;

        if (key.Key == Key.Char && !key.Ctrl)
        {
            switch (key.Char)
            {
                case 'b':
                    link.Send("setSoundboard", body => body["enabled"] = !board.Enabled);
                    return true;
                case 'S':
                    link.Send("stopClips");
                    LetGoAll(link);
                    return true;
                case 'i':
                    Import(app);
                    return true;
                case 'R':
                    if (link.Importing)
                    {
                        app.Say("An import is still running");
                        return true;
                    }
                    _importNote = null;
                    _importName = "the starter sounds";
                    link.RestoreStarterSounds();
                    return true;
                case '>': Master(app, state, board, 0.05); return true;
                case '<': Master(app, state, board, -0.05); return true;
                case '.': Master(app, state, board, 0.01); return true;
                case ',': Master(app, state, board, -0.01); return true;
            }
        }

        if (key.Key == Key.Char && !key.Ctrl)
        {
            switch (key.Char)
            {
                case 'p': Page(board, 1); return true;
                case 'P': Page(board, -1); return true;
                case 'G': RenamePage(app); return true;
            }
        }

        List<ClipEntry> clips = Shown(board);
        if (clips.Count == 0) return false;
        int index = Index(clips);
        ClipEntry clip = clips[index];

        switch (key.Key)
        {
            case Key.Up when key.Ctrl: Reorder(link, board, clips, index, -1); return true;
            case Key.Down when key.Ctrl: Reorder(link, board, clips, index, 1); return true;
            case Key.Up: Select(clips, Move(index, -1, clips.Count)); return true;
            case Key.Down: Select(clips, Move(index, 1, clips.Count)); return true;
            case Key.PageUp: Select(clips, Move(index, -10, clips.Count)); return true;
            case Key.PageDown: Select(clips, Move(index, 10, clips.Count)); return true;
            case Key.Home: Select(clips, 0); return true;
            case Key.End: Select(clips, clips.Count - 1); return true;
            case Key.Enter: Press(app, clip); return true;
            case Key.Char when key.Ctrl: return false;
        }
        if (key.Key != Key.Char) return false;

        switch (key.Char)
        {
            case ' ': Press(app, clip); return true;
            case 'a': Audition(app, board, clip); return true;
            case 's':
                link.Send("stopClip", body => body["clip"] = clip.Id);
                LetGo(link, clip.Id);
                return true;
            case '-' or '_': Gain(link, clip, -GainStep); return true;
            case '+' or '=': Gain(link, clip, GainStep); return true;
            case '[': Gain(link, clip, -FineGainStep); return true;
            case ']': Gain(link, clip, FineGainStep); return true;
            case 'm':
                string mode = ClipEntry.Modes[(Array.IndexOf(ClipEntry.Modes, clip.Mode) + 1) % ClipEntry.Modes.Length];
                link.Send("updateClip", body => { body["clip"] = clip.Id; body["mode"] = mode; });
                return true;
            case 'l':
                link.Send("updateClip", body => { body["clip"] = clip.Id; body["loop"] = !clip.Loop; });
                return true;
            case 'g':
                app.Ask($"Page for {clip.Name} ({DefaultPageWord} for {DefaultPageTitle})", clip.Page.Length == 0 ? DefaultPageWord : clip.Page, typed =>
                {
                    string page = typed == DefaultPageWord ? string.Empty : typed;
                    if (page == clip.Page) return;
                    // The clip leads the way: the tab it went to is shown with it selected.
                    _pendingPage = page;
                    _selected = clip.Id;
                    link.Send("updateClip", body => { body["clip"] = clip.Id; body["page"] = page; });
                }, limit: MaxPageLength);
                return true;
            case 'r':
                app.Ask($"Name for {clip.Name}", clip.Name, name =>
                    link.Send("updateClip", body => { body["clip"] = clip.Id; body["name"] = name; }), limit: 60);
                return true;
            case 'd':
                app.Ask($"Type yes to delete {clip.Name}", string.Empty, answer =>
                {
                    if (!answer.Equals("yes", StringComparison.OrdinalIgnoreCase)) return;
                    // Deleting the clip ends its voices and every hold on it.
                    _holds.Remove(clip.Id);
                    link.Send("deleteClip", body => body["clip"] = clip.Id);
                });
                return true;
            default:
                return false;
        }
    }

    /// <summary>The longest page name the daemon takes.</summary>
    internal const int MaxPageLength = 40;

    /// <summary>Show the next or the previous page, round the end.</summary>
    private void Page(SoundboardEntry board, int by)
    {
        List<string> pages = board.PageList;
        if (pages.Count < 2) return;
        _page = pages[(Math.Max(0, pages.IndexOf(_page)) + by + pages.Count) % pages.Count];
        _pendingPage = null;
        _selected = null;
        _lastIndex = 0;
        _scroll = 0;
    }

    /// <summary>
    /// Rename the page shown. A name another page has puts the clips together
    /// there; the default page's word moves them to the default page, which
    /// ends the page.
    /// </summary>
    private void RenamePage(App app)
    {
        string page = _page;
        if (page.Length == 0)
        {
            app.Say($"The {DefaultPageTitle} page keeps its name; g moves a clip to another page");
            return;
        }
        app.Ask($"New name for {page} ({DefaultPageWord} moves its clips to {DefaultPageTitle})", page, typed =>
        {
            string name = typed == DefaultPageWord ? string.Empty : typed;
            if (name == page) return;
            _pendingPage = name;
            app.Link.Send("renameClipPage", body => { body["page"] = page; body["name"] = name; });
        }, limit: MaxPageLength);
    }

    /// <summary>SFX's own volume is the board's master, stepped as the Mixer tab steps a channel's.</summary>
    private static void Master(App app, Snapshot state, SoundboardEntry board, double by)
    {
        if (Sfx(state, board) is { } sfx) ChannelEntry.StepVolume(app, sfx, by);
        else app.Say("The SFX channel was deleted; b turns the board on and creates it again");
    }

    private void Select(List<ClipEntry> clips, int index)
    {
        _selected = clips[index].Id;
        _lastIndex = index;
    }

    /// <summary>
    /// A press. A Hold or Toggle clip pressed again within
    /// <see cref="RepeatWindow"/> is the key repeating, and nothing is sent.
    /// A Hold clip this terminal holds is let go; otherwise a Hold press asks
    /// the daemon and is held once it is accepted. Any other press plays the
    /// clip in its own mode. The daemon refuses a press the board or the clip
    /// cannot take, with its sentence.
    /// </summary>
    private void Press(App app, ClipEntry clip)
    {
        DaemonLink link = app.Link;
        if (clip.Hold || clip.Mode == "toggle")
        {
            long now = app.Time.GetTimestamp();
            bool repeat = _lastPress.TryGetValue(clip.Id, out long last) && app.Time.GetElapsedTime(last, now) < RepeatWindow;
            _lastPress[clip.Id] = now;
            if (repeat) return;
        }
        if (!clip.Hold)
        {
            link.Send("playClip", body => body["clip"] = clip.Id);
            return;
        }
        if (_holds.ContainsKey(clip.Id))
        {
            LetGo(link, clip.Id);
            return;
        }
        if (!link.Connected)
        {
            app.Say("Not connected to the daemon");
            return;
        }
        _holds[clip.Id] = new Hold { Press = link.Request("playClip", body => body["clip"] = clip.Id) };
    }

    /// <summary>
    /// Audition the clip in the monitor mixes, or stop the audition when it
    /// is this clip's. A second key within <see cref="RepeatWindow"/> is the
    /// key repeating, and nothing is sent, so a key held down never starts
    /// and stops the audition over and over.
    /// </summary>
    private void Audition(App app, SoundboardEntry board, ClipEntry clip)
    {
        long now = app.Time.GetTimestamp();
        string key = "audition:" + clip.Id;
        bool repeat = _lastPress.TryGetValue(key, out long last) && app.Time.GetElapsedTime(last, now) < RepeatWindow;
        _lastPress[key] = now;
        if (repeat) return;
        if (board.Auditioning == clip.Id) app.Link.Send("stopAudition");
        // While the Wave XLR Pro builds the mixes its jacks play, the daemon refuses auditions: say why instead.
        else if (app.Link.State?.Mixer.Disabled("audition") is { } why) app.Say(why);
        else app.Link.Send("auditionClip", body => body["clip"] = clip.Id);
    }

    /// <summary>Ends this terminal's hold on a clip; one still waiting for the daemon ends once it is accepted.</summary>
    private void LetGo(DaemonLink link, string id)
    {
        if (!_holds.TryGetValue(id, out Hold? hold)) return;
        if (hold.Press is not null)
        {
            hold.LetGoWhenAccepted = true;
            return;
        }
        _holds.Remove(id);
        link.Send("releaseClip", body => body["clip"] = id);
    }

    private void LetGoAll(DaemonLink link)
    {
        foreach (string id in _holds.Keys.ToArray()) LetGo(link, id);
    }

    private static void Gain(DaemonLink link, ClipEntry clip, double by)
    {
        double gain = Math.Round(Math.Clamp(clip.Gain + by, 0, 1), 2);
        if (gain == clip.Gain) return;
        link.Send("updateClip", body => { body["clip"] = clip.Id; body["gain"] = gain; });
    }

    /// <summary>Swap the selected clip with its neighbour on the page shown, wherever the two stand in the library.</summary>
    private void Reorder(DaemonLink link, SoundboardEntry board, List<ClipEntry> clips, int index, int by)
    {
        int target = index + by;
        if (target < 0 || target >= clips.Count) return;
        List<string> order = [.. board.Clips.Select(clip => clip.Id)];
        int from = order.IndexOf(clips[index].Id), to = order.IndexOf(clips[target].Id);
        (order[from], order[to]) = (order[to], order[from]);
        _lastIndex = target;
        link.Send("setClipOrder", body => body["clips"] = new System.Text.Json.Nodes.JsonArray([.. order.Select(id => (System.Text.Json.Nodes.JsonNode?)id)]));
    }

    /// <summary>The longest path the daemon takes.</summary>
    internal const int MaxPath = 4096;

    private void Import(App app)
    {
        DaemonLink link = app.Link;
        if (link.Importing)
        {
            app.Say("An import is still running");
            return;
        }
        app.Ask(_page.Length == 0 ? "Path of the file to import" : $"Path of the file to import onto {_page}", string.Empty, typed =>
        {
            string path = ImportPath(typed);
            if (path.Length > MaxPath)
            {
                _importNote = $"The path is {path.Length} characters long; the daemon takes at most {MaxPath}.";
                app.Say(_importNote);
                return;
            }
            _importNote = null;
            _importName = Path.GetFileName(path);
            // The answer lands in the link, so it is not lost while another tab shows.
            link.ImportClip(path, _page);
        }, limit: MaxPath);
    }

    /// <summary>
    /// The path as the daemon wants it, absolute. Quotes a terminal put
    /// around a dropped file are taken off; without them, a backslash before
    /// a space or another character the shell would read is taken off, as a
    /// terminal escapes a dropped file. A <c>file://</c> address on this
    /// machine, with no host or <c>localhost</c>, has its <c>%XX</c> escapes
    /// decoded, and a <c>#</c> in it is part of the name. <c>~</c> is the
    /// home folder, and a relative path is taken from the directory the
    /// terminal mixer was started in, on the daemon's machine since both
    /// speak over the local socket.
    /// </summary>
    internal static string ImportPath(string typed)
    {
        string path = typed.Trim();
        if (path.Length >= 2 && (path[0] == '\'' && path[^1] == '\'' || path[0] == '"' && path[^1] == '"'))
            path = path[1..^1];
        else
            path = Unescape(path);
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string rest = path["file://".Length..];
            if (rest.StartsWith("localhost/", StringComparison.OrdinalIgnoreCase)) rest = rest["localhost".Length..];
            if (rest.StartsWith('/'))
            {
                try { path = Uri.UnescapeDataString(rest); }
                catch (UriFormatException) { path = rest; }
            }
        }
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// <summary>Takes off the backslash a terminal puts before a character the shell would read, such as a space.</summary>
    private static string Unescape(string path)
    {
        if (!path.Contains('\\', StringComparison.Ordinal)) return path;
        System.Text.StringBuilder plain = new(path.Length);
        for (int at = 0; at < path.Length; at++)
        {
            if (path[at] == '\\' && at + 1 < path.Length && " \t'\"\\()[]{}&;!$#*?<>|`~=,".Contains(path[at + 1], StringComparison.Ordinal))
                at++;
            plain.Append(path[at]);
        }
        return plain.ToString();
    }
}
