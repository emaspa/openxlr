namespace OpenXLR.Tests;

/// <summary>
/// Files that are not regular files, put where a reader expects one, and a
/// deadline for the read so a regression fails instead of hanging the suite.
/// </summary>
internal static class SpecialFile
{
    /// <summary>Put a pipe (<c>pipe</c>) or a link to an endless device (<c>device</c>) at <paramref name="file"/>.</summary>
    public static void Make(string file, string kind)
    {
        if (File.Exists(file)) File.Delete(file);
        if (kind == "pipe")
        {
            var created = OpenXLR.Core.ProcessRunner.Run("mkfifo", [file], TimeSpan.FromSeconds(5));
            Assert.True(created.Ok, created.Stderr);
        }
        else File.CreateSymbolicLink(file, "/dev/zero");
    }

    /// <summary>
    /// Run a read against a five second deadline, releasing a pipe's blocking
    /// open when one is left waiting.
    /// </summary>
    public static async Task<T> Promptly<T>(string file, string kind, Func<T> read)
    {
        Task<T> task = Task.Run(read);
        try { return await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally
        {
            if (!task.IsCompleted && kind == "pipe")
            {
                using var keeper = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
        }
    }
}
