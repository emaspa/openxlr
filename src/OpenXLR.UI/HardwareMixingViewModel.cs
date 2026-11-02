using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>
/// Hardware mixing on the Wave XLR Pro, as the mixer state reports it
/// (docs/api.md, hardware mixing): whether the unit is wanted to build the
/// mixes its outputs play, whether it does, why not, and what the mode turns
/// off. The Options window's "Runs on" choice, the SUBMIXER badge and the
/// greyed controls all read it from here.
/// </summary>
public sealed class HardwareMixingViewModel : ViewModelBase
{
    /// <summary>The dry run can take a sweep of the daemon's own; generous, as for the diagnostics.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    private readonly DaemonClient _client;

    public HardwareMixingViewModel(DaemonClient client) => _client = client;

    private bool _capable, _wanted, _active, _busy;
    private string? _reason, _note, _refusal;
    private IReadOnlyDictionary<string, string> _disabled = new Dictionary<string, string>();

    /// <summary>Raised after a state push changed what the mode turns off.</summary>
    public event Action? DisabledChanged;

    /// <summary>
    /// The choice shows while the driven unit can run the mode, and also while
    /// the mode is wanted with no such unit attached, so it can be switched back.
    /// </summary>
    public bool IsOffered => _capable || _wanted;

    /// <summary>The lit side of the switch: the daemon's choice.</summary>
    public bool OnUnit => _wanted;
    public bool OnComputer => !_wanted;

    /// <summary>The unit builds the mixes now: the SUBMIXER badge shows.</summary>
    public bool Active => _active;

    /// <summary>The first thing in the way, as the daemon names it (<c>budget</c>, <c>unitAway</c>, ...), or null.</summary>
    public string? Reason => _reason;

    /// <summary>
    /// Why the mode is wanted and does not run, shown under the switch: the
    /// daemon's own sentence. Nothing while it runs or is not wanted.
    /// </summary>
    public string? Note => _wanted && !_active ? _note ?? Localizer.Text("HardwareMixingWaiting") : null;
    public bool HasNote => Note is not null;

    /// <summary>The daemon refused the last switch.</summary>
    public string? Refusal { get => _refusal; private set { if (Set(ref _refusal, value)) Raise(nameof(HasRefusal)); } }
    public bool HasRefusal => _refusal is not null;

    public bool CanSwitch => !_busy;

    /// <summary>What the mode turns off while it runs, by the daemon's fixed keys, with the sentence each control shows.</summary>
    public IReadOnlyDictionary<string, string> Disabled => _disabled;

    /// <summary>The sentence for one key of <see cref="Disabled"/>, or null when the mode leaves that alone.</summary>
    public string? DisabledSentence(string key) => _disabled.TryGetValue(key, out string? sentence) ? sentence : null;

    /// <summary>Latency compensation leaves out the mixes the unit builds; said under its checkbox in Options.</summary>
    public string? LatencyNote => DisabledSentence("latencyCompensation");
    public bool HasLatencyNote => LatencyNote is not null;

    public string BadgeTip => Localizer.Text("HardwareMixingBadgeTip");

    /// <summary>Apply a state push: the mixer state and whether the driven unit can run the mode.</summary>
    internal void Apply(JsonNode? mixer, bool capable)
    {
        JsonNode? mode = mixer?["hardwareMixing"];
        bool wanted = mode?["wanted"] is JsonValue w && w.TryGetValue(out bool wv) && wv;
        bool active = mode?["active"] is JsonValue a && a.TryGetValue(out bool av) && av;
        string? reason = Text(mode?["reason"]);
        string? note = Text(mode?["note"]);
        var disabled = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (active && mode?["disabled"] is JsonObject map)
            foreach ((string key, JsonNode? value) in map)
                if (Text(value) is { } sentence) disabled[key] = sentence;

        bool offered = IsOffered;
        _capable = capable;
        if (Set(ref _wanted, wanted, nameof(OnUnit))) Raise(nameof(OnComputer));
        Set(ref _active, active, nameof(Active));
        _reason = reason;
        _note = note;
        if (offered != IsOffered) Raise(nameof(IsOffered));
        Raise(nameof(Reason));
        Raise(nameof(Note));
        Raise(nameof(HasNote));
        if (!disabled.SequenceEqual(_disabled))
        {
            _disabled = disabled;
            Raise(nameof(Disabled));
            Raise(nameof(LatencyNote));
            Raise(nameof(HasLatencyNote));
            DisabledChanged?.Invoke();
        }
    }

