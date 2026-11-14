using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Threading;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>
/// One end of the daemon's undo history: the step's id for the guard, its
/// kind, its English label, the ids it names and how many things a burst
/// joined. An insert step names the insert in its channel's or mix's chain.
/// A hardware step names its control, and the additional interface it
/// changed in <see cref="Unit"/>.
/// </summary>
public sealed record HistoryStepItem(long Step, string Kind, string Label,
    string? Channel = null, string? Mix = null, string? Group = null, int Count = 1, string? Insert = null,
    string? Control = null, string? Unit = null);

/// <summary>The names the UI shows for the ids a step names; null for an id it does not know.</summary>
internal sealed record HistoryNames(Func<string, string?> Channel, Func<string, string?> Mix, Func<string, string?> Group)
{
    /// <summary>An insert's name by its chain key (a channel id or <c>mix:&lt;id&gt;</c>) and its id.</summary>
    public Func<string, string, string?>? Insert { get; init; }
}

/// <summary>
/// The daemon's undo history as the UI sees it: the step Ctrl+Z would undo
/// and the one Ctrl+Shift+Z would redo, from the state's <c>history</c>,
/// named in the UI's language for the header's buttons and their tooltips.
/// Before it asks for either, the UI sends its sliders' pending values and
/// lets go of their touch guards, so the newest move is in the history
/// before it is undone and the faders follow the state that comes back.
/// After each, <see cref="Notice"/> says what was done, skipped or refused
/// for <see cref="NoticeHold"/>.
/// </summary>
public sealed class HistoryViewModel : ViewModelBase
{
    /// <summary>How long the header says what an undo or redo did.</summary>
    public static readonly TimeSpan NoticeHold = TimeSpan.FromSeconds(3);

    private readonly HistoryNames? _names;

    /// <param name="send">Sends <c>undo</c> or <c>redo</c> with the step id and resolves to the daemon's refusal or null.</param>
    /// <param name="names">Looks up channel, mix and group names; without it every step reads its English label.</param>
    internal HistoryViewModel(Func<string, long, Task<string?>>? send, HistoryNames? names = null)
    {
        Send = send;
        _names = names;
    }

    /// <summary>Sends <c>undo</c> or <c>redo</c>; tests put a fake daemon here.</summary>
    internal Func<string, long, Task<string?>>? Send { get; set; }

    /// <summary>Starts the notice's hold and calls back when it ends; tests run it by hand.</summary>
    internal Func<TimeSpan, Action, IDisposable> StartNoticeHold { get; set; } =
        (after, elapsed) => DispatcherTimer.RunOnce(elapsed, after);

    private bool _reported;
    /// <summary>False with a daemon from before undo, which reports no <c>history</c>; the header pair is hidden then.</summary>
    public bool IsReported { get => _reported; private set => Set(ref _reported, value); }

    private HistoryStepItem? _undo;
    public HistoryStepItem? Undo { get => _undo; private set { if (Set(ref _undo, value)) Raise(nameof(CanUndo)); } }

    private HistoryStepItem? _redo;
    public HistoryStepItem? Redo { get => _redo; private set { if (Set(ref _redo, value)) Raise(nameof(CanRedo)); } }

    public bool CanUndo => Undo is not null;
    public bool CanRedo => Redo is not null;

    private string _undoTip = Localizer.Text("NothingToUndo");
    /// <summary>The undo button's tooltip: the step it takes back and the key, or that there is none.</summary>
    public string UndoTip { get => _undoTip; private set => Set(ref _undoTip, value); }

    private string _redoTip = Localizer.Text("NothingToRedo");
    /// <summary>The redo button's tooltip: the step it puts back and the key, or that there is none.</summary>
    public string RedoTip { get => _redoTip; private set => Set(ref _redoTip, value); }

    private string? _note;
    /// <summary>The daemon's sentence about what the last undo or redo skipped, or null.</summary>
    public string? Note { get => _note; private set => Set(ref _note, value); }

