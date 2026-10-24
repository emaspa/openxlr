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
            string original = $"bundle: {home}/.vst3/Kotelnikov.vst3\ntoken: hunter2-not-a-real-token\n" +
                "{\"API_KEY\":\"quoted-test-credential\"}\n" + marker + "\n";
            await File.WriteAllTextAsync(Path.Combine(logs, "vst3-aaaabbbbccccdddd.log"),
                original);
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
            Assert.DoesNotContain("quoted-test-credential", kept);
            Assert.Contains("token: <redacted>", kept);
            Assert.Contains("\"API_KEY\":<redacted>", kept);
            Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(logs, "vst3-aaaabbbbccccdddd.log")));

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
    [InlineData("{\"token\":\"abc123\",\"mode\":\"ok\"}", "{\"token\":<redacted>,\"mode\":\"ok\"}")]
    [InlineData("password=\"first\\\" second\" mode=ok", "password=<redacted> mode=ok")]
    [InlineData("'secret': 'first\\' second' mode=ok", "'secret': <redacted> mode=ok")]
    [InlineData("password=\"first second\\", "password=<redacted>")]
    [InlineData("password=first\\ second mode=ok", "password=<redacted> mode=ok")]
    public void RedactSecretValues_MasksCredentialShapedFields(string input, string expected)
        => Assert.Equal(expected, Diagnostics.RedactSecretValues(input));

    [Fact]
    public void RedactSecretValues_DoesNotLeaveTheTailOfAnOversizedCredential()
    {
        string redacted = Diagnostics.RedactSecretValues("token=" + new string('x', 8192) + "\nmode=ok");
        Assert.True(redacted == "token=<redacted>\nmode=ok", "The entire credential must be removed, including bytes after the old 4096-character limit.");
    }

    [Fact]
    public void RedactSecretValues_ProtocolFieldsDoNotDependOnTheDesktopCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("API_KEY=<redacted>\nAUTHORIZATION: <redacted>",
                Diagnostics.RedactSecretValues("API_KEY=private-key\nAUTHORIZATION: BASIC private-credential"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void RedactSecretValues_TimeoutOmitsTheWholeLogInsteadOfPublishingCredentials()
    {
        string input = "token" + new string(' ', 4 * 1024 * 1024) + ": private-test-credential";
        string redacted = Diagnostics.RedactSecretValues(input, TimeSpan.FromMilliseconds(1));
        Assert.False(ReferenceEquals(input, redacted), "The timeout returned the original text with its private credential intact.");
        Assert.True(redacted == "[diagnostic text omitted: credential redaction timed out]",
            "The timeout must omit the diagnostic text instead of keeping an unredacted or partially redacted log.");
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
    public void ArchivedStateMasksBrowserTabTitlesAndUnroutedOrigins()
    {
        string state = """
            {"type":"state","mixer":{"channels":[{"id":"music","name":"Música"}],"browserTabs":[
              {"session":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","id":1,"site":"https://music.example","title":"PRIVATE_SONG","channelId":"music","remember":true,"capturing":true},
              {"session":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","id":2,"site":"https://private-clinic.example","title":"PRIVATE_APPOINTMENT","channelId":null,"remember":false,"capturing":false,"error":"Activate tab audio."}]}}
            """;

        string archived = Diagnostics.StateForArchive(state);

        Assert.DoesNotContain("PRIVATE", archived, StringComparison.Ordinal);
        Assert.DoesNotContain("private-clinic", archived, StringComparison.Ordinal);
        var tabs = System.Text.Json.Nodes.JsonNode.Parse(archived)!["mixer"]!["browserTabs"]!.AsArray();
        Assert.Equal("https://music.example", tabs[0]!["site"]!.GetValue<string>());
        Assert.Equal("music", tabs[0]!["channelId"]!.GetValue<string>());
        Assert.All(tabs, tab => Assert.Equal("<redacted>", tab!["title"]!.GetValue<string>()));
        Assert.Equal("<redacted>", tabs[1]!["site"]!.GetValue<string>());
        Assert.Equal("Activate tab audio.", tabs[1]!["error"]!.GetValue<string>());
        Assert.Contains("Música", archived, StringComparison.Ordinal);
        // A state without tabs, or one that does not parse, is kept as received.
        const string plain = """{"type":"state","mixer":{"browserTabs":[]}}""";
        Assert.Equal(plain, Diagnostics.StateForArchive(plain));
        Assert.Equal("not json", Diagnostics.StateForArchive("not json"));
        Assert.StartsWith("no state received", Diagnostics.StateForArchive(null), StringComparison.Ordinal);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheLoginNameAndHostNamesAppearNowhereInTheArchive(bool commandWarning)
    {
        if (!OperatingSystem.IsLinux()) return;
        // Neither is this machine's: the fake helpers below print them, the
        // exporter is told the first is the login name, and the second is a
        // host name it was never told about.
        const string login = "kestrel7";
        const string otherHost = "studio-pc";
        string machine = Environment.MachineName;
        string root = Directory.CreateTempSubdirectory("diag-login-").FullName;
        string bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        string config = Directory.CreateDirectory(Path.Combine(root, "config")).FullName;
        string output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            ExecutableScript.Write(Path.Combine(bin, "uname"), $"echo 'Linux {machine} 6.18.0 #1 SMP x86_64 GNU/Linux'\n");
            ExecutableScript.Write(Path.Combine(bin, "lsusb"), "echo 'Bus 001 Device 002: ID 0fd9:0070 Elgato Systems GmbH Wave:3'\n");
            ExecutableScript.Write(Path.Combine(bin, "pactl"), $$"""
                if [ "$1" = info ]; then
                  printf '%s\n' 'Server String: /run/user/1000/pulse/native' 'User Name: {{login}}' \
                    'Host Name: {{otherHost}}' 'Server Name: PulseAudio (on PipeWire 1.6.9)' 'Default Sink: OpenXLR_mix_monitor'
                elif [ "$1" = --format=json ]; then
                  {{(commandWarning ? "if [ \"$3\" = source-outputs ]; then printf '%s\\n' '{\"note\":\"/home/" + login + "/audio\"'; exit 7; fi" : "")}}
                  printf '%s\n' '[{"index":47,"sample_specification":"float32le 2ch 48000Hz","buffer_latency_usec":1024,"properties":{"application.process.user":"{{login}}","application.process.host":"{{otherHost}}","application.process.id":123456} }]'
                else
                  printf '%s\n' '1	OpenXLR_mix_monitor	PipeWire	float32le 2ch 48000Hz	RUNNING'
                fi
                """);
            ExecutableScript.Write(Path.Combine(bin, "wpctl"), $$"""
                printf '%s\n' "PipeWire 'pipewire-0' [1.6.9, {{login}}@{{otherHost}}, cookie:1920751475]" \
                  ' └─ Clients:' \
                  '        32. WirePlumber                         [1.6.9, {{login}}@{{machine}}, pid:1585]' \
                  '        43. pipewire                            [1.6.9, {{login}}@{{otherHost}}, pid:1655]'
                """);
            string warning = commandWarning ? $"printf '%s\\n' 'warning for {login}@{machine}: optional metadata unavailable' >&2\n" : "";
            ExecutableScript.Write(Path.Combine(bin, "pw-dump"), warning + $$"""
                cat <<'JSON'
                [{"id":0,"type":"PipeWire:Interface:Core","info":{"user-name":"{{login}}","host-name":"{{otherHost}}","name":"pipewire-0",
                  "props":{"core.name":"pipewire-{{login}}-1655","default.clock.max-quantum":2048} } },
                 {"id":32,"type":"PipeWire:Interface:Client","info":{"props":{"application.process.user":"{{login}}",
                  "application.process.host":"{{otherHost}}","core.name":"pipewire-{{login}}-1585","application.name":"WirePlumber"} } },
                 {"id":60,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_mix_monitor",
                  "media.name":"/run/media/{{login}}/Samples/take.wav","clock.max-quantum":8192} } }]
                JSON
                {{(commandWarning ? "printf '%s\\n' '[{\"id\":62,\"type\":\"PipeWire:Interface:Node\",\"info\":{\"state\":\"error\",\"error\":\"fixture graph error\",\"props\":{\"node.name\":\"OpenXLR_fixture\",\"media.class\":\"Audio/Source\"} } }]'" : "")}}
                """);
            ExecutableScript.Write(Path.Combine(bin, "journalctl"), $$"""
                case "$*" in
                  '--user -u openxlr-daemon --since 2 hours ago --no-pager')
                    echo 'Okt 18 11:23:43 {{machine}} openxlr-daemon[2219]: loaded /mnt/{{login}}/lv2/eq.lv2 for {{login}}@{{machine}}'
                    ;;
                  '--user -u pipewire.service -u pipewire-pulse.service -u wireplumber.service --since 2 hours ago --no-pager')
                    echo 'Okt 18 11:23:44 {{machine}} pipewire[2220]: xrun for {{login}}@{{machine}}'
                    {{(commandWarning ? $"echo 'journal warning for {login}@{machine}: access incomplete' >&2\nexit 1" : "")}}
                    ;;
                  *) echo 'unexpected journal request' >&2; exit 91 ;;
                esac
                """);
            ExecutableScript.Write(Path.Combine(bin, "pw-top"), "printf '%s\\n' 'S ID QUANT RATE ERR NAME' 'R 60 1024 48000 2 OpenXLR_mix_monitor'\n");
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + path);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Directory.CreateDirectory(UiSettings.ConfigDir);
            File.WriteAllText(Path.Combine(UiSettings.ConfigDir, "ui.json"),
                "{\"examplePath\":\"/home/" + login + "/capture\",\"numericValue\":1024}");

            await using var server = await SocketTestServer.Start(async (socket, stop) =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var command = await SocketTestServer.Receive(socket, stop);
                    string cmd = command["cmd"]!.GetValue<string>();
                    if (cmd == "auth")
                    {
                        await SocketTestServer.Send(socket, new { type = "state", note = $"/home/{login}/Music" }, stop);
                        continue;
                    }
                    object reply = cmd switch
                    {
                        "listPlugins" => new { type = "plugins", plugins = Array.Empty<object>() },
                        "getPluginSetup" => new { type = "pluginSetup", windowsDirectories = new[] { $"/run/media/{login}/VST3" } },
                        "getPluginDiagnostics" => new { type = "pluginDiagnostics", discovery = new { scans = Array.Empty<object>() } },
                        _ => new { type = "diagnostics", blocks = new { } },
                    };
                    await SocketTestServer.Send(socket, reply, stop);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
                }
            });
            await using var client = new DaemonClient(server.Url);
            var state = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.StateReceived += _ => state.TrySetResult();
            client.Start();
            await state.Task.WaitAsync(TimeSpan.FromSeconds(5));

            string archive = await Diagnostics.CollectAsync(client, login, output);

            var entries = new Dictionary<string, string>();
            await using (var file = File.OpenRead(archive))
            await using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
            using (var tar = new System.Formats.Tar.TarReader(gzip))
            {
                while (tar.GetNextEntry() is { } entry)
                {
                    // The account that collected the archive is not in its headers.
                    Assert.Equal(0, entry.Uid);
                    Assert.Equal(0, entry.Gid);
                    var posix = Assert.IsAssignableFrom<System.Formats.Tar.PosixTarEntry>(entry);
                    Assert.Equal("", posix.UserName);
                    Assert.Equal("", posix.GroupName);
                    if (entry is System.Formats.Tar.PaxTarEntry pax)
                        Assert.DoesNotContain(pax.ExtendedAttributes, a => a.Key is "uname" or "gname" or "uid" or "gid" && a.Value is not ("" or "0"));
                    if (entry.DataStream is { } data) entries[entry.Name] = new StreamReader(data).ReadToEnd();
                }
            }
            Assert.Contains("plugin-scan-logs/index.txt", entries.Keys);
            foreach ((string name, string text) in entries)
            {
                Assert.False(text.Contains(login, StringComparison.Ordinal), $"{name} still holds the login name");
                Assert.False(text.Contains(otherHost, StringComparison.Ordinal), $"{name} still holds a host name");
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, "(?<![A-Za-z0-9])" + System.Text.RegularExpressions.Regex.Escape(machine) + "(?![A-Za-z0-9])"),
                    $"{name} still holds this machine's name");
            }

            // Only the names went; everything around them is still there to read.
            Assert.Contains("User Name: <redacted>", entries["pactl-info.txt"]);
            Assert.Contains("Server Name: PulseAudio (on PipeWire 1.6.9)", entries["pactl-info.txt"]);
            Assert.Contains("WirePlumber                         [1.6.9, <redacted>@<redacted>, pid:1585]", entries["wpctl-status.txt"]);
            Assert.Contains("loaded /mnt/<redacted>/lv2/eq.lv2 for <redacted>@<redacted>", entries["journal.txt"]);
            Assert.Contains("pipewire[2220]: xrun for <redacted>@<redacted>", entries["audio-journal.txt"]);
            Assert.DoesNotContain("loaded /mnt/", entries["audio-journal.txt"]);
            if (commandWarning)
                Assert.Contains("journal warning for <redacted>@<redacted>: access incomplete", entries["audio-journal.txt"]);
            Assert.Contains("/run/media/<redacted>/VST3", entries["plugin-setup.json"]);
            using var graph = System.Text.Json.JsonDocument.Parse(entries["pw-dump.json"]);
            Assert.Equal("pipewire-<redacted>-1655", graph.RootElement[0].GetProperty("info").GetProperty("props").GetProperty("core.name").GetString());
            Assert.Equal("<redacted>", graph.RootElement[0].GetProperty("info").GetProperty("user-name").GetString());
            Assert.Equal(8192, graph.RootElement[2].GetProperty("info").GetProperty("props").GetProperty("clock.max-quantum").GetInt32());
            if (commandWarning)
                Assert.Contains("warning for <redacted>@<redacted>: optional metadata unavailable", entries["pw-dump-errors.txt"]);
            else
                Assert.DoesNotContain("pw-dump-errors.txt", entries.Keys);
            Assert.DoesNotContain("audio-graph.json", entries.Keys);
            using var preferences = System.Text.Json.JsonDocument.Parse(entries["ui.json"]);
            Assert.Equal(1024, preferences.RootElement.GetProperty("numericValue").GetInt32());
            using var streams = System.Text.Json.JsonDocument.Parse(entries["sink-inputs.json"]);
            Assert.Equal(1024, streams.RootElement[0].GetProperty("buffer_latency_usec").GetInt32());
            Assert.Equal("<redacted>", streams.RootElement[0].GetProperty("properties").GetProperty("application.process.id").GetString());
            Assert.Contains("48000 2 OpenXLR_mix_monitor", entries["pw-top.txt"]);
            Assert.DoesNotContain("collection.json", entries.Keys);
            if (commandWarning)
            {
                Assert.Contains("exited with status 7", entries["source-outputs-errors.txt"]);
                Assert.Contains("did not return complete valid JSON", entries["source-outputs-errors.txt"]);
            }
            Assert.Contains("login name", entries["PRIVACY.txt"]);
            Assert.Contains("audio-server", entries["PRIVACY.txt"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("max", "\"default.clock.max-quantum\": 2048, \"cpu.max-align\": 32", "\"default.clock.max-quantum\": 2048, \"cpu.max-align\": 32")]
    [InlineData("max", "the max@desk account", "the <redacted>@desk account")]
    [InlineData("max", "timed out after max 5 seconds; /usr/share/max/x; data.max@x", "timed out after max 5 seconds; /usr/share/max/x; data.max@x")]
    [InlineData("a", "data@host /home/ab/x pipewire-a-12 pipewire-ab-12 /media/a/disk", "data@host /home/ab/x pipewire-<redacted>-12 pipewire-ab-12 /media/<redacted>/disk")]
    [InlineData("audio", "\"media.class\": \"Audio/Sink\", \"node.name\": \"audio-out\" /mnt/audio-lib/x", "\"media.class\": \"Audio/Sink\", \"node.name\": \"audio-out\" /mnt/audio-lib/x")]
    [InlineData("j.doe", "j.doe@host jXdoe@host /home/j.doe/x", "<redacted>@host jXdoe@host /home/<redacted>/x")]
    public void Redact_MasksTheLoginNameOnlyWhereItStandsAsOne(string login, string input, string expected)
        => Assert.Equal(expected, Diagnostics.Redact(input, [], login));
}
