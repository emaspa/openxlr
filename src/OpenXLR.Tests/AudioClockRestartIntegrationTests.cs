using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    private const string ClockMic = "alsa_input.usb-Test_Clock_Mic_0001-00.mono-fallback";
    private const string ClockOut = "alsa_output.usb-Test_Clock_Card_0002-00.analog-stereo";

    /// <summary>
    /// The audio clock choice end to end on the private servers, with the
    /// daemon's service and hub. A capture input on a microphone (2100)
    /// and an output on a card (2109), each with a clock of its own, a
    /// monitor mix to the output and an application on a channel: the card
    /// drives. setAudioClock writes the rule for the microphone and the
    /// state says it waits for a restart; restartAudioServices runs
    /// systemctl, here a script that restarts the harness's WirePlumber and,
    /// as WirePlumber's own device nodes do, takes the two devices away and
    /// brings them back, the microphone with the rule's priority. The
    /// microphone then drives, nothing is pending, and without a restart of
    /// the daemon its graph is back. Every link goes with WirePlumber, the
    /// ones it made and the ones to the devices; the new WirePlumber links
    /// what names a target again, and the sweep makes the routes to the
    /// devices, as after a replug. The capture input's links, the monitor
    /// route into the output, the application on its channel, the default
    /// output and the number of links are as before. Automatic deletes the
    /// rule and waits for the next restart.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task TheChosenClockDrivesAfterWirePlumberRestartsAndTheGraphComesBack()
    {
        Assert.StartsWith("openxlr-monitor-test-", Path.GetFileName(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!));
        string[] pidfiles = (Environment.GetEnvironmentVariable("OPENXLR_TEST_WIREPLUMBER_PIDS") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(pidfiles);
        string directory = Directory.CreateTempSubdirectory("openxlr-clock-restart-").FullName;
        string rule = Path.Combine(directory, "wireplumber", "wireplumber.conf.d", AudioClockRule.FileName);
        string calls = Path.Combine(directory, "systemctl.log");
        string trace = Path.Combine(directory, "systemctl-trace.log");
        string systemctl = ExecutableScript.Write(Path.Combine(directory, "systemctl"), $$"""
            #!/bin/sh
            exec python3 - "$@" <<'PY'
            import os, pathlib, signal, subprocess, sys, time
            args = sys.argv[1:]
            with open({{JsonSerializer.Serialize(calls)}}, 'a') as log:
                log.write(' '.join(args) + '\n')
            if args != {{JsonSerializer.Serialize(AudioClockRule.RestartArguments)}}:
                sys.exit(2)
            started = time.monotonic()
            def note(what):
                with open({{JsonSerializer.Serialize(trace)}}, 'a') as log:
                    log.write(f'{time.monotonic() - started:6.2f} {what}\n')
            def run(*command):
                note('run ' + ' '.join(command))
                try:
                    done = subprocess.run(command, capture_output=True, text=True, timeout=8)
                except subprocess.TimeoutExpired:
                    note('timed out: ' + ' '.join(command))
                    sys.exit(3)
                note(f'exit {done.returncode} {done.stderr.strip()[:200]}')
                return done
            # WirePlumber's device nodes end with it. They go first: the CI
            # runners' pipewire-pulse (1.6.2, WirePlumber 0.5.13) does not
            # answer pactl from the moment WirePlumber is stopped until the
            # new one has connected.
            for line in run('pactl', 'list', 'modules', 'short').stdout.splitlines():
                if 'Test_Clock_' in line:
                    run('pactl', 'unload-module', line.split()[0])
            pidfiles = [pathlib.Path(p) for p in {{JsonSerializer.Serialize(pidfiles)}}]
            old = [p.read_text() for p in pidfiles]
            for pid in old:
                note('SIGTERM ' + pid)
                os.kill(int(pid), signal.SIGTERM)
            deadline = time.monotonic() + 10
            while any(p.read_text() == o for p, o in zip(pidfiles, old)):
                if time.monotonic() > deadline:
                    note('no new WirePlumber')
                    sys.exit(1)
                time.sleep(0.05)
            note('new WirePlumber ' + ' '.join(p.read_text() for p in pidfiles))
            deadline = time.monotonic() + 15
            while True:
                try:
                    if subprocess.run(['pactl', 'info'], capture_output=True, timeout=2).returncode == 0:
                        break
                except subprocess.TimeoutExpired:
                    pass
                if time.monotonic() > deadline:
                    note('pipewire-pulse does not answer')
                    sys.exit(5)
                time.sleep(0.1)
            note('pipewire-pulse answers')
            # ... and the new one makes them again, with the rule it reads.
            rule = pathlib.Path({{JsonSerializer.Serialize(rule)}})
            text = rule.read_text() if rule.exists() else ''
            for args in {{JsonSerializer.Serialize(new[] { DeviceArguments(ClockMic, 2100), DeviceArguments(ClockOut, 2109) })}}:
                name = args[0]
                priority = {{AudioClockRule.PriorityDriver}} if '"' + name + '"' in text else int(args[1])
                if run('pactl', 'load-module', *args[2:-1], args[-1].replace('PRIORITY', str(priority))).returncode != 0:
                    sys.exit(4)
            note('done')
            PY
            """);
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? oldOutput = Environment.GetEnvironmentVariable("OPENXLR_MONITOR_OUTPUT");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
        Environment.SetEnvironmentVariable("OPENXLR_MONITOR_OUTPUT", null);
        var pw = new PipeWireAdapter();
        using var stop = new CancellationTokenSource();
        Task<ProcessResult>? play = null;
        try
        {
            LoadDevice(ClockMic, 2100);
            LoadDevice(ClockOut, 2109);
            pw.CreateNullSink("clock_desktop", "Desktop output");
            pw.SetDefaultSink("clock_desktop");
            new DaemonSettings { Submixer = true }.Save();
            Assert.Null(new MixerSettings { UserChannels = [new("music", "Music")], UserMixes = [] }.Save());
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            using var lifetime = new StopLifetime();
            using var service = new MixerService(NullLogger<MixerService>.Instance, config, devices)
            {
                ClockRulePath = rule,
                Systemctl = systemctl,
                ClockChoiceAvailable = true,
            };
            var hub = new WebSocketHub(devices, service, NullLogger<WebSocketHub>.Instance, lifetime);
            await service.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(service.Built);
                await Send(hub, $$"""{"cmd":"createCaptureChannel","name":"Mic","source":"{{ClockMic}}","captureMonoChannel":0}""");
                await Send(hub, $$"""{"cmd":"setMonitorOutputs","devices":["{{ClockOut}}"]}""");
                await Send(hub, """{"cmd":"assignApp","identity":"clock-restart-test","channel":"music","label":"Clock test"}""");
                play = ProcessRunner.RunAsync("python3", ["-c", """
                    import subprocess
                    args = ['--format=f32', '--rate=48000', '--channels=2']
                    if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
                    with open('/dev/zero', 'rb') as source:
                        subprocess.run(['pw-cat', '--playback', *args,
                            '--properties={ application.name = clock-restart-test application.process.binary = clock-restart-test }', '-'], stdin=source, check=True)
                    """], TimeSpan.FromSeconds(120), cancel: stop.Token);

                // The card drives, and the graph is up.
                AudioClockChoice choice = Until(service, s => s.Clock?.Driver?.Name == ClockOut
                    && s.ClockChoice?.Options.Any(o => o.Node == ClockMic) == true && GraphUp(service), "the card to drive a complete graph");
                Assert.Null(choice.Node);
                Assert.False(choice.Pending);
                Assert.Equal("input", choice.Options.Single(o => o.Node == ClockMic).Kind);
                Assert.Equal("output", choice.Options.Single(o => o.Node == ClockOut).Kind);

                // Unknown and malformed nodes are refused, and nothing is written.
                await Refused(hub, """{"cmd":"setAudioClock","value":"alsa_input.usb-Not_Here-00.mono-fallback"}""");
                await Refused(hub, """{"cmd":"setAudioClock","value":"alsa_input.x\" } ] other = { "}""");
                await Refused(hub, """{"cmd":"setAudioClock","value":"OpenXLR_mix_monitor"}""");
                await Refused(hub, """{"cmd":"setAudioClock","value":42}""");
                Assert.False(File.Exists(rule));

                await Send(hub, $$"""{"cmd":"setAudioClock","value":"{{ClockMic}}"}""");
                Assert.Equal(AudioClockRule.Render(ClockMic), File.ReadAllText(rule));
                choice = Assert.IsType<AudioClockChoice>(service.Snapshot()!.ClockChoice);
                Assert.Equal(ClockMic, choice.Node);
                Assert.True(choice.Pending);
                Assert.False(choice.Active);
                Assert.False(File.Exists(calls), "choosing a clock restarted something");

                // Every link in the graph goes with WirePlumber: those it made
                // and those to the devices. All of them come back.
                string[] links = NamedLinks();
                await Send(hub, """{"cmd":"restartAudioServices"}""");
                Assert.Equal(string.Join(' ', AudioClockRule.RestartArguments) + "\n", File.ReadAllText(calls));
                choice = Until(service, s => s.Clock?.Driver?.Name == ClockMic && GraphUp(service) && NamedLinks().SequenceEqual(links),
                    "the microphone to drive a complete graph again", links);
                Assert.False(choice.Pending);
                Assert.True(choice.Active);
                Assert.Equal(AudioClockRule.PriorityDriver, service.Snapshot()!.Clock!.Driver!.PriorityDriver);
                Assert.Contains(service.Snapshot()!.Clock!.Followers, f => f.Name == ClockOut && f.Resampled);
                Assert.Equal("clock_desktop", pw.GetDefaultSink());

                // Automatic deletes the rule; the microphone keeps the number
                // until WirePlumber restarts, so it waits for that.
                await Send(hub, """{"cmd":"setAudioClock","value":null}""");
                Assert.False(File.Exists(rule));
                choice = Assert.IsType<AudioClockChoice>(service.Snapshot()!.ClockChoice);
                Assert.Null(choice.Node);
                Assert.True(choice.Pending);
                await Send(hub, """{"cmd":"restartAudioServices"}""");
                choice = Until(service, s => s.Clock?.Driver?.Name == ClockOut && GraphUp(service) && NamedLinks().SequenceEqual(links), "the card to drive again", links);
                Assert.False(choice.Pending);
            }
            finally { await service.StopAsync(CancellationToken.None); }
        }
        finally
        {
            stop.Cancel();
            if (play is not null) await play;
            foreach (string line in ProcessRunner.Run("pactl", ["list", "modules", "short"]).StdoutText.Split('\n'))
                if (line.Contains("Test_Clock_", StringComparison.Ordinal)) ProcessRunner.Run("pactl", ["unload-module", line.Split('\t')[0]]);
            pw.TearDown();
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
            Environment.SetEnvironmentVariable("OPENXLR_MONITOR_OUTPUT", oldOutput);
            Directory.Delete(directory, true);
        }

        static string[] DeviceArguments(string name, int priority)
        {
            bool input = name.StartsWith("alsa_input.", StringComparison.Ordinal);
            return [name, priority.ToString(System.Globalization.CultureInfo.InvariantCulture), "module-null-sink",
                .. input ? new[] { "media.class=Audio/Source/Virtual", "channels=1", "channel_map=mono" } : ["channels=2", "channel_map=front-left,front-right"],
                "sink_name=" + name,
                $"sink_properties=device.api=test device.id={(input ? 9301 : 9302)} priority.driver=PRIORITY node.description={(input ? "Clock_mic" : "Clock_card")}"];
        }

        static void LoadDevice(string name, int priority)
        {
            string[] args = DeviceArguments(name, priority);
            ProcessResult result = ProcessRunner.Run("pactl", ["load-module", .. args[2..^1], args[^1].Replace("PRIORITY", args[1], StringComparison.Ordinal)]);
            Assert.True(result.Ok, result.Stderr);
        }

        // Each part of the graph that ends at a device: the capture input's
        // feed out of the microphone, the monitor route into the output, and
        // the application on its channel.
        bool GraphUp(MixerService service)
        {
            if (Links(ClockMic, incoming: false) == 0 || IncomingRouteLinks(ClockOut).Length < 2
                || Links("OpenXLR_ch_music", incoming: true) < 2) return false;
            StreamAssignment? stream = service.Snapshot()?.Streams.FirstOrDefault(s => s.Identity == "clock-restart-test");
            return stream?.ChannelId == "music" && pw.StreamSinkName(stream.Serial) == "OpenXLR_ch_music";
        }

        AudioClockChoice Until(MixerService service, Func<MixerState, bool> done, string what, string[]? links = null)
        {
            MixerState? last = null;
            Assert.True(SpinWait.SpinUntil(() => (last = service.Snapshot()) is { } state && done(state), TimeSpan.FromSeconds(30)),
                $"Waited for {what}; driver {last?.Clock?.Driver?.Name ?? "none"}, links from the microphone {Links(ClockMic, incoming: false)}, into the output {IncomingRouteLinks(ClockOut).Length}, into the music channel {Links("OpenXLR_ch_music", incoming: true)}, links missing {string.Join(", ", (links ?? []).Except(NamedLinks()))}, links added {string.Join(", ", NamedLinks().Except(links ?? NamedLinks()))}, application on {(last?.Streams.FirstOrDefault(s => s.Identity == "clock-restart-test") is { } app ? app.ChannelId : "nothing")}");
            return Assert.IsType<AudioClockChoice>(last!.ClockChoice);
        }

        async Task Send(WebSocketHub hub, string command)
        {
            ApiCommandResult result = await hub.ExecuteForApiAsync(command);
            Assert.True(result.Ok, command + ": " + string.Join(", ", result.Messages)
                + (File.Exists(trace) ? "\nsystemctl trace:\n" + File.ReadAllText(trace) : ""));
        }

        static async Task Refused(WebSocketHub hub, string command)
            => Assert.False((await hub.ExecuteForApiAsync(command)).Ok, command + " was accepted");
    }

    /// <summary>
    /// The rule the daemon writes, read by the installed WirePlumber the way
    /// it reads the user's rules: a profile of the private servers that runs
    /// one script, which applies the merged <c>monitor.alsa.rules</c> to the
    /// properties of a few nodes. With the packaged Wave:3 rule beside it,
    /// the chosen node gets 3000 over the packaged 2200, and a node of the
    /// same card on another profile and another device keep their numbers.
    /// WirePlumber 0.4 reads Lua rules instead and is not checked.
    /// </summary>
    [MonitorPipeWireFact]
    public void WirePlumberGivesTheChosenNodeTheRulesPriority()
    {
        if (!ProcessRunner.Run("wireplumber", ["--help"]).StdoutText.Contains("--profile", StringComparison.Ordinal)) return;
        const string chosen = "alsa_input.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.analog-stereo";
        const string other = "alsa_input.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.pro-input-0";
        const string card = "alsa_input.usb-Elgato_Elgato_4K_X_ABCDEF-02.analog-stereo";
        string directory = Directory.CreateTempSubdirectory("openxlr-clock-wireplumber-").FullName;
        try
        {
            string config = Path.Combine(directory, "config"), data = Path.Combine(directory, "data");
            string fragments = Path.Combine(config, "wireplumber", "wireplumber.conf.d");
            AudioClockRule.Save(chosen, Path.Combine(fragments, AudioClockRule.FileName));
            File.Copy(Path.Combine(RepositoryRoot(), "packaging", "53-openxlr-wave3-capture-hold.conf"),
                Path.Combine(fragments, "53-openxlr-wave3-capture-hold.conf"));
            File.WriteAllText(Path.Combine(fragments, "99-clock-check.conf"), """
                wireplumber.profiles = {
                  clockcheck = {
                    inherits = [ base ]
                    metadata.sm-settings = required
                    custom.clockcheck = required
                  }
                }
                wireplumber.components = [
                  { name = clock-check.lua, type = script/lua, provides = custom.clockcheck }
                ]
                """);
            Directory.CreateDirectory(Path.Combine(data, "wireplumber", "scripts"));
            File.WriteAllText(Path.Combine(data, "wireplumber", "scripts", "clock-check.lua"), $$"""
                local rules = Conf.get_section_as_json ("monitor.alsa.rules", Json.Array {})
                for _, name in ipairs({ "{{chosen}}", "{{other}}", "{{card}}" }) do
                  local props = Properties ()
                  props["node.name"] = name
                  props["priority.driver"] = "2109"
                  props["device.profile.name"] = name:match("[^.]+$")
                  props = JsonUtils.match_rules_update_properties (rules, props)
                  Log.warning ("CLOCKCHECK " .. name .. " " .. tostring (props["priority.driver"]))
                end
                """);
            ProcessResult run = ProcessRunner.Run("wireplumber", ["--profile", "clockcheck"], TimeSpan.FromSeconds(4),
                environment: new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = config, ["XDG_DATA_HOME"] = data,
                    ["XDG_STATE_HOME"] = Path.Combine(directory, "state") });
            string log = run.StdoutText + run.Stderr;
            Assert.Contains($"CLOCKCHECK {chosen} {AudioClockRule.PriorityDriver}", log);
            Assert.Contains($"CLOCKCHECK {other} 2109", log);
            Assert.Contains($"CLOCKCHECK {card} 2109", log);

            // Automatic: the packaged rule's number again.
            AudioClockRule.Save(null, Path.Combine(fragments, AudioClockRule.FileName));
            run = ProcessRunner.Run("wireplumber", ["--profile", "clockcheck"], TimeSpan.FromSeconds(4),
                environment: new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = config, ["XDG_DATA_HOME"] = data,
                    ["XDG_STATE_HOME"] = Path.Combine(directory, "state") });
            Assert.Contains($"CLOCKCHECK {chosen} 2200", run.StdoutText + run.Stderr);
        }
        finally { Directory.Delete(directory, true); }

        static string RepositoryRoot()
        {
            string? at = AppContext.BaseDirectory;
            while (at is not null && !File.Exists(Path.Combine(at, "packaging", "53-openxlr-wave3-capture-hold.conf"))) at = Path.GetDirectoryName(at);
            return at ?? throw new DirectoryNotFoundException("the repository's packaging directory");
        }
    }

    /// <summary>Every link in the graph, as "node:port > node:port", in order.</summary>
    private static string[] NamedLinks()
    {
        using JsonDocument graph = PipeWireSnapshot.Parse(ProcessRunner.Run("pw-dump", []).Stdout);
        var nodes = new Dictionary<uint, string>();
        var ports = new Dictionary<uint, string>();
        foreach (JsonElement item in graph.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("info", out JsonElement info) || !info.TryGetProperty("props", out JsonElement props)) continue;
            string type = item.GetProperty("type").GetString() ?? "";
            uint id = item.GetProperty("id").GetUInt32();
            if (type == "PipeWire:Interface:Node" && props.TryGetProperty("node.name", out JsonElement name)) nodes[id] = name.GetString() ?? "";
            if (type == "PipeWire:Interface:Port" && props.TryGetProperty("port.name", out JsonElement port))
                ports[id] = (props.TryGetProperty("node.id", out JsonElement node) ? node.GetUInt32() : 0) + ":" + port.GetString();
        }
        string Port(uint id) => ports.TryGetValue(id, out string? port) && port.Split(':', 2) is [var node, var rest]
            ? nodes.GetValueOrDefault(uint.Parse(node), node) + ":" + rest : id.ToString();
        return [.. graph.RootElement.EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "PipeWire:Interface:Link")
            .Select(item => item.GetProperty("info"))
            .Select(info => Port(info.GetProperty("output-port-id").GetUInt32()) + " > " + Port(info.GetProperty("input-port-id").GetUInt32()))
            .Order(StringComparer.Ordinal)];
    }

}
