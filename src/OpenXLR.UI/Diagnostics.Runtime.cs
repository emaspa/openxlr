using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

public static partial class Diagnostics
{
    private static readonly string[] ClockKeys =
    [
        "clock.rate", "clock.quantum", "clock.min-quantum", "clock.max-quantum",
        "clock.force-rate", "clock.force-quantum",
    ];

    internal sealed record AudioApplication(string? Name, string? Executable, string Direction,
        string? State, string[] ConnectedNodes);

    internal sealed record AudioGraphSummary(bool Available, bool Truncated, string? ServerVersion,
        IReadOnlyDictionary<string, int> Clock, AudioApplication[] Streams);

    private static readonly AudioGraphSummary UnavailableAudioGraph = new(false, false, null, new Dictionary<string, int>(), []);

    private static readonly HashSet<string> PrivateAudioFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "application.process.args", "media.name", "media.title", "media.artist", "media.filename", "media.uri", "Tag",
    };

    /// <summary>Read one snapshot for both reports, keeping duplicate graph properties while redacting each value.</summary>
    internal static (string Text, bool Valid, AudioGraphSummary? Graph) AudioJsonForArchive(
        string output, bool pipeWire, Func<string, string> clean)
    {
        try
        {
            using JsonDocument document = pipeWire
                ? PipeWireSnapshot.Parse(Encoding.UTF8.GetBytes(output))
                : JsonDocument.Parse(output);
            // Graph objects repeat the same field names and property values.
            // Bound the cache so distinct labels cannot grow it with the dump.
            var cleaned = new Dictionary<string, string>(StringComparer.Ordinal);
            string CleanValue(string value)
            {
                if (cleaned.TryGetValue(value, out string? cached)) return cached;
                string result = clean(value);
                if (cleaned.Count < 2048 && value.Length <= 256) cleaned.Add(value, result);
                return result;
            }
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                WriteAudioJson(writer, document.RootElement, CleanValue);
            return (Encoding.UTF8.GetString(buffer.WrittenSpan), true,
                pipeWire ? ReadAudioGraph(document.RootElement) : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // A partial or undecodable string can hide private fields from a text redactor.
            return (JsonOmission("Audio helper output could not be safely decoded."), false, null);
        }
    }

    private static void WriteAudioJson(Utf8JsonWriter writer, JsonElement value, Func<string, string> clean)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    string name = property.Name;
                    writer.WritePropertyName(clean(name));
                    if (PrivateJsonFields.Contains(name) || PrivateAudioFields.Contains(name))
                        writer.WriteStringValue("<redacted>");
                    else if (name.Equals("application.process.binary", StringComparison.OrdinalIgnoreCase)
                             && property.Value.ValueKind == JsonValueKind.String)
                        writer.WriteStringValue(clean(ExecutableName(property.Value.GetString()!)));
                    else WriteAudioJson(writer, property.Value, clean);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray()) WriteAudioJson(writer, item, clean);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(clean(value.GetString()!));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static string ExecutableName(string path) => path[(path.AsSpan().LastIndexOfAny('/', '\\') + 1)..];

    private static async Task WriteSystemInfoAsync(string work, DaemonClient.StateObservation observation,
        DateTimeOffset observedAt, string osRelease, AudioGraphSummary graph, Func<string, string> cleanJson)
    {
        JsonObject? daemon = ParseDiagnosticJson(observation.Json) as JsonObject;
        var tools = await Task.WhenAll(new[] { "pipewire", "wireplumber", "pactl", "alsactl" }.Select(ReadToolVersionAsync));
        var report = new
        {
            collectedAt = observedAt,
            window = new
            {
                version = AppVersion.Current,
                buildVersion = AppVersion.BuildVersion,
                buildId = typeof(AppVersion).Assembly.ManifestModule.ModuleVersionId,
            },
            daemon = new
            {
                observation.Connected,
                observation.ReceivedAt,
                version = Text(daemon, "daemonVersion"),
                buildVersion = Text(daemon, "daemonBuildVersion"),
                buildId = Text(daemon, "daemonBuildId"),
            },
            system = new
            {
                osRelease,
                kernel = RuntimeInformation.OSDescription,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                dotnet = RuntimeInformation.FrameworkDescription,
                desktop = Limited(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")),
                sessionType = Limited(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")),
                flatpak = FlatpakEnvironment.IsFlatpak,
            },
            audio = new { pipeWireServerVersion = graph.ServerVersion, graph.Clock, tools },
            hardware = new
            {
                primary = daemon?["device"] is JsonObject device ? new
                {
                    vendor = Limited(Text(device, "vendor")),
                    model = Limited(Text(device, "model")),
                    usbId = Limited(Text(device, "usbId")),
                    firmware = Limited(Text(device, "firmware")),
                } : null,
                detected = (daemon?["detected"] as JsonArray)?.OfType<JsonObject>().Take(64).Select(unit => new
                {
                    name = Limited(Text(unit, "name")),
                    usbId = Limited(Text(unit, "usbId")),
                    active = unit["active"] is JsonValue flag && flag.TryGetValue(out bool active) ? active : (bool?)null,
                }).ToArray(),
            },
        };
        await File.WriteAllTextAsync(Path.Combine(work, "system-info.json"), cleanJson(JsonSerializer.Serialize(report, ReportJson)));
    }

    private sealed record ToolVersion(string Tool, string? Version, string? Problem);

    private static async Task<ToolVersion> ReadToolVersionAsync(string tool)
    {
        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(tool, ["--version"], TimeSpan.FromSeconds(3),
                stdoutCap: 4096, stderrCap: 1024);
            if (result.TimedOut) return new(tool, null, "timed out");
            if (result.Truncated || result.Incomplete) return new(tool, null, "incomplete output");
            if (result.ExitCode != 0) return new(tool, null, $"exit status {result.ExitCode}");
            string version = result.StdoutText.Trim();
            return version.Length == 0 ? new(tool, null, "no version reported") : new(tool, version, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            return new(tool, null, "unavailable");
        }
    }

    /// <summary>Summarize the same graph snapshot saved in the archive.</summary>
    private static AudioGraphSummary ReadAudioGraph(JsonElement snapshot)
    {
        var clock = new Dictionary<string, int>(StringComparer.Ordinal);
        string? serverVersion = null;
        var nodes = new Dictionary<int, JsonElement>();
        var clients = new Dictionary<int, JsonElement>();
        var links = new List<(int From, int To)>();
        foreach (JsonElement item in snapshot.EnumerateArray())
        {
            if (Number(Property(item, "id")) is not int id || id < 0) continue;
            JsonElement info = Property(item, "info");
            switch (GraphText(item, "type"))
            {
                case "PipeWire:Interface:Core":
                    serverVersion ??= GraphText(info, "version");
                    break;
                case "PipeWire:Interface:Client":
                    clients[id] = Property(info, "props");
                    break;
                case "PipeWire:Interface:Node":
                    nodes[id] = info;
                    break;
                case "PipeWire:Interface:Link":
                    if (Number(Property(info, "output-node-id")) is int from && Number(Property(info, "input-node-id")) is int to)
                        links.Add((from, to));
                    break;
                case "PipeWire:Interface:Metadata":
                    JsonElement metadata = Property(item, "metadata");
                    if (GraphText(Property(item, "props"), "metadata.name") != "settings"
                        || metadata.ValueKind != JsonValueKind.Array) break;
                    foreach (JsonElement value in metadata.EnumerateArray())
                        if (Number(Property(value, "subject")) == 0 && GraphText(value, "key") is { } key
                            && ClockKeys.Contains(key, StringComparer.Ordinal)
                            && Number(Property(value, "value")) is int number && number >= 0)
                            clock[key] = number;
                    break;
            }
        }

        var incoming = links.ToLookup(link => link.To, link => link.From);
        var outgoing = links.ToLookup(link => link.From, link => link.To);
        bool truncated = false;
        var streams = new List<AudioApplication>();
        foreach (var (id, info) in nodes.OrderByDescending(node => GraphText(node.Value, "state") == "running").ThenBy(node => node.Key))
        {
            JsonElement props = Property(info, "props");
            string? mediaClass = GraphText(props, "media.class");
            if (mediaClass is not ("Stream/Output/Audio" or "Stream/Input/Audio")
                || Property(props, "node.link-group").ValueKind != JsonValueKind.Undefined
                || GraphText(props, "node.name")?.StartsWith("OpenXLR_", StringComparison.Ordinal) == true)
                continue;
            if (streams.Count == 512)
            {
                truncated = true;
                break;
            }
            bool playback = mediaClass == "Stream/Output/Audio";
            JsonElement client = Number(Property(props, "client.id")) is int clientId ? clients.GetValueOrDefault(clientId) : default;
            string? name = GraphText(props, "application.name") ?? GraphText(client, "application.name");
            string? binary = GraphText(props, "application.process.binary") ?? GraphText(client, "application.process.binary");
            string[] endpoints = (playback ? outgoing[id] : incoming[id])
                .Select(peer => Limited(GraphText(Property(nodes.GetValueOrDefault(peer), "props"), "node.name")))
                .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(33).ToArray();
            if (endpoints.Length > 32)
            {
                truncated = true;
                endpoints = endpoints[..32];
            }
            streams.Add(new(Limited(name), binary is null ? null : Limited(ExecutableName(binary)),
                playback ? "playback" : "capture", Limited(GraphText(info, "state")), endpoints));
        }
        return new(true, truncated, Limited(serverVersion), clock, streams.ToArray());
    }

    private static JsonElement Property(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out JsonElement value) ? value : default;

    private static string? GraphText(JsonElement obj, string key)
        => Property(obj, key) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    private static JsonNode? ParseDiagnosticJson(string? json)
    {
        if (json is null) return null;
        try { return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }); }
        catch (JsonException) { return null; }
    }

    private static int? Number(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;

    private static string? Limited(string? value)
        => value is { Length: > 256 } ? value[..256] : value;
}
