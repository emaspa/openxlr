using OpenXLR.UI.Localization;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>
/// One microphone's Sound Check. The daemon owns the recording; this view
/// only sends the actions and shows the state the daemon reports.
/// </summary>
public sealed class SoundCheckViewModel(DaemonClient client, string channel) : ViewModelBase
{
    private bool _busy, _active;
    private int _connectionEpoch;
    private string _mode = "idle";
    private double _seconds;
    private string? _error;
    public bool Active => _active;
    public bool CanRecord => !_busy && _mode != "recording";
    public bool CanLoop => !_busy && _active && _seconds >= 0.1;
    public bool CanStop => !_busy && (_active || Error is not null);
    public string Status => _mode switch
    {
        "recording" => Localizer.Format("SoundCheckRecording", _seconds),
        "looping" => Localizer.Format("SoundCheckLooping", _seconds),
        "live" when _active => Localizer.Format("SoundCheckReady", _seconds),
        _ => Localizer.Text("SoundCheckEmpty"),
    };
    public string? Error { get => _error; private set => Set(ref _error, value); }

    /// <summary>The daemon's <c>mixer.soundCheck</c>; null or another channel's session reads as idle.</summary>
    public void Apply(JsonNode? state)
    {
        bool own = state?["channel"]?.GetValue<string>() == channel;
        _mode = own ? state?["mode"]?.GetValue<string>() ?? "idle" : "idle";
        _active = own && _mode != "idle";
        _seconds = _active ? state?["seconds"]?.GetValue<double>() ?? 0 : 0;
        Error = own ? state?["error"]?.GetValue<string>() : null;
        Raise(nameof(Active)); Raise(nameof(Status)); RaiseButtons();
    }

    /// <summary>
    /// The connection dropped: a reply still on its way belongs to the old
    /// connection and must not change what this one shows.
    /// </summary>
    public void Reset()
    {
        _connectionEpoch++;
        _busy = false;
        Apply(null);
    }

    private void RaiseButtons() { Raise(nameof(CanRecord)); Raise(nameof(CanLoop)); Raise(nameof(CanStop)); }

    public async Task RunAsync(string action)
    {
        if (_busy) return;
        int epoch = _connectionEpoch;
        _busy = true; RaiseButtons();
        try
        {
            string? error = await client.SoundCheckAsync(channel, action);
            if (epoch == _connectionEpoch) Error = error;
        }
        finally
        {
            if (epoch == _connectionEpoch) { _busy = false; RaiseButtons(); }
        }
    }

    /// <summary>
    /// Closing can follow a pending record command; the stop is sent behind
    /// it rather than dropped because the buttons are disabled for a moment.
    /// </summary>
    public async Task StopOnCloseAsync()
    {
        int epoch = _connectionEpoch;
        string? error = await client.SoundCheckAsync(channel, "stop");
        if (epoch == _connectionEpoch) Error = error;
    }
}
