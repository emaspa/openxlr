using Microsoft.Extensions.Logging;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

public sealed partial class MixerService
{
    // The rule as the last read found it, and whether this run deleted a
    // rule that WirePlumber may still be applying.
    private AudioClockRule.RuleFile _clockRule = new(null, null);
    private bool _clockRuleRead;
    private bool _clockRuleRemoved;
    private int _restartingAudio;
    private readonly Lock _clockRuleGate = new();

    /// <summary>Where the audio clock rule lives; a test points it elsewhere.</summary>
    internal string ClockRulePath { get; init; } = AudioClockRule.DefaultPath;

    /// <summary>The program that restarts WirePlumber; a test hands in a script.</summary>
    internal string Systemctl { get; init; } = "systemctl";

    /// <summary>
    /// The Flatpak's daemon cannot write the host's WirePlumber
    /// configuration or restart the host's services, so it has no choice.
    /// </summary>
    internal bool ClockChoiceAvailable { get; init; } = !FlatpakEnvironment.IsFlatpak;

    private const string FlatpakRefusal = "not available in the Flatpak, which cannot change the host's WirePlumber; see the manual's recipe in docs/manual.md#garbled-mic";

    /// <summary>
    /// Read the rule file again, once per sweep: it is the record of the
    /// choice and someone can change or delete it by hand. True when what
    /// the state reports changed.
    /// </summary>
    private bool SyncClockRule()
    {
        if (!ClockChoiceAvailable) return false;
        AudioClockRule.RuleFile now = AudioClockRule.Read(ClockRulePath);
        lock (_clockRuleGate)
        {
            if (_clockRuleRead && now == _clockRule) return false;
            _clockRule = now;
            _clockRuleRead = true;
            return true;
        }
    }

    /// <summary>The choice in the state: the rule, whether it waits for a restart, and the devices it can name.</summary>
    private AudioClockChoice? ClockChoice(MixerClock? clock)
    {
        if (!ClockChoiceAvailable) return null;
        if (!_clockRuleRead) SyncClockRule();
        AudioClockRule.RuleFile rule;
        bool removed;
        lock (_clockRuleGate) (rule, removed) = (_clockRule, _clockRuleRemoved);
        return AudioClockChoice.From(rule.Node, clock, removed, ClockOptions(clock)) with { Problem = rule.Problem };
    }

    /// <summary>
    /// The devices the clock can be given to: the mixer's hardware inputs
    /// and outputs in its graph (the clock's driver and followers) and the
    /// driven unit's input, ALSA nodes only, since the rule is an ALSA
    /// monitor rule. The unit's input first, then inputs, then outputs.
    /// </summary>
    private List<AudioClockOption> ClockOptions(MixerClock? clock)
    {
        var options = new List<AudioClockOption>();
        string? unit = _mixer.Built ? _mixer.UnitInputNode : null;
        if (unit is not null && AudioClockRule.IsValidNode(unit))
            options.Add(AudioClockOption.For(unit, _devices.SupportedUnitFor(unit)?.Name
                ?? (clock?.Driver?.Name == unit ? clock.Driver.Description : null)
                ?? clock?.Followers.FirstOrDefault(f => f.Name == unit)?.Description ?? unit));
        if (clock?.Driver is { } driver) options.Add(AudioClockOption.For(driver.Name, driver.Description));
        foreach (ClockFollower follower in clock?.Followers ?? []) options.Add(AudioClockOption.For(follower.Name, follower.Description));
        return [.. options.Where(o => AudioClockRule.IsValidNode(o.Node)).DistinctBy(o => o.Node)
            .OrderBy(o => o.Node == unit ? 0 : o.Kind == "input" ? 1 : 2)];
    }

    /// <summary>
    /// <c>setAudioClock</c>: write the rule that makes <paramref name="node"/>
    /// the audio clock, or with null delete it (Automatic). Only a device
    /// the state offers can be named, or the one already chosen. Nothing is
    /// restarted; the rule applies when WirePlumber next starts. Null on success.
    /// </summary>
    public string? SetAudioClock(string? node)
    {
        if (!ClockChoiceAvailable) return "setAudioClock: " + FlatpakRefusal;
        if (node is not null && !AudioClockRule.IsValidNode(node))
            return "setAudioClock: value must be null or the node name of an ALSA device";
        AudioClockRule.RuleFile current = AudioClockRule.Read(ClockRulePath);
        if (node is not null && node != current.Node
            && !ClockOptions(NamedClock(_mixer.Built ? _mixer.GraphClock : null)).Any(o => o.Node == node))
            return $"setAudioClock: '{node}' is not one of the mixer's devices in clockChoice.options";
        if (node == current.Node && current.Problem is null)
        {
            SyncClockRule();
            return null;
        }
        try { AudioClockRule.Save(node, ClockRulePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("audio clock rule not written: {msg}", ex.Message);
            return "setAudioClock: " + ex.Message;
        }
        lock (_clockRuleGate) if (node is null && current.Node is not null) _clockRuleRemoved = true;
        _log.LogInformation(node is null
            ? "audio clock: removed {path}; WirePlumber chooses again after it restarts"
            : "audio clock: wrote {path} for {node}; it applies after WirePlumber restarts", ClockRulePath, node);
        SyncClockRule();
        Changed?.Invoke();
        return null;
    }

    /// <summary>
    /// <c>restartAudioServices</c>: restart WirePlumber so the audio clock
    /// rule applies (<see cref="AudioClockRule.RestartArguments"/>). Every
    /// device stops for a moment while its nodes are made again; the sweep
    /// links OpenXLR's graph to them as it does after a replug. One restart
    /// at a time. Null on success.
    /// </summary>
    public async Task<string?> RestartAudioServicesAsync()
    {
        if (!ClockChoiceAvailable) return "restartAudioServices: " + FlatpakRefusal;
        if (Interlocked.CompareExchange(ref _restartingAudio, 1, 0) != 0) return "restartAudioServices: a restart is already running";
        try
        {
            _log.LogInformation("restarting WirePlumber for the audio clock rule");
            ProcessResult result;
            try { result = await ProcessRunner.RunAsync(Systemctl, AudioClockRule.RestartArguments, TimeSpan.FromSeconds(30)); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return "restartAudioServices: " + ex.Message;
            }
            if (!result.Ok)
            {
                string why = result.TimedOut ? "timed out" : result.Stderr.Trim() is { Length: > 0 } text ? text : $"exit code {result.ExitCode}";
                _log.LogWarning("WirePlumber did not restart: {why}", why);
                return "restartAudioServices: systemctl --user restart wireplumber failed: " + why;
            }
            lock (_clockRuleGate) _clockRuleRemoved = false;
            Changed?.Invoke();
            return null;
        }
        finally { Volatile.Write(ref _restartingAudio, 0); }
    }
}
