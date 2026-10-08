using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core;
#endif

/// <summary>
/// What a helper process left behind. <paramref name="ExitCode"/> is the
/// helper's own status, or -1 when none could be read (the helper never
/// started, or its status was gone by the time it was asked for).
/// <paramref name="Truncated"/> is a cap breach; <paramref name="Incomplete"/>
/// is output that never reached end of file for another reason: the
/// deadline or a cancellation ended the read while a child still held the
/// pipe, or the pipe itself failed. <paramref name="Cancelled"/> is the
/// caller's own token ending the run.
/// </summary>
#if OPENXLR_UI || OPENXLR_TUI
internal sealed record ProcessResult(int ExitCode, byte[] Stdout, string Stderr, bool TimedOut, bool Truncated,
    bool Incomplete = false, bool Cancelled = false)
#else
public sealed record ProcessResult(int ExitCode, byte[] Stdout, string Stderr, bool TimedOut, bool Truncated,
    bool Incomplete = false, bool Cancelled = false)
#endif
{
    public string StdoutText => Encoding.UTF8.GetString(Stdout);
    /// <summary>Exit 0 within the time and output limits, output read to the end, not cancelled.</summary>
    public bool Ok => ExitCode == 0 && !TimedOut && !Truncated && !Incomplete && !Cancelled;
}

