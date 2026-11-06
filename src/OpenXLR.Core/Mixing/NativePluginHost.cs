using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// Owns an isolated DSP/UI process. The small line protocol carries control
/// values only; audio never crosses managed code or these pipes. Reader tasks
/// coalesce native UI edits, which the mixer consumes under its own lock.
/// </summary>
internal sealed class NativePluginHost : IDisposable
{
    private readonly ConcurrentDictionary<string, double> _changes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _meters = new(StringComparer.Ordinal);
    // The meter symbols the catalogue declares for this plugin, or null to
    // take any. Controls are checked against the catalogue downstream, when
    // the mixer drains them, and the drain also empties the table; meters
    // are never drained, so a check there would come too late. A library
    // that writes to the shared stdout while it loads can otherwise fill
    // every slot with names of its own, after which each real meter the
    // host reports is refused for the life of the process, and the junk is
    // copied into every state snapshot sent to every client.
    private readonly IReadOnlySet<string>? _meterSymbols;
    // The reader assembles changed take meters privately. Publish them together
    // when the helper's trailing take counter arrives, keeping the previous
    // waveform and length intact while the next batch is still in the pipe.
    private readonly Dictionary<string, double>? _pendingTake;
    private volatile Dictionary<string, double>? _completedTake;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<string?>? _uiReply;
    private readonly Task _outputReader;
    private readonly Task _errorReader;
    private volatile string _error = "";
    private readonly Action<string>? _note;
    private readonly string _node;
    private int _disposed;
    private long _lastHeartbeat = Stopwatch.GetTimestamp();
    private long _lastUiHeartbeat = Stopwatch.GetTimestamp();
    private readonly int _sampleRate;
    private long _latencySamples = -1;
    private long _measuredSamples = -1;

    /// <summary>
    /// The plugin's algorithmic latency as the helper last reported it, or
    /// null before the first report and while the plugin has none to give.
    /// The helper reports once audio runs and again whenever the figure
    /// changes.
    /// </summary>
    public double? LatencyMilliseconds => Interlocked.Read(ref _latencySamples) is >= 0 and var samples
        ? samples * 1000.0 / _sampleRate : null;

    /// <summary>
    /// The meter a plugin's helper reports the delay it measured on its own
    /// node with, in samples. The dot keeps it apart from every plugin
    /// control: an LV2 symbol cannot hold one, and CLAP and VST3 controls
    /// are named by number.
    /// </summary>
    internal const string MeasuredLatencyMeter = "host.measured_latency";

    /// <summary>
    /// The delay the helper measured between the audio entering its node and
    /// leaving it, or null until it has a confident figure. It needs sound
    /// passing through, and a plugin that changes the sound beyond
    /// recognition may never give one. It is a separate figure from
    /// <see cref="LatencyMilliseconds"/>, which is what the plugin declares.
    /// </summary>
    public double? MeasuredLatencyMilliseconds => Interlocked.Read(ref _measuredSamples) is >= 0 and var samples
        ? samples * 1000.0 / _sampleRate : null;

    public Process Process { get; }

    /// <summary>The sample rate the helper was started at, the graph's at the time.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>
    /// Whether this helper loads a Windows plugin through yabridge, so Wine
    /// is running behind it. What Wine leaves in the daemon's control group
    /// outlives the helper, so the mixer has to know which helpers put it
    /// there. See <see cref="WineSession"/>.
    /// </summary>
    public bool Bridged { get; }

    public bool IsRunning
    {
        get
        {
            try { return !Process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }
    /// <summary>
    /// The instance is worth keeping. The helper's beat says its audio is
    /// moving, not merely that its process is alive: it stops beating when
    /// the plugin has been inside one process callback for several seconds,
    /// because an instance that is neither dead nor processing would
    /// otherwise sit in the chain passing nothing. A node with no callback
    /// outstanding, idle or suspended, keeps beating. The chain healing in
    /// the sweep reads this and replaces the process, with the restart
    /// policy's backoff behind it.
    /// </summary>
    public bool IsHealthy => IsRunning
        && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastHeartbeat)) < _patience;

