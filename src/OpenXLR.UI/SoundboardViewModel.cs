using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>What the daemon answered an import with: whether the clip was added, its sentence and the new clip's id.</summary>
public sealed record ClipImportResult(bool Ok, string Message, string? Clip);

/// <summary>
/// What the daemon answered a library export or import with: whether it went
/// through, its sentence, and whether it left clips out.
/// </summary>
public sealed record SoundboardTransferResult(bool Ok, string Message, bool Partial = false);

/// <summary>The soundboard's commands. The daemon client sends them; a test records them.</summary>
public interface ISoundboardCommands
{
    /// <summary><c>setSoundboard</c>; resolves to null, or the daemon's refusal.</summary>
    Task<string?> SetSoundboardAsync(bool enabled);

    /// <summary><c>importClip</c> with a path the user picked, onto <paramref name="page"/>, empty for the default page; null when the daemon did not answer.</summary>
    Task<ClipImportResult?> ImportClipAsync(string path, string page);

    /// <summary><c>exportSoundboard</c> to a path the user picked; null when the daemon did not answer.</summary>
    Task<SoundboardTransferResult?> ExportSoundboardAsync(string path);

    /// <summary><c>importSoundboard</c> from a path the user picked; null when the daemon did not answer.</summary>
    Task<SoundboardTransferResult?> ImportSoundboardAsync(string path, bool merge);

    /// <summary><c>restoreStarterSounds</c>; null when the daemon did not answer.</summary>
    Task<SoundboardTransferResult?> RestoreStarterSoundsAsync();

    /// <summary><c>renameClipPage</c>: an empty name moves the page's clips to the default page.</summary>
    Task<string?> RenameClipPageAsync(string page, string name);

    /// <summary><c>updateClip</c> with the fields that change; resolves to null, or the daemon's refusal.</summary>
    Task<string?> UpdateClipAsync(string clip, IReadOnlyDictionary<string, object> fields);

    Task<string?> DeleteClipAsync(string clip);

    Task<string?> SetClipOrderAsync(IReadOnlyList<string> clips);

    /// <summary><c>playClip</c> in the clip's own mode.</summary>
    Task<string?> PlayClipAsync(string clip);

    Task<string?> ReleaseClipAsync(string clip);

    Task<string?> StopClipAsync(string clip);

    Task<string?> StopClipsAsync();

    /// <summary><c>auditionClip</c>: the clip in the monitor mixes only, never on the stream.</summary>
    Task<string?> AuditionClipAsync(string clip);

    Task<string?> StopAuditionAsync();
}

/// <summary>
/// One clip of the soundboard as a pad draws it: the library entry, whether
/// the player holds it, whether it plays and how far along it is. Every value
/// is the daemon's; the position comes from the <c>clip:&lt;id&gt;</c> meter.
/// </summary>
public sealed class SoundClipViewModel : ViewModelBase, IHasId
{
    /// <summary>The press modes in the order the edit window lists them.</summary>
    internal static readonly string[] Modes = ["overlap", "restart", "toggle", "hold"];

    /// <summary>A clip with no icon of its own wears SFX's default, as its Deck key does.</summary>
    internal static readonly string DefaultIcon = LayoutDefaultIcons.ForChannel(SoundboardViewModel.DefaultChannel, false, null);

    internal SoundClipViewModel(SoundboardViewModel board, string id)
    {
        Board = board;
        Id = id;
        MeterKey = "clip:" + id;
    }

    /// <summary>The clip's key in a meter frame.</summary>
    internal string MeterKey { get; }

    public SoundboardViewModel Board { get; }

    public string Id { get; }

    private string _name = "";
    public string Name { get => _name; private set { if (Set(ref _name, value)) RaiseText(); } }

    private double _seconds;
    public double Seconds { get => _seconds; private set { if (Set(ref _seconds, value)) RaiseText(); } }

    private double _gain = 1;
    /// <summary>The clip's own gain, 0 to 1, before SFX's fader.</summary>
    public double Gain { get => _gain; private set { if (Set(ref _gain, value)) RaiseText(); } }

    private string _mode = "overlap";
    /// <summary>What a press does: <c>overlap</c>, <c>restart</c>, <c>toggle</c> or <c>hold</c>.</summary>
    public string Mode { get => _mode; private set { if (Set(ref _mode, value)) { Raise(nameof(IsHold)); RaiseText(); } } }

    private bool _loop;
    public bool Loop { get => _loop; private set { if (Set(ref _loop, value)) RaiseText(); } }

    private string _page = "";
    /// <summary>The page the clip is drawn on, empty for the default page.</summary>
    public string Page { get => _page; private set => Set(ref _page, value); }

    /// <summary>The clip's icon and colour, drawn like a channel's.</summary>
    public LayoutAppearanceViewModel Appearance { get; } = new();

    /// <summary>
    /// The pad's face is filled with the clip's colour, like a key of a
    /// Stream Deck, while a press plays it. A pad that cannot play, its clip
    /// loading, unreadable or the board off, keeps the plain face the skin
    /// draws off, so its name and any warning read in the skin's colours,
    /// with the clip's colour as a bar along the top.
    /// </summary>
    public bool Filled => Appearance.HasColour && CanPlay;

    /// <summary>The fill is a light colour: the pad's lettering is dark.</summary>
    public bool FillIsLight => Filled && Appearance.IsLight;

    /// <summary>The fill is a dark colour: the pad's lettering is light.</summary>
    public bool FillIsDark => Filled && !Appearance.IsLight;

    /// <summary>The clip has a colour the face is not filled with, since it cannot play: it runs along the top as a bar.</summary>
    public bool Tinted => Appearance.HasColour && !Filled;

    private string _status = "off";
    /// <summary>
    /// The daemon's one word for the clip: <c>ready</c> when a press plays it,
    /// <c>loading</c>, <c>error</c> with the reason in <see cref="Error"/>, or
    /// <c>off</c> while the board cannot play, the reason in the board's error.
    /// </summary>
    public string Status { get => _status; private set { if (Set(ref _status, value)) { Raise(nameof(Loaded)); Raise(nameof(HasError)); RaiseState(); } } }

    /// <summary>The player holds the clip, so a press plays it at once.</summary>
    public bool Loaded => _status == "ready";

    private string? _error;
    /// <summary>Why the player does not hold the clip, in the daemon's words, or null.</summary>
    public string? Error { get => _error; private set { if (Set(ref _error, value)) { Raise(nameof(HasError)); RaiseState(); } } }

