using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace OpenXLR.UI;

/// <summary>Global keys live with the UI, including while it is hidden in the tray.</summary>
internal sealed class DesktopKeys : IDisposable
{
    internal const string StopClipsKey = "clips_stop";
    internal const string MicMuteKey = "mic_mute";
    internal const string TalkKey = "push_to_talk";
    internal const string GlitchKey = "glitch_mark";
    /// <summary>Where a glitch marked by its key was marked from, on the daemon's timeline and in its journal.</summary>
    internal const string GlitchLabel = "Desktop key";
    internal static string ClipKey(string clip) => "clip_" + clip;

    private readonly DaemonClient _client;

    internal DesktopKeys(DaemonClient client)
    {
        _client = client;
        client.StateReceived += OnState;
        client.ConnectionChanged += OnConnection;
    }

    /// <summary>
    /// A microphone strip as its mute needs it: the channel, its name, whether
    /// it is muted now, and the command that mutes it. <paramref name="Control"/>
    /// is <c>mute</c> or <c>mute2</c> on a unit's input, on the primary when
    /// <paramref name="Device"/> is null and on that additional interface
    /// otherwise; null means a capture input's source mute.
    /// </summary>
    internal sealed record Microphone(string Channel, string Name, bool Muted, string? Device = null, string? Control = null);

    /// <summary>
    /// A soundboard clip as its key needs it: the id, the name the desktop
    /// shows, what a press does, and how long it plays, for a hold whose
    /// release never came.
    /// </summary>
    internal sealed record SoundClip(string Id, string Name, string Mode, double Seconds = 0, bool Loop = false, bool Playing = false);

    /// <summary>A clip the Desktop keys window lists: its label and whether it is ticked.</summary>
    internal sealed record ClipChoice(string Id, string Label, bool Ticked);

    /// <summary>
    /// One portal shortcut session: the keys it binds, and the connection of
    /// its own when it replaced a session the desktop would not bind again.
    /// </summary>
    private sealed class ShortcutSession(DesktopBus bus, string handle, DBusConnection? own)
    {
        internal DesktopBus Bus => bus;
        internal string Handle => handle;
        internal DBusConnection? Own => own;
        internal int State; // 0 binding, 1 active, -1 closed
        internal Dictionary<string, KeyAction> Actions = new(StringComparer.Ordinal);
        // A bind of this session went unanswered: the desktop may have taken it later.
        internal volatile bool Stale;
        internal readonly List<IDisposable> Observers = [];
        internal void Dispose()
        {
            foreach (IDisposable observer in Observers) observer.Dispose();
            Observers.Clear();
            own?.Dispose();
        }
    }

    private readonly SemaphoreSlim _configure = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private DBusConnection? _connection;
    private ShortcutSession? _current;
    private int _invoking;
    private readonly Queue<(KeyAction Action, Func<bool> IsActive)> _pending = new();
    private DesktopKeySettings _settings = new();
    // The soundboard as the daemon last reported it; null until it has.
    private SoundClip[]? _library;
    // Under _gone: clips seen deleted, the last name of every clip seen, and
    // the ids of the last report on this daemon connection. Only a clip in
    // one report and missing from the next on the same connection was
    // deleted; a library the daemon could not read, or a clip it refused to
    // load, is missing from the first report and keeps its key.
    private readonly HashSet<string> _gone = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private HashSet<string>? _lastReport;
    // Under _pending: keys that are down, with the time they went down, so a
    // key repeating while held is not a new press, and the clips a key holds,
    // with whether the daemon has reported them playing since.
    private readonly Dictionary<string, long> _down = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _held = new(StringComparer.Ordinal);
    // The desktop reports keys coming up: known for Plasma, GNOME and
    // Hyprland, learned from the first Deactivated anywhere else.
    private int _releases = ReportsRelease() ? 1 : 0;
    // The microphones the daemon reports on this connection, by channel id;
    // null while it is not connected or has not sent a state yet.
    private Dictionary<string, Microphone>? _mics;
    // Under _pending: the last name of every microphone seen, push to talk
    // holding the microphones open, the microphones it still has to mute
    // again, by channel id, and those mutes on their way.
    private readonly Dictionary<string, string> _micNames = new(StringComparer.Ordinal) { ["xlr1"] = "XLR 1", ["xlr2"] = "XLR 2" };
    private bool _talking;
    private readonly HashSet<string> _toClose = new(StringComparer.Ordinal);
    private Task _closing = Task.CompletedTask;
    internal string Status { get; private set; } = Localizer.Text("DesktopKeysDisabled");
    internal event Action? Changed;
    /// <summary>The glitch key marked a glitch, or could not: the sentence that says so, for the main window's notice. Raised off the UI thread.</summary>
    internal event Action<string>? GlitchMarked;

    /// <summary>How long a bind waits for the desktop's answer, which may be a dialog the user has to answer.</summary>
    internal TimeSpan BindTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A key down longer than this plays again, for a looping clip or one of unknown length whose release was lost.</summary>
    internal static readonly TimeSpan LongestHold = TimeSpan.FromSeconds(10);

    internal Task StartAsync() => ConfigureAsync(DesktopKeySettings.Load(), save: false);

