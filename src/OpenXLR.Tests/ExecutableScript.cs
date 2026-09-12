namespace OpenXLR.Tests;

/// <summary>
/// A fake helper program, for tests that hand one to code which runs it.
///
/// A test cannot write a program and then run it. Writing holds a write
/// handle on the file, a fork on any other thread copies that handle into
/// its child, and Linux refuses to run a file that any process can still
/// write, so the run fails with "Text file busy" until that child reaches
/// its own exec. Test classes run in parallel here, so the window is open
/// most of the time and the failure looks random.
///
/// Every fake program is therefore a link to <c>run-script.sh</c>, which the
/// build copies next to the test assembly, and what the program does is a
/// plain text file beside the link. The test process writes no file it runs.
/// </summary>
internal static class ExecutableScript
{
    private static readonly string Runner = ShippedRunner();

    /// <summary>Create <paramref name="path"/> as a program running <paramref name="body"/>.</summary>
    public static string Write(string path, string body)
    {
        File.WriteAllText(path + ".body", body);
        if (OperatingSystem.IsWindows()) return path;   // no fork, and nothing here runs a shell script
        File.Delete(path);                              // a stale link, not its target
        File.CreateSymbolicLink(path, Runner);
        return path;
    }

    private static string ShippedRunner()
    {
        string runner = Path.Combine(AppContext.BaseDirectory, "run-script.sh");
        if (OperatingSystem.IsWindows()) return runner;
        // chmod, never an open for writing, so this cannot make the file busy.
        UnixFileMode mode = File.GetUnixFileMode(runner);
        if (!mode.HasFlag(UnixFileMode.UserExecute)) File.SetUnixFileMode(runner, mode | UnixFileMode.UserExecute);
        return runner;
    }
}
