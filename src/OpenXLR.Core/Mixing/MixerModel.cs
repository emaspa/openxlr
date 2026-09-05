namespace OpenXLR.Core.Mixing;

/// <summary>
/// The submixer model, mirroring what Wave Link provides: application audio is
/// grouped into channels, and every channel feeds every mix at its own level.
/// One mix is what you hear (monitor), the others are published as virtual
/// capture devices other apps can select (stream/chat).
///
/// In PipeWire this becomes one combine sink per channel feeding one null sink
/// per mix. Each combine's internal stream into a mix is that cell's fader, so
/// a level or mute change never rebuilds the graph. Direct port links carry the
/// completed mixes to hardware outputs.
/// </summary>
public sealed record MixerConfig
{
    public required IReadOnlyList<MixDefinition> Mixes { get; init; }
    public required IReadOnlyList<ChannelDefinition> Channels { get; init; }

    /// <summary>
    /// The layout carried over from the user's Wave Link setup: three mixes
    /// (monitor / stream / chat), one channel per hardware input of the Wave
    /// XLR Pro (XLR 1, XLR 2, Line In, each wired to its capture channel pair),
    /// and the application channel set. The hardware inputs are muted in the
    /// monitor mix by default, exactly like Wave Link mutes the mic locally:
    /// self-monitoring belongs to the device's zero-latency crossfade, not the
    /// software loop.
    /// </summary>
    public static MixerConfig Default() => new()
    {
        Mixes =
        [
            new MixDefinition("monitor", "Monitor A", MixKind.Monitor) { Volume = 1.0 },
            // A second monitor mix for outputs that should hear a different
            // selection (issue #21: a headset with a game side and a chat
            // side). Each monitor output chooses which of the two feeds it.
            new MixDefinition("monitor2", "Monitor B", MixKind.Monitor) { Volume = 1.0 },
            new MixDefinition("stream", "Stream", MixKind.VirtualMic) { Volume = 1.0 },
            new MixDefinition("chat", "Chat", MixKind.VirtualMic) { Volume = 1.0 },
            // What the second computer on the USB Aux port receives.
            new MixDefinition("auxout", "Aux", MixKind.AuxPort) { Volume = 1.0 },
        ],
        Channels =
        [
            new ChannelDefinition("xlr1", "XLR 1") { Levels = Level(1.0, 1.0, 1.0, 1.0), MutedIn = new HashSet<string> { "monitor", "monitor2" }, InputPair = 0 },
            new ChannelDefinition("xlr2", "XLR 2") { Levels = Level(1.0, 1.0, 1.0, 1.0), MutedIn = new HashSet<string> { "monitor", "monitor2" }, InputPair = 1 },
            // The third hardware input stage is shared: the USB Aux port and the
            // Line In jack both arrive on capture pair 2 (verified live with a
            // MacBook on USB Aux; every other capture channel stayed at digital
            // zero). One channel therefore serves both.
            // Aux In must NEVER feed the Aux mix: that would loop the second
            // computer's audio straight back to it.
            new ChannelDefinition("aux", "Aux In") { Levels = Level(1.0, 1.0, 1.0, 0.0), MutedIn = new HashSet<string> { "monitor", "monitor2", "auxout" }, InputPair = 2 },
            new ChannelDefinition("game", "Game") { Levels = Level(0.5, 0.5, 0.5, 0.5) },
            new ChannelDefinition("music", "Music") { Levels = Level(1.0, 1.0, 1.0, 1.0) },
            new ChannelDefinition("browser", "Browser") { Levels = Level(1.0, 1.0, 1.0, 1.0) },
            new ChannelDefinition("system", "System") { Levels = Level(0.6, 0.6, 0.6, 0.6) },
            new ChannelDefinition("voicechat", "Voice Chat") { Levels = Level(1.0, 1.0, 1.0, 1.0) },
            new ChannelDefinition("sfx", "SFX") { Levels = Level(1.0, 1.0, 1.0, 1.0) },
        ],
    };

    // Monitor B starts as a copy of Monitor A, so an output moved to it hears
    // the same until its sends are edited.
    private static Dictionary<string, double> Level(double monitor, double stream, double chat, double auxout)
        => new() { ["monitor"] = monitor, ["monitor2"] = monitor, ["stream"] = stream, ["chat"] = chat, ["auxout"] = auxout };
}

public enum MixKind
{
    /// <summary>What the user hears; routed to a physical output.</summary>
    Monitor,
    /// <summary>Published as a virtual capture device for OBS/Discord.</summary>
    VirtualMic,
    /// <summary>Routed to the audio interface's aux port (the second computer).</summary>
    AuxPort,
}

