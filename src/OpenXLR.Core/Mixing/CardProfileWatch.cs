using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// A driven unit's card as the daemon last checked it: its PipeWire
/// device.name, the active profile, whether that profile gives the card a
/// capture node, the profile OpenXLR switched it from in this connection
/// (null when it switched nothing), and a sentence for the user while the
/// card has no capture (null otherwise).
/// </summary>
public sealed record CardProfileStatus(
    [property: JsonPropertyName("card")] string Card,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("capture")] bool Capture,
    [property: JsonPropertyName("switchedFrom")] string? SwitchedFrom = null,
    [property: JsonPropertyName("problem")] string? Problem = null);

/// <summary>A line for the journal from one card check.</summary>
public readonly record struct CardProfileNote(bool Warning, string Text);

/// <summary>
/// Watches the card of one driven unit from the moment it connects. The
/// USB device is visible before WirePlumber has created and profiled the
/// card, so the first checks come every <see cref="Spacing"/> for up to
/// <see cref="Checks"/> tries.
///
/// A card with a UCM split (<c>parksUcm</c>, the Wave XLR Pro) is parked on
/// pro-audio as before and then left alone. Any other card on a profile that
/// plays but has no capture node is switched, once per connection, to the
/// profile that keeps the same output and adds an input
/// (<see cref="CardProfile.CaptureProfileFor"/>). When there is no such
/// profile, the switch fails, or the card goes back to a profile without
/// capture after the switch, the watch reports it in
/// <see cref="CardProfileStatus.Problem"/> and does not switch again. After
/// the first checks it looks every <see cref="SlowSpacing"/> while the unit
/// is driven, so a problem clears once the user fixes the profile and a
/// later change to a profile without capture is reported.
/// </summary>
public sealed class CardProfileWatch
{
    internal const int Checks = 60;
    internal static readonly TimeSpan Spacing = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan SlowSpacing = TimeSpan.FromSeconds(30);

    private readonly string _unit;
    private readonly IReadOnlyList<string> _fragments;
    private readonly bool _parksUcm;
    private readonly (ushort Vendor, ushort Product)? _usbId;
    private readonly Func<long> _clock;
    private int _checksLeft = Checks;
    private long _next;
    private (string From, string To)? _switched;
    private string? _flipSaid;
    private volatile CardProfileStatus? _status;

    /// <param name="unit">The unit's name, for the sentences.</param>
    /// <param name="fragments">The node-name fragments that find the unit's card, most specific first.</param>
    /// <param name="parksUcm">Park a UCM split on pro-audio and switch nothing else (the Wave XLR Pro).</param>
    /// <param name="switched">
    /// A switch made earlier in the same connection (a reconnect after a USB
    /// error), so it is not made a second time.
    /// </param>
    /// <param name="usbId">The unit's USB ids: a card that states other ones is never taken for its card.</param>
    /// <param name="clock">Milliseconds; tests move it on.</param>
    public CardProfileWatch(string unit, IReadOnlyList<string> fragments, bool parksUcm,
        (string From, string To)? switched = null, (ushort Vendor, ushort Product)? usbId = null, Func<long>? clock = null)
    {
        _unit = unit;
        _fragments = fragments;
        _parksUcm = parksUcm;
        _usbId = usbId;
        _switched = switched;
        _switchSeen = switched is not null;
        _clock = clock ?? (() => Environment.TickCount64);
        _next = _clock();
    }

    /// <summary>What the last check found; null until the card has been found once.</summary>
    public CardProfileStatus? Status => _status;

    /// <summary>The profile this connection switched the card from and to, if it did.</summary>
    public (string From, string To)? Switched => _switched;

    /// <summary>The fragment that found the card, which a restore hands back.</summary>
    public string? Matched { get; private set; }

    /// <summary>The UCM profile parked by this watch, to restore when the daemon stops.</summary>
    public string? Parked { get; private set; }

    /// <summary>Nothing more to check in this connection.</summary>
    public bool Done { get; private set; }

    /// <summary>One look at the card when one is due; the lines it has for the journal.</summary>
    public IReadOnlyList<CardProfileNote> Check()
    {
        List<CardProfileNote> notes = [];
        if (Done || _clock() < _next) return notes;
        if (_fragments.Count == 0) { Done = true; return notes; }
        string first = _fragments[0];
        try
        {
            CardView? card = CardProfile.Read(_fragments, out string? matched, _usbId);
            if (matched is not null)
            {
                if (matched != first && Matched != matched)
                    notes.Add(new(true, $"card {first}: no card carries this unit's serial; found it by the model name {matched} instead"));
                Matched = matched;
            }
            if (card is null) Absent(notes, Matched ?? first);
            else Look(card, Matched ?? first, notes);
        }
        catch (Exception ex)
        {
            notes.Add(new(true, $"card profile check: {ex.Message}"));
            Absent(notes, Matched ?? first);
        }
        return notes;
    }

    private bool FirstChecks => _checksLeft > 0;

