using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>The command the voice changer sends. The daemon client sends it; a test records it.</summary>
public interface IVoiceCommands
{
    /// <summary>Change part of a microphone's voice changer; a null field keeps its value.</summary>
    Task SetVoiceChangerAsync(string channel, bool? enabled = null, string? voice = null, double? pitch = null, double? formant = null);
}

/// <summary>A microphone the Voice changer window can work on.</summary>
public sealed record VoiceMicrophone(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// One tile of the Voice changer window: Off, or a voice. Lit while it is
/// what the microphone plays.
/// </summary>
public sealed class VoiceTileViewModel : ViewModelBase
{
    private bool _selected, _enabled = true;

    internal VoiceTileViewModel(string? voice)
    {
        Voice = voice;
    }

    /// <summary>The voice's id, or null for the Off tile.</summary>
    public string? Voice { get; }

    public bool IsOff => Voice is null;

    public string Name => Voice switch
    {
        null => Localizer.Text("VoiceOff"),
        MicVoice.Custom => Localizer.Text("VoiceCustom"),
        "deep" => Localizer.Text("VoiceDeep"),
        "high" => Localizer.Text("VoiceHigh"),
        "robot" => Localizer.Text("VoiceRobot"),
        "radio" => Localizer.Text("VoiceRadio"),
        "alien" => Localizer.Text("VoiceAlien"),
        "dracula" => Localizer.Text("VoiceDracula"),
        "frankenstein" => Localizer.Text("VoiceFrankenstein"),
        "mummy" => Localizer.Text("VoiceMummy"),
        "werewolf" => Localizer.Text("VoiceWerewolf"),
        "witch" => Localizer.Text("VoiceWitch"),
        _ => Voice,
    };

    /// <summary>What the voice sounds like, in a few words.</summary>
    public string Hint => Voice switch
    {
        null => Localizer.Text("VoiceOffHint"),
        MicVoice.Custom => Localizer.Text("VoiceCustomHint"),
        "deep" => Localizer.Text("VoiceDeepHint"),
        "high" => Localizer.Text("VoiceHighHint"),
        "robot" => Localizer.Text("VoiceRobotHint"),
        "radio" => Localizer.Text("VoiceRadioHint"),
        "alien" => Localizer.Text("VoiceAlienHint"),
        "dracula" => Localizer.Text("VoiceDraculaHint"),
        "frankenstein" => Localizer.Text("VoiceFrankensteinHint"),
        "mummy" => Localizer.Text("VoiceMummyHint"),
        "werewolf" => Localizer.Text("VoiceWerewolfHint"),
        "witch" => Localizer.Text("VoiceWitchHint"),
        _ => "",
    };

    /// <summary>The tile's name for a screen reader, which also says whether it is lit.</summary>
    public string AutomationName => _selected ? Localizer.Format("VoiceTileSelected", Name) : Name;

    public bool IsSelected
    {
        get => _selected;
        internal set { if (Set(ref _selected, value)) Raise(nameof(AutomationName)); }
    }

    /// <summary>False on a voice tile while the voice changer cannot run; Off always takes a press.</summary>
    public bool IsEnabled { get => _enabled; internal set => Set(ref _enabled, value); }
}

/// <summary>
/// The Voice changer window's model: the microphones that have a voice
/// changer, the one the window works on, a tile per voice and Off, the
/// custom voice's pitch and formant, and what the daemon says runs. Every
/// value is one the daemon reported or one the user just set; a report that
/// arrives while an edit is still on its way does not move the control back.
/// </summary>
public sealed class VoiceChangerViewModel : ViewModelBase
{
    /// <summary>How long a recent edit wins over state reports that may still precede it.</summary>
    internal static readonly TimeSpan EchoWait = TimeSpan.FromSeconds(1);

    /// <summary>The microphone a fresh window starts on, when there is one.</summary>
    internal const string DefaultMicrophone = "xlr1";

    private sealed record Report(bool Enabled, string Voice, double Pitch, double Formant, string? Active, bool Available, string? Error)
    {
        public double? LatencyMs { get; init; }
    }

    private readonly IVoiceCommands _commands;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Report> _reports = new(StringComparer.Ordinal);
    private VoiceMicrophone? _selected;
    private bool _enabled, _available, _anyOn;
    private string _voice = MicVoice.DefaultVoice;
    private double _pitch, _formant;
    private string? _active, _error;
    private double? _latencyMs;
    // When the user last changed each part, so a stale report does not undo it.
    private long _choiceAt = long.MinValue, _pitchAt = long.MinValue, _formantAt = long.MinValue;

    public VoiceChangerViewModel(IVoiceCommands commands, TimeProvider? time = null)
    {
        _commands = commands;
        _time = time ?? TimeProvider.System;
        Tiles = [new VoiceTileViewModel(null), .. MicVoice.Voices.Select(v => new VoiceTileViewModel(v))];
        RefreshTiles();
    }

    /// <summary>Off, then every voice in the daemon's order.</summary>
    public IReadOnlyList<VoiceTileViewModel> Tiles { get; }

    /// <summary>Every microphone the daemon reports a voice changer for, in mixer order.</summary>
    public ObservableCollection<VoiceMicrophone> Microphones { get; } = [];

    /// <summary>The daemon reports at least one microphone with a voice changer, so the header button shows.</summary>
    public bool IsReported => Microphones.Count > 0;

    /// <summary>There is a choice of microphone, so the picker takes a press.</summary>
    public bool CanPickMicrophone => Microphones.Count > 1;

    /// <summary>The microphone the tiles and sliders work on.</summary>
    public VoiceMicrophone? SelectedMicrophone
    {
        get => _selected;
        set
        {
            if (value is null || Equals(value, _selected) || !Microphones.Contains(value)) return;
            _selected = value;
            Raise();
            // Another microphone: its own setting, with no edit of the last one holding it back.
            _choiceAt = _pitchAt = _formantAt = long.MinValue;
            Show(_reports.GetValueOrDefault(value.Id));
        }
    }

    /// <summary>Some microphone has a voice on, so the header button lights.</summary>
    public bool AnyOn { get => _anyOn; private set => Set(ref _anyOn, value); }

    /// <summary>The voice changer is on for the shown microphone.</summary>
    public bool Enabled => _enabled;

    /// <summary>The voice chosen for the shown microphone, on or off.</summary>
    public string Voice => _voice;

    /// <summary>The native helper is installed, so a voice can be switched on.</summary>
    public bool Available => _available;

    /// <summary>The custom voice is chosen, so its two sliders show.</summary>
    public bool ShowsCustom => _voice == MicVoice.Custom;

    public double MinShift => MicVoice.MinShift;
    public double MaxShift => MicVoice.MaxShift;
    public double ShiftStep => MicVoice.ShiftStep;

    /// <summary>The custom voice's pitch shift in semitones.</summary>
    public double Pitch
    {
        get => _pitch;
        set
        {
            if (_selected is not { } mic) return;
            double snapped = Math.Clamp(MicVoice.SnapShift(value), MicVoice.MinShift, MicVoice.MaxShift);
            if (!Set(ref _pitch, snapped)) return;
            _pitchAt = _time.GetTimestamp();
            SliderSync.Send($"voicepitch:{mic.Id}", () => _ = _commands.SetVoiceChangerAsync(mic.Id, pitch: snapped));
            Raise(nameof(PitchText));
        }
    }

    /// <summary>The custom voice's formant shift in semitones.</summary>
    public double Formant
    {
        get => _formant;
        set
        {
            if (_selected is not { } mic) return;
            double snapped = Math.Clamp(MicVoice.SnapShift(value), MicVoice.MinShift, MicVoice.MaxShift);
            if (!Set(ref _formant, snapped)) return;
            _formantAt = _time.GetTimestamp();
            SliderSync.Send($"voiceformant:{mic.Id}", () => _ = _commands.SetVoiceChangerAsync(mic.Id, formant: snapped));
            Raise(nameof(FormantText));
        }
    }

    public string PitchText => ShiftText(_pitch);
    public string FormantText => ShiftText(_formant);

    private static string ShiftText(double semitones)
        => Localizer.Format("VoiceShiftValue", semitones.ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture));

    /// <summary>
    /// One line above the tiles: why the voice does not run, else that it is
    /// starting, else the delay it adds.
    /// </summary>
    public string Status
    {
        get
        {
            if (!_enabled) return "";
            if (_error is { Length: > 0 } error) return error;
            if (_active != _voice) return Localizer.Text("VoiceStarting");
            return _latencyMs is double ms ? Localizer.Format("VoiceRunning", Math.Round(ms).ToString(CultureInfo.CurrentCulture)) : "";
        }
    }

    public bool HasStatus => Status.Length > 0;

    /// <summary>True while the line is an error rather than a report.</summary>
    public bool HasError => _enabled && _error is { Length: > 0 };

    /// <summary>
    /// A press on a tile: Off switches the voice changer off, a voice switches
    /// it on with that voice, in one command each, so a press never needs the
    /// state first. A press on the lit tile leaves it lit.
    /// </summary>
    public void Choose(VoiceTileViewModel tile)
    {
        if (_selected is not { } mic) return;
        if (tile.Voice is null)
        {
            if (!_enabled) { RefreshTiles(); return; }
            _enabled = false;
            _choiceAt = _time.GetTimestamp();
            _ = _commands.SetVoiceChangerAsync(mic.Id, enabled: false);
        }
        else
        {
            if (!_available || (_enabled && tile.Voice == _voice) || MicVoice.VoiceIndex(tile.Voice) < 0) { RefreshTiles(); return; }
            _enabled = true;
            _voice = tile.Voice;
            _choiceAt = _time.GetTimestamp();
            _ = _commands.SetVoiceChangerAsync(mic.Id, enabled: true, voice: tile.Voice);
        }
        _reports[mic.Id] = (_reports.GetValueOrDefault(mic.Id) ?? Fresh()) with { Enabled = _enabled, Voice = _voice };
        RaiseChoice();
    }

    private static Report Fresh() => new(false, MicVoice.DefaultVoice, 0, 0, null, false, null);

    private bool Settled(long at) => at == long.MinValue || _time.GetElapsedTime(at) >= EchoWait;

    /// <summary>
    /// Take the mixer's channels from a state push: every present channel
    /// with a <c>voice</c> is a microphone the window offers. Null, or a
    /// mixer that is not running, offers none.
    /// </summary>
    public void Apply(JsonNode? channels)
    {
        var seen = new List<VoiceMicrophone>();
        _reports.Clear();
        foreach (JsonObject channel in (channels as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (channel["voice"] is not JsonObject v || channel["id"]?.GetValue<string>() is not { Length: > 0 } id) continue;
            if (channel["present"]?.GetValue<bool>() == false) continue;
            string name = channel["name"]?.GetValue<string>() is { Length: > 0 } n ? n : id;
            seen.Add(new VoiceMicrophone(id, name));
            string voice = v["voice"]?.GetValue<string>() is string named && MicVoice.VoiceIndex(named) >= 0 ? named : MicVoice.DefaultVoice;
            _reports[id] = new Report(v["enabled"]?.GetValue<bool>() ?? false, voice,
                v["pitch"]?.GetValue<double>() ?? 0, v["formant"]?.GetValue<double>() ?? 0,
                v["active"]?.GetValue<string>(), v["available"]?.GetValue<bool>() ?? false, v["error"]?.GetValue<string>())
            {
                LatencyMs = v["latencyMs"]?.GetValue<double>(),
            };
        }
        bool reported = IsReported;
        if (!seen.SequenceEqual(Microphones))
        {
            VoiceMicrophone? keep = _selected is { } was ? seen.FirstOrDefault(m => m.Id == was.Id) : null;
            Microphones.Clear();
            foreach (VoiceMicrophone each in seen) Microphones.Add(each);
            keep ??= seen.FirstOrDefault(m => m.Id == DefaultMicrophone) ?? seen.FirstOrDefault();
            if (_selected is not null && keep?.Id != _selected.Id) _choiceAt = _pitchAt = _formantAt = long.MinValue;
            _selected = keep;
            // Raised even for the same microphone: the picker lost its item when the list was refilled.
            Raise(nameof(SelectedMicrophone));
            Raise(nameof(CanPickMicrophone));
        }
        if (reported != IsReported) Raise(nameof(IsReported));
        Show(_selected is { } mic ? _reports.GetValueOrDefault(mic.Id) : null);
    }

    /// <summary>The daemon went away: no microphones, nothing lit.</summary>
    public void Reset() => Apply(null);

    /// <summary>Show a microphone's report, keeping the parts the user changed a moment ago.</summary>
    private void Show(Report? report)
    {
        report ??= Fresh();
        _available = report.Available;
        _active = report.Active;
        _latencyMs = report.LatencyMs;
        _error = report.Error;
        if (Settled(_choiceAt))
        {
            _enabled = report.Enabled;
            _voice = report.Voice;
        }
        string? mic = _selected?.Id;
        if (Settled(_pitchAt) && !SliderSync.RecentlyTouched($"voicepitch:{mic}") && report.Pitch != _pitch)
        {
            _pitch = report.Pitch;
            Raise(nameof(Pitch));
            Raise(nameof(PitchText));
        }
        if (Settled(_formantAt) && !SliderSync.RecentlyTouched($"voiceformant:{mic}") && report.Formant != _formant)
        {
            _formant = report.Formant;
            Raise(nameof(Formant));
            Raise(nameof(FormantText));
        }
        Raise(nameof(Available));
        RaiseChoice();
    }

    private void RaiseChoice()
    {
        Raise(nameof(Enabled));
        Raise(nameof(Voice));
        Raise(nameof(ShowsCustom));
        Raise(nameof(Status));
        Raise(nameof(HasStatus));
        Raise(nameof(HasError));
        // The shown microphone as the window holds it, every other as reported.
        AnyOn = (_selected is not null && _enabled) || _reports.Any(r => r.Key != _selected?.Id && r.Value.Enabled);
        RefreshTiles();
    }

    private void RefreshTiles()
    {
        foreach (VoiceTileViewModel tile in Tiles)
        {
            tile.IsSelected = tile.Voice is null ? !_enabled : _enabled && tile.Voice == _voice;
            tile.IsEnabled = tile.Voice is null || _available;
        }
    }
}
