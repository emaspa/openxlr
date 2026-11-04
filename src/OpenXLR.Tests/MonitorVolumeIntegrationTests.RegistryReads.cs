using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void IdleReadsUseTheSubscriptionAndFollowVolumeDefaultAndPortChanges()
    {
        var pw = new PipeWireAdapter();
        string dir = Directory.CreateTempSubdirectory("openxlr-idle-reads-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        string calls = Path.Combine(dir, "reads");
        try
        {
            pw.CreateNullSink("test_registry_reads", "Registry reads");
            pw.CreateNullSink("test_registry_other", "Other");
            pw.SetDefaultSink("test_registry_reads");
            pw.SetDefaultSource("test_registry_reads.monitor");
            using var watch = pw.WatchGraph();
            Assert.Equal("test_registry_reads", pw.GetDefaultSink());
            Assert.Equal("test_registry_reads.monitor", pw.GetDefaultSource());
            foreach (string helper in new[] { "pactl", "pw-link" })
                ExecutableScript.Write(Path.Combine(dir, helper), $$"""
                    #!/bin/sh
                    case "$1" in get-*|list|-o|-i|--format=*) printf '%s\n' "$*" >> '{{calls}}';; esac
                    exec /usr/bin/{{helper}} "$@"
                    """);
            Environment.SetEnvironmentVariable("PATH", dir + ":" + path);
            for (int i = 0; i < 100; i++)
            {
                Assert.True(pw.SinkExists("test_registry_reads"));
                Assert.False(pw.SinkExists("test_registry_missing"));
                Assert.Equal(1, pw.GetSinkVolume("test_registry_reads"));
                Assert.Null(pw.GetSinkVolume("test_registry_missing"));
                Assert.Equal("test_registry_reads", pw.GetDefaultSink());
                Assert.Equal("test_registry_reads.monitor", pw.GetDefaultSource());
                Assert.True(pw.HasCaptureChannel("test_registry_reads", 1));
                Assert.False(pw.HasCaptureChannel("test_registry_reads", 2));
            }
            Assert.False(File.Exists(calls), File.Exists(calls) ? File.ReadAllText(calls) : "");
            // Match pactl's first-channel read without changing stereo balance.
            foreach (var (left, right, expected) in new[] { ("25%", "75%", .25), ("75%", "25%", .75) })
            {
                Assert.Equal(0, ProcessRunner.Run("pactl", ["set-sink-volume", "test_registry_reads", left, right]).ExitCode);
                Assert.True(SpinWait.SpinUntil(() => pw.GetSinkVolume("test_registry_reads") == expected, TimeSpan.FromSeconds(3)));
            }
            Assert.Equal(0, ProcessRunner.Run("pactl", ["set-sink-volume", "test_registry_reads", "200%"]).ExitCode);
            Assert.True(SpinWait.SpinUntil(() => pw.GetSinkVolume("test_registry_reads") == 1.5, TimeSpan.FromSeconds(3)));
            Assert.Equal(0, ProcessRunner.Run("pactl", ["set-default-sink", "test_registry_other"]).ExitCode);
            Assert.Equal(0, ProcessRunner.Run("pactl", ["set-default-source", "test_registry_other.monitor"]).ExitCode);
            Assert.True(SpinWait.SpinUntil(() => pw.GetDefaultSink() == "test_registry_other"
                && pw.GetDefaultSource() == "test_registry_other.monitor", TimeSpan.FromSeconds(3)));
            string[] refreshes = File.ReadAllLines(calls);
            Assert.InRange(refreshes.Count(line => line == "--format=json info"), 1, 4);
            // A metadata change during a paired query discards the pair and
            // reads the requested default directly.
            Assert.All(refreshes, line => Assert.Contains(line, new[] { "--format=json info", "get-default-sink", "get-default-source" }));

            pw.CreateNullSink("test_registry_added", "Added");
            Assert.True(SpinWait.SpinUntil(() => pw.HasCaptureChannel("test_registry_added", 1), TimeSpan.FromSeconds(3)));
            pw.SetSinkVolume("test_registry_reads", 1.25);
            Assert.Equal(1.25, pw.GetSinkVolume("test_registry_reads"));
            pw.TearDown();
            Assert.True(SpinWait.SpinUntil(() => !pw.HasCaptureChannel("test_registry_added", 0), TimeSpan.FromSeconds(3)));

        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            pw.TearDown();
            Directory.Delete(dir, true);
        }
    }
}