    /// <summary>
    /// The editor loop has stopped answering while the DSP keeps running: a
    /// plugin's own interface can block its thread, which freezes control
    /// changes for that insert. Audio is unaffected, so this is reported
    /// rather than treated as a death.
    /// </summary>
    public bool EditorStalled => IsRunning
        && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastUiHeartbeat)) > _patience;

    /// <summary>How long either beat may go quiet before it means something.</summary>
    private readonly TimeSpan _patience = TimeSpan.FromSeconds(10);
    public IReadOnlyDictionary<string, double> Meters
    {
        get
        {
            var meters = new Dictionary<string, double>(_meters);
            if (_completedTake is { } take)
            {
                meters.EnsureCapacity(meters.Count + take.Count);
                foreach ((string symbol, double value) in take) meters[symbol] = value;
            }
            return meters;
        }
    }
    public static string Executable => Path.Combine(AppContext.BaseDirectory, "openxlr-lv2-host");

    /// <summary>
    /// The desktop's display, for a daemon that has none of its own. A user
    /// service that started before the session, or one left running across a
    /// logout, has no DISPLAY, and a plugin's editor cannot open without one.
    /// The session hands its display to systemd's user manager when it
    /// starts, so that is where it is asked for.
    /// </summary>
    private static IReadOnlyDictionary<string, string> SessionDisplay()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        string? environment = null;
        try
        {
            ProcessResult result = ProcessRunner.Run("systemctl", ["--user", "show-environment"], TimeSpan.FromSeconds(3));
            if (result.ExitCode == 0) environment = System.Text.Encoding.UTF8.GetString(result.Stdout);
        }
        catch (Exception) { /* no systemd, or none of it running: nothing to learn */ }
        return DisplayIn(environment);
    }

    /// <summary>
    /// DISPLAY and the cookie file beside it, out of what a manager reports.
    /// A cookie without a display is no use, so neither is passed on.
    /// </summary>
    internal static Dictionary<string, string> DisplayIn(string? environment)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in (environment ?? "").Split('\n'))
        {
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string name = line[..equals].Trim();
            if (name is not ("DISPLAY" or "XAUTHORITY")) continue;
            string value = line[(equals + 1)..].Trim();
            if (value.Length > 1 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            if (value.Length > 0) found[name] = value;
        }
        if (!found.ContainsKey("DISPLAY")) found.Clear();
        return found;
    }

    /// <summary>Whether the optional helper was built and installed beside the daemon.</summary>
    public static bool HostInstalled => File.Exists(Executable);
    /// <summary>
    /// What the helper implements for a plugin's DSP. The list is the same one
    /// native/lv2-host.c checks, and the two have to agree: this side decides
    /// whether an editor is offered, that side refuses to load without it.
    /// </summary>
    internal static bool SupportsFeatures(IEnumerable<string> required)
        => required.All(feature => feature is
            "http://lv2plug.in/ns/ext/urid#map"
            or "http://lv2plug.in/ns/ext/urid#unmap"
            or "http://lv2plug.in/ns/ext/worker#schedule"
            or "http://lv2plug.in/ns/ext/options#options"
            or "http://lv2plug.in/ns/ext/buf-size#boundedBlockLength");
    internal static bool SupportsUiFeatures(IEnumerable<string> required)
        => required.All(feature => feature is
            "http://lv2plug.in/ns/ext/urid#map"
            or "http://lv2plug.in/ns/ext/urid#unmap"
            or "http://lv2plug.in/ns/ext/instance-access"
            or "http://lv2plug.in/ns/extensions/ui#parent"
            or "http://lv2plug.in/ns/extensions/ui#resize"
            or "http://lv2plug.in/ns/extensions/ui#idleInterface");

    public NativePluginHost(InsertDefinition insert, string node, int channels, int sampleRate, string? bundle = null,
        Action<string>? note = null)
        : this(insert, node, channels, sampleRate, Executable, [], bundle: bundle,
            meterSymbols: PluginCatalog.Find(insert)?.Params.Select(p => p.Symbol).ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal), note: note) { }

    internal NativePluginHost(InsertDefinition insert, string node, int channels, int sampleRate,
        string executable, IReadOnlyList<string> prefixArguments, TimeSpan? startupTimeout = null,
        TimeSpan? patience = null, string? bundle = null, IReadOnlySet<string>? meterSymbols = null,
        Action<string>? note = null)
    {
        if (patience is { } chosen) _patience = chosen;
        _note = note;
        _node = node;
        _meterSymbols = meterSymbols;
        if (insert.Kind == "soundcheck") _pendingTake = new(StringComparer.Ordinal);
        _sampleRate = sampleRate;
        Bridged = WineSession.Bridged(bundle);
        if (!File.Exists(executable))
            throw new InvalidOperationException(
                "The native plugin host is not installed. Build it with -p:EnableNativeLv2Host=true, or switch this insert back to the filter chain.");
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in prefixArguments) start.ArgumentList.Add(argument);
        // The helper takes the format first, then what that format needs to
        // find the plugin: an LV2 URI, or a CLAP bundle and the id inside it.
        foreach (string argument in Arguments(insert, bundle))
            start.ArgumentList.Add(argument);
        foreach (string argument in new[] { node, channels.ToString(CultureInfo.InvariantCulture), sampleRate.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        foreach ((string symbol, double value) in insert.Params)
            start.ArgumentList.Add($"{symbol}={value.ToString("R", CultureInfo.InvariantCulture)}");
        foreach ((string name, string value) in SessionDisplay())
            start.Environment[name] = value;
        new PluginHostEnvironment(ManagedYabridge.Discover()).Apply(start);
        PluginSearchPaths.ApplyLv2(start);
        Process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native LV2 host.");
        _outputReader = ReadOutputAsync();
        _errorReader = ReadErrorsAsync();
        try { _ready.Task.WaitAsync(startupTimeout ?? TimeSpan.FromSeconds(8)).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Dispose();
            throw new InvalidOperationException($"Native LV2 startup failed: {_error}", ex);
        }
    }

    /// <summary>
    /// The helper's arguments up to the node: <c>--own-block</c> for a plugin
    /// run on its own block, then the format and what it needs to find the
    /// plugin. The built-in stages never run on their own block.
    /// </summary>
    internal static IReadOnlyList<string> Arguments(InsertDefinition insert, string? bundle)
    {
        IReadOnlyList<string> format = FormatArguments(insert, bundle);
        return insert.OwnBlock && insert.Kind is "lv2" or "clap" or "vst3" ? ["--own-block", .. format] : format;
    }

    private static IReadOnlyList<string> FormatArguments(InsertDefinition insert, string? bundle) => insert.Kind switch
    {
        "soundcheck" => ["soundcheck"],
        "level" => ["level"],
        "eq" => insert.Plugin is "eq" or "4" ? ["eq"] : ["eq-bands", insert.Plugin],
        "duck" => ["duck"],
        "player" => ["player"],
        "bridge" => ["bridge"],
        "clap" => ["clap", bundle ?? throw new InvalidOperationException("A CLAP insert needs the bundle it lives in."), insert.Plugin],
        "vst3" => ["vst3", bundle ?? throw new InvalidOperationException("A VST3 insert needs the bundle it lives in."), insert.Plugin],
        _ => ["lv2", insert.Plugin],
    };

    // Match the native host's MAX_CONTROLS and MAX_SYMBOL. Plugin libraries
    // share its stdout, so even malformed output must stay bounded here.
    private const int MaxProtocolControls = 4096;
    private const int MaxProtocolSymbol = 255;
    private const int ProtocolLineCap = 4096;

    private async Task ReadOutputAsync()
    {
        var block = new char[1024];
        var line = new System.Text.StringBuilder(ProtocolLineCap);
        bool discard = false;
        Action<string> accept = AcceptOutput;
        try
        {
            int read;
            while ((read = await Process.StandardOutput.ReadAsync(block.AsMemory(), _stop.Token).ConfigureAwait(false)) > 0)
                FoldOutputBlock(line, block.AsSpan(0, read), ref discard, accept);
            if (!discard && line.Length > 0) AcceptOutput(line.ToString().TrimEnd('\r'));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        finally
        {
            _ready.TrySetException(new InvalidOperationException("Native host exited before readiness."));
            Volatile.Read(ref _uiReply)?.TrySetResult("Native plugin host disconnected.");
        }
    }

    /// <summary>
    /// Keep a bounded partial protocol line. Drop an oversized line in full,
    /// not just its tail: a truncated prefix could itself be a valid command.
    /// Resume at the following newline so heartbeats and replies still arrive.
    /// </summary>
    internal static void FoldOutputBlock(System.Text.StringBuilder line, ReadOnlySpan<char> block,
        ref bool discard, Action<string> accept)
    {
        foreach (char c in block)
        {
            if (c == '\n')
            {
                if (!discard) accept(line.ToString().TrimEnd('\r'));
                line.Clear();
                discard = false;
            }
            else if (!discard)
            {
                if (line.Length < ProtocolLineCap) line.Append(c);
                else { line.Clear(); discard = true; }
            }
        }
    }

    private void AcceptOutput(string line)
    {
        if (line == "ready") _ready.TrySetResult();
        else if (line == "heartbeat") Interlocked.Exchange(ref _lastHeartbeat, Stopwatch.GetTimestamp());
        else if (line == "ui-heartbeat") Interlocked.Exchange(ref _lastUiHeartbeat, Stopwatch.GetTimestamp());
        else if (line.StartsWith("latency ", StringComparison.Ordinal))
            Interlocked.Exchange(ref _latencySamples, ParseLatency(line, _sampleRate));
        else if (line.StartsWith("ui ", StringComparison.Ordinal))
            Volatile.Read(ref _uiReply)?.TrySetResult(line == "ui opened" ? null : line[3..]);
        else if (line.StartsWith("clip ", StringComparison.Ordinal))
        {
            if (ParseClip(line) is { } reply) ClipReplied?.Invoke(reply);
        }
        else
        {
            string[] parts = line.Split(' ', 3);
            if (parts.Length != 3 || parts[1].Length is 0 or > MaxProtocolSymbol
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value)) return;
            if (parts[0] == "control") StoreValue(_changes, parts[1], value);
            else if (parts[0] == "meter" && parts[1] == MeasuredLatencyMeter)
                Interlocked.Exchange(ref _measuredSamples, value is >= 0 and < int.MaxValue ? (long)Math.Round(value) : -1);
            else if (parts[0] == "meter" && (_meterSymbols is null || _meterSymbols.Contains(parts[1])))
                StoreMeter(parts[1], value);
        }
    }

    /// <summary>
    /// The samples in a <c>latency SAMPLES RATE</c> line, or -1 for a figure
    /// the helper does not have (UINT32_MAX), a malformed line, or one
    /// measured at a rate other than the one this helper was started at.
    /// </summary>
    internal static long ParseLatency(string line, int sampleRate)
    {
        string[] parts = line.Split(' ');
        return parts.Length == 3 && parts[0] == "latency" && sampleRate > 0
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int rate) && rate == sampleRate
            && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint samples) && samples < uint.MaxValue
            ? samples : -1;
    }

    /// <summary>
    /// A soundboard player's answer to <c>load</c> or <c>unload</c>, raised on
    /// the reader's thread. Handlers must not block.
    /// </summary>
    public event Action<ClipReply>? ClipReplied;

    /// <summary>
    /// A <c>clip SLOT TOKEN ok FRAMES CHANNELS</c>, <c>clip SLOT TOKEN error
    /// MESSAGE</c> or <c>clip SLOT unloaded</c> line, or null for anything
    /// else. TOKEN is the one the load was sent with. The message is the
    /// helper's own sentence, kept to 200 characters.
    /// </summary>
    internal static ClipReply? ParseClip(string line)
    {
        string[] parts = line.Split(' ', 5);
        if (parts.Length < 3 || parts[0] != "clip"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int slot) || slot is < 0 or >= SoundboardLibrary.MaxClips)
            return null;
        if (parts[2] == "unloaded") return parts.Length == 3 ? new ClipReply(slot, ClipOutcome.Unloaded, 0, 0, null) : null;
        if (parts.Length < 5 || !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint token)) return null;
        switch (parts[3])
        {
            case "ok":
                string[] numbers = parts[4].Split(' ');
                return numbers.Length == 2
                    && long.TryParse(numbers[0], NumberStyles.None, CultureInfo.InvariantCulture, out long frames)
                    && int.TryParse(numbers[1], NumberStyles.None, CultureInfo.InvariantCulture, out int channels) && channels is 1 or 2
                    ? new ClipReply(slot, ClipOutcome.Loaded, frames, channels, null) { Token = token } : null;
            case "error" when parts[4].Trim() is { Length: > 0 } message:
                return new ClipReply(slot, ClipOutcome.Failed, 0, 0,
                    new string([.. (message.Length > 200 ? message[..200] : message).Where(c => !char.IsControl(c))])) { Token = token };
            default:
                return null;
        }
    }

    /// <summary>
    /// Forget meters the helper reported, so a slot given to another clip
    /// never shows the last one's values before the helper reports again.
    /// </summary>
    internal void ForgetMeters(params string[] symbols)
    {
        foreach (string symbol in symbols) _meters.TryRemove(symbol, out _);
    }

    /// <summary>One meter as the helper last reported it, or null before it did.</summary>
    internal double? Meter(string symbol) => _meters.TryGetValue(symbol, out double value) ? value : null;

    private static void StoreValue(ConcurrentDictionary<string, double> values, string symbol, double value)
    {
        // Only the output reader adds keys; the mixer can only remove them.
        // Existing controls must still update after the table reaches its cap.
        if (values.ContainsKey(symbol) || values.Count < MaxProtocolControls) values[symbol] = value;
    }

    private void StoreMeter(string symbol, double value)
    {
        if (_pendingTake is { } pending && (symbol is "take" or "takeFrames"
            || symbol[0] == 'p' && PipeWireAdapter.SoundCheckMeters.Contains(symbol)))
        {
            // Unchanged peaks are omitted by the native meter writer, so keep
            // their previous values. Only the fixed take symbols enter here.
            pending[symbol] = value;
            if (symbol == "take") _completedTake = new(pending, StringComparer.Ordinal);
        }
        else StoreValue(_meters, symbol, value);
    }

    /// <summary>How much of a line the helper writes to stderr is kept, and all that is ever held.</summary>
    private const int ErrorLineCap = 2048;

    /// <summary>
    /// Fold one block of helper output into the line being assembled and
    /// return the last nonblank complete line in it, or null when there was
    /// none. <paramref name="line"/> never grows past
    /// <see cref="ErrorLineCap"/> characters, whatever the block contains:
    /// everything past that is dropped where it arrives, so a plugin writing
    /// without newlines cannot make the daemon hold its output.
    /// <paramref name="seen"/> is handed every complete line, not only the last.
    /// </summary>
    internal static string? FoldErrorBlock(System.Text.StringBuilder line, ReadOnlySpan<char> block,
        Action<string>? seen = null)
    {
        string? complete = null;
        foreach (char c in block)
        {
            if (c != '\n')
            {
                if (line.Length < ErrorLineCap) line.Append(c);
                continue;
            }
            string finished = line.ToString().TrimEnd('\r');
            line.Clear();
            if (!string.IsNullOrWhiteSpace(finished) && !IsNote(finished)) complete = finished;
            seen?.Invoke(finished);
        }
        return complete;
    }

    /// <summary>
    /// Drain the helper's stderr without letting a plugin decide how much
    /// memory the daemon uses. ReadLineAsync buffers a whole line before it
    /// hands one over, so a plugin that logs through the helper and never
    /// writes a newline would grow that buffer without a limit, while its
    /// heartbeats keep the process looking healthy. Reading in blocks keeps
    /// the drain going, so the helper never blocks on a full pipe, and holds
    /// only the bounded head of the line in hand.
    /// </summary>
    private async Task ReadErrorsAsync()
    {
        var block = new char[1024];
        var line = new System.Text.StringBuilder(ErrorLineCap);
        Action<string>? seen = _note is null ? null : ForwardTrace;
        try
        {
            int read;
            while ((read = await Process.StandardError.ReadAsync(block.AsMemory(), _stop.Token).ConfigureAwait(false)) > 0)
                if (FoldErrorBlock(line, block.AsSpan(0, read), seen) is string complete) _error = complete;
            // A helper that dies mid-sentence still gets to explain itself.
            string tail = line.ToString();
            if (!string.IsNullOrWhiteSpace(tail) && !IsNote(tail)) _error = tail;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
    }

    /// <summary>
    /// A line the helper writes about a single command it set aside, such as
    /// the soundboard player's <c>note: slot 3 holds no clip</c>. It says
    /// nothing about why a helper stopped, so it never becomes its last words.
    /// </summary>
    internal static bool IsNote(string line) => line.StartsWith("note: ", StringComparison.Ordinal);

    /// <summary>How many trace lines one helper may put in the journal.</summary>
    internal const int TraceLineBudget = 64;
    private int _traceLines;

    /// <summary>
    /// Pass the helper's own trace lines to the journal. Only what the helper
    /// marks as trace is forwarded, and only so much of it: a plugin shares
    /// this stream and must not be able to fill the journal through it.
    /// </summary>
    private void ForwardTrace(string line)
    {
        if (!line.StartsWith("trace: ", StringComparison.Ordinal) || _traceLines >= TraceLineBudget) return;
        _traceLines++;
        _note?.Invoke($"Plugin host {_node}: {line}");
    }

    /// <summary>
    /// The last line the helper wrote to stderr, which is where it says why
    /// it is giving up. Once the process has gone, the reader is given a
    /// moment to reach the end of the stream; the wait is bounded because a
    /// child the helper left behind can hold the pipe open.
    /// </summary>
    public string LastMessage
    {
        get
        {
            if (!IsRunning && Volatile.Read(ref _disposed) == 0)
            {
                try { _errorReader.Wait(TimeSpan.FromMilliseconds(250)); }
                catch (AggregateException) { }
            }
            return _error;
        }
    }

    private async Task SendAsync(string command, CancellationToken cancellation)
    {
        await _writes.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await Process.StandardInput.WriteLineAsync(command.AsMemory(), cancellation).ConfigureAwait(false);
            await Process.StandardInput.FlushAsync(cancellation).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    public void SetControl(string symbol, double value)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            SendAsync($"set {symbol} {value.ToString("R", CultureInfo.InvariantCulture)}", timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            throw new InvalidOperationException("The plugin host did not take the change; audio is unaffected.", ex);
        }
    }

    /// <summary>
    /// Hand the helper one line of a backend's own, such as the soundboard
    /// player's <c>play 3 restart</c>. The line must not hold a line break.
    /// </summary>
    public void Send(string line)
    {
        if (line.Contains('\n') || line.Contains('\r'))
            throw new ArgumentException("A helper line holds no line break.", nameof(line));
        try
        {
            // A helper already disposed has a disposed token source too.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            SendAsync(line, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            throw new InvalidOperationException("The plugin host did not take the command.", ex);
        }
    }

    public void ShowUi()
    {
        if (EditorStalled)
            throw new InvalidOperationException("Plugin editor is unresponsive; audio is still running.");
        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _uiReply, reply, null) is not null)
            throw new InvalidOperationException("The plugin editor is already opening.");
        try
        {
            // One budget includes waiting for the writer, flushing and the reply.
            // This path can be called under the mixer lock.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            SendAsync("show", timeout.Token).GetAwaiter().GetResult();
            string? error = reply.Task.WaitAsync(timeout.Token).GetAwaiter().GetResult();
            if (error is not null) throw new InvalidOperationException(error);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Callers turn this into a client error message, so say what it
            // means for the audio rather than reporting a cancelled task.
            throw new InvalidOperationException("The plugin editor did not answer in time; audio is unaffected.", ex);
        }
        finally
        {
            // The protocol has no request IDs. Keep a timed-out request occupied
            // until its late reply arrives, so it cannot acknowledge a new show.
            if (reply.Task.IsCompleted) Interlocked.CompareExchange(ref _uiReply, null, reply);
            else _ = reply.Task.ContinueWith(_ => Interlocked.CompareExchange(ref _uiReply, null, reply),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public IEnumerable<KeyValuePair<string, double>> DrainChanges()
    {
        foreach (string symbol in _changes.Keys)
            if (_changes.TryRemove(symbol, out double value)) yield return new(symbol, value);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { if (!Process.HasExited) { Process.Kill(entireProcessTree: true); Process.WaitForExit(2000); } }
        catch (InvalidOperationException) { }
        // Cancellation releases pipe readers; join before disposing their handles.
        Task.WhenAll(_outputReader, _errorReader).GetAwaiter().GetResult();
        Process.Dispose();
        _stop.Dispose();
        _writes.Dispose();
    }
}

internal enum ClipOutcome { Loaded, Failed, Unloaded }

/// <summary>One <c>clip</c> line from the soundboard player: the slot, what happened, and the clip's length at the helper's rate.</summary>
internal sealed record ClipReply(int Slot, ClipOutcome Outcome, long Frames, int Channels, string? Message)
{
    /// <summary>The token the load was sent with; 0 for an unload.</summary>
    public uint Token { get; init; }
}
