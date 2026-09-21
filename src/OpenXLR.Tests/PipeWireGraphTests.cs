using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PipeWireGraphTests
{
    [Theory]
    [InlineData("objects")]
    [InlineData("text")]
    public void CumulativeRegistryLimitsEndTheSubscription(string budget)
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-graph-budget-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, "watch");
        ExecutableScript.Write(script, $$"""
            #!/bin/sh
            exec python3 - <<'PY'
            import json, time
            print('[{"id":0,"type":"PipeWire:Interface:Core"}]', flush=True)
            if '{{budget}}' == 'objects':
                print(json.dumps([{'id': i} for i in range(1, 65538)]), flush=True)
            else:
                for i in range(1, 12):
                    print(json.dumps([{'id': i, 'text': 'x' * (1024 * 1024)}]), flush=True)
            while True: time.sleep(1)
            PY
            """);
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            using var graph = new PipeWireGraph(notes.Enqueue, script);
            Assert.True(SpinWait.SpinUntil(() => notes.Any(note => note.Contains("memory budget", StringComparison.Ordinal)), TimeSpan.FromSeconds(10)));
            graph.Dispose();
            Assert.Throws<IOException>(() => graph.Read());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RepeatedReadsOfALargeIdleGraphDoNotAllocateOrReparse()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-graph-cost-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, "watch");
        ExecutableScript.Write(script, """
            #!/bin/sh
            exec python3 - <<'PY'
            import json, time
            print(json.dumps([{'id': i, 'type': 'PipeWire:Interface:Core' if i == 0 else 'PipeWire:Interface:Node',
                              'info': {'props': {'text': 'x' * 2048}}} for i in range(1000)]), flush=True)
            while True: time.sleep(1)
            PY
            """);
        try
        {
            using var graph = new PipeWireGraph(executable: script);
            Assert.True(graph.WaitFor(items => items.Length == 1000, TimeSpan.FromSeconds(5)));
            // Measure on a dedicated thread, outside the runner's worker and
            // its diagnostic callbacks. Keep the zero-allocation requirement.
            Exception? failure = null;
            var probe = new Thread(() =>
            {
                try
                {
                    JsonElement[] first = graph.Read();
                    for (int i = 0; i < 100; i++) graph.Read();
                    _ = GC.GetAllocatedBytesForCurrentThread();
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 10000; i++) graph.Read();
                    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    Assert.Same(first, graph.Read());
                    Assert.Equal(0, allocated);
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            probe.Start();
            Assert.True(probe.Join(TimeSpan.FromSeconds(5)), "Graph read allocation probe hung.");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(16384)]
    public async Task FramesCanSplitAtAnyByteIncludingEscapesAndUnicode(int chunk)
    {
        const string input = """
            [{"id":1,"text":"ä[\"}\\"}] []
            [{"id":2,"info":null}]
            """;
        using var stream = new ChunkStream(Encoding.UTF8.GetBytes(input), chunk);
        var batches = new List<JsonElement>();
        await PipeWireGraph.ReadBatchesAsync(stream, element => batches.Add(element.Clone()), default);
        Assert.Equal(3, batches.Count);
        Assert.Equal("ä[\"}\\", batches[0][0].GetProperty("text").GetString());
        Assert.Empty(batches[1].EnumerateArray());
        Assert.True(PipeWireSnapshot.IsRemoval(batches[2][0]));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[}")]
    [InlineData("[{\"id\":1}")]
    [InlineData("[]garbage")]
    [InlineData("[\"unterminated")]
    public async Task MalformedOrIncompleteFramesFailWithoutPublishingThem(string json)
    {
        using var stream = new ChunkStream(Encoding.UTF8.GetBytes(json), 1);
        await Assert.ThrowsAnyAsync<JsonException>(() => PipeWireGraph.ReadBatchesAsync(stream, _ => { }, default));
    }

    [Fact]
    public async Task ByteAndDepthBudgetsApplyBeforeParsingAndResetPerBatch()
    {
        using var oversized = new MemoryStream(Encoding.UTF8.GetBytes("[" + new string(' ', 100)));
        await Assert.ThrowsAsync<JsonException>(() => PipeWireGraph.ReadBatchesAsync(oversized, _ => { }, default, 32));
        using var deep = new MemoryStream(Encoding.UTF8.GetBytes(new string('[', 65)));
        await Assert.ThrowsAsync<JsonException>(() => PipeWireGraph.ReadBatchesAsync(deep, _ => { }, default));
        using var valid = new ChunkStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("[]\n", 100))), 7);
        int batches = 0;
        await PipeWireGraph.ReadBatchesAsync(valid, _ => batches++, default, 2);
        Assert.Equal(100, batches);
    }

    [Fact]
    public async Task StreamingHelpersStopOnCancellationAndStderrFlood()
    {
        foreach (string command in new[] { "sleep 30", "yes error >&2" })
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var elapsed = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunStreamingAsync("sh", ["-c", command],
                async (stream, cancel) =>
                {
                    byte[] buffer = new byte[64];
                    while (await stream.ReadAsync(buffer, cancel) != 0) { }
                }, stop.Token));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void UpdatesReuseUnchangedSnapshotsAndRestartDropsOldIds()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-graph-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, "subscription");
        string stage = Path.Combine(dir, "stage");
        // Handshakes make the test independent of the scheduler and keep the
        // helper alive long enough to inspect each published registry.
        ExecutableScript.Write(script, """
            #!/bin/sh
            exec python3 - "$0" <<'PY'
            import os, sys, time
            path = os.path.join(os.path.dirname(sys.argv[1]), 'stage')
            if os.path.exists(path):
                print('[{"id":0,"type":"PipeWire:Interface:Core"},{"id":1,"type":"reused"}]', flush=True)
                while True: time.sleep(.05)
            print('[{"id":0,"type":"PipeWire:Interface:Core"},{"id":1,"type":"old"},{"id":2},{"id":3}]', flush=True)
            while not os.path.exists(path): time.sleep(.01)
            print('[{"id":1,"type":"changed"},{"id":2,"info":null},{"id":3,"props":null}]', flush=True)
            while os.path.getsize(path) == 0: time.sleep(.01)
            PY
            """);
        try
        {
            using var graph = new PipeWireGraph(executable: script);
            Assert.True(graph.WaitFor(items => items.Length == 4, TimeSpan.FromSeconds(5)));
            JsonElement[] before = graph.Read();
            Assert.Same(before, graph.Read());
            File.WriteAllText(stage, "");
            Assert.True(graph.WaitFor(items => items.Length == 2 && items[1].GetProperty("type").GetString() == "changed", TimeSpan.FromSeconds(5)));
            Assert.Equal("old", before[1].GetProperty("type").GetString());
            File.WriteAllText(stage, "restart");
            Assert.True(graph.WaitFor(items => items.Length == 2 && items[1].GetProperty("type").GetString() == "reused", TimeSpan.FromSeconds(5)));
            graph.Dispose();
            Assert.Throws<IOException>(() => graph.Read());
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class ChunkStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }
}
