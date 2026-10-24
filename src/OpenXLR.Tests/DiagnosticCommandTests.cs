using System.Text.Json;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class DiagnosticCommandTests
{
    [Fact]
    public void ManyAttachedSerialsDoNotRebuildRedactionRulesForEveryJsonField()
    {
        string[] secrets = Enumerable.Range(0, 20).Select(i => "USB-SECRET-" + i).Append("/home/alice").ToArray();
        string input = JsonSerializer.Serialize(Enumerable.Range(0, 500).Select(i =>
            new { index = i, path = "/home/alice/capture", tag = "USB-SECRET-19" }));
        Diagnostics.RedactJsonText("{}", secrets, "alice", out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        string output = Diagnostics.RedactJsonText(input, secrets, "alice", out bool valid);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Allow the parsed tree and rewritten text, but not a fresh set of
        // patterns for each value when the global regex cache is full.
        Assert.InRange(allocated, 0, 16 * 1024 * 1024);
        Assert.True(valid);
        Assert.DoesNotContain("alice", output);
        Assert.DoesNotContain("USB-SECRET-19", output);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(500, json.RootElement.GetArrayLength());
        Assert.Equal(499, json.RootElement[499].GetProperty("index").GetInt32());
    }

    [Fact]
    public async Task MetadataFieldNamesUseTheSameLocaleAsTheirRedactionRules()
    {
        string? locale = Environment.GetEnvironmentVariable("LC_ALL");
        string? language = Environment.GetEnvironmentVariable("LANGUAGE");
        string root = Directory.CreateTempSubdirectory("openxlr-diagnostic-locale-").FullName;
        try
        {
            Environment.SetEnvironmentVariable("LC_ALL", "de_DE.UTF-8");
            Environment.SetEnvironmentVariable("LANGUAGE", "de");
            string helper = Path.Combine(root, "probe");
            ExecutableScript.Write(helper, "printf '%s|%s' \"$LC_ALL\" \"$LANGUAGE\"\n");
            var result = await Diagnostics.RunCommandAsync(helper, []);
            Assert.Equal("C|C", result.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LC_ALL", locale);
            Environment.SetEnvironmentVariable("LANGUAGE", language);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AHelperFailureWithoutStderrStillNamesItsExitStatus()
    {
        string root = Directory.CreateTempSubdirectory("openxlr-diagnostic-command-").FullName;
        try
        {
            string helper = Path.Combine(root, "probe");
            ExecutableScript.Write(helper, "printf partial\nexit 17\n");
            var result = await Diagnostics.RunCommandAsync(helper, []);
            Assert.Equal("partial", result.Output);
            Assert.Contains("exited with status 17", result.Errors);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AppendedGraphUpdatesFoldIntoOneArray()
    {
        string raw = """
            [{"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"first"}}},
             {"id":2,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"gone"}}}]
            [{"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"updated"}}},
             {"id":2,"info":null}]
            """;
        using var graph = JsonDocument.Parse(Diagnostics.GraphForArchive(raw));
        var node = Assert.Single(graph.RootElement.EnumerateArray());
        Assert.Equal("updated", node.GetProperty("info").GetProperty("props").GetProperty("node.name").GetString());
    }

    [Theory]
    [InlineData("[] [")]
    [InlineData("[] {}")]
    public void AnInterruptedGraphIsKeptForTheErrorSidecar(string json)
        => Assert.Equal(json, Diagnostics.GraphForArchive(json));

    [Fact]
    public async Task AMissingHelperIsUnknownRatherThanASuccessfulEmptyReport()
    {
        var result = await Diagnostics.RunCommandAsync("/nonexistent/openxlr-diagnostics-helper", []);
        Assert.Contains("failed to run", result.Errors);
        Assert.Equal("", result.Output);
    }

    [Fact]
    public void AllInterruptionFlagsRemainVisibleAndStderrHasItsOwnBound()
    {
        var result = new OpenXLR.UI.ProcessResult(137, [], "", true, true, Incomplete: true, Cancelled: true);
        string notes = Diagnostics.CommandNotes("probe", result);
        Assert.Contains("15 s", notes);
        Assert.Contains("stdout 8 MiB, stderr 64 KiB", notes);
        Assert.Contains("before the helper closed", notes);
        Assert.Contains("cancelled", notes);
        Assert.Contains("status 137", notes);
        Assert.Empty(Diagnostics.CommandNotes("probe", new OpenXLR.UI.ProcessResult(0, [], "", false, false)));
    }

    [Fact]
    public void JsonRedactionPreservesNumericAudioValuesEvenWhenTheyEqualASerial()
    {
        string text = Diagnostics.RedactJsonText("""
            {"quantum":1024,"serialName":"usb_1024-00","device.serial":1024,
             "application.process.id":1024,"path":"\u002fhome\u002falice\u002ftake.wav"}
            """, ["1024"], "alice", out bool valid);
        Assert.True(valid);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(1024, json.RootElement.GetProperty("quantum").GetInt32());
        Assert.Equal("usb_<redacted>-00", json.RootElement.GetProperty("serialName").GetString());
        Assert.Equal("<redacted>", json.RootElement.GetProperty("device.serial").GetString());
        Assert.Equal("<redacted>", json.RootElement.GetProperty("application.process.id").GetString());
        Assert.DoesNotContain("alice", text);
    }

    [Fact]
    public void PrivateObjectKeysAreMaskedWithoutLosingCollidingValues()
    {
        string text = Diagnostics.RedactJsonText("""
            {"/home/alice/file":1,"/home/bob/file":2,"/home/<redacted>/file":3}
            """, ["/home/alice", "/home/bob"], null, out bool valid);
        Assert.True(valid);
        Assert.DoesNotContain("alice", text);
        Assert.DoesNotContain("bob", text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal([1, 2, 3], json.RootElement.EnumerateObject().Select(p => p.Value.GetInt32()).Order().ToArray());
    }

    [Theory]
    [InlineData("{\"x\":1,\"x\":2,\"path\":\"/home/alice/a\"}")]
    [InlineData("{\"path\":\"/home/alice/a\"")]
    public void IncompleteAndDuplicateJsonIsMarkedInvalidAndStillRedacted(string input)
    {
        string text = Diagnostics.RedactJsonText(input, [], "alice", out bool valid);
        Assert.False(valid);
        Assert.DoesNotContain("alice", text);
    }
}
