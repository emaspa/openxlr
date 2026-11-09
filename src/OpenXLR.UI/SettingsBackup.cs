using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenXLR.UI;

/// <summary>A portable copy of the settings stores, separate from runtime and installation state.</summary>
internal static partial class SettingsBackup
{
    internal const string Extension = "openxlr-backup";
    private const int MaxBytes = 32 * 1024 * 1024;
    private const int MaxArchiveBytes = 48 * 1024 * 1024;
    private const int MaxFiles = 1024;
    private static readonly string[] Settings =
    [
        "mixer.json", "ui.json", "daemon.json", "desktop-keys.json", "native-editors.json",
        "eq-profiles.json", "effect-chain-presets.json", "active-device.json", "gainlock.json",
        "wave-interfaces.json",
    ];
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly JsonDocumentOptions FileJson = new() { AllowDuplicateProperties = false };

    private sealed record Archive(int Version, Dictionary<string, byte[]>? Files);

    internal sealed record Change(string Name, byte[] Next, byte[]? Previous)
    {
        public bool Conflict => Previous is not null;
    }

    [GeneratedRegex(@"\A[0-9a-fA-F]{4}-[0-9a-fA-F]{4}(?:@[0-9a-fA-F]{16})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();

    internal static int Export(string path, bool profilesOnly)
    {
        string root = Root;
        path = Path.GetFullPath(path);
        if (!path.EndsWith("." + Extension, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Choose a file ending in .{Extension}.");
        if (path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Save the backup outside OpenXLR's configuration folder.");

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (string name in Candidates(root, profilesOnly))
        {
            byte[]? bytes = Read(root, name);
            if (bytes is null) continue;
            Count(ref total, files.Count, bytes);
            Validate(name, bytes);
            files.Add(name, bytes);
        }
        if (files.Count == 0) throw new IOException("There are no saved files to export.");
        SaveArchive(path, files);
        return files.Count;
    }

    internal static List<Change> Preview(string path)
    {
        RejectLinkOrSpecial(path, directory: false);
        using var document = JsonDocument.Parse(RegularFile.ReadBytes(path, MaxArchiveBytes), FileJson);
        var archive = document.Deserialize<Archive>(Json);
        if (archive is not { Version: 1, Files.Count: > 0 and <= MaxFiles })
            throw new InvalidDataException("This is not a supported OpenXLR settings backup.");

        var changes = new List<Change>();
        long total = 0, rollbackBytes = 0;
        string root = Root;
        foreach (var (name, bytes) in archive.Files)
        {
            if (bytes is null || Limit(name) == 0)
                throw new InvalidDataException("The backup contains an unsupported file.");
            Count(ref total, changes.Count, bytes);
            Validate(name, bytes);
            byte[]? previous = Read(root, name);
            if (previous is not null)
            {
                if (bytes.AsSpan().SequenceEqual(previous)) continue;
                // The safety backup must fit the same limits and be restorable.
                Validate(name, previous);
                Count(ref rollbackBytes, changes.Count, previous);
            }
            changes.Add(new(name, bytes, previous));
        }
        return changes;
    }

    // The caller holds both application guards from preview through publication.
    internal static string? Apply(IReadOnlyList<Change> changes, bool replace)
    {
        var previous = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (Change change in changes)
            if (change.Previous is { } bytes) previous.Add(change.Name, bytes);

        string root = Root;
        string? safety = null;
        if (previous.Count > 0)
        {
            if (!replace) throw new IOException("Existing settings differ. Review the conflicts before importing with --replace.");
            safety = Path.Combine(root, $"before-import-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.{Extension}");
            SaveArchive(safety, previous);
        }

        int applied = 0;
        try
        {
            for (; applied < changes.Count; applied++)
            {
                Change change = changes[applied];
                string path = CheckedPath(root, change.Name);
                if (change.Previous is { } expected &&
                    (Read(root, change.Name) is not { } current || !current.AsSpan().SequenceEqual(expected)))
                    throw new IOException($"{change.Name} changed after the preview. Try again.");
                OpenXlrPaths.WriteAtomic(path, change.Next, privatePermissions: true, fileMode: null, overwrite: change.Conflict);
            }
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            var rollbackFailures = new List<string>();
            for (int i = applied - 1; i >= 0; i--)
            {
                Change change = changes[i];
                try
                {
                    string path = CheckedPath(root, change.Name);
                    if (Read(root, change.Name) is not { } current || !current.AsSpan().SequenceEqual(change.Next))
                        throw new IOException($"{change.Name} changed during the restore.");
                    if (change.Previous is { } bytes) OpenXlrPaths.WriteAtomic(path, bytes);
                    else File.Delete(path);
                }
                catch (Exception rollback) when (IsFileFailure(rollback)) { rollbackFailures.Add(change.Name); }
            }
            string detail = rollbackFailures.Count == 0 ? "Earlier changes were rolled back." :
                "Could not roll back " + string.Join(", ", rollbackFailures) + ".";
            throw new IOException($"{ex.Message} {detail}" + (safety is null ? "" : $" Previous settings: {safety}"), ex);
        }
        return safety;
    }

    internal static bool IsFileFailure(Exception ex)
        => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;

    private static string Root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(OpenXlrPaths.ConfigDir));

    private static void SaveArchive(string path, Dictionary<string, byte[]> files) =>
        OpenXlrPaths.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(new Archive(1, files), Json),
            privatePermissions: false, fileMode: UnixFileMode.UserRead | UnixFileMode.UserWrite, overwrite: false);

    private static void Count(ref long total, int count, byte[] bytes)
    {
        if ((total += bytes.Length) > MaxBytes || count >= MaxFiles)
            throw new InvalidDataException("The settings backup exceeds its size limit.");
    }

    private static IEnumerable<string> Candidates(string root, bool profilesOnly)
    {
        if (!profilesOnly)
        {
            foreach (string file in Settings) yield return file;
            foreach (string unit in Directories(root, "devices"))
                if (DeviceName().IsMatch(unit)) yield return $"devices/{unit}/last-state.json";
        }
        foreach (string name in Profiles(root, "profiles")) yield return name;
        foreach (string unit in Directories(root, "profiles"))
            if (unit == "generic" || IsModel(unit))
            {
                foreach (string name in Profiles(root, $"profiles/{unit}")) yield return name;
                if (unit != "generic") yield return $"profiles/{unit}/recall-on-connect";
            }
    }

    private static IEnumerable<string> Directories(string root, string name)
    {
        string path = CheckedPath(root, name, directory: true);
        return Directory.Exists(path) ? Directory.EnumerateDirectories(path).Select(dir => Path.GetFileName(dir)) : [];
    }

    private static IEnumerable<string> Profiles(string root, string name)
    {
        string path = CheckedPath(root, name, directory: true);
        if (!Directory.Exists(path)) yield break;
        foreach (string file in Directory.EnumerateFiles(path, "*.json"))
        {
            string relative = name + "/" + Path.GetFileName(file);
            if (Limit(relative) > 0) yield return relative;
        }
    }

    private static int Limit(string name)
    {
        if (Settings.Contains(name, StringComparer.Ordinal)) return OpenXlrPaths.MaxPreferenceBytes;
        string[] parts = name.Split('/');
        if (parts is ["devices", var unit, "last-state.json"] && DeviceName().IsMatch(unit))
            return OpenXlrPaths.MaxPreferenceBytes;
        if (parts is ["profiles", var model, "recall-on-connect"] && IsModel(model)) return 1024;
        if ((parts is ["profiles", _] || parts is ["profiles", var scope, _] && (scope == "generic" || IsModel(scope)))
            && parts[^1].EndsWith(".json", StringComparison.Ordinal) && ProfileName(parts[^1][..^5]))
            return 8 * 1024 * 1024;
        return 0;
    }

    private static bool IsModel(string name) => name.Length == 9 && DeviceName().IsMatch(name);

    private static bool ProfileName(string name) => name.Length is > 0 and <= 60 && name == name.Trim()
        && name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.');

    private static void Validate(string name, byte[] bytes)
    {
        if (bytes.Length > Limit(name)) throw new InvalidDataException($"The file {name} exceeds its size limit.");
        using var reader = new StreamReader(new MemoryStream(bytes));
        string text = reader.ReadToEnd();
        if (name.EndsWith("/recall-on-connect", StringComparison.Ordinal))
        {
            if (text.Trim() is { Length: > 0 } target && !ProfileName(target))
                throw new InvalidDataException($"Invalid profile name in {name}.");
            return;
        }
        using var document = JsonDocument.Parse(text, FileJson);
        JsonValueKind expected = name is "wave-interfaces.json" or "gainlock.json" or "effect-chain-presets.json"
            ? JsonValueKind.Array : JsonValueKind.Object;
        if (document.RootElement.ValueKind != expected) throw new InvalidDataException($"Invalid settings in {name}.");
    }

    private static byte[]? Read(string root, string name)
    {
        string path = CheckedPath(root, name);
        try { return RegularFile.ReadBytes(path, Limit(name)); }
        catch (IOException) when (RegularFile.Inspect(path, noFollow: true) is null) { return null; }
    }

    private static string CheckedPath(string root, string name, bool directory = false)
    {
        RejectLinkOrSpecial(root, directory: true);
        string[] parts = name.Split('/');
        string path = root;
        for (int i = 0; i < parts.Length; i++)
        {
            path = Path.Combine(path, parts[i]);
            RejectLinkOrSpecial(path, directory || i < parts.Length - 1);
        }
        return path;
    }

    private static void RejectLinkOrSpecial(string path, bool directory)
    {
        if (RegularFile.Inspect(path, noFollow: true) is { } status &&
            ((status.Mode & 0xf000) != (directory ? 0x4000 : 0x8000) || status.User != UnixPeerCredentials.User))
            throw new IOException($"Refusing a linked, special or unowned path: {path}");
    }
}
