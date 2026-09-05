using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// One place that decides whether a mixer command is well formed before the
/// mixer sees it: known channel and mix ids, catalogued and supported
/// plugins, declared parameter symbols, finite numbers, bounded strings and
/// lists. The mixer methods used to swallow an unknown id silently; now the
/// client gets an error and nothing unbounded reaches memory or mixer.json.
/// </summary>
public static class CommandValidation
{
    public const int MaxText = 256;          // identities, labels, device names, symbols
    public const int MaxUri = 512;
    public const int MaxDevices = 16;
    public const int MaxInsertsPerChannel = 16;
    public const int MaxInsertId = 64;
    public const int MaxParamsPerInsert = 256;
    public const int MaxOverrides = 512;     // remembered app assignments

    public static string? Check(Command cmd, ILayoutInfo layout, Func<string, PluginInfo?> findPlugin)
    {
        switch (cmd.Cmd)
        {
            case "setLevel":
            case "setChannelMuted":
                if (cmd.Channel is not null && !layout.HasChannel(cmd.Channel)) return $"{cmd.Cmd}: unknown channel '{Short(cmd.Channel)}'";
                if (cmd.Mix is not null && !layout.HasMix(cmd.Mix)) return $"{cmd.Cmd}: unknown mix '{Short(cmd.Mix)}'";
                return cmd.Cmd == "setLevel" ? Finite(cmd, "value") : null;
            case "setMixVolume":
            case "setMixMuted":
                if (cmd.Mix is not null && !layout.HasMix(cmd.Mix)) return $"{cmd.Cmd}: unknown mix '{Short(cmd.Mix)}'";
                return cmd.Cmd == "setMixVolume" ? Finite(cmd, "value") : null;
            case "setOutputVolume":
                return Finite(cmd, "value");
            case "assignStream":
                if (cmd.Channel is not null && !IsChannelOrIgnore(layout, cmd.Channel)) return $"assignStream: unknown channel '{Short(cmd.Channel)}'";
                return null;
            case "assignApp":
                if (cmd.Channel is not null && !IsChannelOrIgnore(layout, cmd.Channel)) return $"assignApp: unknown channel '{Short(cmd.Channel)}'";
                if (TooLong(cmd.Identity, MaxText)) return "assignApp: identity too long";
                if (TooLong(cmd.Label, MaxText)) return "assignApp: label too long";
                if (layout.OverrideCount >= MaxOverrides) return $"assignApp: {MaxOverrides} remembered applications already; forget some first";
                return null;
            case "forgetApp":
                return TooLong(cmd.Identity, MaxText) ? "forgetApp: identity too long" : null;
            case "setMonitorOutput":
                return TooLong(cmd.Device, MaxText) ? "setMonitorOutput: device name too long" : null;
            case "setMonitorOutputs":
                if (cmd.Devices is { Count: > MaxDevices }) return $"setMonitorOutputs: at most {MaxDevices} devices";
                if (cmd.Devices?.Any(d => TooLong(d, MaxText)) == true) return "setMonitorOutputs: device name too long";
                return null;
            case "setRecallOnConnect":
                return TooLong(cmd.Name, MaxText) ? "setRecallOnConnect: name too long" : null;
            case "setEnforcedDefaults":
                if (TooLong(cmd.Sink, MaxText) || TooLong(cmd.Source, MaxText)) return "setEnforcedDefaults: device name too long";
                return null;
            case "setInserts":
                if (cmd.Channel is not null && !layout.IsInsertKey(cmd.Channel)) return $"setInserts: '{Short(cmd.Channel)}' has no insert chain";
                if (cmd.Inserts is { Count: > MaxInsertsPerChannel }) return $"setInserts: at most {MaxInsertsPerChannel} inserts per chain";
                if (cmd.Inserts is not null)
                {
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (InsertDefinition i in cmd.Inserts)
                    {
                        if (string.IsNullOrWhiteSpace(i.Id)) continue;   // the service reports missing fields
                        if (i.Id.Length > MaxInsertId || !i.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                            return "setInserts: insert ids are up to 64 letters, digits, '-' or '_'";
                        if (!ids.Add(i.Id)) return $"setInserts: duplicate insert id '{i.Id}'";
                        if (TooLong(i.Label, MaxText)) return "setInserts: label too long";
                        if (string.IsNullOrWhiteSpace(i.Plugin)) continue;
                        if (i.Plugin.Length > MaxUri) return "setInserts: plugin URI too long";
                        PluginInfo? plugin = findPlugin(i.Plugin);
                        if (plugin is null) return $"setInserts: plugin '{Short(i.Plugin)}' is not installed";
                        if (!plugin.Supported)
                            return $"setInserts: '{plugin.Name}' needs {string.Join(", ", plugin.UnsupportedFeatures.Select(Tail))}, which the PipeWire chain does not provide";
                        if (i.Params.Count > MaxParamsPerInsert) return "setInserts: too many parameters";
                        foreach ((string symbol, double value) in i.Params)
                        {
                            if (!plugin.Params.Any(p => p.Symbol == symbol)) return $"setInserts: '{plugin.Name}' has no control '{Short(symbol)}'";
                            if (!double.IsFinite(value)) return $"setInserts: '{symbol}' must be a finite number";
                        }
                    }
                }
                return null;
            case "setInsertBypass":
                if (cmd.Channel is not null && !layout.IsInsertKey(cmd.Channel)) return $"setInsertBypass: '{Short(cmd.Channel)}' has no insert chain";
                return TooLong(cmd.InsertId, MaxInsertId) ? "setInsertBypass: insert id too long" : null;
            case "setInsertParam":
                if (cmd.Channel is not null && !layout.IsInsertKey(cmd.Channel)) return $"setInsertParam: '{Short(cmd.Channel)}' has no insert chain";
                if (TooLong(cmd.InsertId, MaxInsertId) || TooLong(cmd.Symbol, MaxText)) return "setInsertParam: id or symbol too long";
                return Finite(cmd, "value");
            default:
                return null;   // the service knows the rest, or reports the command as unknown
        }
    }

    private static string? Finite(Command cmd, string field)
        => cmd.Value.ValueKind == JsonValueKind.Number && cmd.Value.TryGetDouble(out double d) && double.IsFinite(d)
            ? null : $"{cmd.Cmd}: '{field}' must be a finite number";

    private static bool TooLong(string? s, int max) => s is not null && s.Length > max;
    private static string Short(string s) => s.Length <= 40 ? s : s[..40] + "...";
    private static string Tail(string uri) => uri[(uri.LastIndexOfAny(['#', '/']) + 1)..];

    /// <summary>A real channel, or the "ignore" pseudo-channel that leaves an app to the desktop.</summary>
    private static bool IsChannelOrIgnore(ILayoutInfo layout, string id)
        => id == OpenXLR.Core.Mixing.StreamMatcher.Ignore || layout.HasChannel(id);
}
