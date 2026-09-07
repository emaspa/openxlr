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
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<string?>? _uiReply;
    private readonly Task _outputReader;
    private readonly Task _errorReader;
    private string _error = "";
    private int _disposed;
    private long _lastHeartbeat = Stopwatch.GetTimestamp();
    private long _lastUiHeartbeat = Stopwatch.GetTimestamp();

    public Process Process { get; }
    public bool IsRunning
    {
        get
        {
            try { return !Process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }
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
    public IReadOnlyDictionary<string, double> Meters => new Dictionary<string, double>(_meters);
    public static string Executable => Path.Combine(AppContext.BaseDirectory, "openxlr-lv2-host");

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

    public NativePluginHost(InsertDefinition insert, string node, int channels, int sampleRate)
        : this(insert, node, channels, sampleRate, Executable, []) { }

    internal NativePluginHost(InsertDefinition insert, string node, int channels, int sampleRate,
        string executable, IReadOnlyList<string> prefixArguments, TimeSpan? startupTimeout = null,
        TimeSpan? patience = null)
    {
        if (patience is { } chosen) _patience = chosen;
        if (!File.Exists(executable))
            throw new InvalidOperationException(
                "The native LV2 host is not installed. Build it with -p:EnableNativeLv2Host=true, or switch this insert back to the filter chain.");
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in prefixArguments) start.ArgumentList.Add(argument);
        foreach (string argument in new[] { insert.Plugin, node, channels.ToString(CultureInfo.InvariantCulture), sampleRate.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        foreach ((string symbol, double value) in insert.Params)
            start.ArgumentList.Add($"{symbol}={value.ToString("R", CultureInfo.InvariantCulture)}");
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

    private async Task ReadOutputAsync()
    {
        try
        {
            while (await Process.StandardOutput.ReadLineAsync(_stop.Token).ConfigureAwait(false) is string line)
            {
                if (line == "ready") _ready.TrySetResult();
                else if (line == "heartbeat") Interlocked.Exchange(ref _lastHeartbeat, Stopwatch.GetTimestamp());
                else if (line == "ui-heartbeat") Interlocked.Exchange(ref _lastUiHeartbeat, Stopwatch.GetTimestamp());
                else if (line.StartsWith("ui ", StringComparison.Ordinal))
                    Volatile.Read(ref _uiReply)?.TrySetResult(line == "ui opened" ? null : line[3..]);
                else
                {
                    string[] parts = line.Split(' ', 3);
                    if (parts.Length == 3 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
                    {
                        if (parts[0] == "control") _changes[parts[1]] = value;
                        else if (parts[0] == "meter") _meters[parts[1]] = value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        finally
        {
            _ready.TrySetException(new InvalidOperationException("Native host exited before readiness."));
            Volatile.Read(ref _uiReply)?.TrySetResult("Native plugin host disconnected.");
        }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await Process.StandardError.ReadLineAsync(_stop.Token).ConfigureAwait(false) is string line)
                _error = line.Length > 2048 ? line[..2048] : line;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
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
