namespace OpenXLR.Daemon;

/// <summary>
/// A service that wants the whole daemon started afresh (the audio server
/// restarted underneath it) records the exit code here and stops the host;
/// Program returns it so systemd's Restart=on-failure brings the daemon back.
/// </summary>
internal static class RestartRequest
{
    /// <summary>EX_TEMPFAIL: a clean exit that systemd retries.</summary>
    public const int TemporaryFailure = 75;

    private static int _exitCode;

    public static int ExitCode => Volatile.Read(ref _exitCode);

    public static void Ask(int exitCode) => Volatile.Write(ref _exitCode, exitCode);
}