    private string? _notice;
    /// <summary>What the last undo or redo did, the part it skipped or why it was refused; null once its hold ends.</summary>
    public string? Notice { get => _notice; private set => Set(ref _notice, value); }

    private IDisposable? _noticeHold;

    /// <summary>The step an undo (or a redo) just wrote, until a state shows it moved to the other side.</summary>
    private (long Step, bool Redo)? _awaited;

    public void Apply(JsonNode? history)
    {
        IsReported = history is JsonObject;
        Undo = Step(history?["undo"]);
        Redo = Step(history?["redo"]);
        Note = history?["note"] is JsonValue note && note.TryGetValue(out string? text) && text.Length > 0 ? text : null;
        UndoTip = Undo is { } undo ? Localizer.Format("UndoStep", Name(undo)) : Localizer.Text("NothingToUndo");
        RedoTip = Redo is { } redo ? Localizer.Format("RedoStep", Name(redo)) : Localizer.Text("NothingToRedo");
        // The reply to undo comes before the state that carries its note, so
        // the note replaces the notice once the state shows the step moved.
        if (_awaited is { } awaited && Moved(awaited))
        {
            _awaited = null;
            if (Note is { } skipped && Notice is not null) Notice = skipped;
        }
    }

    public void Reset()
    {
        Apply(null);
        _awaited = null;
    }

    /// <summary>
    /// Undo, or with <paramref name="redo"/> redo, the step the state names.
    /// Resolves to the sentence the header shows, which is also
    /// <see cref="Notice"/>: the step's name, the part that was skipped or
    /// the refusal. Null when there is nothing to do.
    /// </summary>
    public async Task<string?> RunAsync(bool redo)
    {
        if ((redo ? Redo : Undo) is not { } step || Send is not { } send) return null;
        SliderSync.FlushPending();
        SliderSync.ReleaseTouchGuards();
        string name = Name(step);
        _awaited = null;
        string? error = await send(redo ? "redo" : "undo", step.Step);
        string sentence;
        if (error is not null) sentence = error;
        else if (Moved((step.Step, redo))) sentence = Note ?? Done(redo, name);
        else
        {
            _awaited = (step.Step, redo);
            sentence = Done(redo, name);
        }
        ShowNotice(sentence);
        return sentence;
    }

