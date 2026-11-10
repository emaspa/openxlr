namespace OpenXLR.Core.Mixing;

/// <summary>
/// Decides which channel an application's audio stream belongs to, the job Wave
/// Link does with its per-app assignment list.
///
/// Matching looks at the process binary first, then the application name, then
/// the media name, because the binary is the most reliable field: an app can
/// leave application.name unset (it then inherits whatever the audio library
/// reports) while the binary always reflects the real process.
///
/// Proton and Wine are the known weak point: every Windows game routed through
/// them reports a binary like "wine64-preloader" or "wine", so binary matching
/// alone would lump them together. They are therefore treated as a hint that
/// this is a game rather than as an identity, and the media name (which often
/// carries the real title) is consulted before falling back to the Game channel.
/// </summary>
public sealed class StreamMatcher
{
    /// <summary>
    /// The pseudo-channel that means "not managed": the mixer leaves the
    /// app's streams wherever the desktop routes them (issue #21, a headset
    /// with a game side and a chat side that the user balances by hand).
    /// Stored like any other override and shown in the app list.
    /// </summary>
    public const string Ignore = "ignore";

    /// <summary>Ordered rules; the first match wins.</summary>
    public sealed record Rule(string ChannelId, IReadOnlyList<string> Patterns);

    private readonly List<Rule> _rules;
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _fallbackChannel;

    /// <summary>Apply automatic rules to apps without an explicit assignment.</summary>
    public bool RouteByDefault { get; internal set; } = true;

    public StreamMatcher(IEnumerable<Rule>? rules = null, string fallbackChannel = "system")
    {
        _rules = [.. rules ?? DefaultRules()];
        _fallbackChannel = fallbackChannel;
    }

    /// <summary>
    /// Defaults carried over from the user's Wave Link setup: browsers to
    /// Browser, Spotify and friends to Music, games and launchers to Game,
    /// chat apps, the WhatsApp desktop clients among them, to Voice Chat,
    /// everything else to System.
    /// </summary>
    public static IReadOnlyList<Rule> DefaultRules() =>
    [
        new("browser", ["chrome", "chromium", "firefox", "librewolf", "brave", "vivaldi", "edge", "epiphany"]),
        new("music", ["spotify", "youtube music", "ytmdesktop", "tidal", "rhythmbox", "elisa", "lollypop", "mpv", "vlc"]),
        new("voicechat", ["discord", "vesktop", "teamspeak", "mumble", "element", "signal", "telegram", "zoom", "slack",
            "whatsapp", "whatrust", "zapzap", "whatsie", "karere", "wasistlos"]),
        new("game", ["steam", "gamescope", "lutris", "heroic", "bottles", "minecraft", "hearthstone", "kingdomcome", "bloodlines", "expedition"]),
        new("sfx", ["soundboard", "sfx"]),
    ];

    /// <summary>Binaries that mean "a Windows game under a translation layer".</summary>
    private static readonly string[] WineLike =
        ["wine", "wine64", "wine-preloader", "wine64-preloader", "proton", "wineserver", "steam.exe"];

    /// <summary>
    /// Remember that a specific application belongs to a channel, overriding the
    /// rules. Keyed by the stream's identity so a Proton game keeps its channel
    /// even though its binary is shared with every other Proton game.
    /// </summary>
    /// <summary>Drop a remembered per-app choice (used when forgetting an app).</summary>
    public void RemoveOverride(string identity) => _overrides.Remove(identity);

    public void SetOverride(string identity, string channelId)
    {
        if (!string.IsNullOrWhiteSpace(identity)) _overrides[identity] = channelId;
    }

    public void ClearOverride(string identity) => _overrides.Remove(identity);

    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    /// <summary>The channel this stream should play into.</summary>
    public string Match(AudioStream stream)
    {
        if (_overrides.TryGetValue(stream.Identity, out string? pinned)) return pinned;

        if (!RouteByDefault) return Ignore;

        // Binary first, then app name, then the media name.
        foreach (string? field in new[] { stream.Binary, stream.AppName, stream.MediaName })
        {
            if (string.IsNullOrWhiteSpace(field)) continue;
            foreach (Rule rule in _rules)
                foreach (string pat in rule.Patterns)
                    if (field.Contains(pat, StringComparison.OrdinalIgnoreCase))
                        return rule.ChannelId;
        }

        // Wine and Proton report a shared binary, so treat them as games rather
        // than letting them fall through to System with every other unknown app.
        if (RunsUnderWine(stream)) return "game";

        return _fallbackChannel;
    }

    /// <summary>Whether the stream is a Windows program under Wine or Proton.</summary>
    internal static bool RunsUnderWine(AudioStream stream)
        => IsWineLike(stream.Binary) || IsWineLike(stream.AppName) ||
           IsWindowsExecutable(stream.Binary) || IsWindowsExecutable(stream.AppName);

