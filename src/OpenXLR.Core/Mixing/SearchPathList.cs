using System.Collections.Concurrent;
using System.Diagnostics;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The directories a colon-separated search variable names: CLAP_PATH,
/// VST3_PATH, LV2_PATH and LADSPA_PATH for plugins, PATH for helper
/// programs. Only
/// absolute entries are kept. An empty segment or a relative name such as
/// <c>.</c> or <c>plugins</c> would be read against the daemon's working
/// directory, so plugin code or a helper would be loaded from wherever the
/// daemon happened to start. Each ignored entry is logged once per variable.
/// </summary>
internal static class SearchPathList
{
    private static readonly ConcurrentDictionary<(string Variable, string Entry), bool> Reported = new();

    /// <summary>
    /// The absolute entries of <paramref name="value"/>, in order, and the
    /// entries left out. With <paramref name="expandHome"/>, a leading
    /// <c>~</c> or <c>~/</c> is the home directory, as lilv reads LV2_PATH.
    /// </summary>
    internal static IReadOnlyList<string> Split(string? value, bool expandHome, out IReadOnlyList<string> ignored)
    {
        var kept = new List<string>();
        var dropped = new List<string>();
        if (value is not null)
        {
            foreach (string entry in value.Split(':'))
            {
                string candidate = expandHome ? ExpandHome(entry) : entry;
                if (Path.IsPathFullyQualified(candidate)) kept.Add(candidate);
                else dropped.Add(entry);
            }
        }
        ignored = dropped;
        return kept;
    }

    /// <summary>The absolute entries of a variable's value, logging each one left out.</summary>
    internal static IReadOnlyList<string> Absolute(string variable, string? value, bool expandHome, out int ignored)
    {
        IReadOnlyList<string> kept = Split(value, expandHome, out IReadOnlyList<string> dropped);
        foreach (string entry in dropped) Report(variable, entry);
        ignored = dropped.Count;
        return kept;
    }

    /// <summary>The absolute entries of an environment variable, logging each one left out.</summary>
    internal static IReadOnlyList<string> FromEnvironment(string variable, bool expandHome = false)
        => Absolute(variable, Environment.GetEnvironmentVariable(variable), expandHome, out _);

    /// <summary>
    /// A plugin format's search directories: the variable's absolute entries,
    /// or <paramref name="defaults"/> when it is unset or names none.
    /// <paramref name="adjusted"/> says the variable is set and something in
    /// it was left out, so a library that reads it on its own must be handed
    /// the cleaned list instead.
    /// </summary>
    internal static IReadOnlyList<string> Directories(string variable, Func<IReadOnlyList<string>> defaults, out bool adjusted)
    {
        string? configured = Environment.GetEnvironmentVariable(variable);
        adjusted = false;
        if (configured is null) return defaults();
        IReadOnlyList<string> kept = Absolute(variable, configured, expandHome: true, out int ignored);
        adjusted = ignored > 0;
        return kept.Count > 0 ? kept : defaults();
    }

    /// <summary>
    /// Hand a helper that reads <paramref name="variable"/> itself only its
    /// absolute entries. Nothing changes while the daemon's value is unset or
    /// already absolute; a value with no absolute entry is removed, so the
    /// helper falls back to its own default.
    /// </summary>
    internal static void ApplyAbsolute(ProcessStartInfo start, string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) is not { } value) return;
        IReadOnlyList<string> kept = Absolute(variable, value, expandHome: false, out int ignored);
        if (ignored == 0) return;
        if (kept.Count > 0) start.Environment[variable] = string.Join(':', kept);
        else start.Environment.Remove(variable);
    }

    /// <summary>Log an ignored entry the first time it is seen; true when this call logged it.</summary>
    internal static bool Report(string variable, string entry)
    {
        if (!Reported.TryAdd((variable, entry), true)) return false;
        Console.Error.WriteLine(entry.Length == 0
            ? $"Ignoring an empty entry in {variable}: only absolute paths are searched."
            : $"Ignoring \"{entry}\" in {variable}: only absolute paths are searched.");
        return true;
    }

    private static string ExpandHome(string entry)
    {
        if (entry != "~" && !entry.StartsWith("~/", StringComparison.Ordinal)) return entry;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length == 0 ? entry : home + entry[1..];
    }
}