    /// <summary>
    /// A step in the UI's language: its kind's phrase with the names the UI
    /// shows for its ids, and how many more a burst joined. A kind this
    /// version does not know, or an id it cannot name, reads the daemon's
    /// English label.
    /// </summary>
    internal string Name(HistoryStepItem step)
    {
        string? channel = step.Channel is { } c ? _names?.Channel(c) : null;
        string? mix = step.Mix is { } m ? _names?.Mix(m) : null;
        string? group = step.Group is { } g ? _names?.Group(g) : null;
        string? chain = step.Mix is not null ? mix : channel;
        string? insert = step.Insert is { } i && (step.Mix is { } key ? $"mix:{key}" : step.Channel) is { } chainKey
            ? _names?.Insert?.Invoke(chainKey, i) : null;
        string? phrase = step.Kind switch
        {
            "sendLevel" when channel is not null && mix is not null => Localizer.Format("HistorySendLevel", channel, mix),
            "channelVolume" when channel is not null => Localizer.Format("HistoryChannelVolume", channel),
            "mixVolume" when mix is not null => Localizer.Format("HistoryMixVolume", mix),
            "sendMute" when channel is not null && mix is not null => Localizer.Format("HistorySendMute", channel, mix),
            "mixMute" when mix is not null => Localizer.Format("HistoryMixMute", mix),
            "exclusiveGroup" when group is not null => Localizer.Format("HistoryExclusiveGroup", group),
            "eq" when channel is not null => Localizer.Format("HistoryEq", channel),
            "mixEq" when mix is not null => Localizer.Format("HistoryMixEq", mix),
            "noiseRemoval" when channel is not null => Localizer.Format("HistoryNoiseRemoval", channel),
            "voiceChanger" when channel is not null => Localizer.Format("HistoryVoiceChanger", channel),
            "compressor" when channel is not null => Localizer.Format("HistoryCompressor", channel),
            "expander" when channel is not null => Localizer.Format("HistoryExpander", channel),
            "insertParam" when insert is not null && chain is not null => Localizer.Format("HistoryInsertParam", insert, chain),
            "insertBypass" when insert is not null && chain is not null => Localizer.Format("HistoryInsertBypass", insert, chain),
            "insertName" when insert is not null && chain is not null => Localizer.Format("HistoryInsertName", insert, chain),
            "insertChain" when chain is not null => Localizer.Format("HistoryInsertChain", chain),
            "lowCut" when channel is not null => Localizer.Format("HistoryLowCut", channel),
            "clipGuard" when channel is not null => Localizer.Format("HistoryClipGuard", channel),
            // The unit's own controls. A step on an additional interface
            // names no channel and reads the daemon's label with the unit's name.
            "unitGain" when channel is not null => Localizer.Format("HistoryUnitGain", channel),
            "unitLowCut" when channel is not null => Localizer.Format("HistoryLowCut", channel),
            "unitClipGuard" when channel is not null => Localizer.Format("HistoryClipGuard", channel),
            "unitExpander" when channel is not null => Localizer.Format("HistoryExpander", channel),
            "unitCompressor" when channel is not null => Localizer.Format("HistoryCompressor", channel),
            "unitVoiceTune" when channel is not null => Localizer.Format("HistoryVoiceTune", channel),
            "unitHeadphones" when step.Unit is null && step.Control == "hp2VolumeDb" => Localizer.Text("HistoryHeadphones2"),
            "unitHeadphones" when step.Unit is null => Localizer.Text("HistoryHeadphones"),
            "unitCrossfade" when step.Unit is null => Localizer.Text("HistoryCrossfade"),
            "unitLowImpedance" when step.Unit is null => Localizer.Text("HistoryLowImpedance"),
            "unitAuxLevel" when step.Unit is null => Localizer.Text("HistoryAuxLevel"),
            "unitLineInGain" when step.Unit is null => Localizer.Text("HistoryLineInGain"),
            "monitorDry" when mix is not null => Localizer.Format("HistoryMonitorDry", mix),
            "ducking" => Localizer.Text("HistoryDucking"),
            _ => null,
        };
        if (phrase is null) return step.Label;
        return step.Count > 1 ? Localizer.Format("HistoryMore", phrase, step.Count - 1) : phrase;
    }

    private static string Done(bool redo, string name)
        => redo ? Localizer.Format("RedidStep", name) : Localizer.Format("UndidStep", name);

    /// <summary>Whether the state shows the step on the side an undo (or a redo) moves it to.</summary>
    private bool Moved((long Step, bool Redo) run) => (run.Redo ? Undo : Redo)?.Step == run.Step;

    private void ShowNotice(string text)
    {
        _noticeHold?.Dispose();
        Notice = text;
        _noticeHold = StartNoticeHold(NoticeHold, () => { Notice = null; _awaited = null; });
    }

    private static HistoryStepItem? Step(JsonNode? node)
        => node is JsonObject
           && node["step"] is JsonValue id && id.TryGetValue(out long step)
           && node["kind"] is JsonValue kind && kind.TryGetValue(out string? kindText)
           ? new(step, kindText, node["label"] is JsonValue label && label.TryGetValue(out string? text) ? text : kindText,
               Text(node["channel"]), Text(node["mix"]), Text(node["group"]),
               node["count"] is JsonValue count && count.TryGetValue(out int joined) && joined > 1 ? joined : 1,
               Text(node["insert"]), Text(node["control"]), Text(node["unit"]))
           : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0 ? text : null;
}
