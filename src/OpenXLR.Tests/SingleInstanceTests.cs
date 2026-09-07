using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void ASecondLaunchAsksTheFirstToShowAndDoesNotRun()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "ui.sock");
        try
        {
            using SingleInstance? first = SingleInstance.TryBecomePrimary(path);
            Assert.NotNull(first);
            var shown = new ManualResetEventSlim();
            first.ShowRequested = shown.Set;

            using SingleInstance? second = SingleInstance.TryBecomePrimary(path);
            Assert.Null(second);                                  // handed off, would exit
            Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));     // and the first was asked to show

            first.Dispose();
            using SingleInstance? again = SingleInstance.TryBecomePrimary(path);   // the socket is free again
            Assert.NotNull(again);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void AStaleSocketFileIsReplaced()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "ui.sock");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "");   // a file nobody listens on
            using SingleInstance? first = SingleInstance.TryBecomePrimary(path);
            Assert.NotNull(first);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}
