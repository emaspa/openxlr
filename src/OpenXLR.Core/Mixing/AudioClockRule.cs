using System.Text.RegularExpressions;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The audio clock the user picked: a WirePlumber rule in the user's
/// configuration that gives one ALSA node a <c>priority.driver</c> above
/// every number WirePlumber hands out, so PipeWire takes the timing of any
/// graph that node runs in from it. The file is the record of the choice;
/// nothing else stores it. WirePlumber reads the rule when it starts and
/// sets the property as it creates the node, and PipeWire does not take a
/// new <c>priority.driver</c> on a node that exists, so a written or
/// removed rule applies after WirePlumber restarts (<see cref="RestartArguments"/>)
/// or at the next login.
/// </summary>
public static partial class AudioClockRule
{
    /// <summary>
    /// The drop-in's name. WirePlumber merges the fragments in name order,
    /// so 57 comes after the packaged rules (50 to 55), whose 2200 it
    /// replaces on a unit's input, and after the 56 the manual's
    /// hand-written recipe uses.
    /// </summary>
    public const string FileName = "57-openxlr-clock.conf";

    /// <summary>
    /// Above every number WirePlumber gives an ALSA node: about 2100 for a
    /// USB input, 2600 for a pro-audio input, 2200 from the packaged rules.
    /// </summary>
    public const int PriorityDriver = 3000;

    // The first line of every rule OpenXLR writes. A file without it is
    // someone else's and is neither replaced nor removed.
    private const string Marker = "# Written by OpenXLR: the audio clock chosen in Options, Audio.";

    private const int MaxBytes = 16 * 1024;

    /// <summary>The drop-in under the user's configuration: ~/.config/wireplumber/wireplumber.conf.d.</summary>
    public static string DefaultPath => Path.Combine(OpenXlrPaths.ConfigHome, "wireplumber", "wireplumber.conf.d", FileName);

    /// <summary>
    /// The arguments for <c>systemctl</c> that make the rule apply. Only
    /// WirePlumber restarts: its ALSA monitor creates the device nodes and
    /// applies the rules as it does, and when it stops PipeWire destroys
    /// the nodes it created, so the new WirePlumber creates them again with
    /// the rule. PipeWire and pipewire-pulse keep running, and with them
    /// every node, module and link OpenXLR made that does not end at a
    /// device.
    /// </summary>
    public static readonly IReadOnlyList<string> RestartArguments = ["--user", "restart", "wireplumber.service"];

    /// <summary>
    /// A node name the ALSA monitor makes: <c>alsa_input.</c> or
    /// <c>alsa_output.</c>, then only letters, digits, '_', '-' and '.',
    /// which is all WirePlumber leaves in a device name. The rule matches
    /// <c>node.name</c> exactly, and a name of these characters cannot
    /// close the quoted string, start a regular expression or a negation,
    /// or reach any other node.
    /// </summary>
    public static bool IsValidNode(string? node) => node is { Length: <= 255 } && NodeName().IsMatch(node);

