using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>
/// A microphone EQ profile as the daemon lists it: a built-in one, named
/// from its key in the UI's language, or a saved one, named as saved. It
/// holds no channel and no switch, so it loads on any microphone.
/// </summary>
public sealed record EqProfile(string Id, string Name, bool Builtin, IReadOnlyList<EqBand> Bands)
{
    /// <summary>The name the list shows: a built-in profile's in the UI's language and marked as built in.</summary>
    public string DisplayName => Builtin
        ? Localizer.Format("EqProfileBuiltInName", MicSetupPreset.NameOf(Id[EqProfileIds.BuiltinPrefix.Length..]) ?? Name)
        : Name;

    /// <summary>What loading the profile puts on a microphone: its bands with the EQ on.</summary>
    public MicEq Eq => new() { Enabled = true, Bands = Bands };

    /// <summary>The EQ holds this profile, by <see cref="MicEq.Holds"/>: an EQ that is off holds none.</summary>
    public bool HeldBy(MicEq eq) => eq.Holds(Eq);

    public override string ToString() => DisplayName;
}

/// <summary>
/// Why the saved profiles are not all listed, as the daemon reports it:
/// <c>unreadable</c> entries, a <c>damaged</c> or <c>tooLarge</c> file, or
/// one that <c>failed</c> to read, with the daemon's own sentence.
/// </summary>
public sealed record EqProfileProblem(string Reason, int Count, string Path, string Message)
{
    /// <summary>The problem in the UI's language.</summary>
    public string Text => Reason switch
    {
        "unreadable" => Localizer.Format("EqProfileFileUnreadable", Count, Path),
        "damaged" => Localizer.Format("EqProfileFileDamaged", Path),
        "tooLarge" => Localizer.Format("EqProfileFileTooLarge", Path),
        _ => Message,
    };
}

/// <summary>The EQ profile commands the editors send. The daemon client sends them; a test records them.</summary>
public interface IEqProfileCommands
{
    /// <summary>Save bands under a name, in place of a saved profile of that name when <paramref name="replace"/>; null or the daemon's refusal.</summary>
    Task<string?> SaveEqProfileAsync(string name, IReadOnlyList<EqBand> bands, bool replace);
    Task<string?> RenameEqProfileAsync(string name, string newName);
    Task<string?> DeleteEqProfileAsync(string name);
    /// <summary>Put a profile's bands on a microphone with its EQ on.</summary>
    Task<string?> LoadEqProfileAsync(string channel, string profile);
    /// <summary>Ask the daemon to read the profile file again, for a hand edit.</summary>
    Task ListEqProfilesAsync();
}

/// <summary>
/// The EQ profiles the daemon lists, as this UI last heard them, and the
/// commands that change them. Every open editor lists from here, so a change
/// any client makes shows in all of them with the state that follows it.
/// </summary>
public sealed class EqProfileLibrary(IEqProfileCommands commands)
{
    public const int MaximumProfiles = 64;
    public const int MaximumNameLength = 80;

    private string? _seen;
    // Renames sent from here, old name to new, so a profile chosen in any editor stays
    // chosen. One is kept until a list shows it done, or the daemon refused it: the state
    // and the command's answer may arrive in either order.
    private readonly Dictionary<string, string> _renames = new(StringComparer.OrdinalIgnoreCase);

    public IEqProfileCommands Commands { get; } = commands;

    /// <summary>Built-in profiles first, then the saved ones in the daemon's order.</summary>
    public IReadOnlyList<EqProfile> Profiles { get; private set; } = [];

    public EqProfileProblem? Problem { get; private set; }

    /// <summary>Raised on the UI thread when the list or the problem changed.</summary>
    public event Action? Changed;

