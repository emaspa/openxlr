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
    /// <summary>Ordered rules; the first match wins.</summary>
    public sealed record Rule(string ChannelId, IReadOnlyList<string> Patterns);

    private readonly List<Rule> _rules;
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _fallbackChannel;

    public StreamMatcher(IEnumerable<Rule>? rules = null, string fallbackChannel = "system")
    {
        _rules = [.. rules ?? DefaultRules()];
        _fallbackChannel = fallbackChannel;
    }

    /// <summary>
    /// Defaults carried over from the user's Wave Link setup: browsers to
    /// Browser, Spotify and friends to Music, games and launchers to Game,
    /// chat apps to Voice Chat, everything else to System.
    /// </summary>
    public static IReadOnlyList<Rule> DefaultRules() =>
    [
        new("browser", ["chrome", "chromium", "firefox", "librewolf", "brave", "vivaldi", "edge", "epiphany"]),
        new("music", ["spotify", "youtube music", "ytmdesktop", "tidal", "rhythmbox", "elisa", "lollypop", "mpv", "vlc"]),
        new("voicechat", ["discord", "vesktop", "teamspeak", "mumble", "element", "signal", "telegram", "zoom", "slack"]),
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

    /// <summary>Replace the saved routing table while rebuilding the graph.</summary>
    public void ClearOverrides() => _overrides.Clear();

    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    /// <summary>The channel this stream should play into.</summary>
    public string Match(AudioStream stream)
    {
        if (_overrides.TryGetValue(stream.Identity, out string? pinned)) return pinned;

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
        if (IsWineLike(stream.Binary) || IsWineLike(stream.AppName)) return "game";

        return _fallbackChannel;
    }

    private static bool IsWineLike(string? s) =>
        s is not null && WineLike.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));

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
    /// through unchanged.
    /// </summary>
    public static string MigrateIdentity(string identity)
        => identity.Contains(' ') || identity.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? GameIdentity(identity)
            : identity;
}

/// <summary>One application playback stream in the graph.</summary>
public sealed record AudioStream(int Id, string? AppName, string? Binary, string? MediaName)
{
    /// <summary>PulseAudio sink-input id (PipeWire object.serial); used to move it.</summary>
    public int Serial { get; init; }

    /// <summary>
    /// Stable-ish key for remembering a per-app choice. Prefers the binary, but
    /// for Wine and Proton the binary is shared by every Windows game, so the
    /// application name (Wine sets it to the exe, e.g. "Balatro.exe") is the
    /// stable identity there. A wine stream can surface with the binary before
    /// the name and with probe media names, so without this a single game used
    /// to register under several identities. The media name stays as the last
    /// resort separator for games that never set a name.
    /// </summary>
    public string Identity
    {
        get
        {
            string bin = Binary ?? AppName ?? MediaName ?? "unknown";
            // Steam's audio comes from its embedded browser process; to a
            // person both are "Steam", so they share one identity and one
            // channel assignment.
            if (bin.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase)) return "steam";
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

    /// <summary>What to show in a picker.</summary>
    public string Label
    {
        get
        {
            if ((Binary ?? AppName ?? "").Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase))
                return "Steam";   // matches its merged identity
            bool generic = AppName is not { Length: > 0 } || AppName == "paplay" ||
                Array.Exists(GenericAppNames, g => AppName.Equals(g, StringComparison.OrdinalIgnoreCase));
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