    [GeneratedRegex(@"\Aalsa_(input|output)\.[A-Za-z0-9_.\-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex NodeName();

    [GeneratedRegex(@"\{\s*node\.name\s*=\s*""(?<name>[^""\r\n]*)""\s*\}", RegexOptions.CultureInvariant)]
    private static partial Regex MatchLine();

    /// <summary>The rule that makes <paramref name="node"/> the audio clock, for a node <see cref="IsValidNode"/> accepts.</summary>
    public static string Render(string node)
    {
        if (!IsValidNode(node)) throw new ArgumentException("not an ALSA node name WirePlumber makes", nameof(node));
        return $$"""
            {{Marker}}
            # OpenXLR replaces this file when you pick another clock and deletes it
            # for Automatic. It applies once WirePlumber restarts, from Restart audio
            # now in Options or at the next login. {{PriorityDriver}} is above the number
            # WirePlumber gives any device, a pro-audio input's 2600 included, so
            # PipeWire takes the timing of the node's graph from this node and
            # resamples the other devices in it.
            monitor.alsa.rules = [
              {
                matches = [
                  { node.name = "{{node}}" }
                ]
                actions = {
                  update-props = {
                    priority.driver = {{PriorityDriver}}
                  }
                }
              }
            ]

            """;
    }

    /// <summary>
    /// The node the rule at <paramref name="path"/> makes the clock, null
    /// when there is no rule. <see cref="RuleFile.Problem"/> says why a file
    /// there cannot be used: it is not OpenXLR's, not a regular file, or does
    /// not name one valid node.
    /// </summary>
    public static RuleFile Read(string path)
    {
        string text;
        try
        {
            if (new FileInfo(path).LinkTarget is not null) return new(null, $"{path} is a symbolic link; delete it by hand");
            if (!File.Exists(path)) return new(null, null);
            text = RegularFile.ReadText(path, MaxBytes);
        }
        catch (FileNotFoundException) { return new(null, null); }
        catch (DirectoryNotFoundException) { return new(null, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, $"{path} cannot be read: {ex.Message}");
        }
        if (!text.StartsWith(Marker, StringComparison.Ordinal))
            return new(null, $"{path} was not written by OpenXLR; edit or delete it by hand");
        MatchCollection matches = MatchLine().Matches(text);
        if (matches.Count != 1 || matches[0].Groups["name"].Value is not { } node || !IsValidNode(node))
            return new(null, $"{path} does not name one ALSA node; pick the clock again or delete the file");
        return new(node, null);
    }

    /// <summary>
    /// Write the rule for <paramref name="node"/> at <paramref name="path"/>,
    /// or with null delete it. A file OpenXLR did not write stays as it is
    /// and the call throws <see cref="IOException"/>, as it does for a
    /// symbolic link or a failed write.
    /// </summary>
    public static void Save(string? node, string path)
    {
        if (node is not null && !IsValidNode(node)) throw new ArgumentException("not an ALSA node name WirePlumber makes", nameof(node));
        if (new FileInfo(path).LinkTarget is not null) throw new IOException($"{path} is a symbolic link; delete it by hand");
        if (File.Exists(path))
        {
            string current = RegularFile.ReadText(path, MaxBytes);
            if (!current.StartsWith(Marker, StringComparison.Ordinal))
                throw new IOException($"{path} was not written by OpenXLR; edit or delete it by hand");
        }
        else if (Directory.Exists(path)) throw new IOException($"{path} is a directory");
        if (node is null)
        {
            File.Delete(path);
            return;
        }
        // Readable like the packaged rules; the directory keeps its own mode.
        OpenXlrPaths.WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(Render(node)), privatePermissions: false,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    /// <summary>What <see cref="Read"/> found: the chosen node, or why the file cannot be used.</summary>
    public sealed record RuleFile(string? Node, string? Problem);
}

/// <summary>
/// The audio clock choice in the state: the node the rule makes the clock
/// (null for Automatic), whether it waits for WirePlumber to restart, whether
/// that node drives the mixer's graph now, the devices that can be chosen,
/// and why the rule file cannot be used, if it cannot.
/// </summary>
public sealed record AudioClockChoice(string? Node, bool Pending, bool Active, IReadOnlyList<AudioClockOption> Options)
{
    public string? Problem { get; init; }

    /// <summary>
    /// The choice as the rule and the clock stand.
    ///
    /// A chosen node is pending while it runs in the mixer's graph, as its
    /// driver or a follower, without the rule's <see cref="AudioClockRule.PriorityDriver"/>:
    /// WirePlumber has not read the rule since it was written. A chosen node
    /// that is not in the graph (unplugged, idle) is not pending, since a
    /// restart would not show it either. Automatic is pending while a node
    /// in the graph still carries the rule's number after
    /// <paramref name="removed"/> (OpenXLR deleted its rule in this run).
    /// </summary>
    /// <param name="options">The devices of the mixer that the rule can name; the chosen node is added when the graph does not show it.</param>
    public static AudioClockChoice From(string? node, MixerClock? clock, bool removed, IReadOnlyList<AudioClockOption> options)
    {
        var running = new Dictionary<string, int?>(StringComparer.Ordinal);
        if (clock?.Driver is { } driver) running[driver.Name] = driver.PriorityDriver;
        foreach (ClockFollower follower in clock?.Followers ?? []) running.TryAdd(follower.Name, follower.PriorityDriver);
        bool pending = node is not null
            ? running.TryGetValue(node, out int? priority) && priority != AudioClockRule.PriorityDriver
            : removed && running.Values.Any(p => p == AudioClockRule.PriorityDriver);
        List<AudioClockOption> listed = [.. options.Where(o => AudioClockRule.IsValidNode(o.Node)).DistinctBy(o => o.Node)];
        if (node is not null && !listed.Any(o => o.Node == node)) listed.Add(AudioClockOption.For(node, node));
        return new(node, pending, node is not null && clock?.Driver?.Name == node, listed);
    }
}

/// <summary>A device the audio clock can be given to: its node name, a name to show, and <c>input</c> or <c>output</c>.</summary>
public sealed record AudioClockOption(string Node, string Description, string Kind)
{
    public static AudioClockOption For(string node, string description)
        => new(node, description, node.StartsWith("alsa_input.", StringComparison.Ordinal) ? "input" : "output");
}
