using OpenXLR.UI;
using System.Net.Sockets;
using System.Text;

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

    [Fact]
    public void ALinkedRuntimeDirectoryRunsWithoutTheGuard()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string target = Directory.CreateDirectory(dir + ".target").FullName;
        try
        {
            Directory.CreateSymbolicLink(dir, target);
            using SingleInstance? first = SingleInstance.TryBecomePrimary(Path.Combine(dir, "ui.sock"));
            Assert.NotNull(first);
            Assert.Empty(Directory.GetFileSystemEntries(target));
        }
        finally
        {
            try { File.Delete(dir); } catch (IOException) { }
            try { Directory.Delete(target, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void LaunchesDuringInitializationAreRememberedAndCoalesced()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "ui.sock");
        try
        {
            using SingleInstance? first = SingleInstance.TryBecomePrimary(path);
            Assert.NotNull(first);
            for (int i = 0; i < 2; i++)
            {
                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                client.ReceiveTimeout = 5000;
                client.Connect(new UnixDomainSocketEndPoint(path));
                client.Send(Encoding.ASCII.GetBytes("show\n"));
                client.Shutdown(SocketShutdown.Send);
                // EOF confirms the listener consumed the request before a
                // window has installed its callback. No scheduler delay.
                Assert.Equal(0, client.Receive(new byte[1]));
            }
            int shown = 0;
            first.ShowRequested = () => shown++;
            Assert.Equal(1, shown);
            first.ShowRequested = () => shown++;
            Assert.Equal(1, shown);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void DisposingAnOldGuardTwiceKeepsTheNewGuardReachable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "ui.sock");
        try
        {
            using SingleInstance? first = SingleInstance.TryBecomePrimary(path);
            Assert.NotNull(first);
            first.Dispose();
            using SingleInstance? replacement = SingleInstance.TryBecomePrimary(path);
            Assert.NotNull(replacement);
            using var shown = new ManualResetEventSlim();
            replacement.ShowRequested = shown.Set;
            first.Dispose();
            using SingleInstance? third = SingleInstance.TryBecomePrimary(path);
            Assert.Null(third);
            Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void AFragmentedLaunchRequestWaitsForItsTerminator()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "ui.sock");
        try
        {
            using SingleInstance? first = SingleInstance.TryBecomePrimary(path);
            Assert.NotNull(first);
            using var shown = new ManualResetEventSlim();
            first.ShowRequested = shown.Set;
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(path));
            client.Send(Encoding.ASCII.GetBytes("sh"));
            // The connection stays open for the remainder, rather than
            // treating one socket receive as the complete message.
            Assert.False(client.Poll(100_000, SelectMode.SelectRead));
            client.Send(Encoding.ASCII.GetBytes("ow\n"));
            Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}
