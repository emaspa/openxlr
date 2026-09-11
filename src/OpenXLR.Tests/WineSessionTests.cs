using System.Text;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The decision to end a Wine prefix, with the process runner faked. Nothing
/// here starts wineserver or reads a real prefix: what is proved is when the
/// call is made, which prefix it names, and that a failure is survivable.
/// </summary>
public sealed class WineSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openxlr-wine-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Server, string Arguments, string Prefix)> _calls = [];
    private readonly List<string> _notes = [];

    private const string Prefix = "/home/tester/.wine";
    private const string Server = "/usr/bin/wineserver";

    private ProcessResult Fake(string server, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        _calls.Add((server, string.Join(' ', arguments), environment.TryGetValue("WINEPREFIX", out string? p) ? p : ""));
        return new ProcessResult(0, [], "", false, false);
    }

    private WineSession Session(string? server = Server, IReadOnlyList<string>? prefixes = null,
        Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, ProcessResult>? run = null)
        => new(_notes.Add, run ?? Fake, () => server, () => prefixes ?? [Prefix]);

    private void End(WineSession session, bool anyBridgedHelperAlive) => session.Run(session.Decide(anyBridgedHelperAlive));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void WineIsNeverEndedWhenNoBridgedHelperRanHere()
    {
        WineSession session = Session();
        End(session, anyBridgedHelperAlive: false);
        Assert.Empty(_calls);
    }

    [Fact]
    public void WineIsLeftAloneWhileABridgedHelperIsStillRunning()
    {
        WineSession session = Session();
        session.HelperStarted();
        End(session, anyBridgedHelperAlive: true);
        Assert.Empty(_calls);

        // The same session ends it once the last helper has gone: a rebuild
        // that drops the last bridged insert must not leave Wine resident.
        End(session, anyBridgedHelperAlive: false);
        Assert.Equal([(Server, "-k", Prefix)], _calls);
    }

    [Fact]
    public void AnEndedSessionIsNotEndedAgainAndAFailedOneIsRetriedAtTheStop()
    {
        // The ending is decided from the Wine sessions that are up, so once
        // one is gone the same question answers empty however often it is put.
        List<string> running = [Prefix];
        WineSession session = new(_notes.Add, (server, arguments, environment) =>
        {
            running.Remove(environment["WINEPREFIX"]);
            return Fake(server, arguments, environment);
        }, () => Server, () => running);
        session.HelperStarted();
        End(session, anyBridgedHelperAlive: false);
        End(session, anyBridgedHelperAlive: false);
        Assert.Single(_calls);

        // A kill that failed leaves the session up, and the daemon's stop
        // gets another go at it rather than inheriting the wait.
        _calls.Clear();
        WineSession failing = Session(run: (server, arguments, environment) =>
        {
            Fake(server, arguments, environment);
            return new ProcessResult(1, [], "cannot connect to the wineserver", false, false);
        });
        failing.HelperStarted();
        End(failing, anyBridgedHelperAlive: false);
        End(failing, anyBridgedHelperAlive: false);
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public void EveryPrefixThisDaemonStartedIsEndedAndNothingElseIs()
    {
        WineSession session = Session(prefixes: ["/home/tester/.wine", "/srv/prefixes/plugins"]);
        session.HelperStarted();
        End(session, anyBridgedHelperAlive: false);
        Assert.Equal([(Server, "-k", "/home/tester/.wine"), (Server, "-k", "/srv/prefixes/plugins")], _calls);
    }

    [Fact]
    public void AWineSessionThisDaemonDidNotStartIsNotEnded()
    {
        WineSession session = Session(prefixes: []);
        session.HelperStarted();
        End(session, anyBridgedHelperAlive: false);
        Assert.Empty(_calls);

        // Nothing was spent either: the decision stands for when a session of
        // ours does turn up.
        Assert.Empty(_notes);
    }

    [Fact]
    public void WithoutAWineserverOnPathNothingRunsAndItIsSaidOnce()
    {
        WineSession session = Session(server: null);
        session.HelperStarted();
        End(session, anyBridgedHelperAlive: false);
        End(session, anyBridgedHelperAlive: false);
        Assert.Empty(_calls);
        Assert.Single(_notes);
        Assert.Contains("wineserver", _notes[0]);
    }

    [Fact]
    public void AFailingWineserverIsReportedAndDoesNotThrow()
    {
        WineSession timedOut = Session(run: (_, _, _) => new ProcessResult(-1, [], "", true, false));
        timedOut.HelperStarted();
        End(timedOut, anyBridgedHelperAlive: false);
        Assert.Contains("timed out", _notes[^1]);

        _notes.Clear();
        WineSession broken = Session(run: (_, _, _) => throw new InvalidOperationException("failed to start wineserver"));
        broken.HelperStarted();
        End(broken, anyBridgedHelperAlive: false);
        Assert.Contains("failed to start wineserver", _notes[^1]);
    }

    [Fact]
    public void OnlyYabridgeWrappersCountAsBridged()
    {
        Assert.True(WineSession.Bridged("/home/t/.local/share/openxlr/yabridge/vst3/TDR Nova.vst3"));
        Assert.True(WineSession.Bridged("/home/t/.vst3/yabridge/ValhallaSupermassive.vst3"));
        Assert.True(WineSession.Bridged("/home/t/.clap/yabridge/Plugin.clap"));
        Assert.False(WineSession.Bridged("/usr/lib/vst3/Calf.vst3"));
        Assert.False(WineSession.Bridged("/home/t/.lv2/eq10q.lv2"));
        Assert.False(WineSession.Bridged(null));
    }

    [Fact]
    public void WineprefixIsReadFromAProcessEnvironmentWithWinesOwnFallback()
    {
        byte[] block = Encoding.UTF8.GetBytes("PATH=/usr/bin\0WINEPREFIX=/srv/prefix/\0HOME=/home/t\0");
        Assert.Equal("/srv/prefix", WineSession.PrefixIn(block, "/home/t/.wine"));
        Assert.Equal("/home/t/.wine", WineSession.PrefixIn(Encoding.UTF8.GetBytes("HOME=/home/t\0"), "/home/t/.wine"));
    }

    [Fact]
    public void OnlyTheWineserversInThisProcessesControlGroupAreCollected()
    {
        const string ours = "0::/user.slice/user-1000.slice/user@1000.service/app.slice/openxlr-daemon.service";
        const string theirs = "0::/user.slice/user-1000.slice/session.slice/reaper.scope";
        Directory.CreateDirectory(Path.Combine(_root, "self"));
        File.WriteAllText(Path.Combine(_root, "self", "cgroup"), ours + "\n");
        Fixture("11", "pipewire", Prefix, ours);                     // not a wineserver
        Fixture("13", "wineserver", "/home/tester/.other", theirs);  // another application's session

        Assert.Empty(WineSession.PrefixesStartedHere(_root, Prefix));

        Fixture("14", "wineserver", "/srv/prefixes/plugins/", ours);
        Assert.Equal(["/srv/prefixes/plugins"], WineSession.PrefixesStartedHere(_root, Prefix));

        // Wine's own default counts as the prefix when the variable is unset.
        Fixture("15", "wineserver", null, ours);
        Assert.Equal([Prefix, "/srv/prefixes/plugins"], WineSession.PrefixesStartedHere(_root, Prefix).Order().ToList());
    }

    private void Fixture(string pid, string comm, string? prefix, string cgroup)
    {
        string directory = Path.Combine(_root, pid);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "comm"), comm + "\n");
        File.WriteAllText(Path.Combine(directory, "cgroup"), cgroup + "\n");
        File.WriteAllBytes(Path.Combine(directory, "environ"),
            Encoding.UTF8.GetBytes("HOME=/home/tester\0" + (prefix is null ? "" : $"WINEPREFIX={prefix}\0")));
    }
}
