using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

/// <summary>What a helper process left behind.</summary>
#if OPENXLR_UI
internal sealed record ProcessResult(int ExitCode, byte[] Stdout, string Stderr, bool TimedOut, bool Truncated)
#else
public sealed record ProcessResult(int ExitCode, byte[] Stdout, string Stderr, bool TimedOut, bool Truncated)
#endif
{
    public string StdoutText => Encoding.UTF8.GetString(Stdout);
    /// <summary>Exit 0 within the time and output limits.</summary>
    public bool Ok => ExitCode == 0 && !TimedOut && !Truncated;
}

/// <summary>
/// The one way OpenXLR runs a helper (pw-dump, pactl, wpctl, systemctl,
/// the diagnostics commands): arguments passed as a list (no shell), the
/// C locale so parsed output never changes with the desktop language, a
/// deadline, a byte cap on each output pipe, and the whole process tree
/// killed the moment either limit is reached, so a runaway helper or a
/// pathological PipeWire graph cannot grow the daemon's heap or park a
/// thread. Compiled into the daemon through OpenXLR.Core and into the
/// window as a linked source file.
/// </summary>
#if OPENXLR_UI
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
        CancellationToken cancel = default)
        => RunAsync(exe, args, timeout, stdoutCap, stderrCap, cLocale, cancel).GetAwaiter().GetResult();

    /// <summary>
    /// Run a helper with a deadline and output caps. Throws only when the
    /// process cannot be started; a timeout, a cap breach or a nonzero exit
    /// are reported in the result, with the process tree already killed.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan? timeout = null,
        int stdoutCap = DefaultStdoutCap, int stderrCap = DefaultStderrCap, bool cLocale = true,
        CancellationToken cancel = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
        };
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
        Task<(byte[] Data, bool Truncated)> stdout = ReadCappedAsync(p.StandardOutput.BaseStream, stdoutCap, breach);
        Task<(byte[] Data, bool Truncated)> stderr = ReadCappedAsync(p.StandardError.BaseStream, stderrCap, breach);

        bool timedOut = false;
        try
        {
            await p.WaitForExitAsync(breach.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = limit.IsCancellationRequested && !cancel.IsCancellationRequested;
            KillTree(p);
        }
        (byte[] outData, bool outTrunc) = await stdout.ConfigureAwait(false);
        (byte[] errData, bool errTrunc) = await stderr.ConfigureAwait(false);
        if (!p.HasExited) KillTree(p);
        try { p.WaitForExit(); } catch (Exception) { /* reaped */ }
        int exit;
        try { exit = p.ExitCode; } catch (InvalidOperationException) { exit = -1; }
        return new ProcessResult(exit, outData, Encoding.UTF8.GetString(errData), timedOut, outTrunc || errTrunc);
    }

    private static async Task<(byte[] Data, bool Truncated)> ReadCappedAsync(Stream pipe, int cap, CancellationTokenSource breach)
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
                    return (kept.ToArray(), true);
                }
                kept.Write(buf, 0, n);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The deadline or the other pipe's cap ended the read; keep what arrived.
        }
        return (kept.ToArray(), false);
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
}
