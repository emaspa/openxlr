using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>
/// One entry of the audio clock picker: a device the daemon's
/// <c>mixer.clockChoice.options</c> lists, or Automatic with no node.
/// </summary>
public sealed record AudioClockOptionItem(string? Node, string Description, bool Input)
{
    public string Label => Node is null ? Localizer.Text("AudioClockAutomatic")
        : Input ? Localizer.Format("AudioClockInput", Description) : Localizer.Format("AudioClockOutput", Description);

    internal static readonly AudioClockOptionItem Automatic = new(null, "", false);
}

public sealed partial class MainViewModel
{
    // The daemon's audio clock choice: the WirePlumber rule it wrote, the
    // devices it can name, and whether the rule waits for a restart.
    private AudioClockOptionItem[] _clockOptions = [];
    private string? _clockNode;

    /// <summary>The daemon offers the choice: not an older daemon, and not the Flatpak's.</summary>
    public bool ClockChoiceReported { get; private set; }

    /// <summary>Automatic, then the devices.</summary>
    public IReadOnlyList<AudioClockOptionItem> ClockChoices { get; private set; } = [];

    /// <summary>The picker's entry: Automatic or the chosen device. Picking another one writes the rule.</summary>
    public AudioClockOptionItem? ClockChoice
    {
        get => _clockNode is null ? (ClockChoices.Count > 0 ? AudioClockOptionItem.Automatic : null)
            : _clockOptions.FirstOrDefault(o => o.Node == _clockNode);
        set
        {
            // A ComboBox writes null while its items are replaced.
            if (value is null || value.Node == _clockNode) return;
            _clockNode = value.Node;
            Raise();
            if (!_applying) _ = SetAudioClockAsync(value.Node);
        }
    }

    private bool _clockPending;
    /// <summary>The chosen clock waits for WirePlumber to restart; Options then offers the restart.</summary>
    public bool ClockPending { get => _clockPending; private set => Set(ref _clockPending, value); }

    private string _clockRuleProblem = "";
    /// <summary>Why the daemon cannot use the rule file, or empty.</summary>
    public string ClockRuleProblem { get => _clockRuleProblem; private set => Set(ref _clockRuleProblem, value); }

    private string? _clockActionNode;
    private string _clockActionText = "";
    /// <summary>The warning's "Make device the clock" button, for the microphone's resampled interface; empty when there is none.</summary>
    public string ClockActionText { get => _clockActionText; private set => Set(ref _clockActionText, value); }
    public bool HasClockAction => ClockActionText.Length > 0;

    /// <summary>The name the warning's action gives the device, for its confirm window.</summary>
    internal string ClockActionDevice { get; private set; } = "";

    private bool _restartingAudio;
    public bool CanRestartAudio => !_restartingAudio && DaemonConnected;

    private async Task SetAudioClockAsync(string? node)
    {
        string? error = await _client.SetAudioClockAsync(node);
        if (error is not null) Status = error;
    }

    /// <summary>The warning's action: the microphone's interface becomes the clock, restarted now or left for later.</summary>
    internal async Task<string?> MakeClockAsync(bool restart)
    {
        if (_clockActionNode is not { } node) return null;
        string? error = await _client.SetAudioClockAsync(node);
        if (error is null && restart) error = await RestartAudioAsync();
        if (error is not null) Status = error;
        return error;
    }

    /// <summary>Restart WirePlumber through the daemon so the rule applies. Null on success.</summary>
    internal async Task<string?> RestartAudioAsync()
    {
        if (_restartingAudio) return null;
        _restartingAudio = true;
        Raise(nameof(CanRestartAudio));
        try
        {
            string? error = await _client.RestartAudioServicesAsync();
            Status = error ?? Localizer.Text("AudioRestarted");
            return error;
        }
        finally
        {
            _restartingAudio = false;
            Raise(nameof(CanRestartAudio));
        }
    }

    private void ApplyClockChoice(JsonNode? mixer)
    {
        JsonObject? choice = mixer?["clockChoice"] as JsonObject;
        AudioClockOptionItem[] options = choice?["options"] is JsonArray list
            ? [.. list.OfType<JsonObject>()
                .Select(o => (Node: Text(o, "node"), Description: Text(o, "description"), Kind: Text(o, "kind")))
                .Where(o => o.Node is not null)
                .Select(o => new AudioClockOptionItem(o.Node!, o.Description ?? o.Node!, o.Kind == "input"))]
            : [];
        bool reported = choice is not null;
        if (reported != ClockChoiceReported) { ClockChoiceReported = reported; Raise(nameof(ClockChoiceReported)); }
        if (!options.SequenceEqual(_clockOptions))
        {
            _clockOptions = options;
            ClockChoices = reported ? [AudioClockOptionItem.Automatic, .. options] : [];
            Raise(nameof(ClockChoices));
        }
        _clockNode = choice is null ? null : Text(choice, "node");
        Raise(nameof(ClockChoice));
        ClockPending = choice?["pending"] is JsonValue pending && pending.TryGetValue(out bool waits) && waits;
        ClockRuleProblem = choice is null ? "" : Text(choice, "problem") ?? "";

        // The warning's action: the resampled interface of XLR 1 or XLR 2,
        // when the rule can name it and it is not already the clock.
        AudioClockFollower? microphone = ClockProblem ? Clock?.Followers.FirstOrDefault(f => f.Microphone && f.Resampled) : null;
        AudioClockOptionItem? target = microphone is null ? null : options.FirstOrDefault(o => o.Node == microphone.Name);
        bool offered = target is not null && (_clockNode != target.Node || ClockPending);
        _clockActionNode = offered ? target!.Node : null;
        ClockActionDevice = offered ? microphone!.Description : "";
        ClockActionText = offered ? Localizer.Format("MakeDeviceTheClock", microphone!.Description) : "";
        Raise(nameof(HasClockAction));

        static string? Text(JsonObject node, string key)
            => node[key] is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0 ? text : null;
    }
}
