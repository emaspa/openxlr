using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenXLR.UI;

/// <summary>
/// A saved microphone EQ: a name and its bands, as <c>setEq</c> carries
/// them. It holds no channel and no switch, so it loads on any microphone.
/// </summary>
public sealed record EqProfile(string Name, IReadOnlyList<EqBand> Bands)
{
    public override string ToString() => Name;
}

/// <summary>What the profile file holds: the profiles that read, and how many entries did not.</summary>
public sealed record EqProfileList(IReadOnlyList<EqProfile> Profiles, int Unreadable)
{
    public static EqProfileList Empty { get; } = new([], 0);
}

/// <summary>
/// The saved EQ profiles: one private, bounded file the UI writes whole,
/// beside the effect presets. An entry that does not read is left out of the
/// list and the file is not written while it holds one, so a hand edit is
/// never lost to a save. Values outside the daemon's ranges are held to them
/// and snapped to its steps on the way in, so what loads is what the daemon
/// takes.
/// </summary>
public static class EqProfiles
{
    public const int MaximumProfiles = 64;
    public const int MaximumNameLength = 80;
    internal const int MaximumFileBytes = 1024 * 1024;
    private const int Version = 1;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The file, under the configuration folder.</summary>
    public static string FilePath => OpenXlrPaths.ConfigFile("eq-profiles.json");

    /// <summary>Raised after the file changed here, so every open editor lists the same profiles.</summary>
    public static event Action? Changed;

    /// <summary>Raised before <see cref="Changed"/> when a profile is renamed: its old name, then its new one.</summary>
    public static event Action<string, string>? Renamed;

    /// <summary>The failures the store reports; each becomes the editor's error line.</summary>
    internal static bool IsFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException;

