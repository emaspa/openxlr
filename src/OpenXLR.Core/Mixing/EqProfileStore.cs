using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>A saved microphone EQ: a name and its bands, as <c>setEq</c> carries them, with no channel and no switch.</summary>
public sealed record SavedEqProfile(string Name, IReadOnlyList<EqBand> Bands);

/// <summary>What the profile file holds: the profiles that read, and how many entries did not.</summary>
public sealed record SavedEqProfileList(IReadOnlyList<SavedEqProfile> Profiles, int Unreadable)
{
    public static SavedEqProfileList Empty { get; } = new([], 0);
}

/// <summary>One profile as the state lists it: built in or saved, its id, name and bands.</summary>
public sealed record EqProfileEntry(string Id, string Name, bool Builtin, IReadOnlyList<EqBand> Bands);

/// <summary>
/// Why the profile file is not listed whole: <c>unreadable</c> (some entries
/// did not read, <see cref="Count"/> of them), <c>damaged</c> (not a profile
/// list), <c>tooLarge</c> or <c>failed</c> (it could not be read at all). The
/// message says it in English; a client may say it in its own language from
/// the reason.
/// </summary>
public sealed record EqProfileProblem(string Reason, int? Count, string Path, string Message);

/// <summary>The profiles to list, built-in ones first, and what is wrong with the file, if anything.</summary>
public sealed record EqProfilesState(IReadOnlyList<EqProfileEntry> Profiles, EqProfileProblem? Problem);

/// <summary>A profile file the store will not read or write, with the reason the state names.</summary>
public sealed class EqProfileFileException(string reason, string message) : Exception(message)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// The EQ profiles: the built-in presets and the saved ones, in one private,
/// bounded file the daemon writes whole, eq-profiles.json under the
/// configuration folder. An entry that does not read is left out of the list
/// and the file is not written while it holds one, so a hand edit is never
/// lost to a save. Values outside the EQ's ranges are held to them and
/// snapped to its steps on the way in, so what loads is what the EQ takes.
/// The file is read again when its size or time changes, so a hand edit
/// shows with the next state.
/// </summary>
public static class EqProfileStore
{
    public const int MaximumProfiles = 64;
    public const int MaximumNameLength = 80;
    public const int MaximumFileBytes = 1024 * 1024;
    private const int Version = 1;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static (string Path, bool Exists, DateTime Written, long Length)? _seen;
    private static EqProfilesState? _state;

    /// <summary>The file, under the configuration folder.</summary>
    public static string FilePath => OpenXlrPaths.ConfigFile("eq-profiles.json");

