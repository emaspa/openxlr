using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;

namespace OpenXLR.UI;

/// <summary>Desktop integration is opt-in, independent of audio profiles.</summary>
internal sealed record DesktopKeySettings
{
    public bool Enabled { get; init; }
    public List<string> FocusChannels { get; init; } = [];
    public bool OutputControls { get; init; }
    public bool MonitorControls { get; init; }
    public string? OutputDevice { get; init; }
    public List<string> MainOutputs { get; init; } = [];
    /// <summary>Soundboard clips, by id, that have a key of their own. The daemon never gives a deleted clip's id to another.</summary>
    public List<string> Clips { get; init; } = [];
    /// <summary>A key that stops every clip.</summary>
    public bool ClipsStop { get; init; }
    /// <summary>A key that mutes the chosen microphones, or turns them on when every one is muted.</summary>
    public bool MicMute { get; init; }
    /// <summary>A key that turns the chosen microphones on while it is down; setting it up mutes them.</summary>
    public bool PushToTalk { get; init; }
    /// <summary>
    /// The microphones both keys cover, by mixer channel id: XLR 1 when none
    /// is chosen. One list for both, so the key that opens a microphone is
    /// never the only one that can mute it.
    /// </summary>
    public List<string> Microphones { get; init; } = [DefaultMicrophone];
    /// <summary>A key that marks a glitch on the daemon's timeline, as Ctrl+G does in the UI.</summary>
    public bool GlitchMark { get; init; }
    internal const string DefaultMicrophone = "xlr1";
    internal static string MainKey(string device) => "main_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device)))[..24].ToLowerInvariant();
    internal static bool ValidOutput(string? name) => name is { Length: > 0 and <= 256 } && !name.Any(char.IsControl)
        && (name == "@monitor" || (!name.Contains('#') && name[0] is not ('@' or '-') && !name.All(char.IsAsciiDigit)));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string FilePath => OpenXlrPaths.ConfigFile("desktop-keys.json");
    internal static bool ValidId(string? id) => id is { Length: > 0 and <= 36 } && id[0] is >= 'a' and <= 'z'
        && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
    /// <summary>A clip id as the daemon makes them: lower-case letters, digits and hyphens, starting with a letter.</summary>
    internal static bool ValidClip(string? id) => id is { Length: > 0 and <= 40 } && id[0] is >= 'a' and <= 'z'
        && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    internal DesktopKeySettings Normalize() => this with
    {
        FocusChannels = [.. (FocusChannels ?? []).Where(ValidId).Distinct(StringComparer.Ordinal).Take(32)],
        Clips = [.. (Clips ?? []).Where(ValidClip).Distinct(StringComparer.Ordinal).Take(64)],
        MainOutputs = [.. (MainOutputs ?? []).Where(ValidOutput).Distinct(StringComparer.Ordinal).Take(16)],
        Microphones = (Microphones ?? []).Where(ValidId).Distinct(StringComparer.Ordinal).Take(16).ToList() is { Count: > 0 } microphones
            ? microphones : [DefaultMicrophone],
        OutputControls = OutputControls && (OutputDevice is null || (ValidOutput(OutputDevice) && OutputDevice != "@monitor")),
    };
    internal static DesktopKeySettings Load()
    {
        try { return (JsonSerializer.Deserialize<DesktopKeySettings>(File.ReadAllText(FilePath), Json) ?? new()).Normalize(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save() => OpenXlrPaths.WriteAtomicJson(FilePath, Normalize(), Json);
}