    public static bool ValidName(string? name)
        => name is { Length: > 0 and <= MaximumNameLength } && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);

    /// <summary>
    /// Read the file. No file is no profiles. A file that is not a profile
    /// list at all throws <see cref="InvalidDataException"/>; one entry that
    /// does not read is counted in <see cref="EqProfileList.Unreadable"/>.
    /// </summary>
    public static EqProfileList Read()
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return EqProfileList.Empty;
            RegularFile.Require(FilePath);
            byte[] bytes;
            using (var file = File.OpenRead(FilePath))
            {
                if (file.Length > MaximumFileBytes) throw new InvalidDataException(Localizer.Format("EqProfileFileTooLarge", FilePath));
                bytes = new byte[checked((int)file.Length)];
                file.ReadExactly(bytes);
                if (file.ReadByte() != -1) throw new IOException(Localizer.Format("EqProfileFileChanged", FilePath));
            }
            return Parse(bytes);
        }
    }

    /// <summary>Read a profile file's bytes; see <see cref="Read"/>.</summary>
    internal static EqProfileList Parse(ReadOnlyMemory<byte> utf8)
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
            var profiles = new List<EqProfile>();
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

    private static InvalidDataException Damaged() => new(Localizer.Format("EqProfileFileDamaged", FilePath));

    private static EqProfile? ReadProfile(JsonElement entry)
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
    /// One band with every field, its type one the daemon knows. Numbers
    /// outside the daemon's ranges are held to them and snapped to its steps.
    /// </summary>
    internal static EqBand? ReadBand(JsonElement band)
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

    /// <summary>A band held to the daemon's ranges and snapped to its steps.</summary>
    internal static EqBand Clamp(EqBand band) => MicEq.Snap(band with
    {
        Frequency = Math.Clamp(band.Frequency, MicEq.MinFrequency, MicEq.MaxFrequency),
        Gain = Math.Clamp(band.Gain, MicEq.MinGain, MicEq.MaxGain),
        Q = Math.Clamp(band.Q, MicEq.MinQ, MicEq.MaxQ),
    });

    /// <summary>
    /// Save bands under a name. A name already saved is refused unless
    /// <paramref name="replace"/>, which puts the new bands in its place.
    /// </summary>
    public static EqProfile Save(string name, IReadOnlyList<EqBand> bands, bool replace = false)
    {
        name = name.Trim();
        if (!ValidName(name)) throw new InvalidDataException(Localizer.Format("EqProfileNameRule", MaximumNameLength));
        if (bands.Count is < MicEq.MinimumBands or > MicEq.MaximumBands || bands.Any(b => MicEq.Check(b) is not null))
            throw new InvalidDataException(Localizer.Text("EqProfileBandsInvalid"));
        var profile = new EqProfile(name, [.. bands.Select(Clamp)]);
        lock (Gate)
        {
            List<EqProfile> profiles = Writable();
            int index = profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && !replace) throw new InvalidDataException(Localizer.Format("EqProfileNameExists", profiles[index].Name));
            if (index >= 0) profiles[index] = profile;
            else
            {
                if (profiles.Count >= MaximumProfiles) throw new InvalidDataException(Localizer.Format("EqProfileLimit", MaximumProfiles));
                profiles.Add(profile);
            }
            Write(profiles);
        }
        Changed?.Invoke();
        return profile;
    }

    /// <summary>Give a profile another name; a change of case alone is a rename too.</summary>
    public static EqProfile Rename(string name, string newName)
    {
        newName = newName.Trim();
        if (!ValidName(newName)) throw new InvalidDataException(Localizer.Format("EqProfileNameRule", MaximumNameLength));
        EqProfile renamed;
        lock (Gate)
        {
            List<EqProfile> profiles = Writable();
            int index = profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new InvalidDataException(Localizer.Format("EqProfileMissing", name));
            if (profiles.Where((_, i) => i != index).Any(p => string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(Localizer.Format("EqProfileNameExists", newName));
            renamed = profiles[index] with { Name = newName };
            name = profiles[index].Name;
            profiles[index] = renamed;
            Write(profiles);
        }
        Renamed?.Invoke(name, newName);
        Changed?.Invoke();
        return renamed;
    }

    public static void Delete(string name)
    {
        lock (Gate)
        {
            List<EqProfile> profiles = Writable();
            if (profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
                throw new InvalidDataException(Localizer.Format("EqProfileMissing", name));
            Write(profiles);
        }
        Changed?.Invoke();
    }

    /// <summary>The profiles to change, refused while the file holds an entry that does not read.</summary>
    private static List<EqProfile> Writable()
    {
        EqProfileList list = Read();
        if (list.Unreadable > 0) throw new InvalidDataException(Localizer.Format("EqProfileFileUnreadable", list.Unreadable, FilePath));
        return [.. list.Profiles];
    }

    private static void Write(IReadOnlyList<EqProfile> profiles)
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
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException(Localizer.Format("EqProfileFileTooLarge", FilePath));
        OpenXlrPaths.WriteAtomic(FilePath, bytes);
    }
}

/// <summary>
/// The profile row of one microphone's EQ editor: the saved profiles, the
/// one chosen, and what went wrong last. Every editor lists the same file,
/// and a change made in one shows in the others.
/// </summary>
public sealed class EqProfilesViewModel : ViewModelBase, IDisposable
{
    private readonly MicEqViewModel _eq;
    private IReadOnlyList<EqProfile> _profiles = [];
    private EqProfile? _selected;
    private string? _error, _fileNote;
    private bool _disposed, _failed;

    public EqProfilesViewModel(MicEqViewModel eq)
    {
        _eq = eq;
        EqProfiles.Changed += Refresh;
        EqProfiles.Renamed += OnRenamed;
        Refresh();
    }

    /// <summary>A profile chosen here was renamed, here or in another editor: it stays chosen under its new name.</summary>
    private void OnRenamed(string name, string newName)
    {
        if (_selected is { } chosen && string.Equals(chosen.Name, name, StringComparison.OrdinalIgnoreCase))
            _selected = chosen with { Name = newName };
    }

    /// <summary>The EQ the profiles load onto.</summary>
    public MicEqViewModel Eq => _eq;

    public IReadOnlyList<EqProfile> Profiles => _profiles;

    public EqProfile? Selected
    {
        get => _selected;
        set
        {
            if (value is not null && !_profiles.Contains(value)) return;
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelection));
        }
    }

    /// <summary>A profile is chosen: Load, Rename and Delete act on it.</summary>
    public bool HasSelection => _selected is not null;

    /// <summary>Why the last action failed, or why some saved profiles are not listed; null when all is well.</summary>
    public string? Error => _error;

    public bool HasError => _error is not null;

    /// <summary>Read the file again after it changed here, keeping the chosen profile by name.</summary>
    public void Refresh() => Read(keepFailure: false);

    /// <summary>
    /// Read the file again in case it was edited by hand, as the editor comes
    /// forward; a failure still on show stays, since closing its dialog brings
    /// the editor forward too.
    /// </summary>
    public void Reread() => Read(keepFailure: true);

    private void Read(bool keepFailure)
    {
        if (_disposed) return;
        string? chosen = _selected?.Name;
        try
        {
            EqProfileList list = EqProfiles.Read();
            _profiles = list.Profiles;
            _fileNote = list.Unreadable > 0 ? Localizer.Format("EqProfileFileUnreadable", list.Unreadable, EqProfiles.FilePath) : null;
        }
        catch (Exception ex) when (EqProfiles.IsFailure(ex))
        {
            _profiles = [];
            _fileNote = ex.Message;
        }
        Raise(nameof(Profiles));
        _selected = null;
        Selected = Find(chosen);
        Raise(nameof(Selected));
        Raise(nameof(HasSelection));
        if (!keepFailure || !_failed) Settle();
    }

    /// <summary>The listed profile with this name, whatever its case.</summary>
    public EqProfile? Find(string? name)
        => name is null ? null : _profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Save the EQ's bands as shown under a name, replacing a profile of that name when asked to. False when it failed.</summary>
    public bool Save(string name, bool replace)
    {
        if (_disposed) return false;
        try
        {
            EqProfile saved = EqProfiles.Save(name, _eq.Current.Bands, replace);
            Selected = Find(saved.Name);
            Settle();
            return true;
        }
        catch (Exception ex) when (EqProfiles.IsFailure(ex)) { Fail(ex.Message); return false; }
    }

    /// <summary>
    /// Put the chosen profile's bands on the microphone and switch its EQ on:
    /// one <c>setEq</c>. A profile with more or fewer bands than this EQ takes is refused.
    /// </summary>
    public bool Load()
    {
        if (_disposed || _selected is not { } profile) return false;
        EqLimits limits = _eq.Limits;
        if (profile.Bands.Count < limits.MinBands || profile.Bands.Count > limits.MaxBands)
        {
            Fail(Localizer.Format("EqProfileBandCount", profile.Name, profile.Bands.Count, limits.MinBands, limits.MaxBands));
            return false;
        }
        _eq.Replace(new MicEq { Enabled = true, Bands = profile.Bands });
        Settle();
        return true;
    }

    /// <summary>Give a profile another name; it stays chosen. False when it failed.</summary>
    public bool Rename(EqProfile profile, string newName)
    {
        if (_disposed) return false;
        try
        {
            EqProfile renamed = EqProfiles.Rename(profile.Name, newName);
            Selected = Find(renamed.Name);
            Settle();
            return true;
        }
        catch (Exception ex) when (EqProfiles.IsFailure(ex)) { Fail(ex.Message); return false; }
    }

    /// <summary>Delete a profile. False when it failed.</summary>
    public bool Delete(EqProfile profile)
    {
        if (_disposed) return false;
        try
        {
            EqProfiles.Delete(profile.Name);
            Settle();
            return true;
        }
        catch (Exception ex) when (EqProfiles.IsFailure(ex)) { Fail(ex.Message); return false; }
    }

    /// <summary>Show why an action failed, until the next one.</summary>
    private void Fail(string error)
    {
        _failed = true;
        Show(error);
    }

    /// <summary>Show only what the file itself has to say, if anything.</summary>
    private void Settle()
    {
        _failed = false;
        Show(_fileNote);
    }

    private void Show(string? error)
    {
        if (!Set(ref _error, error, nameof(Error))) return;
        Raise(nameof(HasError));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EqProfiles.Changed -= Refresh;
        EqProfiles.Renamed -= OnRenamed;
    }
}