    public bool HasError => _status == "error" && !string.IsNullOrEmpty(_error);

    private bool _statePlaying, _meterPlaying;
    /// <summary>A voice plays the clip: the state says so, or the last meter frame did.</summary>
    public bool Playing => _statePlaying || _meterPlaying;

    private bool _auditioned;
    /// <summary>The daemon auditions the clip in the monitor mixes now.</summary>
    public bool Auditioned
    {
        get => _auditioned;
        internal set { if (Set(ref _auditioned, value)) { Raise(nameof(PadName)); Raise(nameof(Detail)); Raise(nameof(Tip)); } }
    }

    private double _position;
    /// <summary>How far the newest voice is, 0 to 1; 0 while the clip is silent.</summary>
    public double Position { get => _position; private set => Set(ref _position, value); }

    private int _voices;
    /// <summary>How many voices play the clip now.</summary>
    public int Voices { get => _voices; private set => Set(ref _voices, value); }

    /// <summary>The key has to come up for the clip to end: the pad sends a release.</summary>
    public bool IsHold => _mode == "hold";

    /// <summary>A press plays the clip: the daemon says it is ready, and the mixer still runs.</summary>
    public bool CanPlay => _status == "ready" && Board.CanPlay;

    /// <summary>The player is loading the clip.</summary>
    public bool Loading => _status == "loading";

    /// <summary>The mode's name, as the pad and the edit window say it.</summary>
    public string ModeName => ModeNameOf(_mode);

    internal static string ModeNameOf(string mode) => mode switch
    {
        "restart" => Localizer.Text("SoundClipModeRestart"),
        "toggle" => Localizer.Text("SoundClipModeToggle"),
        "hold" => Localizer.Text("SoundClipModeHold"),
        _ => Localizer.Text("SoundClipModeOverlap"),
    };

    /// <summary>The pad's second line: why it cannot play, that it is loading or auditioned, or its length and mode.</summary>
    public string Detail => HasError ? _error!
        : Loading ? Localizer.Text("SoundClipLoading")
        : _auditioned ? Localizer.Text("SoundClipAuditioning")
        : _loop ? Localizer.Format("SoundClipDetailLoop", _seconds, ModeName)
        : Localizer.Format("SoundClipDetail", _seconds, ModeName);

    /// <summary>The name takes one line while the pad says why the clip cannot load, two otherwise.</summary>
    public int NameLines => HasError ? 1 : 2;

    /// <summary>A reason the clip cannot load may wrap to three lines; the length and mode keep one.</summary>
    public int DetailLines => HasError ? 3 : 1;

    /// <summary>
    /// Why a press plays nothing, in full, for a screen reader: the clip's
    /// own reason, or the board's while the board cannot play. Empty while it is ready.
    /// </summary>
    public string HelpText => CanPlay ? "" : HasError ? _error! : !Board.CanPlay ? Board.Notice : Loading ? Localizer.Text("SoundClipLoading") : "";

    /// <summary>The pad's name for a screen reader: the clip, what a press does, and whether it plays.</summary>
    public string PadName => Playing ? Localizer.Format("SoundPadNamePlaying", _name, ModeName)
        : _auditioned ? Localizer.Format("SoundPadNameAuditioning", _name, ModeName)
        : Localizer.Format("SoundPadName", _name, ModeName);

    /// <summary>The pad's tooltip: its name, its length, mode and gain, or why it cannot play.</summary>
    public string Tip => $"{_name}\n{Detail}\n{Localizer.Format("SoundClipGainTip", Math.Round(_gain * 100))}";

    /// <summary>The menu button's name for a screen reader.</summary>
    public string MenuName => Localizer.Format("SoundClipMenuName", _name);

    private void RaiseFill()
    {
        Raise(nameof(Filled));
        Raise(nameof(FillIsLight));
        Raise(nameof(FillIsDark));
        Raise(nameof(Tinted));
    }

    private void RaiseText()
    {
        Raise(nameof(ModeName));
        Raise(nameof(Detail));
        Raise(nameof(PadName));
        Raise(nameof(Tip));
        Raise(nameof(MenuName));
    }

    internal void RaiseState()
    {
        RaiseFill();
        Raise(nameof(CanPlay));
        Raise(nameof(Loading));
        Raise(nameof(NameLines));
        Raise(nameof(DetailLines));
        Raise(nameof(HelpText));
        RaiseText();
    }

    /// <summary>Take the clip's entry from a state push.</summary>
    internal void Apply(JsonObject clip)
    {
        Name = Text(clip["name"]) ?? Id;
        Seconds = Number(clip["seconds"]) ?? 0;
        Gain = Math.Clamp(Number(clip["gain"]) ?? 1, 0, 1);
        Mode = Text(clip["mode"]) is { } mode && Modes.Contains(mode) ? mode : "overlap";
        Loop = Flag(clip["loop"]);
        Page = Text(clip["page"]) ?? "";
        Appearance.Apply(new JsonObject { ["icon"] = Text(clip["icon"]) ?? "", ["colour"] = Text(clip["colour"]), ["defaultIcon"] = DefaultIcon });
        RaiseFill();
        Error = Text(clip["error"]) is { Length: > 0 } error ? error : null;
        // A daemon from before the word worked it out from loaded and error.
        string? status = Text(clip["status"]);
        Status = status is "ready" or "loading" or "error" or "off" ? status
            : Flag(clip["loaded"]) ? "ready" : _error is not null ? "error" : "loading";
        bool playing = Flag(clip["playing"]);
        if (_statePlaying != playing)
        {
            _statePlaying = playing;
            RaisePlaying();
        }
    }

    /// <summary>Take the clip's <c>[position, voices]</c> from a meter frame, or null when the frame has none.</summary>
    internal void ApplyMeter(JsonArray? meter)
    {
        double position = meter is { Count: > 0 } && Number(meter[0]) is double at ? Math.Clamp(at, 0, 1) : 0;
        int voices = meter is { Count: > 1 } && Number(meter[1]) is double count ? (int)Math.Max(0, count) : meter is null ? 0 : 1;
        Position = position;
        Voices = voices;
        if (_meterPlaying != voices > 0)
        {
            _meterPlaying = voices > 0;
            RaisePlaying();
        }
    }