    private static bool IsWineLike(string? s) =>
        s is not null && WineLike.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>The web process every WebKitGTK and WPE application plays its sound from.</summary>
    internal static bool IsWebKitProcess(string? name)
        => name is not null && (name.Equals("WebKitWebProcess", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WPEWebProcess", StringComparison.OrdinalIgnoreCase));

    internal static bool IsWindowsExecutable(string? name)
        => name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// A Windows game's stable key. The same game surfaces under several
    /// spellings of its own name ("Cyberpunk 2077", "Cyberpunk2077.exe"), so
    /// the key is the name lowercased with the .exe suffix and everything
    /// that is not a letter or digit removed.
    /// </summary>
    public static string GameIdentity(string name)
    {
        string n = name.Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        string key = new string(n.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return key.Length > 0 ? key : n.ToLowerInvariant();
    }

    /// <summary>
    /// Bring a stored identity from an older scheme onto the current one:
    /// identities carrying a space or an .exe suffix came from the app-name
    /// path and collapse to their game key; plain binary identities pass
    /// through unchanged. An identity saved from an ALSA plugin's wrapper
    /// name ("PipeWire ALSA [kded6]", a client seen without its process
    /// binary) becomes the program inside, the key that client has now.
    /// </summary>
    public static string MigrateIdentity(string identity)
    {
        if (identity.EndsWith(" (deleted)", StringComparison.Ordinal)) identity = identity[..^10];
        identity = AudioStream.ProgramName(identity);
        return identity.Contains(' ') || IsWindowsExecutable(identity) ? GameIdentity(identity) : identity;
    }

    /// <summary>Prefer an existing canonical choice over a stale alias.</summary>
    internal static IReadOnlyDictionary<string, string> MigrateOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string identity, string channel) in overrides.OrderBy(pair =>
                     string.Equals(pair.Key, MigrateIdentity(pair.Key), StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            result.TryAdd(MigrateIdentity(identity), channel);
        return result;
    }
}

/// <summary>One application playback stream in the graph.</summary>
public sealed record AudioStream(int Id, string? AppName, string? Binary, string? MediaName)
{
    /// <summary>PulseAudio sink-input id (PipeWire object.serial); used to move it.</summary>
    public int Serial { get; init; }
    /// <summary>Owning application process, when the live PipeWire client supplies it.</summary>
    public int ProcessId { get; init; }
    /// <summary>PipeWire portal instance for a process ID inside a Flatpak namespace.</summary>
    public string? FlatpakInstanceId { get; init; }
    internal string FocusProcessKey => ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + (FlatpakInstanceId is null ? "" : "@" + FlatpakInstanceId);
    /// <summary>The <c>application.icon_name</c> the application published, or null.</summary>
    public string? IconName { get; init; }
    /// <summary>The desktop application id: the Flatpak portal's, else <c>application.id</c>; or null.</summary>
    public string? AppId { get; init; }
    /// <summary>
    /// The stream's own level on the desktop's percentage scale, its loudest
    /// channel, as a volume applet shows it; null when the node reports none.
    /// </summary>
    public double? Volume { get; init; }

    /// <summary>
    /// Stable-ish key for remembering a per-app choice. Prefers the binary, but
    /// for Wine and Proton the binary is shared by every Windows game, so the
    /// application name (Wine sets it to the exe, e.g. "Balatro.exe") is the
    /// stable identity there. A wine stream can surface with the binary before
    /// the name and with probe media names, so without this a single game used
    /// to register under several identities. The media name stays as the last
    /// resort separator for games that never set a name.
    /// <para>
    /// A name with a space in it, as a program without a process binary or a
    /// WebKitGTK application can have, is collapsed the way
    /// <see cref="StreamMatcher.MigrateIdentity"/> collapses a stored one, so
    /// a live stream and its saved assignment always meet on one key.
    /// Plumbing keeps its spelling; it is recognised by it and never stored.
    /// </para>
    /// </summary>
    public string Identity
    {
        get
        {
            string published = PublishedIdentity;
            return PipeWireAdapter.IsPlumbingIdentity(published) ? published : StreamMatcher.MigrateIdentity(published);
        }
    }

    private string PublishedIdentity
    {
        get
        {
            string bin = Binary ?? AppName ?? MediaName ?? "unknown";
            // Steam's audio comes from its embedded browser process; to a
            // person both are "Steam", so they share one identity and one
            // channel assignment.
            if (bin.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase)) return "steam";
            // Every WebKitGTK and WPE application plays from the same web
            // process binary, so it names no one application; the application
            // name the process carries does.
            if (StreamMatcher.IsWebKitProcess(bin) && AppName is { Length: > 0 } app
                && !StreamMatcher.IsWebKitProcess(app)) return app;
            // The executable can be reported directly, or as the app name
            // while the playback node omits its runtime binary entirely.
            if (StreamMatcher.IsWindowsExecutable(bin)) return StreamMatcher.GameIdentity(bin);
            bool shared = bin.Contains("wine", StringComparison.OrdinalIgnoreCase) ||
                          bin.Contains("proton", StringComparison.OrdinalIgnoreCase);
            if (!shared) return bin;
            if (AppName is { Length: > 0 } &&
                !AppName.Equals("Wine", StringComparison.OrdinalIgnoreCase) &&
                !AppName.Contains("wine", StringComparison.OrdinalIgnoreCase))
                return StreamMatcher.GameIdentity(AppName);
            return !string.IsNullOrWhiteSpace(MediaName) ? $"{bin}|{MediaName}" : bin;
        }
    }


    /// <summary>
    /// Application names that identify a runtime, not the actual app: Electron
    /// apps (Discord and friends) all report "Chromium", so the process binary
    /// is the truthful name for them.
    /// </summary>
    private static readonly string[] GenericAppNames =
        ["Chromium", "Chromium input", "Electron", "WEBRTC VoiceEngine", "Wine",
         "ALSA plug-in", "ringrtc", "libcanberra"];

    /// <summary>
    /// Programs whose own name says nothing to a user, by their process
    /// binary: Steam's web helper reads as Steam, which also matches its
    /// merged identity, and KDE's service daemon, which plays the desktop's
    /// notification sounds through pipewire-alsa, as KDE.
    /// </summary>
    private static readonly Dictionary<string, string> KnownLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["steamwebhelper"] = "Steam",
        ["kded5"] = "KDE",
        ["kded6"] = "KDE",
    };

    /// <summary>The label a program with a name of no use to a user goes by, or null for any other.</summary>
    public static string? KnownLabel(string? binary) =>
        binary is not null && KnownLabels.TryGetValue(binary, out string? label) ? label : null;

    /// <summary>
    /// The names ALSA plugins wrap around a program's own: pipewire-alsa
    /// reports a program that plays or records through ALSA as
    /// "PipeWire ALSA [kded6]", the PulseAudio ALSA plugin as
    /// "ALSA plug-in [kded6]".
    /// </summary>
    private static readonly string[] AlsaWrappers = ["PipeWire ALSA", "ALSA plug-in"];

    /// <summary>
    /// The program an ALSA plugin's <c>application.name</c> stands for:
    /// "PipeWire ALSA [kded6]" reads "kded6", as if the program had named
    /// itself. Any other name, and a wrapper with nothing between its
    /// brackets or with text after the closing one, comes back unchanged,
    /// so the result is empty only when the name was.
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(name))]
    public static string? ProgramName(string? name)
    {
        if (name is null) return null;
        string text = name.Trim();
        foreach (string wrapper in AlsaWrappers)
        {
            if (!text.StartsWith(wrapper, StringComparison.Ordinal)) continue;
            string rest = text[wrapper.Length..].TrimStart();
            if (rest.Length < 2 || rest[0] != '[' || rest[^1] != ']') return name;
            string program = rest[1..^1].Trim();
            return program.Length > 0 ? program : name;
        }
        return name;
    }

    /// <summary>
    /// True for an <c>application.name</c> that is an identifier rather than
    /// a name: one word joined by underscores, as Discord's
    /// <c>discord_hardware_mute</c>, the client it opens to watch a headset's
    /// mute switch. An application names one of its streams or helper
    /// clients that way, not itself, so the process binary names it better.
    /// A Windows executable keeps its name: under Wine the binary is the
    /// runtime's, shared by every game.
    /// </summary>
    public static bool IsTechnicalName(string? name) =>
        name is { Length: > 0 } && name.Contains('_') && !name.Any(char.IsWhiteSpace)
        && !StreamMatcher.IsWindowsExecutable(name);

    /// <summary>
    /// What to show for the application: its <c>application.name</c> unless
    /// that names a runtime (<see cref="GenericAppNames"/>) or is an
    /// identifier (<see cref="IsTechnicalName"/>); then its process binary,
    /// capitalised, when it has one of its own; else whatever it has. Every
    /// stream and client of one identity resolves the same way, so an
    /// application's helper streams do not rename it.
    /// </summary>
    public string Label
    {
        get
        {
            if (KnownLabel(Binary ?? AppName) is { } known) return known;
            bool ownBinary = Binary is { Length: > 1 } && Binary != "paplay" && !StreamMatcher.IsWindowsExecutable(Binary)
                && !Binary.Contains("wine", StringComparison.OrdinalIgnoreCase)
                && !Binary.Contains("proton", StringComparison.OrdinalIgnoreCase);
            bool generic = AppName is not { Length: > 0 } || AppName == "paplay" ||
                Array.Exists(GenericAppNames, g => AppName.Equals(g, StringComparison.OrdinalIgnoreCase)) ||
                IsTechnicalName(AppName) && ownBinary;
            string name;
            if (!generic) name = AppName!;
            else if (Binary is { Length: > 1 } && Binary != "paplay")
                name = char.ToUpperInvariant(Binary[0]) + Binary[1..];
            else name = AppName is { Length: > 0 } ? AppName : MediaName ?? $"stream {Id}";
            // Wine exposes the exe file name; "Balatro" reads better than
            // "Balatro.exe". The identity keeps the full name.
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }
    }
}