    public static bool ValidName(string? name)
        => name is { Length: > 0 and <= MaximumNameLength } && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);

    public static string NameRule => $"a profile name has 1 to {MaximumNameLength} characters and no control characters";

    /// <summary>
    /// Read the file. No file is no profiles. A file that is not a profile
    /// list at all throws <see cref="EqProfileFileException"/>; one entry that
    /// does not read is counted in <see cref="SavedEqProfileList.Unreadable"/>.
    /// </summary>
    public static SavedEqProfileList Read()
    {
        lock (Gate) return ReadFile();
    }

    private static SavedEqProfileList ReadFile()
    {
        string path = FilePath;
        if (!File.Exists(path)) return SavedEqProfileList.Empty;
        RegularFile.Require(path);
        byte[] bytes;
        using (var file = File.OpenRead(path))
        {
            if (file.Length > MaximumFileBytes) throw TooLarge(path);
            bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1) throw new IOException($"{path} changed while it was read. Try again.");
        }
        return Parse(bytes);
    }

    private static EqProfileFileException TooLarge(string path) => new("tooLarge", $"{path} is larger than 1 MiB and was not read.");

    private static EqProfileFileException Damaged() => new("damaged",
        $"{FilePath} is not an EQ profile file OpenXLR can read. Fix or move it; OpenXLR does not overwrite it.");

    private static string UnreadableMessage(int count) =>
        $"Some profiles in {FilePath} could not be read ({count}) and are not listed. Until they are fixed or removed, OpenXLR saves no changes to the file.";

    /// <summary>Read a profile file's bytes; see <see cref="Read"/>.</summary>
    public static SavedEqProfileList Parse(ReadOnlyMemory<byte> utf8)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(utf8, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 16 }); }
        catch (JsonException) { throw Damaged(); }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int number) || number != Version
                || !root.TryGetProperty("profiles", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array
                || entries.GetArrayLength() > MaximumProfiles)
                throw Damaged();
            var profiles = new List<SavedEqProfile>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int unreadable = 0;
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (ReadProfile(entry) is { } profile && names.Add(profile.Name)) profiles.Add(profile);
                else unreadable++;
            }
            return new(profiles, unreadable);
        }
    }

    private static SavedEqProfile? ReadProfile(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String
            || name.GetString()?.Trim() is not { } text || !ValidName(text)
            || !entry.TryGetProperty("bands", out JsonElement bands) || bands.ValueKind != JsonValueKind.Array
            || bands.GetArrayLength() is < MicEq.MinimumBands or > MicEq.MaximumBands)
            return null;
        var read = new List<EqBand>();
        foreach (JsonElement band in bands.EnumerateArray())
        {
            if (ReadBand(band) is not { } value) return null;
            read.Add(value);
        }
        return new(text, read);
    }

    /// <summary>
    /// One band with every field, its type one the EQ knows. Numbers outside
    /// the EQ's ranges are held to them and snapped to its steps.
    /// </summary>
    private static EqBand? ReadBand(JsonElement band)
    {
        if (band.ValueKind != JsonValueKind.Object
            || !band.TryGetProperty("enabled", out JsonElement on) || on.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !band.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String
            || MicEq.TypeIndex(type.GetString()) < 0
            || Number(band, "frequency") is not double frequency
            || Number(band, "gain") is not double gain
            || Number(band, "q") is not double q)
            return null;
        return Clamp(new EqBand { Enabled = on.GetBoolean(), Type = type.GetString()!, Frequency = frequency, Gain = gain, Q = q });

        static double? Number(JsonElement band, string name)
            => band.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
    }

    /// <summary>A band held to the EQ's ranges and snapped to its steps.</summary>
    public static EqBand Clamp(EqBand band) => MicEq.Snap(band with
    {
        Frequency = Math.Clamp(band.Frequency, MicEq.MinFrequency, MicEq.MaxFrequency),
        Gain = Math.Clamp(band.Gain, MicEq.MinGain, MicEq.MaxGain),
        Q = Math.Clamp(band.Q, MicEq.MinQ, MicEq.MaxQ),
    });

    /// <summary>
    /// The list the state carries: the built-in presets, then the saved
    /// profiles in the file's order. The file is read again only when it
    /// changed, or when <paramref name="reread"/> asks; a file that cannot be
    /// read lists the built-in profiles and names the problem.
    /// </summary>
    public static EqProfilesState State(bool reread = false)
    {
        lock (Gate)
        {
            string path = FilePath;
            var info = new FileInfo(path);
            (string, bool, DateTime, long) seen = info.Exists ? (path, true, info.LastWriteTimeUtc, info.Length) : (path, false, default, 0);
            if (!reread && _state is not null && _seen.Equals(seen)) return _state;
            EqProfileProblem? problem = null;
            IReadOnlyList<SavedEqProfile> saved = [];
            try
            {
                SavedEqProfileList list = ReadFile();
                saved = list.Profiles;
                if (list.Unreadable > 0) problem = new("unreadable", list.Unreadable, path, UnreadableMessage(list.Unreadable));
            }
            catch (EqProfileFileException ex) { problem = new(ex.Reason, null, path, ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problem = new("failed", null, path, ex.Message); }
            _state = new(
                [.. EqPresets.All.Select(p => new EqProfileEntry(p.Id, p.Name, true, p.Eq.Bands)),
                 .. saved.Select(p => new EqProfileEntry(EqProfileIds.Saved(p.Name), p.Name, false, p.Bands))],
                problem);
            _seen = seen;
            return _state;
        }
    }

    /// <summary>The list as last read, without looking at the file; null when it has not been read or was written since.</summary>
    public static EqProfilesState? Listed
    {
        get { lock (Gate) return _state; }
    }

    /// <summary>The profile with this id, built in or saved (a saved name matched whatever its case), or null.</summary>
    public static EqProfileEntry? Find(string? id)
    {
        if (id is null) return null;
        if (id.StartsWith(EqProfileIds.BuiltinPrefix, StringComparison.Ordinal))
            return EqPresets.FindId(id) is { } preset ? new(preset.Id, preset.Name, true, preset.Eq.Bands) : null;
        if (!id.StartsWith(EqProfileIds.SavedPrefix, StringComparison.Ordinal)) return null;
        string name = id[EqProfileIds.SavedPrefix.Length..];
        return State().Profiles.FirstOrDefault(p => !p.Builtin && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Why a profile id cannot be one, or null.</summary>
    public static string? CheckId(string? id)
    {
        if (id is null) return "need 'profile'";
        if (id.StartsWith(EqProfileIds.BuiltinPrefix, StringComparison.Ordinal))
            return id.Length <= 64 && !id.Any(char.IsControl) ? null : "'profile' is not a profile id";
        if (id.StartsWith(EqProfileIds.SavedPrefix, StringComparison.Ordinal))
            return ValidName(id[EqProfileIds.SavedPrefix.Length..]) ? null : "'profile' is not a profile id";
        return "'profile' must start with 'builtin:' or 'saved:'";
    }

    /// <summary>
    /// Save bands under a name. A name already saved is refused unless
    /// <paramref name="replace"/>, which puts the new bands in its place.
    /// Bands outside the EQ's ranges are refused, not held.
    /// </summary>
    public static SavedEqProfile Save(string name, IReadOnlyList<EqBand> bands, bool replace = false)
    {
        name = name.Trim();
        if (!ValidName(name)) throw new InvalidDataException(NameRule);
        if (MicEq.Check(new MicEq { Bands = bands }) is { } problem) throw new InvalidDataException($"bands: {problem}");
        var profile = new SavedEqProfile(name, [.. bands.Select(Clamp)]);
        lock (Gate)
        {
            List<SavedEqProfile> profiles = Writable();
            int index = profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && !replace) throw new InvalidDataException($"an EQ profile named {profiles[index].Name} already exists");
            if (index >= 0) profiles[index] = profile;
            else
            {
                if (profiles.Count >= MaximumProfiles) throw new InvalidDataException($"OpenXLR keeps up to {MaximumProfiles} EQ profiles; delete one to save another");
                profiles.Add(profile);
            }
            Write(profiles);
        }
        return profile;
    }

    /// <summary>Give a saved profile another name; a change of case alone is a rename too.</summary>
    public static SavedEqProfile Rename(string name, string newName)
    {
        newName = newName.Trim();
        if (!ValidName(newName)) throw new InvalidDataException(NameRule);
        lock (Gate)
        {
            List<SavedEqProfile> profiles = Writable();
            int index = profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new InvalidDataException($"there is no EQ profile named {name}");
            if (profiles.Where((_, i) => i != index).Any(p => string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"an EQ profile named {newName} already exists");
            SavedEqProfile renamed = profiles[index] with { Name = newName };
            profiles[index] = renamed;
            Write(profiles);
            return renamed;
        }
    }

    public static void Delete(string name)
    {
        lock (Gate)
        {
            List<SavedEqProfile> profiles = Writable();
            if (profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
                throw new InvalidDataException($"there is no EQ profile named {name}");
            Write(profiles);
        }
    }

    /// <summary>The profiles to change, refused while the file holds an entry that does not read.</summary>
    private static List<SavedEqProfile> Writable()
    {
        SavedEqProfileList list = ReadFile();
        if (list.Unreadable > 0) throw new EqProfileFileException("unreadable", UnreadableMessage(list.Unreadable));
        return [.. list.Profiles];
    }

    private static void Write(IReadOnlyList<SavedEqProfile> profiles)
    {
        var document = new
        {
            version = Version,
            profiles = profiles.Select(p => new
            {
                name = p.Name,
                bands = p.Bands.Select(b => new { enabled = b.Enabled, type = b.Type, frequency = b.Frequency, gain = b.Gain, q = b.Q }),
            }),
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaximumFileBytes) throw TooLarge(FilePath);
        try { OpenXlrPaths.WriteAtomic(FilePath, bytes); }
        finally { _state = null; }
    }

    /// <summary>The failures the commands report as their error.</summary>
    public static bool IsFailure(Exception ex) => ex is IOException or InvalidDataException or EqProfileFileException or UnauthorizedAccessException;
}
