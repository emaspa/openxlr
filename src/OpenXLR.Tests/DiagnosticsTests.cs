using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public async Task UnavailableDaemonStillProducesValidPluginReports()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-diag-offline-").FullName;
        await using var client = new DaemonClient();
        try
        {
            await Diagnostics.WritePluginDataAsync(client, dir, TimeSpan.FromMilliseconds(100));
            foreach (string file in Directory.GetFiles(dir))
            {
                using var data = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                Assert.Contains("No reply", data.RootElement.GetProperty("unavailable").GetString());
            }
            Assert.Equal(3, Directory.GetFiles(dir).Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ArchiveIncludesPluginEvidenceFromDaemonAndRedactsPaths()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd == "auth") continue;
                Assert.DoesNotContain(cmd, new[] { "rescanPlugins", "syncWindowsPlugins", "setInserts" });
                object reply = cmd switch
                {
                    "listPlugins" => new { type = "plugins", plugins = new[] { new { name = "Windows EQ", path = home + "/.vst3/EQ.vst3", audioIns = 2 } } },
                    "getPluginSetup" => new { type = "pluginSetup", bridgeProvider = "openxlr", wineVersion = "wine-11.0", windowsDirectories = new[] { home + "/.wine/VST3" } },
                    "getPluginDiagnostics" => new { type = "pluginDiagnostics", scans = new[] { new { path = home + "/.vst3/Missing.vst3", error = "module not found" } } },
                    _ => new { type = "diagnostics", blocks = new { } }
                };
                await SocketTestServer.Send(socket, reply, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string archive = await Diagnostics.CollectAsync(client);
        try
        {
            using var file = File.OpenRead(archive);
            using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
            using var tar = new System.Formats.Tar.TarReader(gzip);
            var entries = new Dictionary<string, string>();
            while (tar.GetNextEntry() is { } entry)
                if (entry.DataStream is { } data)
                    entries[entry.Name.TrimStart('.', '/')] = new StreamReader(data).ReadToEnd();
            Assert.Contains("plugins.json", entries.Keys);
            Assert.Contains("plugin-setup.json", entries.Keys);
            Assert.Contains("plugin-discovery.json", entries.Keys);
            Assert.Contains("Windows EQ", entries["plugins.json"]);
            Assert.Contains("openxlr", entries["plugin-setup.json"]);
            Assert.Contains("module not found", entries["plugin-discovery.json"]);
            foreach (string name in new[] { "plugins.json", "plugin-setup.json", "plugin-discovery.json" })
            {
                Assert.DoesNotContain(home, entries[name]);
                Assert.Contains("<redacted>", entries[name]);
                using var json = System.Text.Json.JsonDocument.Parse(entries[name]);
            }
            Assert.Contains("plugin", entries["PRIVACY.txt"], StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(archive); }
    }

    [Fact]
    public void Redact_RemovesCommonIdentityAndSerialFields()
    {
        string input = $$"""
            home={{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}}
            host={{Environment.MachineName}}
            {"device.serial":"ABC123","object.serial":42,"application.process.id":9001}
            """;

        string redacted = Diagnostics.Redact(input);

        Assert.DoesNotContain("ABC123", redacted);
        Assert.DoesNotContain("9001", redacted);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), redacted);
        Assert.Contains("<redacted>", redacted);
    }

    [Fact]
    public void RedactHex_MasksSerialsEncodedInsideVendorBlocks()
    {
        const string serial = "A8A9A40410KH90";
        string hex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(serial));
        string block = "0104000009020001" + hex + "8403";
        string json = "{\"blocks\":{\"devinfo\":\"" + block + "\"}}";

        string redacted = Diagnostics.RedactHex(json, [serial, "short"]);

        Assert.DoesNotContain(hex, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(serial, redacted);
        Assert.Contains("0104000009020001" + string.Concat(Enumerable.Repeat("3F", serial.Length)) + "8403", redacted);
        Assert.Equal(json.Length, redacted.Length);
    }

    [Fact]
    public void Redact_StripsUsbSerialsInsidePipeWireNodeNames()
    {
        const string serial = "AAY4I55111P6X2";
        string input = $"alsa_input.usb-Elgato_Elgato_Wave_XLR_MK.2_{serial}-00.analog-stereo";

        string redacted = Diagnostics.Redact(input, [serial]);

        Assert.DoesNotContain(serial, redacted);
        Assert.Contains("alsa_input.usb-Elgato_Elgato_Wave_XLR_MK.2_<redacted>-00.analog-stereo", redacted);
    }

    [Fact]
    public void Redact_LeavesUnrelatedTokensAloneForShortSecrets()
    {
        string redacted = Diagnostics.Redact("\"clock.max-quantum\": 8192", ["/home/max"]);

        Assert.Equal("\"clock.max-quantum\": 8192", redacted);
    }

    [Fact]
    public void Redact_DoesNotTouchDigitsInsideLargerNumbers()
    {
        // A numeric USB serial must not be found inside int.MaxValue in a graph dump.
        string json = "{ \"max\": 2147483647, \"node.name\": \"alsa_card.usb-Foo_147483647-00\" }";

        string redacted = Diagnostics.Redact(json, ["147483647"]);

        Assert.Contains("\"max\": 2147483647", redacted);
        Assert.Contains("alsa_card.usb-Foo_<redacted>-00", redacted);
    }
}
