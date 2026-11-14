using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// The undo history's side of the hub: recording around the mixer commands,
/// the barriers, and <c>undo</c> and <c>redo</c>. A failure while recording
/// is logged and never fails the command it wraps.
/// </summary>
public sealed partial class WebSocketHub
{
    /// <summary>The one history every client shares.</summary>
    internal MixHistory History { get; } = new();

    /// <summary>
    /// Run a mixer command through <see cref="MixerService.Apply"/>. A
    /// recorded command reads the values it may change before and after and
    /// records what moved; a barrier clears the history once it succeeded; any
    /// other command costs nothing more. <paramref name="client"/> is the
    /// WebSocket the command came on, null over HTTP.
    /// </summary>
    private string? ApplyMixer(Command cmd, Guid? client)
    {
        switch (HistoryPolicy.ClassOf(cmd.Cmd))
        {
            case HistoryClass.Recorded:
                return ApplyRecorded(cmd, client);
            case HistoryClass.Barrier:
            {
                string? error = _mixer.Apply(cmd, client);
                if (error is null) ClearHistory();
                return error;
            }
            default:
                return _mixer.Apply(cmd, client);
        }
    }

    private string? ApplyRecorded(Command cmd, Guid? client)
    {
        HistoryTarget? target = null;
        Dictionary<SceneKey, object>? before = null;
        try
        {
            target = HistoryPolicy.TargetOf(cmd, _mixer.ExclusivePeersOf, _mixer.ExclusiveGroupMembers);
            if (target is not null) before = _mixer.ReadSceneValues(target.Fields, target.Keep);
        }
        catch (Exception ex) { _log.LogWarning(ex, "history: could not read the values before {command}", cmd.Cmd); }

        string? error = _mixer.Apply(cmd, client);
        if (error is not null || target is null || before is null) return error;
        try
        {
            if (_mixer.ReadSceneValues(target.Fields, target.Keep) is not { } after) return null;
            var changes = new List<SceneChange>();
            foreach ((SceneKey key, object was) in before)
                if (after.TryGetValue(key, out object? now) && !SceneValues.Same(was, now)) changes.Add(new(key, was, now));
            if (History.Record(client?.ToString("N") ?? "http", target, HistoryPolicy.LabelOf(target, _mixer.LayoutConfig, _mixer.InsertName), changes))
                _stateBroadcasts.Signal();
        }
        catch (Exception ex) { _log.LogWarning(ex, "history: could not record {command}", cmd.Cmd); }
        return null;
    }

    /// <summary>Empty the history after a barrier, and tell the clients.</summary>
    private void ClearHistory()
    {
        if (History.Clear()) _stateBroadcasts.Signal();
    }

    /// <summary>
    /// <c>undo</c> or <c>redo</c>, after <see cref="CommandValidation.CheckHistory"/>
    /// and under the install gate. Each value is written only where it is
    /// still what the step left; one moved since is skipped and named in
    /// <c>history.note</c>. A step that wrote nothing is dropped with an
    /// error. Only a mixer that cannot act at all keeps the step. Null on
    /// success.
    /// </summary>
    private string? ApplyHistory(Command cmd)
    {
        bool redo = cmd.Cmd == "redo";
        string verb = cmd.Cmd;
        if (History.Begin(redo) is not { } begun) return $"{verb}: nothing to {verb}";
        (HistoryStep step, SceneChange[] changes, string label) = begun;
        if (CommandValidation.HistoryStep(cmd) is long id && id != step.Id) return $"{verb}: the history changed";

        ScenePatchEntry[] entries = [.. changes.Select(c => redo
            ? new ScenePatchEntry(c.Key, c.Before, c.After)
            : new ScenePatchEntry(c.Key, c.After, c.Before))];
        if (_mixer.ApplyScenePatch(entries) is not { } outcomes) return $"{verb}: {_mixer.NotBuiltError}";

        var done = new List<SceneChange>();
        int changed = 0, gone = 0, failed = 0;
        for (int i = 0; i < outcomes.Count; i++)
        {
            switch (outcomes[i])
            {
                case ScenePatchOutcome.Applied or ScenePatchOutcome.AlreadyThere: done.Add(changes[i]); break;
                case ScenePatchOutcome.Changed: changed++; break;
                case ScenePatchOutcome.Gone: gone++; break;
                default: failed++; break;
            }
        }
        string? reason = changed > 0 ? "it changed since"
            : gone > 0 ? "its channel or mix no longer exists"
            : failed > 0 ? "it could not be written" : null;
        string? note = reason is null ? null : $"{char.ToUpperInvariant(verb[0])}{verb[1..]} skipped part of {label}: {reason}.";
        History.Complete(step, redo, done, done.Count > 0 ? note : null);
        _stateBroadcasts.Signal();
        return done.Count > 0 ? null : $"{verb}: {label} was skipped: {reason}";
    }
}