    private void RaisePlaying()
    {
        Raise(nameof(Playing));
        Raise(nameof(PadName));
        if (!Playing) Position = 0;
    }

    internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    internal static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue(out bool flag) && flag;

    internal static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double number)
        && double.IsFinite(number) ? number : null;
}

/// <summary>One page of the soundboard as its tab draws it: the daemon's name for it, empty for the default page, and whether it is shown.</summary>
public sealed class SoundPageViewModel : ViewModelBase
{
    internal SoundPageViewModel(string key) => Key = key;

    /// <summary>The page as the daemon names it; empty for the default page.</summary>
    public string Key { get; }

    /// <summary>The tab's text: the page's name, or Main for the default page.</summary>
    public string Title => SoundboardViewModel.PageTitle(Key);

    private bool _selected;
    /// <summary>The window shows this page's pads.</summary>
    public bool IsSelected { get => _selected; internal set => Set(ref _selected, value); }
}

/// <summary>
/// The soundboard as the Soundboard window shows it: whether the daemon has
/// one, whether this installation can play it, whether it is on, the channel
/// its clips play into, the clips in library order, and a line saying what
/// the last import or change did. The board's master is SFX's own channel
/// volume, taken from the mixer's channel and never held here. Pressing a
/// pad sends <c>playClip</c>, and a hold pad's release sends
/// <c>releaseClip</c>; nothing is played here.
/// </summary>
public sealed class SoundboardViewModel : ViewModelBase
{
    /// <summary>The channel the clips play into while the daemon names none, as the daemon names it.</summary>
    internal const string DefaultChannel = "sfx";

    private readonly ISoundboardCommands _commands;
    private bool _applying;

    public SoundboardViewModel(ISoundboardCommands commands) => _commands = commands;

    private bool _reported;
    /// <summary>The daemon reports a soundboard: the buttons that open the window show.</summary>
    public bool IsReported { get => _reported; private set { if (Set(ref _reported, value)) RaiseBoard(); } }

    private bool _mixerRunning = true;
    /// <summary>
    /// The software mixer runs, so the daemon can report the board. While it
    /// does not (turned off, waiting for the audio server, rebuilding, or no
    /// daemon at all) the window keeps the last board it saw and plays nothing.
    /// </summary>
    public bool MixerRunning { get => _mixerRunning; private set { if (Set(ref _mixerRunning, value)) RaiseBoard(); } }

    private bool _connected = true;

    private bool _available;
    /// <summary>This installation has the native helper the player needs.</summary>
    public bool Available { get => _available; private set { if (Set(ref _available, value)) RaiseBoard(); } }