    internal async Task ConfigureAsync(DesktopKeySettings settings, bool save = true)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (!await _configure.WaitAsync(0))
        {
            // A configuration can sit in the desktop's permission dialog for
            // a while. Queue this one behind it rather than dropping it: the
            // window's Apply stays disabled until it has run.
            SetStatus(Localizer.Text("DesktopKeysWaiting"));
            try { await _configure.WaitAsync(_lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
        try { await ConfigureHeldAsync(settings, save); }
        finally { _configure.Release(); }
    }

    // Runs with _configure held.
    private async Task ConfigureHeldAsync(DesktopKeySettings settings, bool save)
    {
        bool replacing = false;
        try
        {
            settings = WithoutDeleted(settings.Normalize());
            if (save) settings.Save(); // a failed save leaves the running session alone
            replacing = true;
            Stop();
            Volatile.Write(ref _settings, settings);
            if (!settings.Enabled) { SetStatus(Localizer.Text("DesktopKeysDisabled")); return; }
            SetStatus(Localizer.Text("DesktopKeysConnecting"));
            var connection = new DBusConnection(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") ?? DBusAddress.Session ?? throw new InvalidOperationException(Localizer.Text("NoSessionBus")));
            _connection = connection;
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
            _ = ObserveConnectionAsync(connection);
            var bus = new DesktopBus(connection);
            connection.AddMethodHandler(new DesktopFocus(bus));
            await connection.RequestNameAsync(DesktopFocus.Service).WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
            var actions = Actions(settings);
            if (actions.Count == 0)
            {
                SetStatus(Localizer.Text("DesktopKeysFocusOnly"));
                return;
            }
            ShortcutSession session;
            try { session = await CreateSessionAsync(bus, own: null); }
            catch (DBusErrorReplyException ex) when (ex.ErrorName is "org.freedesktop.DBus.Error.UnknownMethod"
                or "org.freedesktop.DBus.Error.UnknownInterface" or "org.freedesktop.DBus.Error.ServiceUnknown")
            {
                // No shortcut session exists. Keep the independent focus
                // service available to Deck instead of closing its bus too.
                SetStatus(Localizer.Text("DesktopKeysFocusOnly") + " " + Localizer.Text("DesktopKeysShortcutsUnavailable"));
                return;
            }
            Volatile.Write(ref _current, session);
            await BindNewAsync(session, actions);
            SetStatus(Localizer.Text("DesktopKeysActive"));
            // Push to talk starts closed: the key is what opens the
            // microphones, at every start of the UI as after an Apply.
            if (settings.PushToTalk) Close(settings.Microphones);
            if (save)
            {
                try
                {
                    await bus.Empty(DesktopBus.Portal, DesktopBus.PortalPath, DesktopBus.Shortcuts,
                        "ConfigureShortcuts", "osa{sv}", (ref MessageWriter w) =>
                        {
                            w.WriteObjectPath(session.Handle);
                            w.WriteString("");
                            w.WriteDictionary(new Dictionary<string, VariantValue>());
                        }, _lifetime.Token);
                }
                catch (DBusErrorReplyException ex) when (ex.ErrorName is
                    "org.freedesktop.DBus.Error.UnknownMethod" or "org.freedesktop.DBus.Error.UnknownInterface")
                {
                    if (IsActive(session)) SetStatus(Localizer.Text("DesktopKeysActive") + " " + Localizer.Text("DesktopKeysConfigureUnsupported"));
                }
                catch (Exception ex)
                {
                    // The keys are already bound. An editor failure must not
                    // tear down a working session or hide the opening error.
                    if (ReferenceEquals(_current, session) && !_lifetime.IsCancellationRequested)
                        SetStatus(Localizer.Format("DesktopKeysError", ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            if (replacing) Stop();
            SetStatus(Localizer.Format("DesktopKeysError", ex.Message));
        }
    }

    /// <summary>Open a shortcut session on the bus and listen to it; nothing is bound yet.</summary>
    private async Task<ShortcutSession> CreateSessionAsync(DesktopBus bus, DBusConnection? own)
    {
        await bus.RegisterHostAsync(_lifetime.Token);
        var created = await bus.Request("CreateSession", "a{sv}", token => (ref MessageWriter w) =>
            w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token, ["session_handle_token"] = token }), _lifetime.Token);
        var session = new ShortcutSession(bus, created["session_handle"].GetString(), own);
        DBusConnection connection = bus.Connection;
        try
        {
            session.Observers.Add(await connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = DesktopBus.Portal,
                Path = DesktopBus.PortalPath, Interface = DesktopBus.Shortcuts, Member = "Activated" },
                static (message, _) => { var r = message.GetBodyReader(); return (r.ReadObjectPath().ToString(), r.ReadString()); },
                notification =>
                {
                    if (notification.IsCompletion)
                    {
                        Interlocked.Exchange(ref session.State, -1);
                        if (!ReferenceEquals(_current, session) || _lifetime.IsCancellationRequested) return;
                        ReleaseHeld();
                        SetStatus(Localizer.Text("DesktopConnectionLost"));
                        return;
                    }
                    var (path, id) = notification.Value;
                    if (!IsActive(session) || path != session.Handle) return;
                    if (session.Actions.TryGetValue(id, out KeyAction? action)) Dispatch(session, id, action);
                    else if (session.Stale) _ = ReconcileAsync(session, id);
                }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed));
            session.Observers.Add(await connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = DesktopBus.Portal,
                Path = DesktopBus.PortalPath, Interface = DesktopBus.Shortcuts, Member = "Deactivated" },
                static (message, _) => { var r = message.GetBodyReader(); return (r.ReadObjectPath().ToString(), r.ReadString()); },
                notification =>
                {
                    // The Activated observer reports a lost connection.
                    if (notification.IsCompletion || !IsActive(session)) return;
                    var (path, id) = notification.Value;
                    if (path != session.Handle) return;
                    Volatile.Write(ref _releases, 1);
                    bool talked;
                    lock (_pending)
                    {
                        _down.Remove(id);
                        talked = id == TalkKey && _talking;
                        if (talked)
                        {
                            _talking = false;
                            _toClose.UnionWith(Volatile.Read(ref _settings).Microphones);
                        }
                    }
                    if (session.Actions.TryGetValue(id, out KeyAction? action) && action.Clip is { } clip)
                        _ = InvokeAsync(ReleaseAction(action, clip), () => IsActive(session), release: true);
                    // Behind the press in the queue, so the mute follows the
                    // unmute; a session that ends first mutes in ReleaseHeld.
                    else if (talked && action is not null && IsActive(session))
                        _ = InvokeAsync(action with { Invoke = CloseAndReportAsync }, () => IsActive(session), release: true);
                    else if (talked) _ = FlushCloseAsync();
                }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed));
            session.Observers.Add(await connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = DesktopBus.Portal,
                Path = session.Handle, Interface = "org.freedesktop.portal.Session", Member = "Closed" },
                static (_, _) => true, _ =>
                {
                    Interlocked.Exchange(ref session.State, -1);
                    if (!ReferenceEquals(_current, session) || _lifetime.IsCancellationRequested) return;
                    ReleaseHeld();
                    SetStatus(Localizer.Text("ShortcutSessionClosed"));
                }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed));
        }
        catch
        {
            session.Dispose();
            throw;
        }
        return session;
    }

    /// <summary>Bind a new session's keys; its signals count from here.</summary>
    private async Task BindNewAsync(ShortcutSession session, Dictionary<string, KeyAction> actions)
    {
        session.Actions = actions;
        await BindAsync(session, actions);
        if (Interlocked.CompareExchange(ref session.State, 1, 0) != 0)
            throw new InvalidOperationException(Localizer.Text("ShortcutSessionClosedWhileConfiguring"));
    }

    private bool IsActive(ShortcutSession session) => ReferenceEquals(Volatile.Read(ref _current), session)
        && Volatile.Read(ref session.State) == 1 && !_lifetime.IsCancellationRequested;

    private Task BindAsync(ShortcutSession session, Dictionary<string, KeyAction> actions)
        => session.Bus.Request("BindShortcuts", "oa(sa{sv})sa{sv}", token => (ref MessageWriter w) =>
        {
            w.WriteObjectPath(session.Handle);
            var array = w.WriteArrayStart(DBusType.Struct);
            foreach (var (id, action) in actions)
            {
                w.WriteStructureStart(); w.WriteString(id);
                var properties = new Dictionary<string, VariantValue> { ["description"] = action.Description };
                if (action.PreferredTrigger is { } trigger) properties["preferred_trigger"] = trigger;
                w.WriteDictionary(properties);
            }
            w.WriteArrayEnd(array);
            w.WriteString(""); // the portal supplies its own permission/configuration window
            w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
        }, _lifetime.Token, timeout: BindTimeout);

    /// <summary>The ids the session has bound, as the desktop lists them.</summary>
    private async Task<HashSet<string>> ListAsync(ShortcutSession session)
    {
        var listed = await session.Bus.Request("ListShortcuts", "oa{sv}", token => (ref MessageWriter w) =>
        {
            w.WriteObjectPath(session.Handle);
            w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
        }, _lifetime.Token);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (listed.TryGetValue("shortcuts", out VariantValue shortcuts))
            for (int i = 0; i < shortcuts.Count; i++) ids.Add(shortcuts.GetItem(i).GetItem(0).GetString());
        return ids;
    }

    /// <summary>
    /// Take a bound list as the session's. A clip that lost its key while a
    /// key held it is released: no release will come for it now.
    /// </summary>
    private void Adopt(ShortcutSession session, Dictionary<string, KeyAction> actions)
    {
        var kept = actions.Values.Select(a => a.Clip).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var dropped = new List<string>();
        lock (_pending)
        {
            foreach (string clip in _held.Keys.Where(c => !kept.Contains(c)).ToArray())
            {
                _held.Remove(clip);
                _down.Remove(ClipKey(clip));
                dropped.Add(clip);
            }
        }
        foreach (string clip in dropped) _ = _client.ReleaseClipAsync(clip);
        session.Actions = actions;
        session.Stale = false;
    }

    /// <summary>
    /// The soundboard as the daemon reports it, on every state that carries
    /// one. <paramref name="fresh"/> marks the first report on a new daemon
    /// connection, which tells what is in the library, never what was
    /// deleted from it. A clip added, renamed or deleted changes the keys
    /// bound for the clips the user chose; a mode change only changes what
    /// the next press does.
    /// </summary>
    internal void SetClips(IEnumerable<SoundClip> clips, bool fresh)
    {
        SoundClip[] next = [.. clips.Where(c => DesktopKeySettings.ValidClip(c.Id)).Select(c => c with
        {
            Name = new string([.. (c.Name ?? "").Where(ch => !char.IsControl(ch))]).Trim() is { Length: > 0 } name
                ? name[..Math.Min(name.Length, 60)] : c.Id,
        })];
        string[] deleted;
        lock (_gone)
        {
            var ids = next.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            deleted = fresh || _lastReport is null ? [] : [.. _lastReport.Where(id => !ids.Contains(id))];
            foreach (string id in deleted) { _gone.Add(id); _names.Remove(id); }
            foreach (SoundClip clip in next) _names[clip.Id] = clip.Name;
            _lastReport = ids;
        }
        EndStoppedHolds(next);
        SoundClip[]? previous = Volatile.Read(ref _library);
        Volatile.Write(ref _library, next);
        // Whether keys were bound for clips is decided before a deleted
        // clip's tick is forgotten: the last one going still needs a bind.
        bool clipKeys = Volatile.Read(ref _settings) is { Enabled: true, Clips.Count: > 0 };
        if (deleted.Length > 0) ForgetDeleted(deleted);
        bool rebind = deleted.Length > 0 || previous is null
            || !previous.Select(c => (c.Id, c.Name)).SequenceEqual(next.Select(c => (c.Id, c.Name)));
        if (rebind && clipKeys) _ = RebindAsync();
    }

    /// <summary>A deleted clip's tick goes from the saved choices; its id will never name another clip.</summary>
    private void ForgetDeleted(string[] deleted)
    {
        DesktopKeySettings settings = Volatile.Read(ref _settings);
        if (!settings.Clips.Any(deleted.Contains)) return;
        DesktopKeySettings kept = WithoutDeleted(settings);
        try { kept.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the keys still leave it out */ }
        Volatile.Write(ref _settings, kept);
    }

    private DesktopKeySettings WithoutDeleted(DesktopKeySettings settings)
    {
        lock (_gone) return settings.Clips.Any(_gone.Contains) ? settings with { Clips = [.. settings.Clips.Where(id => !_gone.Contains(id))] } : settings;
    }

    /// <summary>The clip was seen deleted on this daemon connection.</summary>
    internal bool IsDeleted(string clip) { lock (_gone) return _gone.Contains(clip); }

    /// <summary>
    /// The clips the Desktop keys window lists: every clip the daemon reports,
    /// and every ticked clip it does not report now and was not seen deleted,
    /// still ticked, so an Apply while a library is unreadable keeps them.
    /// </summary>
    internal List<ClipChoice> ClipChoices(DesktopKeySettings saved, IEnumerable<(string Id, string Name)> reported)
    {
        var choices = reported.Select(c => new ClipChoice(c.Id, Localizer.Format("KeyPlayClip", c.Name), saved.Clips.Contains(c.Id))).ToList();
        lock (_gone)
            foreach (string id in saved.Clips.Where(id => choices.All(c => c.Id != id) && !_gone.Contains(id)))
                choices.Add(new(id, Localizer.Format("ClipKeyUnavailable", _names.GetValueOrDefault(id, id)), true));
        return choices;
    }

    /// <summary>
    /// A held clip the daemon has seen play and now reports silent was
    /// stopped or reached its end: its hold ends here, whatever became of the
    /// key, so a release that never came does not keep the key down.
    /// </summary>
    private void EndStoppedHolds(SoundClip[] clips)
    {
        var ended = new List<string>();
        lock (_pending)
        {
            foreach (var (clip, seen) in _held.ToArray())
            {
                SoundClip? reported = clips.FirstOrDefault(c => c.Id == clip);
                if (reported is { Playing: true }) _held[clip] = true;
                else if (seen || reported is null)
                {
                    _held.Remove(clip);
                    _down.Remove(ClipKey(clip));
                    ended.Add(clip);
                }
            }
        }
        // The daemon still counts this connection's hold; let it go.
        foreach (string clip in ended) _ = _client.ReleaseClipAsync(clip);
    }

    /// <summary>
    /// Bind the chosen clips again after the library changed. The session
    /// that runs binds the new list itself, so its keys keep working while
    /// the desktop asks about a new one; KDE drops the keys of a clip left
    /// out and keeps every other. A desktop that binds a session only once
    /// gets a new session, bound before the old one closes.
    /// </summary>
    private async Task RebindAsync()
    {
        try { await _configure.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            DesktopKeySettings settings = Volatile.Read(ref _settings);
            if (!settings.Enabled) return;
            ShortcutSession? session = Volatile.Read(ref _current);
            // With no session running, the next start or Apply binds the list.
            if (session is null || !IsActive(session)) return;
            var actions = Actions(settings);
            if (!session.Stale && SameBinding(actions, session.Actions)) return;
            if (actions.Count == 0)
            {
                // Tell the desktop first, so KDE forgets the deleted clips'
                // keys, then keep the focus service alone.
                try { await BindAsync(session, actions); }
                catch (Exception) when (!_lifetime.IsCancellationRequested) { /* the session closes next anyway */ }
                await ConfigureHeldAsync(settings, save: false);
                return;
            }
            await BindAgainAsync(session, actions);
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested) { SetStatus(Localizer.Format("DesktopKeysError", ex.Message)); }
        catch (Exception) { /* the UI is closing */ }
        finally { _configure.Release(); }
    }

    // Runs with _configure held.
    private async Task BindAgainAsync(ShortcutSession session, Dictionary<string, KeyAction> actions)
    {
        try
        {
            await BindAsync(session, actions);
            Adopt(session, actions);
            if (IsActive(session)) SetStatus(Localizer.Text("DesktopKeysActive"));
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or PortalResponseException { Code: 2 }
            && IsActive(session))
        {
            await ReplaceAsync(session, actions);
        }
        catch (TimeoutException) when (IsActive(session))
        {
            // Nobody answered, perhaps the desktop's dialog. It may still
            // take the list later: ask what is bound now, and again when a
            // key arrives that the list in use does not have.
            session.Stale = true;
            SetStatus(Localizer.Format("DesktopKeysError", Localizer.Text("DesktopKeysBindUnanswered")));
            try
            {
                if ((await ListAsync(session)).SetEquals(actions.Keys)) Adopt(session, actions);
            }
            catch (Exception) when (!_lifetime.IsCancellationRequested) { /* asked again on the next unknown key */ }
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested)
        {
            // Cancelled or refused: the keys bound before keep working.
            if (IsActive(session)) SetStatus(Localizer.Format("DesktopKeysError", ex.Message));
        }
    }

    /// <summary>
    /// A key the list in use does not have came after a bind went unanswered:
    /// the desktop may have taken the new list since. Ask it, take the list
    /// when it matches, and otherwise bind it again; the press plays when
    /// its key is in the list the desktop has.
    /// </summary>
    private async Task ReconcileAsync(ShortcutSession session, string id)
    {
        try { await _configure.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (!IsActive(session)) return;
            if (session.Stale)
            {
                var actions = Actions(Volatile.Read(ref _settings));
                if ((await ListAsync(session)).SetEquals(actions.Keys))
                {
                    Adopt(session, actions);
                    SetStatus(Localizer.Text("DesktopKeysActive"));
                }
                else
                {
                    await BindAgainAsync(session, actions);
                    return;
                }
            }
            if (IsActive(session) && session.Actions.TryGetValue(id, out KeyAction? action)) Dispatch(session, id, action);
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested)
        {
            if (IsActive(session)) SetStatus(Localizer.Format("DesktopKeysError", ex.Message));
        }
        catch (Exception) { /* the UI is closing */ }
        finally { _configure.Release(); }
    }

    /// <summary>
    /// The desktop will not bind this session again (GNOME binds a session
    /// once). Bind a new session on a connection of its own first, and only
    /// once it holds the keys close the old one, so a failure keeps every
    /// key that worked.
    /// </summary>
    private async Task ReplaceAsync(ShortcutSession old, Dictionary<string, KeyAction> actions)
    {
        var connection = new DBusConnection(DesktopBus.SessionAddress());
        ShortcutSession? session = null;
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
            session = await CreateSessionAsync(new DesktopBus(connection), own: connection);
            await BindNewAsync(session, actions);
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested)
        {
            if (session is not null) session.Dispose();
            else connection.Dispose();
            if (IsActive(old)) SetStatus(Localizer.Format("DesktopKeysError", ex.Message));
            return;
        }
        catch (Exception)
        {
            if (session is not null) session.Dispose();
            else connection.Dispose();
            throw;
        }
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _current, session, old), old))
        {
            session.Dispose(); // replaced or stopped meanwhile
            return;
        }
        // Keys down on the old session will never report coming up here.
        ReleaseHeld();
        await CloseAsync(old);
        SetStatus(Localizer.Text("DesktopKeysActive"));
    }

    /// <summary>Close a session that is no longer the current one.</summary>
    private async Task CloseAsync(ShortcutSession session)
    {
        Interlocked.Exchange(ref session.State, -1);
        DBusConnection? own = session.Own;
        session.Dispose();
        if (own is not null) return; // closing its connection closed it
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await session.Bus.Empty(DesktopBus.Portal, session.Handle, "org.freedesktop.portal.Session", "Close", cancel: deadline.Token);
        }
        catch (Exception) { /* a desktop that kept it closes it with the connection */ }
    }

    private static bool SameBinding(Dictionary<string, KeyAction> next, Dictionary<string, KeyAction> bound)
        => next.Count == bound.Count && next.All(pair => bound.TryGetValue(pair.Key, out KeyAction? old) && old.Description == pair.Value.Description);

    /// <summary>The desktops whose shortcut portal sends Deactivated when a key comes up.</summary>
    internal static bool ReportsRelease(Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        string? current = env("XDG_CURRENT_DESKTOP");
        string desktop = string.IsNullOrWhiteSpace(current) ? env("XDG_SESSION_DESKTOP") ?? "" : current;
        return desktop.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(name => name.Equals("KDE", StringComparison.OrdinalIgnoreCase) || name.Equals("Plasma", StringComparison.OrdinalIgnoreCase)
                || name.Equals("GNOME", StringComparison.OrdinalIgnoreCase) || name.Equals("Hyprland", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The desktop has reported keys coming up, or is known to.</summary>
    internal bool DeliversRelease => Volatile.Read(ref _releases) == 1;

    /// <param name="Invoke">What a press sends; null for a clip, whose press depends on its mode and the desktop.</param>
    /// <param name="Clip">The soundboard clip a press plays, or null.</param>
    /// <param name="Quiet">Success leaves the status line as it was.</param>
    internal sealed record KeyAction(string Description, Func<Task<string?>>? Invoke, string? PreferredTrigger = null,
        string? Clip = null, bool Quiet = false);
    internal Dictionary<string, KeyAction> Actions(DesktopKeySettings settings)
    {
        var actions = new Dictionary<string, KeyAction>(StringComparer.Ordinal);
        foreach (string channel in settings.FocusChannels)
            actions["focus_" + channel] = new(Localizer.Format("KeyRouteFocusedApp", channel), () => _client.RouteFocusedAppAsync(channel));
        if (settings.MonitorControls)
            actions["monitor_mute"] = new(Localizer.Text("KeyToggleMonitoringPause"), _client.ToggleMonitorMutedAsync, "XF86AudioMute");
        if (settings.OutputControls)
        {
            actions["output_up"] = new(Localizer.Text("KeyOutputVolumeUp"), () => _client.AdjustOutputVolumeAsync(settings.OutputDevice, .05));
            actions["output_down"] = new(Localizer.Text("KeyOutputVolumeDown"), () => _client.AdjustOutputVolumeAsync(settings.OutputDevice, -.05));
            actions["output_mute"] = new(Localizer.Text("KeyToggleOutputMute"), () => _client.ToggleOutputMuteAsync(settings.OutputDevice));
        }
        foreach (string output in settings.MainOutputs)
            actions[DesktopKeySettings.MainKey(output)] = new(Localizer.Format("KeySetSystemOutput", output), () => _client.SetMainOutputAsync(output));
        if (settings.Clips.Count > 0)
        {
            // Library order for the clips the daemon reports, then every
            // other ticked clip not seen deleted, under its last name or its
            // id: a daemon that has not reported the board yet, or could not
            // read it, drops no binding.
            SoundClip[] library = Volatile.Read(ref _library) ?? [];
            var chosen = settings.Clips.ToHashSet(StringComparer.Ordinal);
            var clips = library.Where(c => chosen.Contains(c.Id)).Select(c => (c.Id, c.Name)).ToList();
            lock (_gone)
                foreach (string id in settings.Clips)
                    if (!_gone.Contains(id) && clips.All(c => c.Id != id)) clips.Add((id, _names.GetValueOrDefault(id, id)));
            foreach (var (id, name) in clips)
                actions[ClipKey(id)] = new(Localizer.Format("KeyPlayClip", name), null, Clip: id);
        }
        if (settings.ClipsStop) actions[StopClipsKey] = new(Localizer.Text("KeyStopClips"), _client.StopClipsAsync);
        IReadOnlyList<string> microphones = settings.Microphones;
        if (settings.MicMute)
            actions[MicMuteKey] = new(Localizer.Text("KeyToggleMicMute"), () => ToggleMicrophonesAsync(microphones), "XF86AudioMicMute", Quiet: true);
        // What a press sends is built on the press, which needs the key's release.
        if (settings.PushToTalk) actions[TalkKey] = new(Localizer.Text("KeyPushToTalk"), null, Quiet: true);
        // No suggested key: no keyboard has a glitch key, and a suggested
        // combination would be taken from every application that uses it.
        if (settings.GlitchMark) actions[GlitchKey] = new(Localizer.Text("KeyMarkGlitch"), MarkGlitchAsync, Quiet: true);
        return actions;
    }

    /// <summary>
    /// Mark a glitch as Ctrl+G does, labelled as a desktop key's. The sentence
    /// the UI shows for Ctrl+G, with the time, goes to the status line and to
    /// the main window.
    /// </summary>
    private async Task<string?> MarkGlitchAsync()
    {
        string at = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string? error = await _client.MarkGlitchAsync(GlitchLabel);
        string text = error is null ? Localizer.Format("GlitchMarkedAt", at) : Localizer.Format("GlitchNotMarked", error);
        GlitchMarked?.Invoke(text);
        return text;
    }

    private void Dispatch(ShortcutSession session, string id, KeyAction action)
    {
        if (action.Clip is { } clip) PressClip(session, id, clip, action);
        else if (id == TalkKey) PressTalk(session, action);
        else if (id is not (MicMuteKey or GlitchKey) || FirstPress(id)) _ = InvokeAsync(action, () => IsActive(session));
    }

    /// <summary>
    /// A key that toggles, or marks a glitch, acts once per press: where the
    /// desktop reports keys coming up, a repeat while it is down does
    /// nothing, until a release that never came has been waited for as long
    /// as a clip of unknown length is.
    /// </summary>
    private bool FirstPress(string id)
    {
        if (!DeliversRelease) return true;
        long now = Environment.TickCount64;
        lock (_pending)
        {
            if (_down.TryGetValue(id, out long since) && now - since < LongestHold.TotalMilliseconds) return false;
            _down[id] = now;
            return true;
        }
    }

    private void PressClip(ShortcutSession session, string id, string clip, KeyAction action)
    {
        bool releases = DeliversRelease;
        SoundClip? entry = Volatile.Read(ref _library)?.FirstOrDefault(c => c.Id == clip);
        if (releases)
        {
            // A key held down may repeat; only its first press plays. A key
            // that stays down longer than its clip lost its release, so the
            // press is a new one.
            TimeSpan window = entry is { Loop: false, Seconds: > 0 }
                ? TimeSpan.FromSeconds(Math.Max(entry.Seconds, .5)) : LongestHold;
            long now = Environment.TickCount64;
            lock (_pending)
            {
                if (_down.TryGetValue(id, out long since) && now - since < window.TotalMilliseconds) return;
                _down[id] = now;
            }
        }
        // The mode is said explicitly, so that a hold the key could never end
        // is not made: without a release, Hold plays as Overlap.
        string mode = entry?.Mode is "overlap" or "restart" or "toggle" ? entry.Mode
            : entry?.Mode == "hold" && releases ? "hold" : "overlap";
        Func<bool> isActive = () => IsActive(session);
        KeyAction press = mode == "hold" ? action with { Invoke = () => HoldAsync(clip, isActive) }
            : entry?.Mode == "hold" ? action with { Invoke = async () => await _client.PlayClipAsync(clip, mode)
                ?? Localizer.Format("DesktopKeyClipNoRelease", entry.Name) }
            : action with { Invoke = () => _client.PlayClipAsync(clip, mode) };
        _ = InvokeAsync(press, isActive);
    }

    private async Task<string?> HoldAsync(string clip, Func<bool> isActive)
    {
        string? error = await _client.PlayClipAsync(clip, "hold");
        if (error is not null) return error;
        bool ended;
        lock (_pending)
        {
            ended = !isActive();
            if (!ended) _held[clip] = _held.GetValueOrDefault(clip);
        }
        // The session ended while the press was on its way: nothing will
        // report the key coming up, so the hold ends here.
        if (ended) await _client.ReleaseClipAsync(clip);
        return null;
    }

    private KeyAction ReleaseAction(KeyAction action, string clip) => action with
    {
        Quiet = true,
        Invoke = async () =>
        {
            bool held;
            lock (_pending) held = _held.Remove(clip);
            return held ? await _client.ReleaseClipAsync(clip) : null;
        },
    };

    /// <summary>The session ended with keys down: the daemon ends their holds, since no release will come.</summary>
    private void ReleaseHeld()
    {
        string[] held;
        lock (_pending)
        {
            held = [.. _held.Keys];
            _held.Clear();
            _down.Clear();
            // Push to talk held open: no release will come for it now.
            if (_talking) _toClose.UnionWith(Volatile.Read(ref _settings).Microphones);
            _talking = false;
        }
        foreach (string clip in held) _ = _client.ReleaseClipAsync(clip);
        _ = FlushCloseAsync();
    }

    /// <summary>
    /// The microphones a state reports, by channel id, each with its mute as
    /// the Deck's mute keys find it: a channel that carries a driven unit's
    /// input has that unit's <c>mute</c> or <c>mute2</c>; a capture input
    /// whose source OpenXLR does not drive has the source's mute; and while
    /// no channel names the primary's inputs, XLR 1 and XLR 2 on the
    /// interface stand for them. A strip that is not present, an interface
    /// that is not connected and a channel with no mute are left out.
    /// </summary>
    internal static Dictionary<string, Microphone> ReadMicrophones(JsonNode? state)
    {
        var found = new Dictionary<string, Microphone>(StringComparer.Ordinal);
        if (state is not JsonObject root || root["mixer"] is not JsonObject mixer || mixer["channels"] is not JsonArray channels) return found;
        bool named = channels.Any(c => c is JsonObject channel && UnitInputInfo.From(channel["unitInput"]) is { Primary: true });
        foreach (JsonNode? node in channels)
        {
            if (node is not JsonObject c || Text(c["id"]) is not { Length: > 0 } id || Flag(c["present"]) == false) continue;
            string name = Text(c["name"]) is { Length: > 0 } label ? label : id;
            if (UnitInputInfo.From(c["unitInput"]) is { } input)
            {
                if (input.Input is not (1 or 2)) continue;
                JsonNode? holder = input.Primary ? root : (root["waveInterfaces"] as JsonArray)?.FirstOrDefault(unit => unit is JsonObject entry
                    && Text(entry["id"]) == input.Unit && Flag(entry["active"]) != true && Flag(entry["enabled"]) == true);
                if (UnitMute(holder, input.Input) is bool muted)
                    found[id] = new(id, name, muted, input.Primary ? null : input.Unit, input.Input == 2 ? "mute2" : "mute");
            }
            else if (c["captureGain"] is JsonObject gain && Flag(gain["muted"]) is bool captureMuted)
                found[id] = new(id, name, captureMuted);
            else if (!named && Flag(c["hardware"]) == true && id is "xlr1" or "xlr2"
                && (c["binding"] is not JsonObject binding || Text(binding["kind"]) is null or "unit")
                && UnitMute(root, id == "xlr2" ? 2 : 1) is bool stripMuted)
                found[id] = new(id, name, stripMuted, null, id == "xlr2" ? "mute2" : "mute");
        }
        return found;
    }

    private static bool? UnitMute(JsonNode? holder, int input)
    {
        if (holder is not JsonObject unit || Flag(unit["connected"]) != true || unit["capabilities"] is not JsonObject capabilities
            || Flag(capabilities["mute"]) != true || unit["state"] is not JsonObject state) return null;
        int inputs = capabilities["xlrInputs"] is JsonValue count && count.TryGetValue(out int number) ? number : 1;
        return input == 2 && inputs < 2 ? null : Flag(state[input == 2 ? "mute2" : "mute"]);
    }

    private static bool? Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>The microphones the daemon reports on this connection, or null before its first state.</summary>
    internal IReadOnlyDictionary<string, Microphone>? Microphones => Volatile.Read(ref _mics);

    private void OnState(JsonNode state)
    {
        Dictionary<string, Microphone> mics;
        try { mics = ReadMicrophones(state); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return; }
        lock (_pending) foreach (Microphone mic in mics.Values) _micNames[mic.Channel] = mic.Name;
        Volatile.Write(ref _mics, mics);
        // A microphone push to talk still has to mute may have appeared.
        _ = FlushCloseAsync();
    }

    /// <summary>
    /// The daemon connection dropped. Push to talk held open ends here: the
    /// microphones are muted with the first state of the next connection.
    /// </summary>
    private void OnConnection(bool up)
    {
        if (up) return;
        Volatile.Write(ref _mics, null);
        bool held;
        lock (_pending)
        {
            held = _talking;
            if (held)
            {
                _talking = false;
                _down.Remove(TalkKey);
                _toClose.UnionWith(Volatile.Read(ref _settings).Microphones);
            }
        }
        if (held) SetStatus(Localizer.Text("PushToTalkDaemonLost"));
    }

    /// <summary>The covered microphones the daemon reports now, and the names of the others.</summary>
    private (List<Microphone> Found, List<string> Missing) Resolve(IReadOnlyList<string> covered)
    {
        Dictionary<string, Microphone> mics = Volatile.Read(ref _mics) ?? [];
        var found = covered.Where(mics.ContainsKey).Select(id => mics[id]).ToList();
        lock (_pending)
            return (found, [.. covered.Where(id => !mics.ContainsKey(id)).Select(id => _micNames.GetValueOrDefault(id, id))]);
    }

    /// <summary>
    /// Mute or unmute each microphone, every one even when one is refused,
    /// and return the first refusal with the microphone's name, or null.
    /// </summary>
    private async Task<string?> SetAllAsync(IEnumerable<Microphone> mics, bool muted)
    {
        string? first = null;
        foreach (Microphone mic in mics)
        {
            string? error;
            try { error = await _client.SetMicrophoneMutedAsync(mic.Channel, mic.Device, mic.Control, muted); }
            catch (Exception ex) { error = ex.Message; }
            if (error is null) Remember(mic.Channel, muted);
            else first ??= Localizer.Format("MicKeyRefused", mic.Name, error);
        }
        return first;
    }

    // The state that reports the change can come after the next press asks.
    private void Remember(string channel, bool muted)
    {
        Dictionary<string, Microphone>? mics = Volatile.Read(ref _mics);
        if (mics is null || !mics.TryGetValue(channel, out Microphone? mic) || mic.Muted == muted) return;
        Interlocked.CompareExchange(ref _mics, new Dictionary<string, Microphone>(mics, StringComparer.Ordinal) { [channel] = mic with { Muted = muted } }, mics);
    }

    private static string Names(IEnumerable<string> names) => string.Join(", ", names);

    /// <summary>
    /// The mute key: with any covered microphone on, it mutes them all;
    /// with every one muted, it turns them all on.
    /// </summary>
    private async Task<string?> ToggleMicrophonesAsync(IReadOnlyList<string> covered)
    {
        var (mics, missing) = Resolve(covered);
        if (mics.Count == 0) return Localizer.Text("MicKeyNoMicrophones");
        bool mute = mics.Any(m => !m.Muted);
        if (await SetAllAsync(mics, mute) is { } error) return error;
        string names = Names(mics.Select(m => m.Name));
        string done = mute ? Localizer.Format("MicKeyMuted", names) : Localizer.Format("MicKeyUnmuted", names);
        return missing.Count == 0 ? done : done + " " + Localizer.Format("MicKeyNotReported", Names(missing));
    }

    /// <summary>
    /// Push to talk going down. It needs the key's release, so a desktop that
    /// has not reported one leaves the microphones muted and says why; a
    /// repeat while the key is down does nothing.
    /// </summary>
    private void PressTalk(ShortcutSession session, KeyAction action)
    {
        if (!DeliversRelease)
        {
            SetStatus(Localizer.Text("PushToTalkNoRelease"));
            return;
        }
        lock (_pending)
        {
            // Under the lock ReleaseHeld takes, so a session that ends after
            // this sees the hold and mutes.
            if (!IsActive(session) || _down.ContainsKey(TalkKey)) return;
            _down[TalkKey] = Environment.TickCount64;
            _talking = true;
            _toClose.Clear();
        }
        IReadOnlyList<string> covered = Volatile.Read(ref _settings).Microphones;
        Func<bool> isActive = () => IsActive(session);
        _ = InvokeAsync(action with { Invoke = () => TalkAsync(covered, isActive) }, isActive);
    }

    private async Task<string?> TalkAsync(IReadOnlyList<string> covered, Func<bool> isActive)
    {
        var (mics, missing) = Resolve(covered);
        if (mics.Count == 0) return Localizer.Text("MicKeyNoMicrophones");
        string? error = await SetAllAsync(mics, muted: false);
        bool ended;
        lock (_pending)
        {
            // The key came up, or the session or the connection ended, while
            // the microphones were opening; whatever muted them first may have
            // gone out before this unmute did.
            ended = !_talking || !isActive();
            if (ended) _toClose.UnionWith(covered);
        }
        if (ended)
        {
            _ = FlushCloseAsync();
            return null;
        }
        string open = Localizer.Format("PushToTalkOpen", Names(mics.Select(m => m.Name)));
        return error ?? (missing.Count == 0 ? open : open + " " + Localizer.Format("MicKeyNotReported", Names(missing)));
    }

    /// <summary>Push to talk has these microphones to mute: now for those the daemon reports, the others when it does.</summary>
    private void Close(IEnumerable<string> covered)
    {
        lock (_pending) _toClose.UnionWith(covered);
        _ = FlushCloseAsync();
    }

    /// <summary>
    /// Send the mutes push to talk owes for every microphone the daemon
    /// reports now, unless the key holds them open. A refusal is shown and
    /// not retried; a mute lost with the connection waits for the next one.
    /// Returns the microphones it sent mutes for and the first refusal.
    /// </summary>
    private Task<(List<Microphone> Muted, string? Error)> FlushCloseAsync()
    {
        Dictionary<string, Microphone>? mics = Volatile.Read(ref _mics);
        if (mics is null) return Task.FromResult<(List<Microphone>, string?)>(([], null));
        Task<(List<Microphone>, string?)> sending;
        lock (_pending)
        {
            if (_talking || _toClose.Count == 0) return Task.FromResult<(List<Microphone>, string?)>(([], null));
            List<Microphone> due = [.. _toClose.Where(mics.ContainsKey).Select(id => mics[id])];
            if (due.Count == 0) return Task.FromResult<(List<Microphone>, string?)>(([], null));
            foreach (Microphone mic in due) _toClose.Remove(mic.Channel);
            sending = CloseAsync(due);
            _closing = Task.WhenAll(_closing, sending);
        }
        return sending;
    }

    private async Task<(List<Microphone>, string?)> CloseAsync(List<Microphone> due)
    {
        string? error = await SetAllAsync(due, muted: true);
        if (error is null) return (due, null);
        if (Volatile.Read(ref _mics) is null)
        {
            // The connection went while the mutes were on their way.
            lock (_pending) _toClose.UnionWith(due.Select(m => m.Channel));
            return (due, error);
        }
        SetStatus(Localizer.Format("PushToTalkCloseFailed", error));
        return (due, error);
    }

    /// <summary>Push to talk coming up: mute, and say so.</summary>
    private async Task<string?> CloseAndReportAsync()
    {
        var (muted, error) = await FlushCloseAsync();
        return muted.Count == 0 ? null : error is null ? Localizer.Format("PushToTalkClosed", Names(muted.Select(m => m.Name)))
            : Localizer.Format("PushToTalkCloseFailed", error);
    }

    /// <summary>
    /// The microphones the Desktop keys window lists: every one the mixer
    /// shows, ticked when the keys cover it, and every covered one it does
    /// not show now, still ticked, so an Apply meanwhile keeps it.
    /// </summary>
    internal List<ClipChoice> MicrophoneChoices(DesktopKeySettings saved, IEnumerable<(string Id, string Name)> shown)
    {
        var choices = shown.Select(m => new ClipChoice(m.Id, m.Name, saved.Microphones.Contains(m.Id))).ToList();
        lock (_pending)
            foreach (string id in saved.Microphones.Where(id => choices.All(c => c.Id != id)))
                choices.Add(new(id, Localizer.Format("MicKeyUnavailable", _micNames.GetValueOrDefault(id, id)), true));
        return choices;
    }

    private async Task ObserveConnectionAsync(DBusConnection connection)
    {
        await connection.DisconnectedAsync().ConfigureAwait(false);
        if (ReferenceEquals(_connection, connection) && !_lifetime.IsCancellationRequested)
            SetStatus(Localizer.Text("DesktopConnectionLost"));
    }

    // Presses wait at most this many deep. Releases may go past it, so a
    // held clip hears its key come up, up to one for every clip key.
    private const int MaxPending = 16, MaxReleases = 64;

    /// <param name="release">A key coming up, queued past the bound for presses.</param>
    private async Task InvokeAsync(KeyAction action, Func<bool> isActive, bool release = false)
    {
        bool full;
        lock (_pending)
        {
            if (!isActive()) return;
            full = _pending.Count >= (release ? MaxPending + MaxReleases : MaxPending);
            if (!full)
            {
                _pending.Enqueue((action, isActive));
                if (_invoking != 0) return;
                Volatile.Write(ref _invoking, 1);
            }
        }
        if (full)
        {
            if (isActive()) SetStatus(Localizer.Text("DesktopKeyQueueFull"));
            return;
        }
        // One consumer preserves output-switch/volume order without losing
        // ordinary key repeats or accumulating unbounded daemon requests.
        while (true)
        {
            lock (_pending)
            {
                if (_pending.Count == 0) { Volatile.Write(ref _invoking, 0); return; }
                (action, isActive) = _pending.Dequeue();
            }
            if (!isActive() || action.Invoke is not { } invoke) continue;
            try
            {
                string? error = await invoke();
                if (isActive() && (error is not null || !action.Quiet))
                    SetStatus(error ?? Localizer.Format("DesktopKeyCompleted", action.Description));
            }
            catch (Exception ex) { if (isActive()) SetStatus(ex.Message); }
        }
    }

    private void SetStatus(string text) { Status = text; Changed?.Invoke(); }
    private void Stop()
    {
        DBusConnection? connection = _connection;
        _connection = null; // invalidate queued callbacks before disposing their observers
        ShortcutSession? session = Interlocked.Exchange(ref _current, null);
        lock (_pending) _pending.Clear();
        ReleaseHeld();
        session?.Dispose();
        // A portal session is owned by this connection and closes with it.
        connection?.Dispose();
    }
    public void Dispose()
    {
        _lifetime.Cancel();
        _client.StateReceived -= OnState;
        _client.ConnectionChanged -= OnConnection;
        Stop();
    }

    /// <summary>
    /// Stop, and give the mutes push to talk still owes the daemon a moment
    /// to go out, so a UI that quits during a hold does not leave the
    /// microphones open. The caller disposes the daemon client after this.
    /// </summary>
    internal async Task ShutdownAsync()
    {
        Dispose();
        Task closing;
        lock (_pending) closing = _closing;
        try { await closing.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* sent or not, the UI is closing */ }
    }
}