    /// <summary>The connection dropped: nothing is known until the next state.</summary>
    internal void Reset()
    {
        Refusal = null;
        Apply(null, false);
    }

    /// <summary>
    /// The dry run, for the confirm window: what this layout loses and how far
    /// each jack is turned down. Null when the daemon did not answer.
    /// </summary>
    public async Task<HardwareMixingPlan?> CheckAsync()
    {
        JsonNode? reply = await _client.CheckHardwareMixingAsync(CheckTimeout);
        return reply is null ? null : HardwareMixingPlan.From(reply);
    }

    /// <summary>
    /// Switch to <c>hardware</c> or <c>software</c>. The lit side follows the
    /// daemon's next state; a refusal is kept in <see cref="Refusal"/> and
    /// returned.
    /// </summary>
    public async Task<string?> SwitchAsync(bool hardware)
    {
        if (_busy) return null;
        _busy = true;
        Raise(nameof(CanSwitch));
        Refusal = null;
        try
        {
            string? error = await _client.SetMixingAsync(hardware ? "hardware" : "software");
            Refusal = error;
            return error;
        }
        finally
        {
            _busy = false;
            Raise(nameof(CanSwitch));
            // A press that switched a side's toggle off turns it back to the daemon's choice.
            Raise(nameof(OnUnit));
            Raise(nameof(OnComputer));
        }
    }

    /// <summary>A press on a side that is already lit, or while a switch runs: the toggles show the daemon's choice again.</summary>
    internal void Reassert()
    {
        Raise(nameof(OnUnit));
        Raise(nameof(OnComputer));
    }

    internal static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0 ? text : null;
}

/// <summary>
/// The daemon's dry run of hardware mixing, as the confirm window lists it:
/// whether the mode can run, why not, what the layout loses and how far each
/// jack is turned down on the way in.
/// </summary>
public sealed record HardwareMixingPlan(
    bool Active, string? Reason, string? Note,
    IReadOnlyList<string> Losses, IReadOnlyList<(string Jack, double Db)> JackDrops)
{
    /// <summary>Only a layout with more channels than pairs is refused; anything else waits for the unit.</summary>
    public bool CanSwitch => Reason != "budget";

    internal static HardwareMixingPlan From(JsonNode reply)
    {
        JsonNode plan = reply["plan"] as JsonObject ?? reply;
        bool active = plan["active"] is JsonValue a && a.TryGetValue(out bool av) && av;
        var losses = new List<string>();
        if (plan["disabled"] is JsonObject disabled)
            foreach ((string _, JsonNode? value) in disabled.OrderBy(p => p.Key, StringComparer.Ordinal))
                if (HardwareMixingViewModel.Text(value) is { } sentence) losses.Add(sentence);
        var drops = new List<(string, double)>();
        if (plan["entryOffsetsDb"] is JsonObject offsets)
            foreach (string jack in new[] { "hp1", "hp2", "lineout" })
                if (offsets[jack] is JsonValue v && v.TryGetValue(out double db) && db > 0) drops.Add((jack, db));
        return new HardwareMixingPlan(active, HardwareMixingViewModel.Text(plan["reason"]),
            HardwareMixingViewModel.Text(plan["note"]), losses, drops);
    }

    /// <summary>The jack's name as the confirm window says it.</summary>
    public static string JackName(string jack) => jack switch
    {
        "hp1" => Localizer.Text("HardwareMixingJackHp1"),
        "hp2" => Localizer.Text("HardwareMixingJackHp2"),
        _ => Localizer.Text("HardwareMixingJackLineOut"),
    };

    /// <summary>The lines the confirm window shows, in order: the losses, then each jack's drop.</summary>
    public IEnumerable<string> Lines() => Losses.Concat(JackDrops.Select(d =>
        Localizer.Format("HardwareMixingJackDrop", JackName(d.Jack), d.Db.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture))));
}