    private void Look(CardView card, string label, List<CardProfileNote> notes)
    {
        if (_parksUcm)
        {
            // As before this watch: parked, or already on pro-audio, settles
            // it; anything else is looked at again until the first checks
            // run out. After that the card is only watched for a problem.
            if (FirstChecks && CardProfile.ParkProAudio(card) is { } parked)
            {
                Parked = parked;
                notes.Add(new(false, $"card {label}: UCM profile {parked} parked, pro-audio active"));
                _status = new(card.Name, "pro-audio", true);
                Done = true;
                return;
            }
            if (FirstChecks && card.ActiveName == "pro-audio")
            {
                _status = new(card.Name, card.ActiveName, card.Capture);
                Done = true;
                return;
            }
            if (card.Capture)
            {
                Settle(card, label, notes);
                if (FirstChecks) Wait(notes, label, card); else Done = true;
                return;
            }
            if (FirstChecks) Wait(notes, label, card);
            else Report(card, Missing(card), notes, label);
            return;
        }
        if (card.Capture)
        {
            bool ours = Settle(card, label, notes);
            // After a switch the first checks go on, so a session manager that
            // puts the old profile back is seen; otherwise a slow look is enough.
            if (ours && FirstChecks) Next();
            else { _checksLeft = 0; Next(); }
            return;
        }
        bool plays = card.Active is { Capture: false, Outputs.Count: > 0 };
        if (!plays)
        {
            // Off, or a profile whose classes do not say: the session manager
            // may still be bringing the card up.
            if (FirstChecks) Wait(notes, label, card);
            else Report(card, Missing(card), notes, label);
            return;
        }
        if (_switched is { } before)
        {
            // Never a second switch in one connection: whatever put the
            // profile back would only put it back again.
            if (!_switchSeen && FirstChecks) { Next(); return; }
            string problem = _switchSeen
                ? $"{_unit} has no microphone input: OpenXLR switched its audio card to {before.To}, and something else put it back on {card.ActiveName}, " +
                  "which has none. OpenXLR leaves it there. " + ChooseOne
                : Lacks(card.ActiveName) + $", and switching it to {before.To} did not take. " + ChooseOne;
            if (_flipSaid != card.ActiveName)
            {
                _flipSaid = card.ActiveName;
                notes.Add(new(true, $"card {label}: profile {card.ActiveName} is active after OpenXLR switched it to {before.To}; not switching it a second time"));
            }
            Report(card, problem, notes, label, said: true);
            return;
        }
        if (CardProfile.CaptureProfileFor(card) is not { } better)
        {
            Report(card, Lacks(card.ActiveName) + ", and the card offers no profile with the same output and an input. " + ChooseOne, notes, label);
            return;
        }
        try { CardProfile.Switch(card, better); }
        catch (Exception ex)
        {
            Report(card, Lacks(card.ActiveName) + $". Switching it to {better.Name} failed ({ex.Message}). " + ChooseOne, notes, label);
            return;
        }
        _switched = (card.ActiveName, better.Name);
        notes.Add(new(true, $"card {label}: profile {card.ActiveName} has no microphone input; switched it to {better.Name}"));
        _status = new(card.Name, card.ActiveName, false, card.ActiveName);
        Next();
    }

    private bool _switchSeen;

    /// <summary>The card has capture: clear any problem. True when it is on the profile this connection switched it to.</summary>
    private bool Settle(CardView card, string label, List<CardProfileNote> notes)
    {
        bool ours = _switched is { } made && made.To == card.ActiveName;
        if (ours) _switchSeen = true;
        if (_status?.Problem is not null)
            notes.Add(new(false, $"card {label}: profile {card.ActiveName} has a microphone input again"));
        _status = new(card.Name, card.ActiveName, true, _switched?.From);
        _flipSaid = null;
        return ours;
    }

    private string Lacks(string profile) => $"{_unit} has no microphone input: its audio card is on the profile {profile}, which has none";

    private string Missing(CardView card) => card.Active is null || card.ActiveName == "off" || card.Active.Outputs is { Count: 0 }
        ? $"{_unit} has no microphone input: its audio card is off. {ChooseOne}"
        : Lacks(card.ActiveName) + ". " + ChooseOne;

    private const string ChooseOne = "Choose a profile with an input for it in the desktop's sound settings, or with pactl set-card-profile.";

    private void Report(CardView card, string problem, List<CardProfileNote> notes, string label, bool said = false)
    {
        if (!said && _status?.Problem != problem) notes.Add(new(true, $"card {label}: {problem}"));
        _status = new(card.Name, card.ActiveName, false, _switched?.From, problem);
        Next();
    }

    /// <summary>
    /// No card in this dump, or no dump. During the first checks that counts
    /// down; a card never found by then is not looked for again, and one seen
    /// before (gone while the session manager restarts) is looked for slowly.
    /// </summary>
    private void Absent(List<CardProfileNote> notes, string label)
    {
        if (FirstChecks) { Wait(notes, label, null); return; }
        Next();
    }

    /// <summary>One of the first checks found nothing settled: try again, and say what is left when they run out.</summary>
    private void Wait(List<CardProfileNote> notes, string label, CardView? card)
    {
        _checksLeft--;
        if (FirstChecks) { _next = _clock() + (long)Spacing.TotalMilliseconds; return; }
        if (_parksUcm) notes.Add(new(true, $"card {label}: gave up waiting for a pro-audio profile"));
        if (card is null)
        {
            if (_status is null) Done = true;
            else Next();
            return;
        }
        if (card.Capture) { Done = _parksUcm; Next(); return; }
        Report(card, Missing(card), notes, label);
    }

    /// <summary>The next look: soon while the first checks last, then slowly for as long as the unit is driven.</summary>
    private void Next()
    {
        if (_checksLeft > 0) _checksLeft--;
        _next = _clock() + (long)(_checksLeft > 0 ? Spacing : SlowSpacing).TotalMilliseconds;
    }
}
