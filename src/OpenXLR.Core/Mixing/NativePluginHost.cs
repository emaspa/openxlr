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
        && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastHeartbeat)) < TimeSpan.FromSeconds(10);
    public IReadOnlyDictionary<string, double> Meters => new Dictionary<string, double>(_meters);
    public static string Executable => Path.Combine(AppContext.BaseDirectory, "openxlr-lv2-host");
    internal static bool SupportsFeatures(IEnumerable<string> required)
        => required.All(feature => feature is
            "http://lv2plug.in/ns/ext/urid#map" or "http://lv2plug.in/ns/ext/urid#unmap");
    internal static bool SupportsUiFeatures(IEnumerable<string> required)
        => required.All(feature => feature is
            "http://lv2plug.in/ns/ext/urid#map"
            or "http://lv2plug.in/ns/ext/urid#unmap"
            or "http://lv2plug.in/ns/ext/instance-access"
            or "http://lv2plug.in/ns/extensions/ui#parent"
            or "http://lv2plug.in/ns/extensions/ui#resize"
            or "http://lv2plug.in/ns/extensions/ui#idleInterface");

    public NativePluginHost(InsertDefinition insert, string node, int channels, int sampleRate)
    {
        if (!File.Exists(Executable)) throw new InvalidOperationException("Native LV2 host is missing; rebuild/install the complete OpenXLR package.");
        var start = new ProcessStartInfo(Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { insert.Plugin, node, channels.ToString(CultureInfo.InvariantCulture), sampleRate.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        foreach ((string symbol, double value) in insert.Params)
            start.ArgumentList.Add($"{symbol}={value.ToString("R", CultureInfo.InvariantCulture)}");
        Process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native LV2 host.");
        _outputReader = ReadOutputAsync();
        _errorReader = ReadErrorsAsync();
        try { _ready.Task.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult(); }
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

    private async Task SendAsync(string command)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await _writes.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            await Process.StandardInput.WriteLineAsync(command.AsMemory(), timeout.Token).ConfigureAwait(false);
            await Process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    public void SetControl(string symbol, double value)
        => SendAsync($"set {symbol} {value.ToString("R", CultureInfo.InvariantCulture)}").GetAwaiter().GetResult();

    public void ShowUi()
    {
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastUiHeartbeat)) > TimeSpan.FromSeconds(10))
            throw new InvalidOperationException("Plugin editor is unresponsive; audio is still running.");
        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _uiReply, reply, null) is not null)
            throw new InvalidOperationException("The plugin editor is already opening.");
        try
        {
            SendAsync("show").GetAwaiter().GetResult();
            string? error = reply.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (error is not null) throw new InvalidOperationException(error);
        }
        finally { Interlocked.Exchange(ref _uiReply, null); }
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
