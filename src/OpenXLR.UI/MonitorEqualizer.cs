using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

public sealed record MonitorEqOption(string? Id)
{
    public override string ToString() => Id switch
    {
        null => Localizer.Text("MonitorEqOff"),
        "voice-boost" => Localizer.Text("MonitorEqVoiceBoost"),
        "fps" => Localizer.Text("MonitorEqFps"),
        "bass-boost" => Localizer.Text("MonitorEqBassBoost"),
        "soft-treble" => Localizer.Text("MonitorEqSoftTreble"),
        _ => Id,
    };
}

public sealed partial class InsertsViewModel
{
    private static readonly IReadOnlyList<MonitorEqOption> ListeningPresets =
        [new(null), .. MonitorEqPresets.All.Select(p => new MonitorEqOption(p.Id))];

    public IReadOnlyList<MonitorEqOption> MonitorEqOptions => ListeningPresets;

    private bool _hasMonitorEq;
    public bool HasMonitorEq { get => _hasMonitorEq; private set => Set(ref _hasMonitorEq, value); }

    private bool _monitorEqBusy;
    public bool MonitorEqBusy { get => _monitorEqBusy; private set => Set(ref _monitorEqBusy, value); }

    private string? _monitorEqError;
    public string? MonitorEqError { get => _monitorEqError; private set => Set(ref _monitorEqError, value); }

    private MonitorEqOption? _reportedMonitorEq, _selectedMonitorEq;
    private string? _reportedMonitorEqError;
    public MonitorEqOption? SelectedMonitorEq
    {
        get => _selectedMonitorEq;
        set
        {
            if (value is null || !HasMonitorEq || MonitorEqBusy || IsDetached || !Set(ref _selectedMonitorEq, value)) return;
            _ = ChooseMonitorEqAsync(value);
        }
    }

    internal void ApplyMonitorEq(JsonNode? state)
    {
        HasMonitorEq = state is JsonObject;
        _reportedMonitorEq = HasMonitorEq ? ListeningPresets.FirstOrDefault(p => p.Id == state?["preset"]?.GetValue<string>()) : null;
        _reportedMonitorEqError = state?["error"]?.GetValue<string>();
        if (MonitorEqBusy) return;
        Set(ref _selectedMonitorEq, _reportedMonitorEq, nameof(SelectedMonitorEq));
        MonitorEqError = _reportedMonitorEqError;
    }

    private async Task ChooseMonitorEqAsync(MonitorEqOption choice)
    {
        MonitorEqBusy = true;
        MonitorEqError = null;
        try
        {
            string? error = await _client.SetMonitorEqPresetAsync(_channel[4..], choice.Id);
            Set(ref _selectedMonitorEq, _reportedMonitorEq, nameof(SelectedMonitorEq));
            MonitorEqError = error ?? _reportedMonitorEqError;
        }
        finally { MonitorEqBusy = false; }
    }
}
