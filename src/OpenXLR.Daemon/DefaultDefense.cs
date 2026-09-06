namespace OpenXLR.Daemon;

/// <summary>
/// After the graph is built, WirePlumber may switch the default sink and
/// source to the new nodes seconds later. This re-asserts the wanted
/// defaults in a few passes with growing delays. It is its own small loop
/// so a stop cancels it and a test can prove that; the pactl runner comes
/// in as a delegate.
/// </summary>
internal static class DefaultDefense
{
    public static readonly IReadOnlyList<int> DelaysMs = [2000, 5000, 10000, 20000];

    /// <summary>
    /// Run the passes on a worker. <paramref name="run"/> executes a pactl
    /// command line and returns its trimmed output; failures are reported
    /// through <paramref name="onError"/> and the loop goes on. Cancelling
    /// <paramref name="stop"/> ends the loop before its next pass.
    /// </summary>
    public static Task RunAsync(string? wantSink, string? wantSource, Func<string[], string> run,
        IReadOnlyList<int> delaysMs, CancellationToken stop, Action<string>? onError = null)
        => Task.Run(async () =>
        {
            foreach (int delayMs in delaysMs)
            {
                try { await Task.Delay(delayMs, stop); }
                catch (OperationCanceledException) { return; }
                if (stop.IsCancellationRequested) return;
                // Each helper call can take seconds; check between them so a
                // stop that arrives mid-pass never lets a later write land.
                string Guarded(string[] args) { stop.ThrowIfCancellationRequested(); return run(args); }
                try
                {
                    if (wantSink is { Length: > 0 } && Guarded(["get-default-sink"]) != wantSink)
                        Guarded(["set-default-sink", wantSink]);
                    if (wantSource is { Length: > 0 } && Guarded(["get-default-source"]) != wantSource)
                        Guarded(["set-default-source", wantSource]);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { onError?.Invoke(ex.Message); }
            }
        }, CancellationToken.None);
}
