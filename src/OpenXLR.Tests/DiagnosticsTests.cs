using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class DiagnosticsTests
{
    [Fact]
    public async Task ArchiveIsNamedByTimestampAndPrivate()
    {
        string root = Directory.CreateTempSubdirectory("openxlr-diag-archive-").FullName;
        try
        {
            string work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            string output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
            File.WriteAllText(Path.Combine(work, "meta.txt"), "diagnostic content");
            string archive = await Diagnostics.WriteArchiveAsync(work, output, "20261005-120000");
            Assert.Equal(Path.Combine(output, "openxlr-diagnostics-20261005-120000.tar.gz"), archive);
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(archive));
            using (var file = File.OpenRead(archive))
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
            using (var tar = new System.Formats.Tar.TarReader(gzip))
            {
                var entry = tar.GetNextEntry();
                Assert.NotNull(entry);
                Assert.Equal("meta.txt", entry.Name.TrimStart('.', '/'));
                Assert.Equal("diagnostic content", new StreamReader(entry.DataStream!).ReadToEnd());
                Assert.Null(tar.GetNextEntry());
            }

            // A second collection in the same second is refused and leaves the first archive alone.
            long length = new FileInfo(archive).Length;
            await Assert.ThrowsAsync<IOException>(() => Diagnostics.WriteArchiveAsync(work, output, "20261005-120000"));
            Assert.Equal(length, new FileInfo(archive).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedArchiveLeavesNoPartialOutput()
    {
        string root = Directory.CreateTempSubdirectory("openxlr-diag-failed-").FullName;
        try
        {
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                Diagnostics.WriteArchiveAsync(Path.Combine(root, "missing"), root, "20261005-120000"));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ArchiveRecordsWineTraceEnabledThroughTheDaemonCommand()
    {
        await using var fixture = new PluginWineTraceFixture();
        await using var server = await fixture.StartServerAsync();
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var setup = await client.SetPluginWineTraceAsync(true, TimeSpan.FromSeconds(30));
        Assert.True(setup!["wineTrace"]!.GetValue<bool>());
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
            using var discovery = System.Text.Json.JsonDocument.Parse(entries["plugin-discovery.json"]);
            var environment = discovery.RootElement.GetProperty("discovery").GetProperty("hostEnvironment");
            Assert.True(environment.GetProperty("wineTrace").GetBoolean());
            Assert.Equal("+seh,+unwind,+loaddll", environment.GetProperty("scannerWineDebug").GetString());
            using var savedSetup = System.Text.Json.JsonDocument.Parse(entries["plugin-setup.json"]);
            Assert.True(savedSetup.RootElement.GetProperty("wineTrace").GetBoolean());
            setup = await client.SetPluginWineTraceAsync(false, TimeSpan.FromSeconds(30));
            Assert.False(setup!["wineTrace"]!.GetValue<bool>());
        }
        finally { File.Delete(archive); }
    }

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
                    "getPluginSetup" => new { type = "pluginSetup", bridgeProvider = "openxlr", wineVersion = "wine-11.0", windowsDirectories = new[] { home + "/.wine/VST3" }, memoryLockLimitBytes = 8388608L, memoryLockHardLimitBytes = -1L },
                    "getPluginDiagnostics" => new
                    {
                        type = "pluginDiagnostics",
                        discovery = new
                        {
                            memoryLockLimitBytes = 8388608L, memoryLockHardLimitBytes = -1L,
                            hostEnvironment = new
                            {
                                cleanLaunch = true,
                                wineRunner = home + "/runners/wine",
                                wineLoader = home + "/runners/wine",
                                loaderEnvironment = new Dictionary<string, string?> { ["LD_LIBRARY_PATH"] = null, ["LD_PRELOAD"] = null, ["LD_AUDIT"] = null },
                                removedLoaderEnvironment = new Dictionary<string, string>
                                {
                                    ["LD_LIBRARY_PATH"] = home + "/cuda:/usr/lib",
                                    ["LD_PRELOAD"] = home + "/overlays/capture.so",
                                    ["LD_AUDIT"] = home + "/audit.so",
                                },
                            },
                            skippedFailedCount = 1,
                            skippedFailedBundles = new[] { new { path = home + "/.vst3/Missing.vst3", reason = "timed out", failedAt = "2026-09-17T10:00:00Z" } },
                            scans = new[] { new { path = home + "/.vst3/Missing.vst3", error = "module not found" } },
                        },
                    },
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
            using var discovery = System.Text.Json.JsonDocument.Parse(entries["plugin-discovery.json"]);
            var discoveryData = discovery.RootElement.GetProperty("discovery");
            Assert.Equal(-1, discoveryData.GetProperty("memoryLockHardLimitBytes").GetInt64());
            Assert.Equal(8388608, discoveryData.GetProperty("memoryLockLimitBytes").GetInt64());
            var environment = discoveryData.GetProperty("hostEnvironment");
            Assert.True(environment.GetProperty("cleanLaunch").GetBoolean());
            Assert.Equal("<redacted>/runners/wine", environment.GetProperty("wineRunner").GetString());
            Assert.Equal("<redacted>/runners/wine", environment.GetProperty("wineLoader").GetString());
            foreach (var value in environment.GetProperty("removedLoaderEnvironment").EnumerateObject())
                Assert.StartsWith("<redacted>/", value.Value.GetString());
            Assert.Equal("<redacted>/.vst3/Missing.vst3", discoveryData.GetProperty("skippedFailedBundles")[0].GetProperty("path").GetString());
            foreach (string name in new[] { "plugins.json", "plugin-setup.json", "plugin-discovery.json" })
            {
                Assert.DoesNotContain(home, entries[name]);
                Assert.Contains("<redacted>", entries[name]);
                using var json = System.Text.Json.JsonDocument.Parse(entries[name]);
            }
            Assert.Contains("plugin", entries["PRIVACY.txt"], StringComparison.OrdinalIgnoreCase);
            // The archive carries the saved scan logs and always says what it
            // found there, even when a daemon this old names no directory.
            Assert.Contains("plugin-scan-logs/index.txt", entries.Keys);
            Assert.Contains("The daemon did not say where it keeps them", entries["plugin-scan-logs/index.txt"]);
        }
        finally { File.Delete(archive); }
    }

    [Fact]
    public async Task SavedScanLogsAreCollectedRedactedAndCorrelatedWithoutFollowingLinks()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string marker = "OPENXLR-SAVED-LOG-MARKER";
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string root = Directory.CreateTempSubdirectory("scan-log-collect-").FullName;
        string logs = Path.Combine(root, "plugin-scan-logs");
        string output = Path.Combine(root, "archive");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(output);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(logs, "vst3-aaaabbbbccccdddd.log"),
                $"bundle: {home}/.vst3/Kotelnikov.vst3\ntoken: hunter2-not-a-real-token\n{marker}\n");
            await File.WriteAllTextAsync(Path.Combine(root, "outside.txt"), "SECRET-OUTSIDE-THE-DIRECTORY");
            File.CreateSymbolicLink(Path.Combine(logs, "vst3-1111222233334444.log"), Path.Combine(root, "outside.txt"));
            await File.WriteAllTextAsync(Path.Combine(logs, "bad name.log"), "SHOULD-NOT-APPEAR");
            await File.WriteAllTextAsync(Path.Combine(logs, "notes.txt"), "SHOULD-NOT-APPEAR-EITHER");
            string[] before = [.. Directory.GetFileSystemEntries(logs).Order(StringComparer.Ordinal)];

            await using var server = await SocketTestServer.Start(async (socket, stop) =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var command = await SocketTestServer.Receive(socket, stop);
                    string cmd = command["cmd"]!.GetValue<string>();
                    if (cmd == "auth") continue;
                    Assert.DoesNotContain(cmd, new[] { "rescanPlugins", "syncWindowsPlugins", "setInserts" });
                    object reply = cmd == "getPluginDiagnostics"
                        ? new
                        {
                            type = "pluginDiagnostics",
                            discovery = new
                            {
                                scanLogs = new { directory = logs, maxFiles = 24 },
                                scans = new[]
                                {
                                    new
                                    {
                                        kind = "vst3",
                                        entries = new[]
                                        {
                                            new { path = home + "/.vst3/Kotelnikov.vst3", outcome = "timeout", logId = "vst3-aaaabbbbccccdddd" },
                                            new { path = "/opt/vst3/Gone.vst3", outcome = "timeout", logId = "vst3-9999888877776666" },
                                        },
                                    },
                                },
                            },
                        }
                        : new { type = cmd == "listPlugins" ? "plugins" : "pluginSetup" };
                    await SocketTestServer.Send(socket, reply, stop);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
                }
            });
            await using var client = new DaemonClient(server.Url);
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
            client.Start();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Diagnostics.WritePluginDataAsync(client, output, TimeSpan.FromSeconds(5));

            string collected = Path.Combine(output, "plugin-scan-logs");
            string kept = await File.ReadAllTextAsync(Path.Combine(collected, "vst3-aaaabbbbccccdddd.log"));
            Assert.Contains(marker, kept);
            Assert.DoesNotContain(home, kept);
            Assert.Contains("bundle: <redacted>/.vst3/Kotelnikov.vst3", kept);
            Assert.DoesNotContain("hunter2", kept);
            Assert.Contains("token: <redacted>", kept);

            // A link is never followed, and a name the store could not have
            // written is never copied, whatever it points at.
            Assert.Equal(["index.txt", "vst3-aaaabbbbccccdddd.log"],
                Directory.GetFiles(collected).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            foreach (string file in Directory.GetFiles(collected))
            {
                string text = await File.ReadAllTextAsync(file);
                Assert.DoesNotContain("SECRET-OUTSIDE-THE-DIRECTORY", text);
                Assert.DoesNotContain("SHOULD-NOT-APPEAR", text);
            }

            string index = await File.ReadAllTextAsync(Path.Combine(collected, "index.txt"));
            Assert.Contains("vst3-1111222233334444.log: a link, not a file of its own", index);
            Assert.Contains("a name this directory should not hold (bad name.log)", index);
            Assert.Contains("vst3-aaaabbbbccccdddd.log: ", index);
            Assert.Contains("vst3-9999888877776666.log: named by a scan entry but not collected", index);
            Assert.Contains("No plugin, scanner,", index);

            // Collecting reads: the daemon's directory comes out as it went in.
            Assert.Equal(before, Directory.GetFileSystemEntries(logs).Order(StringComparer.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ScanLogCollectionSaysSoWhenThereIsNothingToCollect()
    {
        string root = Directory.CreateTempSubdirectory("scan-log-empty-").FullName;
        string output = Path.Combine(root, "archive");
        Directory.CreateDirectory(output);
        string index = Path.Combine(output, "plugin-scan-logs", "index.txt");
        try
        {
            await Diagnostics.WriteScanLogsAsync(Path.Combine(root, "never-written", "plugin-scan-logs"), null, output, []);
            Assert.Contains("The directory does not exist", await File.ReadAllTextAsync(index));

            await Diagnostics.WriteScanLogsAsync(null, null, output, []);
            Assert.Contains("The daemon did not say where it keeps them", await File.ReadAllTextAsync(index));

            // Only the daemon's own retention directory is ever read.
            await Diagnostics.WriteScanLogsAsync(root, null, output, []);
            Assert.Contains("named a directory this does not collect from", await File.ReadAllTextAsync(index));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("token: abc123", "token: <redacted>")]
    [InlineData("API_KEY=\"sk-live-9\"", "API_KEY=<redacted>")]
    [InlineData("Authorization: Bearer eyJhbGciOi", "Authorization: <redacted>")]
    [InlineData("password = swordfish", "password = <redacted>")]
    [InlineData("no credentials here", "no credentials here")]
    public void RedactSecretValues_MasksCredentialShapedFields(string input, string expected)
        => Assert.Equal(expected, Diagnostics.RedactSecretValues(input));

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