public sealed record MixDefinition(string Id, string Name, MixKind Kind)
{
    public double Volume { get; init; } = 1.0;
    public bool Muted { get; init; }

    /// <summary>PipeWire node name of this mix's sink.</summary>
    public string SinkName => $"OpenXLR_mix_{Id}";
    /// <summary>PipeWire node name of the published virtual capture device.</summary>
    public string VirtualMicName => $"OpenXLR_{Id}";
    /// <summary>
    /// Virtual-mic mixes: the sink the capture device actually reads, fed
    /// from the mix directly or through its insert chain, so inserts can
    /// come and go without recreating the device an app records from.
    /// </summary>
    public string PostSinkName => $"OpenXLR_post_{Id}";
}

public sealed record ChannelDefinition(string Id, string Name)
{
    /// <summary>Per-mix send level, keyed by mix id (0.0 = not in that mix).</summary>
    public IReadOnlyDictionary<string, double> Levels { get; init; } = new Dictionary<string, double>();
    /// <summary>Per-mix mute, keyed by mix id.</summary>
    public IReadOnlySet<string> MutedIn { get; init; } = new HashSet<string>();

    /// <summary>
    /// Capture channel pair of the hardware interface feeding this channel
    /// (0 = first stereo pair), or null for an application channel. On the Wave
    /// XLR Pro's 6-channel source: pair 0 = XLR 1, pair 1 = XLR 2, pair 2 =
    /// Line In.
    /// </summary>
    public int? InputPair { get; init; }

    /// <summary>PipeWire node name of the sink applications play into.</summary>
    public string SinkName => $"OpenXLR_ch_{Id}";
}

/// <summary>Live mixer state pushed to clients.</summary>
public sealed record MixerState
{
    public required IReadOnlyList<MixStatus> Mixes { get; init; }
    public required IReadOnlyList<ChannelStatus> Channels { get; init; }

    /// <summary>First selected monitor output, or null (legacy single view).</summary>
    public string? MonitorOutput { get; init; }

    /// <summary>node.names of every sink the monitor mixes feed.</summary>
    public IReadOnlyList<string> MonitorOutputs { get; init; } = [];

    /// <summary>
    /// Which monitor mix feeds an output, by output name; an output absent
    /// here is fed by the first monitor mix.
    /// </summary>
    public IReadOnlyDictionary<string, string> MonitorFeeds { get; init; } = new Dictionary<string, string>();

    /// <summary>Volume of the selected output device (0..1), or null.</summary>
    public double? OutputVolume { get; init; }

    /// <summary>Whether the Aux mix is sent to the device's USB Aux port.</summary>
    public bool AuxPortEnabled { get; init; }

    /// <summary>Software low cut on the first XLR channel (0, 80, or 120 Hz).</summary>
    public int LowCutHz { get; init; }

    /// <summary>Software ClipGuard (post-ADC hard limiter) on the first XLR channel.</summary>
    public bool SoftClipGuard { get; init; }

    /// <summary>Whether the optional LADSPA limiter is installed and discoverable.</summary>
    public bool SoftClipGuardAvailable { get; init; }

    /// <summary>Actionable dependency/load error when software ClipGuard is unavailable.</summary>
    public string? SoftClipGuardError { get; init; }

    /// <summary>Plugin insert chains by channel id, with live load status.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<InsertStatus>> Inserts { get; init; }
        = new Dictionary<string, IReadOnlyList<InsertStatus>>();

    /// <summary>Enforced system default devices; null = not enforced.</summary>
    public string? EnforcedDefaultSink { get; init; }
    public string? EnforcedDefaultSource { get; init; }

    /// <summary>Application streams currently placed in channels.</summary>
    public IReadOnlyList<StreamAssignment> Streams { get; init; } = [];
}

/// <param name="Kind">"monitor", "virtualMic" or "auxPort", so clients can tell monitor mixes apart.</param>
public sealed record MixStatus(string Id, string Name, double Volume, bool Muted, string Kind = "monitor");

public sealed record ChannelStatus(string Id, string Name,
    IReadOnlyDictionary<string, double> Levels,
    IReadOnlyList<string> MutedIn);


/// <summary>
/// An application the mixer knows about and the channel it plays into. Active
/// means a live stream exists right now; inactive apps are remembered (and
/// persisted) so their routing can be changed while they are silent.
/// </summary>
public sealed record StreamAssignment(int Id, int Serial, string Label, string Identity, string ChannelId)
{
    /// <summary>A live audio stream exists right now.</summary>
    public bool Active { get; init; } = true;

    /// <summary>The app is running and registered with PipeWire (audio-capable).</summary>
    public bool Running { get; init; } = true;
}