/// <summary>
/// The one way OpenXLR runs a helper (pw-dump, pactl, wpctl, systemctl,
/// the diagnostics commands): arguments passed as a list (no shell), the
/// C locale so parsed output never changes with the desktop language, a
/// deadline, a byte cap on each output pipe, and attached descendants
/// killed when either limit is reached, so a runaway helper or a
/// pathological PipeWire graph cannot grow the daemon's heap or park a
/// thread. Compiled into the daemon through OpenXLR.Core and into the
/// window and the terminal mixer as a linked source file.
/// </summary>
#if OPENXLR_UI || OPENXLR_TUI
internal static class ProcessRunner
#else
public static class ProcessRunner
#endif
{
    /// <summary>Room for a large PipeWire graph dump many times over.</summary>
    public const int DefaultStdoutCap = 64 * 1024 * 1024;
    /// <summary>Errors are read by people; the head is what matters.</summary>
    public const int DefaultStderrCap = 64 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Run to completion on the calling thread. See <see cref="RunAsync"/>.</summary>
    public static ProcessResult Run(string exe, IReadOnlyList<string> args, TimeSpan? timeout = null,
        int stdoutCap = DefaultStdoutCap, int stderrCap = DefaultStderrCap, bool cLocale = true,
        CancellationToken cancel = default, IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyCollection<string>? removeEnvironment = null)
        => RunAsync(exe, args, timeout, stdoutCap, stderrCap, cLocale, cancel, environment, removeEnvironment).GetAwaiter().GetResult();

    /// <summary>
    /// Run a helper with a deadline and output caps. Throws only when the
    /// process cannot be started; a timeout, a cap breach or a nonzero exit
    /// are reported in the result. Cancellation returns a result flagged
    /// <see cref="ProcessResult.Cancelled"/> that keeps the exit code the
    /// helper had. Descendants still attached to the helper are killed on
    /// interruption; a child already reparented after its parent exited is
    /// outside that tree.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan? timeout = null,
        int stdoutCap = DefaultStdoutCap, int stderrCap = DefaultStderrCap, bool cLocale = true,
        CancellationToken cancel = default, IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyCollection<string>? removeEnvironment = null)
    {
        if (cancel.IsCancellationRequested) return new ProcessResult(-1, [], "", false, false, Cancelled: true);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
        };
        ApplyEnvironment(psi, environment, removeEnvironment);
        if (cLocale)
        {
            psi.Environment["LC_ALL"] = "C";
            psi.Environment["LANGUAGE"] = "C";
        }
        foreach (string a in args) psi.ArgumentList.Add(a);

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout ?? DefaultTimeout);

        // Both pipes drain concurrently so a chatty helper never blocks on a
        // full pipe; a cap breach cancels the other reader and kills the tree.
        using var breach = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        Task<(byte[] Data, bool Truncated, bool Incomplete)> stdout = ReadCappedAsync(p.StandardOutput.BaseStream, stdoutCap, breach);
        Task<(byte[] Data, bool Truncated, bool Incomplete)> stderr = ReadCappedAsync(p.StandardError.BaseStream, stderrCap, breach);

        bool interrupted = false;
        try
        {
            await p.WaitForExitAsync(breach.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
            KillTree(p);
        }
        (byte[] outData, bool outTrunc, bool outIncomplete) = await stdout.ConfigureAwait(false);
        (byte[] errData, bool errTrunc, bool errIncomplete) = await stderr.ConfigureAwait(false);
        // A parent can exit before its child's inherited pipes close, so an
        // unfinished read counts as an interruption even when the wait
        // succeeded. A cap breach ends the other pipe's read too; that run
        // is reported as truncated, not as incomplete.
        bool truncated = outTrunc || errTrunc;
        bool incomplete = (outIncomplete || errIncomplete) && !truncated;
        interrupted |= outIncomplete || errIncomplete;
        bool cancelled = interrupted && cancel.IsCancellationRequested;
        bool timedOut = interrupted && limit.IsCancellationRequested && !cancelled;
        if (!p.HasExited) KillTree(p);
        try { p.WaitForExit(); } catch (Exception) { /* reaped */ }
        int exit;
        try { exit = p.ExitCode; } catch (InvalidOperationException) { exit = -1; }
        return new ProcessResult(exit, outData, Encoding.UTF8.GetString(errData), timedOut, truncated, incomplete, cancelled);
    }

    /// <summary>
    /// Run a user-facing program without a deadline or captured output. Its
    /// lifetime belongs to the user: never kill an installer halfway through.
    /// </summary>
    public static async Task<int> RunInteractiveAsync(string exe, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null, IReadOnlyCollection<string>? removeEnvironment = null)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        ApplyEnvironment(start, environment, removeEnvironment);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"failed to start {exe}");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>
    /// Subscribe to a helper until cancellation, EOF or a reader failure. The
    /// reader bounds individual messages and startup time; silence after startup
    /// is normal. Stderr remains capped and both pipes stop together.
    /// </summary>
    internal static async Task RunStreamingAsync(string exe, IReadOnlyList<string> args,
        Func<Stream, CancellationToken, Task> read, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANGUAGE"] = "C";
        foreach (string argument in args) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"failed to start {exe}");
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task<(byte[] Data, bool Truncated, bool Incomplete)> errors =
            ReadCappedAsync(process.StandardError.BaseStream, DefaultStderrCap, stopping);
        try
        {
            await read(process.StandardOutput.BaseStream, stopping.Token).ConfigureAwait(false);
            throw new IOException($"{exe} subscription ended");
        }
        finally
        {
            stopping.Cancel();
            KillTree(process);
            await errors.ConfigureAwait(false);
            // A process stuck in the kernel must not hold daemon shutdown.
            process.WaitForExit(2000);
        }
    }

    /// <summary>Overlay inherited values, then remove only the names the caller chose.</summary>
    internal static void ApplyEnvironment(ProcessStartInfo start, IReadOnlyDictionary<string, string>? environment,
        IReadOnlyCollection<string>? removeEnvironment = null)
    {
        if (environment is not null)
            foreach ((string name, string value) in environment) start.Environment[name] = value;
        if (removeEnvironment is not null)
            foreach (string name in removeEnvironment) start.Environment.Remove(name);
    }

    private static async Task<(byte[] Data, bool Truncated, bool Incomplete)> ReadCappedAsync(Stream pipe, int cap, CancellationTokenSource breach)
    {
        var buf = new byte[16 * 1024];
        var kept = new MemoryStream();
        try
        {
            int n;
            while ((n = await pipe.ReadAsync(buf, breach.Token).ConfigureAwait(false)) > 0)
            {
                int room = cap - (int)kept.Length;
                if (n > room)
                {
                    if (room > 0) kept.Write(buf, 0, room);
                    breach.Cancel();          // stops the other reader and the wait; the tree is killed there
                    return (kept.ToArray(), true, false);
                }
                kept.Write(buf, 0, n);
            }
        }
        catch (OperationCanceledException)
        {
            return (kept.ToArray(), false, true);   // the deadline, a cancellation or the other pipe's cap ended the read
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return (kept.ToArray(), false, true);   // the pipe failed before end of file
        }
        return (kept.ToArray(), false, false);
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
}
