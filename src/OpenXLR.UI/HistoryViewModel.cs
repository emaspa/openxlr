using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>One end of the daemon's undo history: the step's id for the guard, its kind and its English label.</summary>
public sealed record HistoryStepItem(long Step, string Kind, string Label);

/// <summary>
/// The daemon's undo history as the UI sees it: the step Ctrl+Z would undo
/// and the one Ctrl+Shift+Z would redo, from the state's <c>history</c>.
/// Before it asks for either, the UI sends its sliders' pending values and
/// lets go of their touch guards, so the newest move is in the history
/// before it is undone and the faders follow the state that comes back.
/// </summary>
public sealed class HistoryViewModel : ViewModelBase
{
    private readonly Func<string, long, Task<string?>>? _send;

    /// <param name="send">Sends <c>undo</c> or <c>redo</c> with the step id and resolves to the daemon's refusal or null.</param>
    internal HistoryViewModel(Func<string, long, Task<string?>>? send) => _send = send;

    private bool _reported;
    /// <summary>False with a daemon from before undo, which reports no <c>history</c>.</summary>
    public bool IsReported { get => _reported; private set => Set(ref _reported, value); }

    private HistoryStepItem? _undo;
    public HistoryStepItem? Undo { get => _undo; private set { if (Set(ref _undo, value)) Raise(nameof(CanUndo)); } }

    private HistoryStepItem? _redo;
    public HistoryStepItem? Redo { get => _redo; private set { if (Set(ref _redo, value)) Raise(nameof(CanRedo)); } }

    public bool CanUndo => Undo is not null;
    public bool CanRedo => Redo is not null;

    private string? _note;
    /// <summary>The daemon's sentence about what the last undo or redo skipped, or null.</summary>
    public string? Note { get => _note; private set => Set(ref _note, value); }

    public void Apply(JsonNode? history)
    {
        IsReported = history is JsonObject;
        Undo = Step(history?["undo"]);
        Redo = Step(history?["redo"]);
        Note = history?["note"] is JsonValue note && note.TryGetValue(out string? text) && text.Length > 0 ? text : null;
    }

    public void Reset() => Apply(null);

    /// <summary>
    /// Undo, or with <paramref name="redo"/> redo, the step the state names.
    /// Resolves to the sentence the UI shows, the refusal or what was
    /// skipped, or null when there is nothing to say or nothing to do.
    /// </summary>
    public async Task<string?> RunAsync(bool redo)
    {
        if ((redo ? Redo : Undo) is not { } step || _send is null) return null;
        SliderSync.FlushPending();
        SliderSync.ReleaseTouchGuards();
        string? error = await _send(redo ? "redo" : "undo", step.Step);
        return error ?? Note;
    }

    private static HistoryStepItem? Step(JsonNode? node)
        => node is JsonObject
           && node["step"] is JsonValue id && id.TryGetValue(out long step)
           && node["kind"] is JsonValue kind && kind.TryGetValue(out string? kindText)
           ? new(step, kindText, node["label"] is JsonValue label && label.TryGetValue(out string? text) ? text : kindText)
           : null;
}
