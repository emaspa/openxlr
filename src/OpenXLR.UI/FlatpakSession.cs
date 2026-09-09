using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>The Flatpak UI owns its daemon, including restart and shutdown.</summary>
internal sealed class FlatpakSession : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private TaskCompletionSource<bool>? _restart;
    private Task _worker;
    public static FlatpakSession? Current { get; private set; }
    public static string LogPath => Path.Combine(Deployment.LogDirectory, "daemon.log");

    public FlatpakSession()
    {
        Current = this;
        _worker = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        int failures = 0;
        DateTime lastStart = DateTime.UtcNow;
        while (!_lifetime.IsCancellationRequested)
        {
            using var run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            lock (_gate) _run = run;
            try
            {
                await ProcessRunner.RunServiceAsync("/app/lib/openxlr/daemon/OpenXLR.Daemon", [], LogPath, run.Token,
                    () => { lock (_gate) { _restart?.TrySetResult(true); _restart = null; } });
            }
            catch (Exception ex)
            {
                OpenXlrPaths.EnsurePrivateDir(Deployment.LogDirectory);
                OpenXlrPaths.WriteAtomic(LogPath, $"Could not run bundled daemon: {ex.Message}\n");
            }
            lock (_gate) _run = null;
            if (_lifetime.IsCancellationRequested) break;
            if (!run.IsCancellationRequested)
            {
                if (DateTime.UtcNow - lastStart > TimeSpan.FromMinutes(5)) failures = 0;
                if (++failures >= 3) break;
            }
            lastStart = DateTime.UtcNow;
            try { await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token); }
            catch (OperationCanceledException) { break; }
        }
        lock (_gate) { _restart?.TrySetResult(false); _restart = null; }
    }

    public async Task<bool> RestartAsync()
    {
        Task<bool> restarted;
        lock (_gate)
        {
            _restart ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            restarted = _restart.Task;
            if (_worker.IsCompleted) _worker = Task.Run(RunAsync);
            else if (_run is not null) _run.Cancel();
        }
        try { return await restarted.WaitAsync(TimeSpan.FromSeconds(55)); }
        catch (TimeoutException) { return false; }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _worker.GetAwaiter().GetResult();
        Current = null;
        _lifetime.Dispose();
    }
}
