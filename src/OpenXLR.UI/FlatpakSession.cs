using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>A sandbox owns its daemon without talking to the host's systemd.</summary>
internal sealed class FlatpakSession : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _generation;
    private readonly Task _run;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    internal static FlatpakSession? Current { get; private set; }

    /// <summary>
    /// Another daemon held the lock when this one started: the user service
    /// from docs/flatpak.md serves the UI, and Quit leaves its audio running.
    /// </summary>
    internal bool Attached { get; private set; }

    internal FlatpakSession(string binary, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _delay = delay ?? Task.Delay;
        _run = RunAsync(binary);
    }

    internal static void Start()
    {
        if (FlatpakEnvironment.IsFlatpak)
            Current = new FlatpakSession(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "daemon", "OpenXLR.Daemon")));
    }

    internal bool Restart()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested || _run.IsCompleted) return false;
            _generation?.Cancel();
            return true;
        }
    }

    private async Task RunAsync(string binary)
    {
        int failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            using var generation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            lock (_gate) _generation = generation;
            long began = Environment.TickCount64;
            try
            {
                try
                {
                    int exit = await ProcessRunner.RunOwnedAsync(binary, [], generation.Token, TimeSpan.FromSeconds(45),
                        ["NOTIFY_SOCKET", "WATCHDOG_USEC", "WATCHDOG_PID"],
                        new System.Collections.Generic.Dictionary<string, string>
                        { ["OPENXLR_PARENT_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ConfigureAwait(false);
                    // A host installation owns the API port or lock: do not keep
                    // claiming hardware in a retry loop against another daemon.
                    if (exit == 73 && !generation.IsCancellationRequested) { Attached = true; return; }
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Console.Error.WriteLine("Could not start the sandbox daemon: " + ex.Message);
                }
                if (_stop.IsCancellationRequested) break;
                // Restart covers both the child and its retry wait. A deliberate
                // restart begins as soon as the previous child has stopped.
                if (generation.IsCancellationRequested) { failures = 0; continue; }
                failures = Environment.TickCount64 - began > 60_000 ? 0 : Math.Min(failures + 1, 5);
                await _delay(TimeSpan.FromSeconds(failures * 2), generation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (generation.IsCancellationRequested) { failures = 0; }
            finally { lock (_gate) _generation = null; }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _run.GetAwaiter().GetResult();
        _stop.Dispose();
        if (Current == this) Current = null;
    }
}
