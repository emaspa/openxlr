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
        var result = Diagnostics.AudioJsonForArchive(raw, pipeWire: true, text => text);
        Assert.True(result.Valid);
        using var graph = JsonDocument.Parse(result.Text);
        var node = Assert.Single(graph.RootElement.EnumerateArray());
        Assert.Equal("updated", node.GetProperty("info").GetProperty("props").GetProperty("node.name").GetString());
    }

    [Theory]
    [InlineData("[] [")]
    [InlineData("[] {}")]
    [InlineData("[{\"note\":\"private-metadata\\uD800\"}]")]
    [InlineData("[{\"note\":\"private-metadata\"}")]
    public void AnInvalidGraphIsOmittedWithoutEchoingPrivateMetadata(string json)
    {
        var report = Diagnostics.AudioJsonForArchive(json, pipeWire: true, text => text);
        Assert.False(report.Valid);
        Assert.Null(report.Graph);
        Assert.DoesNotContain("private-metadata", report.Text);
        using var omission = JsonDocument.Parse(report.Text);
        Assert.True(omission.RootElement.TryGetProperty("unavailable", out _));
    }

    [Fact]
    public void RepeatedFieldsKeepTheirRoutingValuesAndMaskEveryPrivateValue()
    {
        var report = Diagnostics.AudioJsonForArchive("""
            [{"id":1,"type":"PipeWire:Interface:Node","info":{"props":{
                "application.process.id":"secret\"identifier",
                "application.process.id":{"nested":"another private identifier"},
                "application.process.args":"private args",
                "application.process.binary":"C:\\Users\\private\\player.exe",
                "node.name":"openxlr_route", "clock.rate":48000,
                "clock.rate":96000}}}]
            """, pipeWire: true, text => text);
        Assert.True(report.Valid);
        Assert.DoesNotContain("private", report.Text);
        Assert.DoesNotContain("secret", report.Text);
        using var saved = JsonDocument.Parse(report.Text);
        var properties = saved.RootElement[0].GetProperty("info").GetProperty("props");
        Assert.Equal("openxlr_route", properties.GetProperty("node.name").GetString());
        Assert.Equal("player.exe", properties.GetProperty("application.process.binary").GetString());
        Assert.Equal([48000, 96000], properties.EnumerateObject().Where(p => p.Name == "clock.rate").Select(p => p.Value.GetInt32()));
        Assert.Equal(["<redacted>", "<redacted>"], properties.EnumerateObject()
            .Where(p => p.Name == "application.process.id").Select(p => p.Value.GetString()));
    }

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
    [InlineData("{\"path\":\"\\u002fhome\\u002falice\\u002fprivate-name\"")]
    [InlineData("{\"path\":\"private-name\\uD800\"}")]
    public void InvalidConfigurationJsonIsOmitted(string input)
    {
        string text = Diagnostics.RedactJsonText(input, [], "alice", out bool valid);
        Assert.False(valid);
        Assert.DoesNotContain("alice", text);
        Assert.DoesNotContain("private-name", text);
        using var report = JsonDocument.Parse(text);
        Assert.True(report.RootElement.TryGetProperty("unavailable", out _));
    }

    [Fact]
    public void TheAlsaSnapshotCarriesOpenDevicesTheirUsbFrequencyAndNoProcessIds()
    {
        string root = Directory.CreateTempSubdirectory("openxlr-diagnostic-asound-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "cards"), " 6 [Pro            ]: USB-Audio - Elgato Wave XLR Pro\n");
            string card = Directory.CreateDirectory(Path.Combine(root, "card6")).FullName;
            File.WriteAllText(Path.Combine(card, "id"), "Pro\n");
            File.WriteAllText(Path.Combine(card, "usbid"), "0fd9:009b\n");
            File.WriteAllText(Path.Combine(card, "stream0"), "Capture:\n  Status: Running\n    Momentary freq = 47999 Hz (0x5.fff8)\n");
            string open = Directory.CreateDirectory(Path.Combine(card, "pcm0c", "sub0")).FullName;
            File.WriteAllText(Path.Combine(open, "hw_params"), "format: FLOAT_LE\nrate: 48000 (48000/1)\nperiod_size: 1024\nbuffer_size: 3072\n");
            File.WriteAllText(Path.Combine(open, "status"), "state: RUNNING\nowner_pid   : 1727\ndelay       : 80\n");
            string closed = Directory.CreateDirectory(Path.Combine(card, "pcm0p", "sub0")).FullName;
            File.WriteAllText(Path.Combine(closed, "hw_params"), "closed\n");
            File.WriteAllText(Path.Combine(closed, "status"), "closed\n");
            // card10 sorts after card6 by number, and the named link
            // /proc/asound keeps beside each card is not read twice.
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "card10")).FullName, "id"), "Katana\n");
            Directory.CreateSymbolicLink(Path.Combine(root, "Pro"), card);

            string text = Diagnostics.AlsaSnapshot(root);
            Assert.Contains("== card6/usbid ==\n0fd9:009b", text);
            Assert.Contains("Momentary freq = 47999 Hz", text);
            Assert.Contains("== card6/pcm0c/sub0/hw_params ==\nformat: FLOAT_LE\nrate: 48000 (48000/1)\nperiod_size: 1024", text);
            Assert.Contains("owner_pid   : <redacted>", text);
            Assert.DoesNotContain("1727", text);
            Assert.Contains("delay       : 80", text);
            Assert.Contains("== card6/pcm0p/sub0 ==\nclosed", text);
            Assert.DoesNotContain("pcm0p/sub0/status", text);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "Momentary freq = "));
            Assert.True(text.IndexOf("== card6/id ==", StringComparison.Ordinal) < text.IndexOf("== card10/id ==\nKatana", StringComparison.Ordinal));
            Assert.Contains("ALSA sound cards and their open PCM devices", Diagnostics.AlsaSnapshot(Path.Combine(root, "missing")));
            Assert.Contains("does not exist", Diagnostics.AlsaSnapshot(Path.Combine(root, "missing")));
        }
        finally { Directory.Delete(root, true); }
    }
}