    public static bool ValidName(string? name)
        => name is { Length: > 0 and <= MaximumNameLength } && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);

    /// <summary>The saved profiles, without the built-in ones.</summary>
    public IEnumerable<EqProfile> Saved => Profiles.Where(p => !p.Builtin);

    /// <summary>Take <c>eqProfiles</c> and <c>eqProfileProblem</c> from a state push; a state like the last one changes nothing.</summary>
    public void Apply(JsonNode? state)
    {
        JsonNode? list = state?["eqProfiles"], problem = state?["eqProfileProblem"];
        string seen = (list?.ToJsonString() ?? "null") + "|" + (problem?.ToJsonString() ?? "null");
        if (seen == _seen) return;
        _seen = seen;
        Profiles = list is JsonArray entries ? [.. entries.Select(ReadProfile).OfType<EqProfile>()] : [];
        Problem = ReadProblem(problem);
        Changed?.Invoke();
        foreach ((string old, string renamed) in _renames.ToArray())
            if (FindSaved(old) is null && FindSaved(renamed) is not null) _renames.Remove(old);
    }

    /// <summary>Forget the list when the daemon goes away.</summary>
    public void Reset()
    {
        _seen = null;
        Profiles = [];
        Problem = null;
        Changed?.Invoke();
    }

    private static EqProfile? ReadProfile(JsonNode? node)
    {
        try
        {
            if (node?["id"]?.GetValue<string>() is not { } id || node["name"]?.GetValue<string>() is not { } name
                || node["bands"] is not JsonArray bands || bands.Count is < MicEq.MinimumBands or > MicEq.MaximumBands)
                return null;
            bool builtin = node["builtin"]?.GetValue<bool>() ?? false;
            if (!id.StartsWith(builtin ? EqProfileIds.BuiltinPrefix : EqProfileIds.SavedPrefix, StringComparison.Ordinal)) return null;
            var read = new List<EqBand>();
            foreach (JsonNode? band in bands)
            {
                if (band is null) return null;
                var value = new EqBand
                {
                    Enabled = band["enabled"]!.GetValue<bool>(),
                    Type = band["type"]!.GetValue<string>(),
                    Frequency = band["frequency"]!.GetValue<double>(),
                    Gain = band["gain"]!.GetValue<double>(),
                    Q = band["q"]!.GetValue<double>(),
                };
                if (MicEq.Check(value) is not null) return null;
                read.Add(value);
            }
            return new(id, name, builtin, read);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException) { return null; }
    }

    private static EqProfileProblem? ReadProblem(JsonNode? node)
    {
        try
        {
            return node?["reason"]?.GetValue<string>() is { } reason
                ? new(reason, node["count"]?.GetValue<int>() ?? 0, node["path"]?.GetValue<string>() ?? "", node["message"]?.GetValue<string>() ?? reason)
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>The saved profile with this name, whatever its case.</summary>
    public EqProfile? FindSaved(string? name)
        => name is null ? null : Saved.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public EqProfile? FindId(string? id) => id is null ? null : Profiles.FirstOrDefault(p => p.Id == id);

    /// <summary>The name a saved profile was renamed to from here, or null.</summary>
    internal string? RenamedTo(string name) => _renames.GetValueOrDefault(name);

    internal async Task<string?> RenameAsync(string name, string newName)
    {
        _renames[name] = newName;
        string? error = await Commands.RenameEqProfileAsync(name, newName);
        if (error is not null || FindSaved(name) is null) _renames.Remove(name);
        // Every editor settles on what is listed now, chosen profile and all.
        Changed?.Invoke();
        return error;
    }
}

/// <summary>
/// The profile row of one microphone's EQ editor: the built-in and saved
/// profiles, the one the EQ holds, and what went wrong last. Picking a
/// profile in the list loads it. The list shows the profile the EQ holds
/// now, so after an edit a pick of the same profile loads it again; the
/// choices the row makes for itself, as the list or the EQ changes, send
/// nothing. Every editor lists from the same library, and a change made in
/// one shows in the others.
/// </summary>
public sealed class EqProfilesViewModel : ViewModelBase, IDisposable
{
    private readonly MicEqViewModel _eq;
    private readonly EqProfileLibrary _library;
    private IReadOnlyList<EqProfile> _profiles = [];
    private EqProfile? _selected;
    // The profile last picked or saved here, while it is listed: Save offers a saved one's name.
    private EqProfile? _picked;
    private string? _error, _fileNote;
    // A saved profile to prefer once the list shows it: the one just saved here.
    private string? _choose;
    // The row chooses for itself: what the list reports back meanwhile is not a pick.
    private bool _quiet;
    // A pick puts its bands on the EQ: the EQ's changes meanwhile are the pick's own.
    private bool _loading;
    private bool _disposed, _failed;

    public EqProfilesViewModel(MicEqViewModel eq, EqProfileLibrary library)
    {
        _eq = eq;
        _library = library;
        _library.Changed += Refresh;
        _eq.Changed += Follow;
        Refresh();
    }

    /// <summary>The EQ the profiles load onto.</summary>
    public MicEqViewModel Eq => _eq;

    public EqProfileLibrary Library => _library;

    public IReadOnlyList<EqProfile> Profiles => _profiles;

    /// <summary>
    /// The profile chosen in the list: the one the EQ holds, or null for the
    /// placeholder. Setting a listed profile other than the chosen one is a
    /// pick and loads it; the list binds here, so that is what choosing in it
    /// does. The row's own choices go round this setter and send nothing.
    /// </summary>
    public EqProfile? Selected
    {
        get => _selected;
        set
        {
            if (value is not null && !_profiles.Contains(value)) return;
            if (_quiet || value is null || Equals(value, _selected))
            {
                Choose(value);
                return;
            }
            Choose(value);
            _ = Load(value);
        }
    }

    /// <summary>A saved profile is chosen: Rename and Delete act on it. A built-in one keeps its name.</summary>
    public bool HasSavedSelection => _selected is { Builtin: false };

    /// <summary>
    /// The name Save offers: the chosen saved profile's, or else that of the
    /// profile last picked or saved here when it is a saved one, so picking
    /// one, changing a band and saving offers to replace it. Empty otherwise:
    /// a built-in profile is never replaced.
    /// </summary>
    public string SaveName => (_selected is { Builtin: false } chosen ? chosen : _picked is { Builtin: false } picked ? picked : null)?.Name ?? "";

    /// <summary>Why the last action failed, or why some saved profiles are not listed; null when all is well.</summary>
    public string? Error => _error;

    public bool HasError => _error is not null;

    /// <summary>List the library again after it changed, choosing the profile the EQ holds, a renamed one under its new name.</summary>
    public void Refresh() => Read(keepFailure: false);

    /// <summary>
    /// Ask the daemon to read the file again in case it was edited by hand,
    /// as the editor comes forward; a failure still on show stays, since
    /// closing its dialog brings the editor forward too.
    /// </summary>
    public void Reread()
    {
        if (_disposed) return;
        _ = _library.Commands.ListEqProfilesAsync();
    }

    private void Read(bool keepFailure)
    {
        if (_disposed) return;
        EqProfile? chosen = _selected;
        bool quiet = _quiet;
        _quiet = true;
        try
        {
            _profiles = _library.Profiles;
            _fileNote = _library.Problem?.Text;
            Raise(nameof(Profiles));
            EqProfile? saved = _library.FindSaved(_choose);
            if (saved is not null)
            {
                _choose = null;
                _picked = saved;
            }
            else _picked = Relisted(_picked);
            // The list may have dropped its choice with the old items: tell it again whatever the choice is.
            _selected = Held(saved, Relisted(chosen));
            Raise(nameof(Selected));
            Raise(nameof(HasSavedSelection));
        }
        finally { _quiet = quiet; }
        if (!keepFailure || !_failed) Settle();
    }

    /// <summary>A profile as the list shows it now: by its id, or a saved one renamed from here under its new name; null once it is gone.</summary>
    private EqProfile? Relisted(EqProfile? profile)
        => profile is null ? null
            : _library.FindId(profile.Id)
                ?? (profile.Builtin || _library.RenamedTo(profile.Name) is not { } renamed ? null : _library.FindSaved(renamed));

    /// <summary>The listed profile the EQ holds now: the first of <paramref name="preferred"/> it holds, else the first in the list; null when it holds none.</summary>
    private EqProfile? Held(params EqProfile?[] preferred)
    {
        MicEq shown = _eq.Current;
        return preferred.FirstOrDefault(p => p is not null && p.HeldBy(shown)) ?? _profiles.FirstOrDefault(p => p.HeldBy(shown));
    }

    /// <summary>
    /// The EQ changed, here or from anywhere else: the list shows the profile
    /// it holds now, keeping the chosen one while it holds that, and the
    /// placeholder when it holds none. Nothing is loaded. A comparison keeps
    /// the switch on, so it keeps the choice too.
    /// </summary>
    private void Follow()
    {
        if (_disposed || _loading) return;
        Choose(Held(_selected));
    }

    /// <summary>Show a choice the row made itself.</summary>
    private void Choose(EqProfile? value)
    {
        bool quiet = _quiet;
        _quiet = true;
        try
        {
            if (!Set(ref _selected, value, nameof(Selected))) return;
            Raise(nameof(HasSavedSelection));
        }
        finally { _quiet = quiet; }
    }

    /// <summary>The listed saved profile with this name, whatever its case.</summary>
    public EqProfile? Find(string? name) => _library.FindSaved(name);

    /// <summary>Why the file cannot take a change now, or null: the daemon writes no file it could not read whole.</summary>
    private string? Unwritable => _library.Problem is { Reason: not "failed" } ? _fileNote : null;

    /// <summary>Save the EQ's bands as shown under a name, replacing a saved profile of that name when asked to. False when it failed.</summary>
    public async Task<bool> Save(string name, bool replace)
    {
        if (_disposed) return false;
        name = name.Trim();
        IReadOnlyList<EqBand> bands = _eq.Current.Bands;
        string? refused = !EqProfileLibrary.ValidName(name) ? Localizer.Format("EqProfileNameRule", EqProfileLibrary.MaximumNameLength)
            : bands.Count is < MicEq.MinimumBands or > MicEq.MaximumBands || bands.Any(b => MicEq.Check(b) is not null) ? Localizer.Text("EqProfileBandsInvalid")
            : Unwritable
            ?? (Find(name) is { } existing ? replace ? null : Localizer.Format("EqProfileNameExists", existing.Name)
                : _library.Saved.Count() >= EqProfileLibrary.MaximumProfiles ? Localizer.Format("EqProfileLimit", EqProfileLibrary.MaximumProfiles) : null);
        if (refused is not null) { Fail(refused); return false; }
        // Kept until a list shows the profile saved, which may come before or after the answer.
        _choose = name;
        string? error = await _library.Commands.SaveEqProfileAsync(name, bands, replace);
        if (error is not null) { _choose = null; Fail(error); return false; }
        if (Find(name) is { } saved)
        {
            _picked = saved;
            Choose(Held(saved, _selected));
        }
        Settle();
        return true;
    }

    /// <summary>
    /// Put a picked profile's bands on the microphone and switch its EQ on:
    /// one <c>loadEqProfile</c>, shown at once. A profile with more or fewer
    /// bands than this EQ takes is refused here, and stays chosen with the
    /// reason until the EQ or the list next changes.
    /// </summary>
    private async Task Load(EqProfile profile)
    {
        if (_disposed) return;
        _picked = profile;
        EqLimits limits = _eq.Limits;
        if (profile.Bands.Count < limits.MinBands || profile.Bands.Count > limits.MaxBands)
        {
            Fail(Localizer.Format("EqProfileBandCount", profile.DisplayName, profile.Bands.Count, limits.MinBands, limits.MaxBands));
            return;
        }
        _loading = true;
        try { _eq.ShowLoaded(profile.Eq); }
        finally { _loading = false; }
        Follow();
        Settle();
        if (await _library.Commands.LoadEqProfileAsync(_eq.Channel, profile.Id) is { } error) Fail(error);
    }

    /// <summary>Give a saved profile another name; it stays chosen in every editor. False when it failed.</summary>
    public async Task<bool> Rename(EqProfile profile, string newName)
    {
        if (_disposed || profile.Builtin) return false;
        newName = newName.Trim();
        string? refused = !EqProfileLibrary.ValidName(newName) ? Localizer.Format("EqProfileNameRule", EqProfileLibrary.MaximumNameLength)
            : Find(profile.Name) is null ? Localizer.Format("EqProfileMissing", profile.Name)
            : Unwritable
            ?? (_library.Saved.Any(p => !string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase))
                ? Localizer.Format("EqProfileNameExists", newName) : null);
        if (refused is not null) { Fail(refused); return false; }
        string? error = await _library.RenameAsync(profile.Name, newName);
        if (error is not null) { Fail(error); return false; }
        Settle();
        return true;
    }

    /// <summary>Delete a saved profile. False when it failed.</summary>
    public async Task<bool> Delete(EqProfile profile)
    {
        if (_disposed || profile.Builtin) return false;
        string? refused = Find(profile.Name) is null ? Localizer.Format("EqProfileMissing", profile.Name) : Unwritable;
        if (refused is not null) { Fail(refused); return false; }
        string? error = await _library.Commands.DeleteEqProfileAsync(profile.Name);
        if (error is not null) { Fail(error); return false; }
        Settle();
        return true;
    }

    /// <summary>Show why an action failed, until the next one.</summary>
    private void Fail(string error)
    {
        if (_disposed) return;
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
        _library.Changed -= Refresh;
        _eq.Changed -= Follow;
    }
}
