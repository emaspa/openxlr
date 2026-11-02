using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The soundboard as clients see it: the channel its clips play into (null
/// once that channel was deleted), whether it is on, whether this build has
/// the helper it needs, why it cannot play now (null exactly when it can),
/// the formats this build decodes, the clips in library order, the ones playing now,
/// the one auditioned in the monitor mixes, or null, and the pages the clips
/// are on, in the order clients draw them.
/// </summary>
public sealed record SoundboardState(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Channel,
    bool Enabled,
    bool Available,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Error,
    IReadOnlyList<string> Formats,
    IReadOnlyList<SoundboardClipState> Clips,
    IReadOnlyList<string> Playing,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Auditioning = null)
{
    public static SoundboardState Off { get; } = new(null, false, false, Mixer.SoundboardOff, [], [], []);

    /// <summary>
    /// Every page a clip is on: the default page, empty, first while it holds
    /// a clip, then each named page where its first clip stands in the library.
    /// </summary>
    public IReadOnlyList<string> Pages { get; init; } = [];
}

/// <summary>
/// One clip as the state publishes it: the library entry, whether the player
/// holds it, why not, whether it plays, and <see cref="Status"/>, which says
/// the same in one word so a client never has to work it out.
/// </summary>
public sealed record SoundboardClipState(
    string Id,
    string Name,
    double Seconds,
    double Gain,
    string Mode,
    bool Loop,
    string Icon,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Colour,
    bool Loaded,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Error,
    bool Playing,
    string Status = SoundboardClipStatus.Off,
    string Page = "");

/// <summary>
/// What a clip's <c>status</c> holds: <c>ready</c> to play, <c>loading</c>
/// into the player, <c>error</c> with the reason in the clip's <c>error</c>,
/// or <c>off</c> while the board cannot play at all, its reason in the
/// board's <c>error</c>.
/// </summary>
public static class SoundboardClipStatus
{
    public const string Ready = "ready";
    public const string Loading = "loading";
    public const string Error = "error";
    public const string Off = "off";
}

/// <summary>What <c>updateClip</c> changes; a null field is kept. An empty colour clears it, an empty page is the default page.</summary>
public sealed record SoundboardClipChange(string? Name = null, double? Gain = null, string? Mode = null, bool? Loop = null,
    string? Icon = null, string? Colour = null, string? Page = null);

public sealed partial class Mixer
{
    /// <summary>The channel the soundboard plays into, by id: renaming it keeps the board's binding.</summary>
    public const string SoundboardChannel = "sfx";

    /// <summary>The name a recreated SFX channel gets, from which its id is made.</summary>
    internal const string SoundboardChannelName = "SFX";

    /// <summary>What the soundboard refuses with when this build has no native helper.</summary>
    public const string SoundboardNeedsHost =
        "The soundboard needs the OpenXLR native plugin host, which this installation does not include. Install a package that ships it, or build with -p:EnableNativeLv2Host=true.";

    /// <summary>What the state says after the SFX channel was deleted while the board was on.</summary>
    public const string SoundboardChannelDeleted =
        "The soundboard is off because the SFX channel was deleted. Turn it on again to create SFX, muted in every mix.";

    /// <summary>What the state says when the board is on in the file but the layout has no SFX channel.</summary>
    internal const string SoundboardChannelMissing =
        "The soundboard has no SFX channel to play into. Turn it on again to create SFX, muted in every mix.";

    /// <summary>Why the board cannot play while it is off by choice.</summary>
    public const string SoundboardOff = "The soundboard is off. Turn it on first.";

    /// <summary>Why the board cannot play while it is on and its player has not started yet.</summary>
    public const string SoundboardStarting = "The soundboard is starting.";

    /// <summary>Why a clip in hold mode cannot be pressed without a connection to release it from.</summary>
    public const string SoundboardHoldNeedsConnection =
        "A clip in hold mode plays until its key comes up, which needs a connection that stays open to send releaseClip, such as the WebSocket. Press it in another mode here.";

    /// <summary>Why a press is refused while the player's link into SFX is being made again.</summary>
    public const string SoundboardReconnecting = "The soundboard is reconnecting to SFX. Press the clip again in a moment.";

    /// <summary>Why an audition is refused while the monitor outputs play no monitor mix to hear it in.</summary>
    public const string SoundboardAuditionUnheard =
        "The monitor outputs play no monitor mix, so an audition would not be heard. Have an output play a monitor mix first.";

    /// <summary>Why an audition is refused while no monitor output is selected.</summary>
    public const string SoundboardAuditionNoOutput =
        "No monitor output is selected, so an audition would not be heard. Select a monitor output first.";

    /// <summary>Why an audition is refused while listening is paused.</summary>
    public const string SoundboardAuditionPaused =
        "Listening is paused, so an audition would not be heard. Resume listening first.";

    /// <summary>Why an audition is refused while its links into the monitor mixes are being made again.</summary>
    public const string SoundboardAuditionReconnecting =
        "The soundboard is reconnecting to the monitor mixes. Audition the clip again in a moment.";

    private const string SoundboardKey = "soundboard";

    // The library is replaced whole, never changed in place, so a press can
    // read it without the mixer's lock.
    private volatile SoundboardLibrary _board = SoundboardLibrary.Empty;
    private Func<SoundboardLibrary, string?> _boardSave = library => library.Save();
    private string _clipDirectory = SoundboardLibrary.ClipDirectory;
    private string? _boardError;
    private FilterHandle? _playerFilter;
    private PortLink? _playerLink;
    // Read by presses and the meter push without the mixer's lock.
    private volatile SoundboardPlayer? _player;
    // Whether the player's outputs are linked into SFX now. An unlinked node
    // does not run, so a press sent then would wait and sound late.
    private volatile bool _playerLinked;
    // The player's audition pair into each monitor mix the monitor outputs
    // play, by mix id, and why an audition cannot be heard now, or null.
    // The refusal is read by auditions without the mixer's lock.
    private readonly Dictionary<string, PortLink> _auditionLinks = new(StringComparer.Ordinal);
    private volatile string? _auditionRefusal = SoundboardAuditionReconnecting;
    // A link failure is noted once, not at every sweep that tries again.
    private bool _auditionLinkFailing;
    // Clips the player died while loading. A file that crashes the decoder
    // is not given to the next player, which would die the same way.
    private readonly Dictionary<string, string> _crashedClips = new(StringComparer.Ordinal);
    private string? _lastBoardState;
    private string _lastBoardActivity = "";
    // Clips pressed in hold mode, by the connection that holds them: the
    // player that took each press and its number. A hold whose voice has
    // ended, by itself or otherwise, is forgotten before any hold is read.
    private readonly Dictionary<Guid, Dictionary<string, SoundboardHold>> _holds = [];

    private readonly record struct SoundboardHold(SoundboardPlayer Player, uint Press);

    /// <summary>
    /// Take the library read at start, where it is saved, and where its clip
    /// files live, and start the player when the library says so. Called
    /// once the graph is built; a later rebuild starts the player again by
    /// itself.
    /// </summary>
    public void UseSoundboard(SoundboardLibrary library, Func<SoundboardLibrary, string?>? save = null, string? clipDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        lock (_gate)
        {
            StopPlayerLocked();
            _board = library;
            if (save is not null) _boardSave = save;
            if (clipDirectory is not null) _clipDirectory = clipDirectory;
            _boardError = null;
            RemoveStagingFilesLocked();
            if (_built) StartSoundboardIfWantedLocked();
        }
    }

    /// <summary>
    /// The copies an import was checking when the daemon stopped. Nothing
    /// lists them, and the next import never reuses their names.
    /// </summary>
    private void RemoveStagingFilesLocked()
    {
        try
        {
            if (!Directory.Exists(_clipDirectory)) return;
            foreach (string pattern in new[] { ".import-*.tmp", ".export-*.tmp" })
                foreach (string leftover in Directory.EnumerateFiles(_clipDirectory, pattern))
                    TryDelete(leftover);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The library as it is now.</summary>
    public SoundboardLibrary SoundboardLibrary => _board;

    /// <summary>The soundboard's part of the state.</summary>
    public SoundboardState Soundboard()
    {
        lock (_gate) return SoundboardSnapshotLocked();
    }

    /// <summary>
    /// Why the board cannot play now, or null when its player runs: no
    /// helper, the last failure or the deletion of SFX, off by choice, or
    /// starting. Reads nothing that needs the mixer's lock.
    /// </summary>
    private string? SoundboardBlocker(SoundboardPlayer? player)
        => player is not null ? null
            : !_pw.PlayerHostInstalled ? SoundboardNeedsHost
            : Volatile.Read(ref _boardError) is { } error ? error
            : !_board.Enabled ? SoundboardOff
            : SoundboardStarting;

    private ChannelDefinition? SoundboardTargetLocked()
        => _config.Channels.FirstOrDefault(c => c.Id == SoundboardChannel && c.IsApplication);

    private SoundboardState SoundboardSnapshotLocked()
    {
        SoundboardPlayer? player = _player;
        IReadOnlyDictionary<string, (double Position, int Voices)> playing = player?.Progress()
            ?? new Dictionary<string, (double, int)>();
        SoundboardLibrary board = _board;
        bool available = _pw.PlayerHostInstalled;
        return new SoundboardState(
            SoundboardTargetLocked()?.Id,
            board.Enabled,
            available,
            SoundboardBlocker(player),
            available ? _pw.PlayerFormats : [],
            [.. board.Clips.Select(c =>
            {
                ClipLoad? load = player?.Status(c.Id);
                string? error = _crashedClips.GetValueOrDefault(c.Id) ?? load?.Error;
                string status = error is not null ? SoundboardClipStatus.Error
                    : player is null || load is null ? SoundboardClipStatus.Off
                    : load.Loaded ? SoundboardClipStatus.Ready : SoundboardClipStatus.Loading;
                return new SoundboardClipState(c.Id, c.Name, Math.Round(load is { Loaded: true } ? load.Seconds : c.Seconds, 2),
                    c.Gain, c.Mode, c.Loop, c.Icon, c.Colour, load?.Loaded == true, error, playing.ContainsKey(c.Id), status, c.Page);
            })],
            [.. board.Clips.Where(c => playing.ContainsKey(c.Id)).Select(c => c.Id)],
            player?.Auditioning is { } auditioned && board.Clips.Any(c => c.Id == auditioned) ? auditioned : null)
        {
            Pages = SoundboardLibrary.PagesOf(board.Clips),
        };
    }

    /// <summary>
    /// Turn the board on or off, and save that. On creates an application
    /// channel named SFX first when the layout has none with the id
    /// <c>sfx</c>; it starts muted in every mix, as every created channel
    /// does. An application channel that already holds the id is used as it
    /// is. <paramref name="persist"/> saves the layout when SFX is created.
    /// </summary>
    public void SetSoundboard(bool enabled, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!enabled)
            {
                // Saved first: a save that fails leaves the board on, in
                // memory and on disk, with its player playing.
                SaveBoardLocked(_board with { Enabled = false });
                StopPlayerLocked();
                _boardError = null;
                _restarts.Forget(SoundboardKey);
                return;
            }
            if (!_pw.PlayerHostInstalled) throw new InvalidOperationException(SoundboardNeedsHost);
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (_config.Channels.FirstOrDefault(c => c.Id == SoundboardChannel) is { IsApplication: false })
                throw new InvalidOperationException("The soundboard plays into the channel with the id sfx, and that channel is an input here.");
            if (SoundboardTargetLocked() is null)
            {
                CreateApplicationChannel(SoundboardChannelName, persist);
                if (SoundboardTargetLocked() is null)
                    throw new InvalidOperationException("SFX was created under another id, so the soundboard has no channel to play into.");
            }
            SoundboardLibrary previous = _board;
            SaveBoardLocked(_board with { Enabled = true });
            _boardError = null;
            _restarts.Forget(SoundboardKey);
            if (_player is not null) return;
            try { StartPlayerLocked(); }
            catch (Exception ex)
            {
                _boardError = ex.Message;
                try { SaveBoardLocked(previous); }
                catch (IOException) { _board = previous; }
                throw;
            }
        }
    }

    /// <summary>
    /// Copy a file the user picked into the clip directory and add it to the
    /// library under an id made from <paramref name="name"/>, or from the
    /// file's name, on <paramref name="page"/>, the default page when that is
    /// null or empty. The copy is checked by the helper in a process of its
    /// own before anything else reads it, so a file that crashes the decoder
    /// never reaches the running player. Returns the new clip.
    /// </summary>
    public SoundboardClip ImportClip(string path, string? name, string? page = null)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Give the clip's file as an absolute path.");
        if (!_pw.PlayerHostInstalled) throw new InvalidOperationException(SoundboardNeedsHost);
        string clipName = ClipName(name, path);
        string clipPage = PageName(page);
        string directory;
        lock (_gate)
        {
            if (_board.Clips.Count >= SoundboardLibrary.MaxClips)
                throw new InvalidOperationException($"The soundboard holds {SoundboardLibrary.MaxClips} clips already. Delete one first.");
            directory = _clipDirectory;
        }
        OpenXlrPaths.EnsurePrivateDir(directory);
        string staging = Path.Combine(directory, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            CopyClipIn(path, staging);
            // The copy is what is checked and what the player reads, so a file
            // changed after this check is never the one played.
            ClipProbe probe = _pw.ProbeClip(staging);
            if (probe.Seconds > SoundboardLibrary.MaxSeconds)
                throw new InvalidOperationException($"The clip is {probe.Seconds:0.0} s long; the limit is {SoundboardLibrary.MaxSeconds} s.");
            lock (_gate)
            {
                if (_board.Clips.Count >= SoundboardLibrary.MaxClips)
                    throw new InvalidOperationException($"The soundboard holds {SoundboardLibrary.MaxClips} clips already. Delete one first.");
                if (directory != _clipDirectory) throw new InvalidOperationException("The clip directory changed; import the file again.");
                // A deleted clip's id is never given again: a key still bound
                // to it must not play another clip.
                string id = SoundboardLibrary.NewId(clipName, _board.Clips.Select(c => c.Id).Concat(_board.Retired));
                var clip = new SoundboardClip
                {
                    Id = id,
                    Name = clipName,
                    File = FreeFileNameLocked(id, probe.Extension),
                    Seconds = Math.Round(probe.Seconds, 3),
                    Page = SamePageLocked(clipPage),
                };
                string target = Path.Combine(directory, clip.File);
                // No clip lists this name, so a file under it is a leftover of
                // an earlier import; rename replaces it, link or not.
                File.Move(staging, target, overwrite: true);
                try { SaveBoardLocked(_board with { Clips = [.. _board.Clips, clip] }); }
                catch
                {
                    TryDelete(target);
                    throw;
                }
                _crashedClips.Remove(id);
                try { _player?.Add(clip.Id, target, clip.Gain, clip.Loop); }
                catch (InvalidOperationException ex)
                {
                    // A player that cannot take a line is on its way out. The
                    // next one loads every clip, this one included; a start
                    // that fails says why in the board's error.
                    _pw.Note($"The soundboard player did not take {clip.Id}, so it starts again: {ex.Message}");
                    StopPlayerLocked();
                    StartSoundboardIfWantedLocked();
                }
                return clip;
            }
        }
        finally { TryDelete(staging); }
    }

    /// <summary>
    /// A clip file name no clip in the library lists, nor any of
    /// <paramref name="taken"/>: <c>id.ext</c>, or <c>id-2.ext</c> and on
    /// when that is in use. A clip's file is not always named after its id:
    /// a replace that met the id here gives the archive's clip the next name.
    /// </summary>
    private string FreeFileNameLocked(string id, string extension, ICollection<string>? taken = null)
    {
        var listed = new HashSet<string>(_board.Clips.Select(c => c.File), StringComparer.Ordinal);
        string file = $"{id}.{extension}";
        for (int n = 2; listed.Contains(file) || taken?.Contains(file) == true; n++) file = $"{id}-{n}.{extension}";
        return file;
    }

    /// <summary>The name a clip is imported under: the one given, else the file's name without its extension.</summary>
    internal static string ClipName(string? name, string path)
    {
        string text = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name;
        text = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        if (text.Length > SoundboardLibrary.MaxNameLength) text = text[..SoundboardLibrary.MaxNameLength].TrimEnd();
        if (text.Length == 0) text = "Clip";
        if (!string.IsNullOrWhiteSpace(name) && name.Trim() != text)
            throw new InvalidOperationException("The clip's name must have 1 to 60 printable characters.");
        return text;
    }

    /// <summary>A page as the library keeps it, trimmed; refused when it breaks the page rules.</summary>
    internal static string PageName(string? page)
    {
        string text = page?.Trim() ?? "";
        if (!SoundboardLibrary.IsValidPage(text))
            throw new InvalidOperationException($"A page name must have at most {SoundboardLibrary.MaxPageLength} printable characters, and cannot be {SoundboardLibrary.DefaultPageWord} alone.");
        return text;
    }

    /// <summary>
    /// The page a clip goes to: one already in the library under the same
    /// letters in another case keeps its own spelling, so "intro" and "Intro"
    /// never stand as two tabs. Only the clips <paramref name="others"/>
    /// takes are looked at, so the clips being moved never decide the
    /// spelling, and a page's case can be changed.
    /// </summary>
    private string SamePageLocked(string page, Func<SoundboardClip, bool>? others = null)
        => page.Length == 0 ? page
            : _board.Clips.Where(others ?? (_ => true)).Select(c => c.Page)
                .FirstOrDefault(p => string.Equals(p, page, StringComparison.OrdinalIgnoreCase)) ?? page;

    /// <summary>
    /// Copy a regular file of at most 64 MiB into a new private file. The
    /// source is opened once, following a link, and checked through that
    /// handle, so what is copied is what was checked.
    /// </summary>
    internal static void CopyClipIn(string source, string target)
    {
        using FileStream input = OpenClip(source);
        if (input.Length > SoundboardLibrary.MaxFileBytes)
            throw new InvalidOperationException($"The file is {input.Length / 1048576.0:0.0} MiB; the limit is {SoundboardLibrary.MaxFileBytes >> 20} MiB.");
        using FileStream output = OpenXlrPaths.CreatePrivate(target);
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            copied += read;
            if (copied > SoundboardLibrary.MaxFileBytes)
                throw new InvalidOperationException($"The file is larger than {SoundboardLibrary.MaxFileBytes >> 20} MiB.");
            output.Write(buffer, 0, read);
        }
        if (copied == 0) throw new InvalidOperationException("The file is empty.");
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenFile([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags);

    // Linux's values, the same on x86-64 and AArch64.
    private const int ReadOnly = 0, NoControllingTerminal = 0x100, NonBlocking = 0x800, CloseOnExec = 0x80000;
    private const int NoSuchFile = 2, AccessDenied = 13;

    /// <summary>
    /// Open a clip the user picked, following a link, and require the handle
    /// to be a regular file. The open does not block, so a pipe or a device
    /// put in the file's place between any check and the open is refused at
    /// once instead of holding the import, and with it the command slot.
    /// </summary>
    internal static FileStream OpenClip(string source)
    {
        if (!OperatingSystem.IsLinux())
        {
            RegularFile.Require(source);
            return new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        int fd = OpenFile(source, ReadOnly | NonBlocking | CloseOnExec | NoControllingTerminal);
        if (fd < 0)
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(error == NoSuchFile ? "The file does not exist."
                : error == AccessDenied ? "The file cannot be read." : "The file cannot be opened.");
        }
        var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true);
        try
        {
            if (RegularFile.Inspect($"/proc/self/fd/{fd}") is not { } status || (status.Mask & 1) == 0 || (status.Mode & 0xf000) != 0x8000)
                throw new InvalidOperationException("The path is not a regular file.");
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Change a clip's name, gain, press mode, loop, icon, colour or page.
    /// Gain and loop reach a playing clip at once. A clip moved to another
    /// page goes to the end of the library, so it is that page's last clip
    /// and a page it starts comes after the others.
    /// </summary>
    public void UpdateClip(string id, SoundboardClipChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        string? page = change.Page is null ? null : PageName(change.Page);
        lock (_gate)
        {
            SoundboardClip clip = BoardClip(id);
            SoundboardClip next = clip with
            {
                Name = change.Name?.Trim() ?? clip.Name,
                Gain = change.Gain ?? clip.Gain,
                Mode = change.Mode ?? clip.Mode,
                Loop = change.Loop ?? clip.Loop,
                Icon = change.Icon ?? clip.Icon,
                Colour = change.Colour is null ? clip.Colour : change.Colour.Length == 0 ? null : change.Colour,
                Page = page is null ? clip.Page : SamePageLocked(page, c => c.Id != id),
            };
            if (SoundboardLibrary.Problem(next) is { } problem)
                throw new InvalidOperationException(char.ToUpperInvariant(problem[0]) + problem[1..] + ".");
            SaveBoardLocked(_board with
            {
                Clips = next.Page == clip.Page ? [.. _board.Clips.Select(c => c.Id == id ? next : c)]
                    : [.. _board.Clips.Where(c => c.Id != id), next],
            });
            if (_player is { } player)
            {
                if (next.Gain != clip.Gain) player.SetGain(id, next.Gain);
                if (next.Loop != clip.Loop) player.SetLoop(id, next.Loop);
            }
        }
    }

    /// <summary>
    /// Give every clip on <paramref name="page"/> the page <paramref name="name"/>.
    /// An empty name moves them to the default page, which is how a page is
    /// removed; a name another page has joins them to it. The clips keep
    /// their places in the library.
    /// </summary>
    public void RenameClipPage(string page, string name)
    {
        ArgumentNullException.ThrowIfNull(page);
        string target = PageName(name);
        lock (_gate)
        {
            if (!_board.Clips.Any(c => c.Page == page))
                throw new InvalidOperationException(page.Length == 0 ? "No clip is on the default page." : $"No clip is on the page {page}.");
            target = SamePageLocked(target, c => c.Page != page);
            if (target == page) return;
            SaveBoardLocked(_board with { Clips = [.. _board.Clips.Select(c => c.Page == page ? c with { Page = target } : c)] });
        }
    }

    /// <summary>Stop a clip, forget it and delete its file.</summary>
    public void DeleteClip(string id)
    {
        lock (_gate)
        {
            SoundboardClip clip = BoardClip(id);
            SaveBoardLocked(_board with { Clips = [.. _board.Clips.Where(c => c.Id != id)], Retired = SoundboardLibrary.Retire(_board.Retired, id) });
            _crashedClips.Remove(id);
            ForgetHolds(id);
            try { _player?.Remove(id); }
            catch (InvalidOperationException) { /* a player that cannot take the line is restarted without the clip */ }
            string file = Path.Combine(_clipDirectory, clip.File);
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _pw.Note($"The soundboard clip file {file} could not be deleted: {ex.Message}");
            }
        }
    }

    /// <summary>Put the clips in the order clients draw them; <paramref name="ids"/> names every clip once.</summary>
    public void SetClipOrder(IReadOnlyList<string> ids)
    {
        lock (_gate)
        {
            if (ids.Count != _board.Clips.Count || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count
                || ids.Any(id => _board.Clips.All(c => c.Id != id)))
                throw new InvalidOperationException("Name every clip once.");
            SaveBoardLocked(_board with { Clips = [.. ids.Select(id => _board.Clips.First(c => c.Id == id))] });
        }
    }

    /// <summary>
    /// Press a clip's key: its own mode unless <paramref name="mode"/> gives
    /// another. Refused while the board is off, SFX is missing or the clip
    /// has not loaded. Never waits for the mixer's lock.
    /// </summary>
    /// <param name="holder">
    /// The connection the press came on. A press in hold mode needs one: the
    /// clip plays until that connection releases it or closes.
    /// </param>
    public void PlayClip(string id, string? mode, Guid? holder = null)
    {
        SoundboardClip clip = BoardClip(id);
        string press = mode ?? clip.Mode;
        bool hold = press == "hold";
        if (hold && holder is null) throw new InvalidOperationException(SoundboardHoldNeedsConnection);
        SoundboardPlayer player = LivePlayer();
        if (!_playerLinked) throw new InvalidOperationException(SoundboardReconnecting);
        uint number = player.Play(id, press, clip.Name);
        if (hold)
            lock (_holds)
            {
                ForgetEndedHoldsLocked();
                if (!_holds.TryGetValue(holder!.Value, out Dictionary<string, SoundboardHold>? held))
                    _holds[holder.Value] = held = new(StringComparer.Ordinal);
                held[id] = new SoundboardHold(player, number);
            }
    }

    /// <summary>The key came up: end the clip's hold voices.</summary>
    /// <param name="holder">
    /// The connection the key came up on. Its hold ends; the clip's hold
    /// voices end only when no other open connection still holds it with a
    /// voice that plays. A hold whose voice ended by itself holds nothing.
    /// </param>
    public void ReleaseClip(string id, Guid? holder = null)
    {
        BoardClip(id);
        bool othersHold;
        lock (_holds)
        {
            ForgetEndedHoldsLocked();
            if (holder is Guid mine && _holds.TryGetValue(mine, out Dictionary<string, SoundboardHold>? held) && held.Remove(id) && held.Count == 0)
                _holds.Remove(mine);
            othersHold = _holds.Values.Any(other => other.ContainsKey(id));
        }
        if (!othersHold) _player?.Release(id);
    }

    /// <summary>Fade the clip's voices out.</summary>
    public void StopClip(string id)
    {
        BoardClip(id);
        ForgetHolds(id);
        _player?.Stop(id);
    }

    /// <summary>Fade every voice out, the audition included. Nothing plays while the board is off, so that is no error.</summary>
    public void StopClips()
    {
        lock (_holds) _holds.Clear();
        _player?.StopAll();
    }

    /// <summary>
    /// Audition a clip: play it once from its start on the player's audition
    /// pair, in place of the audition playing. The pair reaches the monitor
    /// mixes the monitor outputs play and no other mix, so the clip is heard
    /// on the headphones and never on the stream: SFX's fader, mutes and
    /// sends do not apply, the clip's own gain does. The voice is not one of
    /// the eight a press plays, and a press never ends it. Refused while the
    /// board cannot play, the clip has not loaded, or no monitor mix would
    /// hear it, and while the player's link into SFX is made again. Never
    /// waits for the mixer's lock.
    /// </summary>
    public void AuditionClip(string id)
    {
        SoundboardClip clip = BoardClip(id);
        SoundboardPlayer player = LivePlayer();
        // The player stands still while its link into SFX is away, the
        // audition with it, so an audition then would wait like a press.
        if (!_playerLinked) throw new InvalidOperationException(SoundboardReconnecting);
        if (_auditionRefusal is { } refusal) throw new InvalidOperationException(refusal);
        player.Audition(id, clip.Name);
    }

    /// <summary>Fade the audition out. Nothing plays while the board is off, so that is no error.</summary>
    public void StopAudition() => _player?.StopAudition();

    /// <summary>
    /// A connection closed: release the clips it pressed in hold mode and
    /// did not release, unless another open connection holds them too, so a
    /// client that goes away with a key down never leaves a clip playing.
    /// Returns how many clips were released.
    /// </summary>
    public int ReleaseHeldClips(Guid holder)
    {
        string[] released;
        lock (_holds)
        {
            ForgetEndedHoldsLocked();
            if (!_holds.Remove(holder, out Dictionary<string, SoundboardHold>? held)) return 0;
            released = [.. held.Keys.Where(id => !_holds.Values.Any(other => other.ContainsKey(id)))];
        }
        foreach (string id in released)
        {
            try { _player?.Release(id); }
            catch (InvalidOperationException) { /* a player that cannot take the line is going; its voices go with it */ }
        }
        return released.Length;
    }

    /// <summary>The clips each open connection holds, for tests.</summary>
    internal IReadOnlyCollection<string> HeldClips(Guid holder)
    {
        lock (_holds)
        {
            ForgetEndedHoldsLocked();
            return _holds.TryGetValue(holder, out Dictionary<string, SoundboardHold>? held) ? [.. held.Keys] : [];
        }
    }

    private void ForgetHolds(string id)
    {
        lock (_holds)
            foreach (Guid holder in _holds.Keys.ToList())
                if (_holds[holder].Remove(id) && _holds[holder].Count == 0) _holds.Remove(holder);
    }

    /// <summary>
    /// Forget every connection's hold whose voice is over: the clip ended by
    /// itself, a toggle, stop or steal ended it, the helper dropped the
    /// press, or the player that took it is gone. Otherwise a key that stayed
    /// down past the clip's end would keep the next holder's release from
    /// ending that holder's voice. The player numbers its presses and reports
    /// the newest one whose hold voice has ended, so a press it has not taken
    /// yet, as when it comes just as the last voice ends, is kept.
    /// </summary>
    private void ForgetEndedHoldsLocked()
    {
        SoundboardPlayer? live = _player;
        foreach ((Guid holder, Dictionary<string, SoundboardHold> held) in _holds.ToList())
        {
            foreach ((string id, SoundboardHold hold) in held.ToList())
                if (!ReferenceEquals(hold.Player, live) || hold.Player.HoldEnded(id, hold.Press)) held.Remove(id);
            if (held.Count == 0) _holds.Remove(holder);
        }
    }

    private SoundboardClip BoardClip(string id)
        => _board.Clips.FirstOrDefault(c => c.Id == id) ?? throw new InvalidOperationException($"No clip has the id {id}.");

    private SoundboardPlayer LivePlayer()
    {
        SoundboardPlayer? player = _player;
        return player ?? throw new InvalidOperationException(SoundboardBlocker(null));
    }

    private void SaveBoardLocked(SoundboardLibrary next)
    {
        if (_boardSave(next) is string error) throw new IOException($"The soundboard library could not be saved: {error}");
        _board = next;
    }

    /// <summary>
    /// Start the player and link it into SFX. The link is made before any
    /// load or press is sent: an unlinked player never runs, and a press
    /// queued then would play late, all at once.
    /// </summary>
    private void StartPlayerLocked()
    {
        ChannelDefinition target = SoundboardTargetLocked() ?? throw new InvalidOperationException(SoundboardChannelMissing);
        List<SoundboardClip> clips = [.. _board.Clips.Where(c => !_crashedClips.ContainsKey(c.Id))];
        var controls = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int slot = 0; slot < clips.Count; slot++)
        {
            controls[SoundboardPlayer.Gain(slot)] = clips[slot].Gain;
            controls[SoundboardPlayer.Loop(slot)] = clips[slot].Loop ? 1 : 0;
        }
        FilterHandle filter = _pw.CreatePlayer(controls, out _);
        PortLink? link = null;
        try
        {
            link = _pw.LinkNodes(PipeWireAdapter.PlayerNode, "capture", target.SinkName, "playback");
            if (!link.Complete || link.Pairs.Count < 2)
                throw new InvalidOperationException("The soundboard player could not be connected to SFX.");
        }
        catch
        {
            if (link is not null) _pw.Unlink(link);
            _pw.StopFilter(filter);
            throw;
        }
        _playerFilter = filter;
        _playerLink = link;
        _playerLinked = true;
        string directory = _clipDirectory;
        _player = new SoundboardPlayer(filter.NativeHost!, clips.Select((c, slot) => (c.Id, slot, Path.Combine(directory, c.File))));
        _boardError = null;
        // After the player is in place: a sync that cannot link leaves the
        // audition refused and the sweep tries again, never a half-started player.
        SyncAuditionLinksLocked();
    }

    private void StopPlayerLocked()
    {
        SoundboardPlayer? player = _player;
        _player = null;
        _playerLinked = false;
        player?.Dispose();
        // Unlinked first: a node with no link is not run, so it falls silent
        // at once, whatever the helper is still doing.
        if (_playerLink is not null) _pw.Unlink(_playerLink);
        _playerLink = null;
        UnlinkAuditionLocked();
        UnlinkCueLocked();
        if (_playerFilter is not null) _pw.StopFilter(_playerFilter);
        _playerFilter = null;
    }

    /// <summary>
    /// Link the player's audition pair into each monitor mix the monitor
    /// outputs play (<see cref="HeadphoneMixesLocked"/>), and into nothing
    /// else: never a virtual microphone, the Aux mix or a channel, so an
    /// audition never reaches the stream or a call. While no monitor output
    /// is selected, or listening is paused, nothing would hear it: no link is
    /// kept and an audition is refused with the reason. A link into a mix that
    /// left the set is removed, and a broken one is made again, the new pairs
    /// first and only the ones it no longer has removed. Without a player
    /// every link goes. A link the audio server's tools refuse leaves the
    /// audition refused until the next sweep makes it. Called when the player
    /// starts, when the monitor outputs, their feeds or the pause change, and
    /// by the sweep; it never throws for a link, so none of those fails
    /// because of the soundboard.
    /// </summary>
    private void SyncAuditionLinksLocked()
    {
        if (_player is null || _playerFilter is null || !_built)
        {
            UnlinkAuditionLocked();
            return;
        }
        string? unheard = _monitorOutputs.Count == 0 ? SoundboardAuditionNoOutput
            : _monitorMuted ? SoundboardAuditionPaused : null;
        HashSet<string> wanted = unheard is null ? HeadphoneMixesLocked() : [];
        try
        {
            foreach (string gone in _auditionLinks.Keys.Where(id => !wanted.Contains(id)).ToList())
            {
                _pw.Unlink(_auditionLinks[gone]);
                _auditionLinks.Remove(gone);
            }
            bool complete = true;
            foreach (string id in wanted)
            {
                if (MonitorMixLocked(id) is not { } mix) continue;
                PortLink? previous = _auditionLinks.GetValueOrDefault(id);
                if (previous is { Pairs.Count: >= 2 } && _pw.EnsureLinks(previous) != LinkHealth.Broken) continue;
                PortLink fresh = _pw.LinkNodes(PipeWireAdapter.PlayerNode, PipeWireAdapter.AuditionPorts, mix.SinkName, "playback");
                _auditionLinks[id] = fresh;
                if (previous is not null) _pw.Unlink(new PortLink([.. previous.Pairs.Except(fresh.Pairs)]));
                if (!fresh.Complete || fresh.Pairs.Count < 2) complete = false;
            }
            _auditionRefusal = unheard ?? (wanted.Count == 0 ? SoundboardAuditionUnheard : complete ? null : SoundboardAuditionReconnecting);
            _auditionLinkFailing = false;
        }
        catch (InvalidOperationException ex)
        {
            _auditionRefusal = SoundboardAuditionReconnecting;
            if (!_auditionLinkFailing)
                _pw.Note($"The soundboard's audition could not be linked into the monitor mixes and is tried again: {ex.Message}");
            _auditionLinkFailing = true;
        }
    }

    private void UnlinkAuditionLocked()
    {
        _auditionRefusal = SoundboardAuditionReconnecting;
        foreach (PortLink link in _auditionLinks.Values) _pw.Unlink(link);
        _auditionLinks.Clear();
    }

    /// <summary>The monitor mixes the audition pair is linked into now, for tests.</summary>
    internal IReadOnlyCollection<string> AuditionMixes
    {
        get { lock (_gate) return [.. _auditionLinks.Where(l => l.Value.Pairs.Count >= 2).Select(l => l.Key).Order(StringComparer.Ordinal)]; }
    }

    /// <summary>Start the player when the library wants it and nothing stands in the way; a failure is the state's error.</summary>
    private void StartSoundboardIfWantedLocked()
    {
        if (!_built || !_board.Enabled || _player is not null) return;
        if (SoundboardTargetLocked() is null) { _boardError ??= SoundboardChannelMissing; return; }
        if (!_pw.PlayerHostInstalled) { _boardError = SoundboardNeedsHost; return; }
        if (_restarts.Blocked(SoundboardKey)) return;
        try { StartPlayerLocked(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _boardError = ex.Message;
            _restarts.Failed(SoundboardKey, ex.Message);
            _pw.Note($"The soundboard player did not start: {ex.Message}");
        }
    }

    /// <summary>Link the player into SFX again, after SFX's sink was reloaded under a new name.</summary>
    private void RelinkPlayerLocked()
    {
        if (_player is not { } player || SoundboardTargetLocked() is not { } target) return;
        _playerLinked = false;
        // An unlinked node does not run, so what it was playing and every
        // press since waited; they would all sound at once on the new link.
        // The player drops presses that waited long, and this ends the rest.
        try { player.StopAll(); }
        catch (InvalidOperationException) { }
        if (_playerLink is not null) _pw.Unlink(_playerLink);
        _playerLink = _pw.LinkNodes(PipeWireAdapter.PlayerNode, "capture", target.SinkName, "playback");
        _playerLinked = _playerLink.Complete && _playerLink.Pairs.Count >= 2;
    }

    /// <summary>
    /// SFX was deleted: the player stops and is unlinked, and the board is
    /// off with a notice. The library and the clip files stay.
    /// </summary>
    private void SoundboardChannelDeletedLocked()
    {
        bool wasOn = _board.Enabled || _player is not null;
        StopPlayerLocked();
        if (!wasOn) return;
        _boardError = SoundboardChannelDeleted;
        _restarts.Forget(SoundboardKey);
        SoundboardLibrary off = _board with { Enabled = false };
        if (_boardSave(off) is string error) _pw.Note($"The soundboard library could not be saved: {error}");
        _board = off;
    }

    /// <summary>
    /// The sweep's part: a player that died is started again; a player whose
    /// load got stuck is replaced without that clip; a player left behind by
    /// a new graph rate starts again at it; a link that broke is made again.
    /// Only a death the restart policy can blame counts towards turning the
    /// board off. <paramref name="rateAway"/> is the graph's new rate, when it
    /// is no longer the player's, read before the mixer's lock was taken.
    /// True when what clients see changed.
    /// </summary>
    private bool EnsureSoundboardLocked((SoundboardPlayer Player, int Rate)? rateAway = null)
    {
        if (_player is { } player && _playerFilter is { } filter)
        {
            int? rate = rateAway is { } away && ReferenceEquals(away.Player, player) ? away.Rate : null;
            if (!filter.IsAlive)
            {
                string? words = filter.LastWords;
                string? loading = player.Loading;
                int? signal = ExitSignal(player.Host);
                StopPlayerLocked();
                if (rate is int now)
                {
                    // The helper refuses a cycle at a rate it was not started
                    // at and stops: the graph moved, nothing failed.
                    _pw.Note($"The graph's sample rate is now {now} Hz; the soundboard player starts again at it.");
                }
                else
                {
                    // A clip is blamed only when the decoder crashed on it: a
                    // kill, a stall or a refused cycle says nothing about the
                    // file, and the clip is loaded again with the rest.
                    if (loading is not null && signal is int crash && DecoderCrash(crash)
                        && _board.Clips.FirstOrDefault(c => c.Id == loading) is { } culprit)
                        _crashedClips[loading] = $"The player crashed while it loaded {culprit.Name}, so it is left out. Import the file again once it plays elsewhere.";
                    _restarts.Failed(SoundboardKey, words);
                    _pw.Note($"The soundboard player stopped{(words is null ? "." : $". Its last message: {words}")}");
                    if (_restarts.Blocked(SoundboardKey))
                    {
                        _boardError = _restarts.Message(SoundboardKey, "The soundboard player kept failing and is off; turn the soundboard on to try again");
                        SoundboardLibrary off = _board with { Enabled = false };
                        if (_boardSave(off) is string error) _pw.Note($"The soundboard library could not be saved: {error}");
                        _board = off;
                    }
                }
            }
            else if (player.Stuck is { } stuck)
            {
                // The load did not answer in time and the decoder may never
                // come back, so a new helper starts without that clip. Not
                // counted: the file, not the player, is at fault.
                StopPlayerLocked();
                if (_board.Clips.FirstOrDefault(c => c.Id == stuck) is { } slow)
                    _crashedClips[stuck] = $"The player did not finish loading {slow.Name} in time, so it is left out. Import the file again once it plays elsewhere.";
                _pw.Note($"The soundboard player did not load {stuck} in time and starts again without it.");
            }
            else if (rate is int now)
            {
                // The clips were converted to the old rate when they loaded,
                // so the player starts again at the new one and loads them
                // again. Not a failure: the restart policy is not told.
                _pw.Note($"The graph's sample rate is now {now} Hz; the soundboard player starts again at it.");
                StopPlayerLocked();
            }
            else if (!_playerLinked || _playerLink is null || _pw.EnsureLinks(_playerLink) == LinkHealth.Broken)
                RelinkPlayerLocked();
            if (_player is not null) SyncAuditionLinksLocked();
        }
        if (_player is null && _board.Enabled) StartSoundboardIfWantedLocked();
        string state = JsonSerializer.Serialize(SoundboardSnapshotLocked());
        bool changed = state != _lastBoardState;
        _lastBoardState = state;
        return changed;
    }

    /// <summary>The signal that ended a helper, or null while it runs or after it exited by itself.</summary>
    private static int? ExitSignal(NativePluginHost host)
    {
        try { return host.Process.HasExited && host.Process.ExitCode > 128 ? host.Process.ExitCode - 128 : null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// The signals a decoder that read a bad file dies of: a bad address, a
    /// bus error, an arithmetic fault, an illegal instruction, an abort. A
    /// kill, which is how the kernel ends a process out of memory and how the
    /// daemon ends a stalled one, is not among them.
    /// </summary>
    internal static bool DecoderCrash(int signal) => signal is 4 or 6 or 7 or 8 or 11;

    /// <summary>
    /// The graph's sample rate when it is no longer the one the player was
    /// started at, with that player, else null. Called without the mixer's
    /// lock. The registry snapshot costs nothing; pw-metadata, the source the
    /// player's own rate came from, confirms a difference, and is asked
    /// whenever the helper has stopped too, so a death at a new rate is never
    /// taken for a failure while the snapshot catches up.
    /// </summary>
    private (SoundboardPlayer Player, int Rate)? SoundboardRateAway()
    {
        if (_player is not { } player) return null;
        int started = player.Host.SampleRate;
        if (player.Host.IsHealthy && (_pw.GraphSampleRate() is not int seen || seen == started)) return null;
        try
        {
            int now = PipeWireAdapter.ParseGraphSampleRate(_pw.Run("pw-metadata", "-n", "settings"));
            return now != started ? (player, now) : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Whether a clip started, ended or finished loading, or an audition
    /// started or ended, since the last call, for the meter push: a clip that ends by itself pushes the state
    /// at once, not at the next sweep. Never takes the mixer's lock.
    /// </summary>
    public bool SoundboardActivityChanged()
    {
        string activity;
        if (_player is not { } player) activity = "";
        else
        {
            IReadOnlyDictionary<string, (double Position, int Voices)> playing = player.Progress();
            activity = player.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + string.Join(",", playing.Keys.Order(StringComparer.Ordinal))
                + ":" + player.Auditioning;
        }
        if (activity == Volatile.Read(ref _lastBoardActivity)) return false;
        Volatile.Write(ref _lastBoardActivity, activity);
        return true;
    }

    /// <summary>
    /// Where each playing clip is, for the meter push: the key is
    /// <c>clip:</c> and the clip's id, the values its position, 0 to 1, and
    /// how many voices play it. Nothing for a clip that is not playing.
    /// </summary>
    private void AddClipPositions(Dictionary<string, double[]> meters)
    {
        if (_player is not { } player || !player.Host.IsRunning) return;
        foreach ((string id, (double position, int voices)) in player.Progress())
            meters[$"clip:{id}"] = [position, voices];
    }
}