    private bool _enabled;
    /// <summary>The board's switch: on starts the player into SFX, creating SFX when the layout has none.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Raise();
            RaiseBoard();
            if (!_applying) Send(_commands.SetSoundboardAsync(value));
        }
    }

    private string? _channelId;
    /// <summary>The channel the clips play into, <c>sfx</c>, or null after that channel was deleted.</summary>
    public string? ChannelId { get => _channelId; private set { if (Set(ref _channelId, value)) RaiseBoard(); } }

    private ChannelViewModel? _channel;
    /// <summary>
    /// The mixer's own SFX channel, whose volume is the board's master: the
    /// window's fader is bound to the very object the strip draws.
    /// </summary>
    public ChannelViewModel? Channel
    {
        get => _channel;
        private set
        {
            if (ReferenceEquals(_channel, value)) return;
            if (_channel is not null) _channel.PropertyChanged -= OnChannelChanged;
            _channel = value;
            if (_channel is not null) _channel.PropertyChanged += OnChannelChanged;
            Raise();
            Raise(nameof(HasMaster));
            Raise(nameof(ShowsMaster));
            Raise(nameof(ChannelName));
            Raise(nameof(MasterHint));
            Raise(nameof(MasterName));
        }
    }

    private void OnChannelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelViewModel.HasVolume)) Raise(nameof(HasMaster));
        if (e.PropertyName != nameof(ChannelViewModel.Name)) return;
        Raise(nameof(ChannelName));
        Raise(nameof(MasterHint));
        Raise(nameof(MasterName));
        Raise(nameof(Notice));
    }

    /// <summary>The master fader shows: the mixer runs and has the channel, with a volume.</summary>
    public bool ShowsMaster => _mixerRunning && _channel is not null;

    /// <summary>The window draws SFX's volume as the board's master: the channel is there and has a volume.</summary>
    public bool HasMaster => _channel is { HasVolume: true };

    /// <summary>The channel's own name, which the user may have changed.</summary>
    public string ChannelName => _channel?.Name ?? "SFX";

    /// <summary>The line beside the master: whose fader it is and what decides where the clips are heard.</summary>
    public string MasterHint => Localizer.Format("SoundboardMasterHint", ChannelName);

    /// <summary>The master fader's name for a screen reader.</summary>
    public string MasterName => Localizer.Format("ChannelVolumeName", ChannelName);

    private string? _error;
    /// <summary>Why the board cannot play, in the daemon's own sentence; null exactly when it can.</summary>
    public string? Error { get => _error; private set { if (Set(ref _error, value)) RaiseBoard(); } }

    private IReadOnlyList<string> _formats = [];
    /// <summary>What this installation decodes, by extension: the file picker offers these.</summary>
    public IReadOnlyList<string> Formats => _formats;

    public ObservableCollection<SoundClipViewModel> Clips { get; } = [];

    /// <summary>The pages the clips are on, in the daemon's order: the default page first while it has a clip.</summary>
    public ObservableCollection<SoundPageViewModel> Pages { get; } = [];

    /// <summary>The clips on the page shown, in library order: the pads the window draws.</summary>
    public ObservableCollection<SoundClipViewModel> PageClips { get; } = [];

    /// <summary>The tabs show: there is a page other than the default one.</summary>
    public bool ShowsPages => Pages.Count > 1 || Pages.Count == 1 && Pages[0].Key.Length > 0;

    /// <summary>The longest page name the daemon takes.</summary>
    internal const int MaxPageLength = 40;

    /// <summary>A page's name as a tab and a sentence say it: Main for the default page.</summary>
    internal static string PageTitle(string page) => page.Length == 0 ? Localizer.Text("SoundboardMainPage") : page;

    private string _selectedPage = "";
    /// <summary>The page shown, empty for the default page. Clips added with Add clips or by a drop of files go to it.</summary>
    public string SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (!Set(ref _selectedPage, value ?? "")) return;
            Raise(nameof(SelectedPageTitle));
            Raise(nameof(DropHint));
            SyncPageClips();
        }
    }

    /// <summary>The page shown, as its tab says it.</summary>
    public string SelectedPageTitle => PageTitle(_selectedPage);

    /// <summary>The page shown is a named one, which can be renamed or emptied into the default page.</summary>
    public bool CanEditPage => _selectedPage.Length > 0 && Pages.Any(p => p.Key == _selectedPage);

    public bool HasClips => Clips.Count > 0;

    /// <summary>The window shows the line that says there are no clips yet.</summary>
    public bool ShowsEmpty => _reported && Clips.Count == 0;

    /// <summary>Stop all can reach a player: there are clips and the mixer runs.</summary>
    public bool CanStop => _mixerRunning && Clips.Count > 0;

    /// <summary>Any clip plays: Stop all lights.</summary>
    public bool AnyPlaying => Clips.Any(c => c.Playing);

    /// <summary>The board can play: the mixer runs, and the daemon reports the board and names no reason it cannot.</summary>
    public bool CanPlay => _mixerRunning && _reported && _error is null;

    /// <summary>Clips can be added: the daemon has the board and this installation the helper that checks them.</summary>
    public bool CanImport => _mixerRunning && _reported && _available;

    /// <summary>
    /// The sentence over the pads while they cannot play: the daemon's own,
    /// or that the daemon has no soundboard. Empty while they can.
    /// </summary>
    public string Notice => !_connected ? Localizer.Text("DaemonNotRunningSentence")
        : !_mixerRunning ? Localizer.Text("SoundboardMixerOff")
        : !_reported ? Localizer.Text("SoundboardNotReported") : _error ?? "";

    public bool HasNotice => Notice.Length > 0;

    /// <summary>The notice is a failure rather than a board, or a mixer, the user turned off.</summary>
    public bool NoticeWarns => _mixerRunning && _connected
        && (!_reported || _error is not null && !(_available && !_enabled && _channelId is not null));

    private string _status = "";
    /// <summary>What the last import, change or press came to, or empty.</summary>
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }

    public bool HasStatus => _status.Length > 0;

    private bool _statusWarns;
    /// <summary>The status line is a refusal.</summary>
    public bool StatusWarns { get => _statusWarns; private set => Set(ref _statusWarns, value); }

    private bool _importing;
    /// <summary>An import is running: the add buttons wait for it.</summary>
    public bool Importing { get => _importing; private set { if (Set(ref _importing, value)) { Raise(nameof(CanAdd)); Raise(nameof(CanExport)); } } }

    public bool CanAdd => CanImport && !_importing && !_transferring;

    private void RaiseBoard()
    {
        Raise(nameof(CanPlay));
        Raise(nameof(CanImport));
        Raise(nameof(CanAdd));
        Raise(nameof(CanExport));
        Raise(nameof(Notice));
        Raise(nameof(HasNotice));
        Raise(nameof(NoticeWarns));
        Raise(nameof(ShowsEmpty));
        Raise(nameof(ShowsBoard));
        Raise(nameof(ShowsMaster));
        Raise(nameof(CanStop));
        foreach (SoundClipViewModel clip in Clips) clip.RaiseState();
    }

    /// <summary>The window draws the board's actions: the daemon reported a board, now or before the mixer stopped.</summary>
    public bool ShowsBoard => _reported;

    // The last board the daemon reported, put back when it refuses an order the pads already show.
    private JsonObject? _lastBoard;
    private IReadOnlyList<ChannelViewModel> _lastChannels = [];

    /// <summary>
    /// Take <c>mixer.soundboard</c> from a state push, with the mixer's
    /// channels for the master. A daemon without a soundboard passes null;
    /// <paramref name="mixerRunning"/> false says the state has no mixer,
    /// and the last board stays as it was, unable to play.
    /// </summary>
    public void Apply(JsonNode? board, IEnumerable<ChannelViewModel> channels, bool mixerRunning = true)
    {
        _applying = true;
        try
        {
            _connected = true;
            MixerRunning = mixerRunning;
            if (!mixerRunning)
            {
                RaiseBoard();
                return;
            }
            if (board is not JsonObject state)
            {
                IsReported = false;
                Available = false;
                Enabled = false;
                ChannelId = null;
                Error = null;
                _formats = [];
                Channel = null;
                Clips.Clear();
                SyncPages([]);
                RaiseClips();
                return;
            }
            _lastBoard = (JsonObject)state.DeepClone();
            _lastChannels = [.. channels];
            IsReported = true;
            Available = SoundClipViewModel.Flag(state["available"]);
            Enabled = SoundClipViewModel.Flag(state["enabled"]);
            ChannelId = SoundClipViewModel.Text(state["channel"]);
            Error = SoundClipViewModel.Text(state["error"]) is { Length: > 0 } error ? error : null;
            _formats = [.. (state["formats"] as JsonArray ?? []).Select(SoundClipViewModel.Text).OfType<string>()
                .Where(f => f.Length > 0 && f.All(char.IsAsciiLetterOrDigit))];
            Channel = _channelId is null ? null : _lastChannels.FirstOrDefault(c => c.Id == _channelId);
            SyncClips(state["clips"] as JsonArray ?? [], state["pages"] as JsonArray);
            // A daemon from before auditions says nothing, which is none.
            string? auditioned = SoundClipViewModel.Text(state["auditioning"]);
            foreach (SoundClipViewModel clip in Clips) clip.Auditioned = clip.Id == auditioned;
            RaiseBoard();
        }
        finally { _applying = false; }
    }

    private void SyncClips(JsonArray clips, JsonArray? pages)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int position = 0;
        foreach (JsonObject clip in clips.OfType<JsonObject>())
        {
            if (SoundClipViewModel.Text(clip["id"]) is not { Length: > 0 } id || !seen.Add(id)) continue;
            SoundClipViewModel? existing = Clips.FirstOrDefault(c => c.Id == id);
            if (existing is null)
            {
                existing = new SoundClipViewModel(this, id);
                existing.Apply(clip);
                Clips.Insert(position, existing);
            }
            else
            {
                existing.Apply(clip);
                int at = Clips.IndexOf(existing);
                if (at != position) Clips.Move(at, position);
            }
            position++;
        }
        for (int i = Clips.Count - 1; i >= 0; i--)
            if (!seen.Contains(Clips[i].Id)) Clips.RemoveAt(i);
        SyncPages(pages);
        RaiseClips();
        bool fresh = _freshConnection;
        _freshConnection = false;
        LibraryReported?.Invoke(fresh);
    }

    /// <summary>
    /// Take the pages from a state push, or from the clips when the daemon
    /// is older than pages. The page shown stays shown while it has clips;
    /// once it has none, the window shows the page of the same name in
    /// another case, which a rename to it leaves, or else the first page.
    /// </summary>
    private void SyncPages(JsonArray? pages)
    {
        List<string> keys = pages is null
            ? [.. Clips.Select(c => c.Page).Distinct(StringComparer.Ordinal).OrderBy(p => p.Length == 0 ? 0 : 1)]
            : [.. pages.Select(SoundClipViewModel.Text).OfType<string>().Distinct(StringComparer.Ordinal)];
        for (int i = 0; i < keys.Count; i++)
        {
            SoundPageViewModel? existing = Pages.FirstOrDefault(p => p.Key == keys[i]);
            if (existing is null) Pages.Insert(i, new SoundPageViewModel(keys[i]));
            else if (Pages.IndexOf(existing) != i) Pages.Move(Pages.IndexOf(existing), i);
        }
        while (Pages.Count > keys.Count) Pages.RemoveAt(Pages.Count - 1);
        if (keys.Count > 0 && !keys.Contains(_selectedPage, StringComparer.Ordinal))
            _selectedPage = keys.FirstOrDefault(k => string.Equals(k, _selectedPage, StringComparison.OrdinalIgnoreCase)) ?? keys[0];
        Raise(nameof(SelectedPage));
        Raise(nameof(SelectedPageTitle));
        Raise(nameof(ShowsPages));
        Raise(nameof(CanEditPage));
        Raise(nameof(DropHint));
        SyncPageClips();
    }

    /// <summary>Show the selected page's clips, keeping each pad that stays, in library order.</summary>
    private void SyncPageClips()
    {
        foreach (SoundPageViewModel page in Pages) page.IsSelected = page.Key == _selectedPage;
        Raise(nameof(CanEditPage));
        List<SoundClipViewModel> shown = [.. Clips.Where(c => c.Page == _selectedPage)];
        for (int i = PageClips.Count - 1; i >= 0; i--)
            if (!shown.Contains(PageClips[i])) PageClips.RemoveAt(i);
        for (int i = 0; i < shown.Count; i++)
        {
            int at = PageClips.IndexOf(shown[i]);
            if (at < 0) PageClips.Insert(i, shown[i]);
            else if (at != i) PageClips.Move(at, i);
        }
    }

    /// <summary>Show a page's pads.</summary>
    public void ShowPage(SoundPageViewModel page) => SelectedPage = page.Key;

    /// <summary>Give the page shown another name; a name another page has puts the clips together on it. Resolves to null, or the refusal.</summary>
    public async Task<string?> RenamePageAsync(string name)
    {
        string page = _selectedPage;
        string clean = PageName(name);
        if (page.Length == 0 || clean.Length == 0 || clean == page) return null;
        string? error = await Run(_commands.RenameClipPageAsync(page, clean));
        if (error is null) SelectedPage = Pages.FirstOrDefault(p => string.Equals(p.Key, clean, StringComparison.OrdinalIgnoreCase))?.Key ?? clean;
        return error;
    }

    /// <summary>Move the page shown's clips to the default page, which ends the page. Resolves to null, or the refusal.</summary>
    public async Task<string?> RemovePageAsync()
    {
        string page = _selectedPage;
        if (page.Length == 0) return null;
        string? error = await Run(_commands.RenameClipPageAsync(page, ""));
        if (error is null) SelectedPage = "";
        return error;
    }

    /// <summary>
    /// A folder's name as a page: printable, trimmed and cut to the length
    /// the daemon takes. Empty, the default page, for a name with nothing left
    /// or a lone dash.
    /// </summary>
    internal static string PageName(string? name)
    {
        string text = new string([.. (name ?? "").Where(c => !char.IsControl(c))]).Trim();
        text = text.Length > MaxPageLength ? text[..MaxPageLength].TrimEnd() : text;
        // A lone dash is the default page's word in the terminal mixer, never a page.
        return text == DefaultPageWord ? "" : text;
    }

    /// <summary>What the daemon refuses as a page name: it stands for the default page where no empty name can be typed.</summary>
    internal const string DefaultPageWord = "-";

    // No board was reported on the daemon connection yet. Reset runs on the
    // UI thread in order with the states, so a state still queued from the
    // connection before cannot count for the new one.
    private bool _freshConnection = true;

    /// <summary>
    /// The daemon reported the board's clips, changed or not; true for the
    /// first report on a daemon connection, which says what the library
    /// holds and nothing about what was deleted from it. Raised only for a
    /// reported board, so a listener never takes a daemon without a board,
    /// or a stopped mixer, for an empty library.
    /// </summary>
    internal event Action<bool>? LibraryReported;

    private void RaiseClips()
    {
        Raise(nameof(HasClips));
        Raise(nameof(CanExport));
        Raise(nameof(CanStop));
        Raise(nameof(ShowsEmpty));
        Raise(nameof(AnyPlaying));
    }

    /// <summary>Route each <c>clip:&lt;id&gt;</c> meter to its pad; a pad absent from the frame is silent.</summary>
    public void ApplyMeters(JsonObject levels)
    {
        if (Clips.Count == 0) return;
        bool before = AnyPlaying;
        foreach (SoundClipViewModel clip in Clips)
            clip.ApplyMeter(levels[clip.MeterKey] as JsonArray);
        if (before != AnyPlaying) Raise(nameof(AnyPlaying));
    }

    /// <summary>The daemon went away: keep the last board, unable to play, until it comes back.</summary>
    public void Reset()
    {
        _connected = false;
        _freshConnection = true;
        Raise(nameof(Notice));
        MixerRunning = false;
        RaiseBoard();
    }

    // --- presses ---

    /// <summary>A pad went down: play the clip in its own mode.</summary>
    public void Press(SoundClipViewModel clip)
    {
        if (!clip.CanPlay) return;
        Send(_commands.PlayClipAsync(clip.Id));
    }

    /// <summary>A pad came up: a hold clip ends. A clip deleted meanwhile ended with its deletion.</summary>
    public void Release(SoundClipViewModel clip)
    {
        if (clip.IsHold && _reported && Clips.Contains(clip)) Send(_commands.ReleaseClipAsync(clip.Id));
    }

    public void Stop(SoundClipViewModel clip)
    {
        if (_reported) Send(_commands.StopClipAsync(clip.Id));
    }

    public void StopAll()
    {
        if (_reported) Send(_commands.StopClipsAsync());
    }

    /// <summary>
    /// Audition the clip in the monitor mixes, in place of any other
    /// audition. The daemon's next state says which clip it auditions.
    /// </summary>
    public void Audition(SoundClipViewModel clip)
    {
        if (clip.CanPlay && AuditionNote is null) Send(_commands.AuditionClipAsync(clip.Id));
    }

    private string? _auditionNote;
    /// <summary>
    /// Why no clip can be auditioned: the Wave XLR Pro builds the mixes its
    /// jacks play, and the daemon refuses auditions then. Null otherwise. A
    /// pad's menu greys its Audition entry and carries this as its tooltip.
    /// </summary>
    public string? AuditionNote { get => _auditionNote; internal set => Set(ref _auditionNote, value); }

    /// <summary>End the audition, whichever clip it plays.</summary>
    public void StopAudition()
    {
        if (_reported) Send(_commands.StopAuditionAsync());
    }

    // --- the library ---

    /// <summary>
    /// Move a clip one place earlier (-1) or later (+1). The pads move at
    /// once; the daemon's next state confirms or restores the order.
    /// </summary>
    public async Task Move(SoundClipViewModel clip, int delta)
    {
        // Among the pads of the clip's page: it trades places with its
        // neighbour there, wherever the two stand in the library.
        int shown = PageClips.IndexOf(clip), next = shown + delta;
        if (shown < 0 || next < 0 || next >= PageClips.Count) return;
        int at = Clips.IndexOf(clip), to = Clips.IndexOf(PageClips[next]);
        if (at < 0 || to < 0) return;
        Clips.Move(at, to);
        Clips.Move(at < to ? to - 1 : to + 1, at);
        SyncPageClips();
        string? error = await Run(_commands.SetClipOrderAsync([.. Clips.Select(c => c.Id)]));
        // Refused: the pads go back to the order the daemon last reported.
        if (error is not null && _lastBoard is { } last) Apply(last.DeepClone(), _lastChannels);
    }

    public bool CanMove(SoundClipViewModel clip, int delta)
    {
        int to = PageClips.IndexOf(clip) + delta;
        return PageClips.Contains(clip) && to >= 0 && to < PageClips.Count;
    }

    /// <summary>Change a clip's entry; resolves to null, or the daemon's refusal, which the caller shows.</summary>
    public Task<string?> UpdateAsync(SoundClipViewModel clip, IReadOnlyDictionary<string, object> fields)
        => fields.Count == 0 ? Task.FromResult<string?>(null) : _commands.UpdateClipAsync(clip.Id, fields);

    /// <summary>Delete a clip and its copy; the status line says what happened.</summary>
    public async Task DeleteAsync(SoundClipViewModel clip)
    {
        string? error = await _commands.DeleteClipAsync(clip.Id);
        ShowReport(error ?? Localizer.Format("SoundClipDeleted", clip.Name), error is not null);
    }

    /// <summary>The most clips the library holds, as the daemon enforces it.</summary>
    internal const int MaxClips = 64;

    private bool _cancelImport;

    /// <summary>Stop an import after the file it is on; the report says how far it got.</summary>
    public void CancelImport()
    {
        if (_importing) _cancelImport = true;
    }

    /// <summary>Import files onto the page shown; see <see cref="ImportOntoAsync"/>.</summary>
    public Task ImportAsync(IReadOnlyList<string> paths, IReadOnlyList<string>? unreadable = null)
        => ImportOntoAsync([.. paths.Select(path => (path, _selectedPage))], unreadable);

    /// <summary>
    /// Import each file onto its page in turn, one at a time, and say how it
    /// went: the daemon's own sentence for a single file, a count and the
    /// refusals for several. Files past the library's room are left out and
    /// counted, and a cancel stops the import after the file it is on. The
    /// window then shows the page the last clip went to.
    /// </summary>
    public async Task ImportOntoAsync(IReadOnlyList<(string Path, string Page)> files, IReadOnlyList<string>? unreadable = null)
    {
        unreadable ??= [];
        if (files.Count == 0 && unreadable.Count == 0 || !CanAdd) return;
        int room = Math.Max(0, MaxClips - Clips.Count);
        IReadOnlyList<(string Path, string Page)> taken = files.Count > room ? [.. files.Take(room)] : files;
        int skipped = files.Count - taken.Count;
        Importing = true;
        _cancelImport = false;
        try
        {
            int added = 0, tried = 0;
            var refusals = new List<string>(unreadable.Select(name => Localizer.Format("SoundboardFileUnreadable", name)));
            string? only = null;
            string? landed = null;
            foreach ((string path, string page) in taken)
            {
                if (_cancelImport) break;
                tried++;
                if (taken.Count > 1) ShowReport(Localizer.Format("SoundboardImporting", tried, taken.Count), false);
                ClipImportResult? result = await _commands.ImportClipAsync(path, page);
                string name = Path.GetFileName(path);
                if (result is null) refusals.Add(Localizer.Format("NoAnswerForPath", name));
                else if (result.Ok) { added++; only = result.Message; landed = page; }
                else refusals.Add(taken.Count > 1 ? $"{name}: {result.Message}" : result.Message);
            }
            if (landed is not null) SelectedPage = Pages.FirstOrDefault(p => string.Equals(p.Key, landed, StringComparison.OrdinalIgnoreCase))?.Key ?? landed;
            var lines = new List<string>();
            if (taken.Count == 1 && skipped == 0 && unreadable.Count == 0 && tried == 1) lines.Add(refusals.Count > 0 ? refusals[0] : only ?? "");
            else
            {
                if (taken.Count > 0) lines.Add(Localizer.Format("SoundboardImported", added, taken.Count));
                if (tried < taken.Count) lines.Add(Localizer.Format("SoundboardImportStopped", taken.Count - tried));
                if (skipped > 0) lines.Add(Localizer.Format("SoundboardImportFull", skipped, MaxClips));
                lines.AddRange(refusals);
            }
            ShowReport(string.Join("\n", lines.Where(l => l.Length > 0)), refusals.Count > 0 || skipped > 0);
        }
        finally
        {
            _cancelImport = false;
            Importing = false;
        }
    }

    /// <summary>
    /// The files in a folder this installation can import, by extension, in
    /// name order: the folder's own files, not its subfolders. Null, with the
    /// reason under the pads, when the folder cannot be read.
    /// </summary>
    public IReadOnlyList<string>? ClipFilesIn(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder)
                .Where(f => _formats.Contains(Path.GetExtension(f).TrimStart('.').ToLowerInvariant(), StringComparer.Ordinal))
                .Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowReport(ex.Message, true);
            return null;
        }
    }

    /// <summary>Import a folder's clips onto a page named after the folder, or say it has none.</summary>
    public async Task ImportFolderAsync(string folder)
    {
        if (ClipFilesIn(folder) is not { } files) return;
        if (files.Count == 0)
        {
            ShowReport(Localizer.Format("SoundboardFolderEmpty", Path.GetFileName(folder.TrimEnd('/'))), true);
            return;
        }
        string page = FolderPage(folder);
        await ImportOntoAsync([.. files.Select(f => (f, page))]);
    }

    /// <summary>The page a folder's clips go to: the folder's own name.</summary>
    internal static string FolderPage(string folder) => PageName(Path.GetFileName(folder.TrimEnd('/')));

    /// <summary>
    /// Files and folders dropped on the window: a file goes onto the page
    /// shown, and a folder's sound files, not its subfolders, onto a page
    /// named after it, all in one import. A folder that cannot be read, or
    /// has no sound file, is named in the report.
    /// </summary>
    public async Task ImportDroppedAsync(IReadOnlyList<string> paths, IReadOnlyList<string>? unreadable = null)
    {
        if (!CanAdd) return;
        var files = new List<(string Path, string Page)>();
        var refused = new List<string>(unreadable ?? []);
        var notes = new List<string>();
        foreach (string path in paths)
        {
            if (!Directory.Exists(path))
            {
                files.Add((path, _selectedPage));
                continue;
            }
            string name = Path.GetFileName(path.TrimEnd('/'));
            IReadOnlyList<string>? inside;
            try
            {
                inside = [.. Directory.EnumerateFiles(path)
                    .Where(f => _formats.Contains(Path.GetExtension(f).TrimStart('.').ToLowerInvariant(), StringComparer.Ordinal))
                    .Order(StringComparer.CurrentCultureIgnoreCase)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                notes.Add($"{name}: {ex.Message}");
                continue;
            }
            if (inside.Count == 0) notes.Add(Localizer.Format("SoundboardFolderEmpty", name));
            string page = FolderPage(path);
            files.AddRange(inside.Select(f => (f, page)));
        }
        if (files.Count == 0 && refused.Count == 0)
        {
            if (notes.Count > 0) ShowReport(string.Join("\n", notes), true);
            return;
        }
        await ImportOntoAsync(files, refused);
        if (notes.Count > 0) ShowReport(string.Join("\n", notes.Prepend(Status).Where(l => l.Length > 0)), true);
    }

    private bool _dropping;
    /// <summary>Files are being dragged over the window: it says where they would go.</summary>
    public bool Dropping { get => _dropping; set => Set(ref _dropping, value); }

    /// <summary>What a drop would do, while files are dragged over the window.</summary>
    public string DropHint => Localizer.Format("SoundboardDropHint", SelectedPageTitle);

    // --- the library as one archive ---

    private bool _transferring;
    /// <summary>A library export or import is running: the add, export and import buttons wait for it.</summary>
    public bool Transferring
    {
        get => _transferring;
        private set
        {
            if (!Set(ref _transferring, value)) return;
            Raise(nameof(CanAdd));
            Raise(nameof(CanExport));
        }
    }

    /// <summary>The library can be exported: the daemon reports a board with clips, and nothing else is copying.</summary>
    public bool CanExport => _mixerRunning && _reported && Clips.Count > 0 && !_importing && !_transferring;

    /// <summary>
    /// Write the library into a zip archive the user picked, and say how it
    /// went. The daemon writes only a name that ends in .zip, and the name is
    /// never changed here, since the overwrite question and a desktop
    /// portal's grant were for the name picked: another is refused at once.
    /// </summary>
    public Task ExportLibraryAsync(string path)
    {
        if (CanExport && !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ShowReport(Localizer.Text("SoundboardExportNeedsZip"), true);
            return Task.CompletedTask;
        }
        return TransferAsync(CanExport, Localizer.Text("SoundboardExporting"), () => _commands.ExportSoundboardAsync(path));
    }

    /// <summary>Add a library archive's clips, or replace the library with them, and say how it went.</summary>
    public Task ImportLibraryAsync(string path, bool merge)
        => TransferAsync(CanAdd, Localizer.Text("SoundboardImportingLibrary"), () => _commands.ImportSoundboardAsync(path, merge));

    /// <summary>Add back the starter sounds the library no longer has, and say how it went: the daemon says when none is missing.</summary>
    public Task RestoreStarterSoundsAsync()
        => TransferAsync(CanAdd, Localizer.Text("SoundboardRestoringStarter"), () => _commands.RestoreStarterSoundsAsync());

    private async Task TransferAsync(bool allowed, string running, Func<Task<SoundboardTransferResult?>> transfer)
    {
        if (!allowed) return;
        Transferring = true;
        try
        {
            ShowReport(running, false);
            SoundboardTransferResult? result;
            try { result = await transfer(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { result = new(false, ex.Message); }
            if (result is null) ShowReport(Localizer.Text("SoundboardTransferNoAnswer"), true);
            else ShowReport(result.Message, !result.Ok || result.Partial);
        }
        finally { Transferring = false; }
    }

    // Whether the line under the pads is a command's refusal, which the next
    // command that goes through clears, or a report of an import or a
    // deletion, which stays until the next report.
    private bool _statusIsRefusal;

    /// <summary>Show a report under the pads: an import's or a deletion's.</summary>
    internal void ShowReport(string text, bool warns)
    {
        _statusIsRefusal = false;
        StatusWarns = warns;
        Status = text;
    }

    /// <summary>Send a command, and show the daemon's refusal when it has one.</summary>
    private async void Send(Task<string?> command) => await Run(command);

    /// <summary>
    /// Wait for a command: a refusal shows under the pads, and a command that
    /// goes through clears an earlier refusal, never a report.
    /// </summary>
    private async Task<string?> Run(Task<string?> command)
    {
        string? error;
        try { error = await command; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { error = ex.Message; }
        if (error is not null)
        {
            _statusIsRefusal = true;
            StatusWarns = true;
            Status = error;
        }
        else if (_statusIsRefusal)
        {
            _statusIsRefusal = false;
            StatusWarns = false;
            Status = "";
        }
        return error;
    }
}

/// <summary>
/// The edit window's copy of one clip: its name, gain, press mode, loop,
/// icon, colour and page as the user changes them. Saving sends only the fields
/// that changed, in one <c>updateClip</c>, and shows the daemon's refusal.
/// </summary>
public sealed class SoundClipEditViewModel : ViewModelBase
{
    private readonly SoundClipViewModel _clip;
    // The clip as the window opened on it: a field is sent only when it differs from this.
    private readonly string _openName, _openColour, _openIcon, _openPage;
    private readonly double _openGainPercent;
    private readonly int _openModeIndex;
    private readonly bool _openLoop;

    public SoundClipEditViewModel(SoundClipViewModel clip)
    {
        _clip = clip;
        _name = _openName = clip.Name;
        _gainPercent = _openGainPercent = Math.Round(clip.Gain * 100);
        _modeIndex = _openModeIndex = Math.Max(0, Array.IndexOf(SoundClipViewModel.Modes, clip.Mode));
        _loop = _openLoop = clip.Loop;
        _colour = _openColour = clip.Appearance.Colour ?? "";
        Icon = _openIcon = clip.Appearance.Icon;
        _page = _openPage = clip.Page;
        ClipError = clip.HasError ? clip.Error! : "";
    }

    /// <summary>Why the player cannot hold the clip, in full, or empty.</summary>
    public string ClipError { get; }

    public bool HasClipError => ClipError.Length > 0;

    public SoundClipViewModel Clip => _clip;

    /// <summary>The window's title.</summary>
    public string Title => Localizer.Format("SoundClipEditTitle", _clip.Name);

    /// <summary>The press modes by name, in the order <see cref="ModeIndex"/> counts.</summary>
    public IReadOnlyList<string> ModeNames { get; } = [.. SoundClipViewModel.Modes.Select(SoundClipViewModel.ModeNameOf)];

    private string _name;
    public string Name { get => _name; set => Set(ref _name, value); }

    private double _gainPercent;
    /// <summary>The clip's gain in percent, 0 to 100.</summary>
    public double GainPercent { get => _gainPercent; set { if (Set(ref _gainPercent, value)) Raise(nameof(GainText)); } }

    public string GainText => $"{_gainPercent:0}%";

    private int _modeIndex;
    public int ModeIndex { get => _modeIndex; set { if (Set(ref _modeIndex, value)) Raise(nameof(ModeHint)); } }

    /// <summary>What a press does in the chosen mode.</summary>
    public string ModeHint => SoundClipViewModel.Modes.ElementAtOrDefault(_modeIndex) switch
    {
        "restart" => Localizer.Text("SoundClipModeRestartHint"),
        "toggle" => Localizer.Text("SoundClipModeToggleHint"),
        "hold" => Localizer.Text("SoundClipModeHoldHint"),
        _ => Localizer.Text("SoundClipModeOverlapHint"),
    };

    private bool _loop;
    public bool Loop { get => _loop; set => Set(ref _loop, value); }

    private string _colour;
    /// <summary>#RRGGBB, or empty for none.</summary>
    public string Colour { get => _colour; set => Set(ref _colour, value); }

    private string _page;
    /// <summary>The page the clip is on; empty for the default page.</summary>
    public string Page { get => _page; set => Set(ref _page, value); }

    /// <summary>The default page's name, shown in the page box while it is empty.</summary>
    public string MainPage => SoundboardViewModel.PageTitle("");

    /// <summary>What an empty page and a new name do, under the page box.</summary>
    public string PageHint => Localizer.Format("SoundClipPageHint", MainPage);

    /// <summary>The chosen icon's stored value; the window's picker sets it.</summary>
    public string Icon { get; set; }

    private string _note = "";
    /// <summary>Why the last save did not happen, or empty.</summary>
    public string Note { get => _note; private set { if (Set(ref _note, value)) Raise(nameof(HasNote)); } }

    public bool HasNote => _note.Length > 0;

    /// <summary>The fields that differ from the clip, as <c>updateClip</c> takes them; null with a note when one is not valid.</summary>
    internal Dictionary<string, object>? Changes()
    {
        var fields = new Dictionary<string, object>(StringComparer.Ordinal);
        string name = (_name ?? "").Trim();
        if (name.Length is 0 or > 60 || name.Any(char.IsControl))
        {
            Note = Localizer.Text("SoundClipNameHint");
            return null;
        }
        string colour = (_colour ?? "").Trim();
        if (colour.Length > 0 && !LayoutColours.IsHex(colour))
        {
            Note = Localizer.Text("ColourFormatHint");
            return null;
        }
        string page = (_page ?? "").Trim();
        if (page.Length > SoundboardViewModel.MaxPageLength || page.Any(char.IsControl) || page == SoundboardViewModel.DefaultPageWord)
        {
            Note = Localizer.Text("SoundClipPageInvalid");
            return null;
        }
        if (name != _openName) fields["name"] = name;
        // Compared as the percent the window shows, so a rename never sends a rounded gain.
        double percent = Math.Clamp(Math.Round(_gainPercent), 0, 100);
        if (percent != _openGainPercent) fields["gain"] = percent / 100;
        if (_modeIndex != _openModeIndex && SoundClipViewModel.Modes.ElementAtOrDefault(_modeIndex) is { } mode) fields["mode"] = mode;
        if (_loop != _openLoop) fields["loop"] = _loop;
        if ((Icon ?? "") != _openIcon) fields["icon"] = Icon ?? "";
        if (!string.Equals(colour, _openColour, StringComparison.OrdinalIgnoreCase)) fields["colour"] = colour;
        if (page != _openPage) fields["page"] = page;
        return fields;
    }

    /// <summary>Send the changes; true once the daemon took them, or when nothing changed.</summary>
    public async Task<bool> SaveAsync()
    {
        if (Changes() is not { } fields) return false;
        string? error = await _clip.Board.UpdateAsync(_clip, fields);
        Note = error ?? "";
        return error is null;
    }
}
