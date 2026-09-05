using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace OpenXLR.UI;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Set a field and notify; returns false if unchanged.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>
/// Root view model. Applies daemon state pushes to the bound properties and
/// sends user changes back. A guard flag suppresses echo: while applying a push
/// we must not re-send the values we just received.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    public DaemonRestartViewModel DaemonRestart { get; } = new();

    private readonly DaemonClient _client;
    private bool _applying;

    public MainViewModel(DaemonClient client)
    {
        _client = client;
        Inserts = new InsertsViewModel(client, "xlr1", 1, "XLR 1");
        Inserts2 = new InsertsViewModel(client, "xlr2", 1, "XLR 2");
        _client.StateReceived += node => Dispatcher.UIThread.Post(() => Apply(node));
        _client.ConnectionChanged += up => Dispatcher.UIThread.Post(() =>
        {
            DaemonConnected = up;
            if (up) DaemonRestart.ConnectionRestored();
            if (!up)
            {
                DeviceConnected = false; Status = "daemon not running"; HasMixer = false;
                Inserts.ResetForNewConnection(); Inserts2.ResetForNewConnection();
                foreach (MixViewModel mv in Mixes) mv.Inserts.ResetForNewConnection();
                StateApplied?.Invoke();
            }
            else { Inserts.EnsurePluginsLoaded(); Inserts2.EnsurePluginsLoaded(); }
        });
        _client.ErrorReceived += msg => Dispatcher.UIThread.Post(() => Status = msg);
        _client.MetersReceived += levels => Dispatcher.UIThread.Post(() => ApplyMeters(levels));
    }

    // --- connection / device identity ---

    private bool _daemonConnected;
    public bool DaemonConnected
    {
        get => _daemonConnected;
        private set
        {
            if (SetAndRaiseMismatch(ref _daemonConnected, value))
            {
                Raise(nameof(StatusLine));
                Raise(nameof(MixerPlaceholder));
                Raise(nameof(CanEditLayout));
                Raise(nameof(LayoutNote));
            }
        }
    }

    /// <summary>What the empty SUBMIXER tile says: the two reasons differ.</summary>
    public string MixerPlaceholder => !DaemonConnected
        ? "Daemon not running."
        : "Submixer is off. Turn it on in Options for per-app channels, mixes, virtual microphones and inserts; OpenXLR is controlling the hardware only.";

    private bool _deviceConnected;
    public bool DeviceConnected { get => _deviceConnected; private set { if (Set(ref _deviceConnected, value)) Raise(nameof(StatusLine)); } }

    private string _deviceName = "none";
    public string DeviceName { get => _deviceName; private set { if (Set(ref _deviceName, value)) Raise(nameof(StatusLine)); } }

    private string _status = "connecting…";
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(StatusLine)); } }

    public string StatusLine => !DaemonConnected ? "Daemon not running"
        : DeviceConnected ? DeviceName
        : "No device";

    // --- hardware controls ---

    private bool _gainLocked;
    public bool GainLocked
    {
        get => _gainLocked;
        set { if (Set(ref _gainLocked, value) && !_applying) _ = _client.SetControlAsync("gainLock", value); }
    }

    private int _gainDb;
    public int GainDb
    {
        get => _gainDb;
        set
        {
            if (Set(ref _gainDb, value) && !_applying)
            {
                int v = value;
                SliderSync.Touch("gain");
                SliderSync.Send("gain", () => _ = _client.SetControlAsync("gain", v));
            }
            Raise(nameof(GainText));
        }
    }
    public string GainText => $"{_gainDb} dB";

    // What the connected device can do, from the daemon's capabilities block;
    // the layout hides what a device does not have. Defaults keep the full
    // Pro layout until a state push says otherwise.
    private bool _hasXlr2 = true;
    public bool HasXlr2 { get => _hasXlr2; set => Set(ref _hasXlr2, value); }
    private bool _hasHp2 = true;
    public bool HasHp2 { get => _hasHp2; set => Set(ref _hasHp2, value); }
    private bool _capLowCut = true;
    public bool CapLowCut { get => _capLowCut; set => Set(ref _capLowCut, value); }
    private bool _capExpander = true;
    public bool CapExpander { get => _capExpander; set => Set(ref _capExpander, value); }
    private bool _capVoiceTune = true;
    public bool CapVoiceTune { get => _capVoiceTune; set => Set(ref _capVoiceTune, value); }
    private bool _capPhantom = true;
    public bool CapPhantom { get => _capPhantom; set => Set(ref _capPhantom, value); }
    private bool _capClipGuard = true;
    public bool CapClipGuard { get => _capClipGuard; set => Set(ref _capClipGuard, value); }
    private bool _capCompressor = true;
    public bool CapCompressor { get => _capCompressor; set => Set(ref _capCompressor, value); }
    private bool _capCrossfade = true;
    public bool CapCrossfade { get => _capCrossfade; set => Set(ref _capCrossfade, value); }
    private bool _capLowImpedance = true;
    public bool CapLowImpedance { get => _capLowImpedance; set => Set(ref _capLowImpedance, value); }
    private bool _capAuxInput = true;
    public bool CapAuxInput { get => _capAuxInput; set => Set(ref _capAuxInput, value); }

    private bool _capOutputRouting = true;
    public bool CapOutputRouting { get => _capOutputRouting; set => Set(ref _capOutputRouting, value); }

    private bool _capPhysicalControls = true;
    public bool CapPhysicalControls { get => _capPhysicalControls; set => Set(ref _capPhysicalControls, value); }

    // The daemon-side gain lock cannot stop a physical dial, so it only
    // shows for devices without one.
    private bool _showGainLock;
    public bool ShowGainLock { get => _showGainLock; private set => Set(ref _showGainLock, value); }

    // Software ClipGuard (host-side limiter) for devices without the
    // hardware one.
    private bool _softClipGuard;
    public bool SoftClipGuard
    {
        get => _softClipGuard;
        set { if (Set(ref _softClipGuard, value) && !_applying) _ = _client.SetSoftClipGuardAsync(value); }
    }

    private bool _showSoftClipGuard;
    public bool ShowSoftClipGuard { get => _showSoftClipGuard; private set => Set(ref _showSoftClipGuard, value); }

    private bool _softClipGuardAvailable = true;
    public bool SoftClipGuardAvailable
    {
        get => _softClipGuardAvailable;
        private set { if (Set(ref _softClipGuardAvailable, value)) Raise(nameof(SoftClipGuardTip)); }
    }

    private string? _softClipGuardError;
    public string? SoftClipGuardError
    {
        get => _softClipGuardError;
        private set { if (Set(ref _softClipGuardError, value)) Raise(nameof(SoftClipGuardTip)); }
    }

    public string SoftClipGuardTip => SoftClipGuardAvailable
        ? "Host-side ClipGuard: a post-ADC hard limiter at -3 dB; it cannot repair clipping in the analog preamp or ADC"
        : SoftClipGuardError ?? "Software ClipGuard is unavailable";

    // Software low cut (host-side high-pass) for devices without the
    // hardware filter; state lives in the mixer, not the device.
    private int _softLowCutHz;
    public int SoftLowCutHz
    {
        get => _softLowCutHz;
        private set
        {
            if (Set(ref _softLowCutHz, value))
            {
                Raise(nameof(SoftLowCutText));
                Raise(nameof(SoftLowCutOn));
            }
        }
    }
    public string SoftLowCutText => _softLowCutHz == 0 ? "Low Cut Off" : $"Low Cut {_softLowCutHz}";

    private bool _showSoftLowCut;
    public bool ShowSoftLowCut { get => _showSoftLowCut; private set => Set(ref _showSoftLowCut, value); }

    public bool SoftLowCutOn => _softLowCutHz > 0;

    public void CycleSoftLowCut()
        => _ = _client.SetLowCutHzAsync(_softLowCutHz switch { 0 => 80, 80 => 120, _ => 0 });

    private bool _mute;
    public bool Mute { get => _mute; set { if (Set(ref _mute, value) && !_applying) _ = _client.SetControlAsync("mute", value); } }

    private bool _lowCut;
    public bool LowCut { get => _lowCut; set { if (Set(ref _lowCut, value) && !_applying) _ = _client.SetControlAsync("lowCut", value); } }

    private bool _expander;
    public bool Expander { get => _expander; set { if (Set(ref _expander, value) && !_applying) _ = _client.SetControlAsync("expander", value); } }

    private bool _voiceTune;
    public bool VoiceTune { get => _voiceTune; set { if (Set(ref _voiceTune, value) && !_applying) _ = _client.SetControlAsync("voiceTune", value); } }

    private int _voiceTuneStrength;
    public int VoiceTuneStrength
    {
        get => _voiceTuneStrength;
        set
        {
            if (Set(ref _voiceTuneStrength, value) && !_applying)
            {
                int v = value;
                SliderSync.Touch("vts");
                SliderSync.Send("vts", () => _ = _client.SetControlAsync("voiceTuneStrength", v));
            }
            Raise(nameof(VoiceTuneStrengthText));
        }
    }
    public string VoiceTuneStrengthText => $"{_voiceTuneStrength}%";

    private int _voiceTuneStrength2;
    public int VoiceTuneStrength2
    {
        get => _voiceTuneStrength2;
        set
        {
            if (Set(ref _voiceTuneStrength2, value) && !_applying)
            {
                int v = value;
                SliderSync.Touch("vts2");
                SliderSync.Send("vts2", () => _ = _client.SetControlAsync("voiceTuneStrength2", v));
            }
            Raise(nameof(VoiceTuneStrength2Text));
        }
    }
    public string VoiceTuneStrength2Text => $"{_voiceTuneStrength2}%";

    // Crossfade: the hardware's zero-latency direct-monitor blend, 0 = only
    // the mic side, 200 = only PC playback, 100 = centre.
    private int _crossfade;
    public int Crossfade
    {
        get => _crossfade;
        set
        {
            if (Set(ref _crossfade, value) && !_applying)
            {
                int v = value;
                SliderSync.Touch("xfade");
                SliderSync.Send("xfade", () => _ = _client.SetControlAsync("crossfade", v));
            }
            Raise(nameof(CrossfadeText));
        }
    }
    public string CrossfadeText => _crossfade == 100 ? "centre"
        : _crossfade < 100 ? $"mic +{100 - _crossfade}" : $"pc +{_crossfade - 100}";

    private bool _lowImpedance;
    public bool LowImpedance { get => _lowImpedance; set { if (Set(ref _lowImpedance, value) && !_applying) _ = _client.SetControlAsync("lowImpedance", value); } }

    private double _hpVolumeDb;
    public double HpVolumeDb
    {
        get => _hpVolumeDb;
        set
        {
            if (Set(ref _hpVolumeDb, value) && !_applying)
            {
                double v = value;
                SliderSync.Touch("hp");
                SliderSync.Send("hp", () => _ = _client.SetControlAsync("hpVolumeDb", v));
            }
            Raise(nameof(HpText));
            Raise(nameof(HpPercent));
        }
    }
    public string HpText => $"{HpPercent:0}%";

    /// <summary>
    /// The hardware register is an attenuator in -0.25 dB steps (0 dB = full
    /// output, -60 dB = floor). Shown as 0..100% linear in dB, which is how a
    /// volume knob is expected to feel.
    /// </summary>
    public double HpPercent
    {
        get => (60.0 + _hpVolumeDb) / 60.0 * 100.0;
        set => HpVolumeDb = -60.0 + Math.Clamp(value, 0, 100) * 0.6;
    }

    private double _hp2VolumeDb;
    public double Hp2VolumeDb
    {
        get => _hp2VolumeDb;
        set
        {
            if (Set(ref _hp2VolumeDb, value) && !_applying)
            {
                double v = value;
                SliderSync.Touch("hp2");
                SliderSync.Send("hp2", () => _ = _client.SetControlAsync("hp2VolumeDb", v));
            }
            Raise(nameof(Hp2Text));
            Raise(nameof(Hp2Percent));
        }
    }
    public string Hp2Text => $"{Hp2Percent:0}%";

    public double Hp2Percent
    {
        get => (60.0 + _hp2VolumeDb) / 60.0 * 100.0;
        set => Hp2VolumeDb = -60.0 + Math.Clamp(value, 0, 100) * 0.6;
    }

    // --- second XLR input ---

    private int _gain2Db;
    public int Gain2Db
    {
        get => _gain2Db;
        set
        {
            if (Set(ref _gain2Db, value) && !_applying)
            {
                int v = value;
                SliderSync.Touch("gain2");
                SliderSync.Send("gain2", () => _ = _client.SetControlAsync("gain2", v));
            }
            Raise(nameof(Gain2Text));
        }
    }
    public string Gain2Text => $"{_gain2Db} dB";

    private bool _mute2;
    public bool Mute2 { get => _mute2; set { if (Set(ref _mute2, value) && !_applying) _ = _client.SetControlAsync("mute2", value); } }

    private bool _lowCut2;
    public bool LowCut2 { get => _lowCut2; set { if (Set(ref _lowCut2, value) && !_applying) _ = _client.SetControlAsync("lowCut2", value); } }

    private bool _expander2;
    public bool Expander2 { get => _expander2; set { if (Set(ref _expander2, value) && !_applying) _ = _client.SetControlAsync("expander2", value); } }

    private bool _voiceTune2;
    public bool VoiceTune2 { get => _voiceTune2; set { if (Set(ref _voiceTune2, value) && !_applying) _ = _client.SetControlAsync("voiceTune2", value); } }

    private bool _phantom2;
    public bool Phantom2 { get => _phantom2; set { if (Set(ref _phantom2, value) && !_applying) _ = _client.SetControlAsync("phantom2", value); } }

    private bool _clipGuard2;
    public bool ClipGuard2 { get => _clipGuard2; set { if (Set(ref _clipGuard2, value) && !_applying) _ = _client.SetControlAsync("clipGuard2", value); } }

    private bool _compressor2;
    public bool Compressor2 { get => _compressor2; set { if (Set(ref _compressor2, value) && !_applying) _ = _client.SetControlAsync("compressor2", value); } }

    // Pro-only DSP, confirmed against the logged capture of 2026-08-25.
    private bool _phantom;
    public bool Phantom { get => _phantom; set { if (Set(ref _phantom, value) && !_applying) _ = _client.SetControlAsync("phantom", value); } }

    // The device holds the input muted for ~13 s around every 48V change and
    // unmutes it itself; the daemon stamps these while that hold is active,
    // and the mute buttons disable so nobody fights the firmware.
    private bool _phantomSettling;
    public bool PhantomSettling { get => _phantomSettling; set { if (Set(ref _phantomSettling, value)) Raise(nameof(MuteLabel)); } }
    private bool _phantomSettling2;
    public bool PhantomSettling2 { get => _phantomSettling2; set { if (Set(ref _phantomSettling2, value)) Raise(nameof(Mute2Label)); } }

    private int _phantomSettleSeconds;
    public int PhantomSettleSeconds { get => _phantomSettleSeconds; set { if (Set(ref _phantomSettleSeconds, value)) Raise(nameof(MuteLabel)); } }
    private int _phantomSettleSeconds2;
    public int PhantomSettleSeconds2 { get => _phantomSettleSeconds2; set { if (Set(ref _phantomSettleSeconds2, value)) Raise(nameof(Mute2Label)); } }

    // The mute button counts the hold down while the firmware settles 48V.
    public string MuteLabel => PhantomSettling ? $"48V {PhantomSettleSeconds}s" : "Mute";
    public string Mute2Label => PhantomSettling2 ? $"48V {PhantomSettleSeconds2}s" : "Mute";

    private bool _clipGuard;
    public bool ClipGuard { get => _clipGuard; set { if (Set(ref _clipGuard, value) && !_applying) _ = _client.SetControlAsync("clipGuard", value); } }

    private bool _compressor;
    public bool Compressor { get => _compressor; set { if (Set(ref _compressor, value) && !_applying) _ = _client.SetControlAsync("compressor", value); } }

    // Physical output routing: which jacks carry the hardware monitor bus.
    private bool _outHp1;
    public bool OutHp1 { get => _outHp1; set { if (Set(ref _outHp1, value) && !_applying) _ = _client.SetControlAsync("outHp1", value); } }

    private bool _outHp2;
    public bool OutHp2 { get => _outHp2; set { if (Set(ref _outHp2, value) && !_applying) _ = _client.SetControlAsync("outHp2", value); } }

    private bool _outUsbAux;
    public bool OutUsbAux { get => _outUsbAux; set { if (Set(ref _outUsbAux, value) && !_applying) _ = _client.SetControlAsync("outUsbAux", value); } }

    private bool _outLineOut;
    public bool OutLineOut { get => _outLineOut; set { if (Set(ref _outLineOut, value) && !_applying) _ = _client.SetControlAsync("outLineOut", value); } }

    // USB Aux input stage.
    private double _auxLevelDb;
    public double AuxLevelDb
    {
        get => _auxLevelDb;
        set
        {
            if (Set(ref _auxLevelDb, value))
            {
                Raise(nameof(AuxLevelText));
                if (!_applying)
                {
                    var v = value;
                    SliderSync.Touch("aux");
                    SliderSync.Send("aux", () => _ = _client.SetControlAsync("auxLevelDb", v));
                }
            }
        }
    }
    public string AuxLevelText => $"{_auxLevelDb:0} dB";

    private bool _auxLevelLock;
    public bool AuxLevelLock { get => _auxLevelLock; set { if (Set(ref _auxLevelLock, value) && !_applying) _ = _client.SetControlAsync("auxLevelLock", value); } }

    // --- mixer ---

    public ObservableCollection<ChannelViewModel> Channels { get; } = [];
    public ObservableCollection<MixViewModel> Mixes { get; } = [];

    /// <summary>Running audio-capable apps (the main card shows these).</summary>
    public ObservableCollection<AppStreamViewModel> ActiveApps { get; } = [];

    /// <summary>True when the registry holds anything (enables Manage…).</summary>
    public bool HasApps => Apps.Count > 0;

    /// <summary>Pre-register an installed app with a channel (Manage dialog).</summary>
    public void AddApp(string identity, string label, string channel)
        => _ = _client.AssignAppAsync(identity, channel, label);

    public Task<bool> CreateChannel(string name) => EditLayout(() => _client.CreateChannelAsync(name));
    public Task<bool> RenameChannel(string id, string name) => EditLayout(() => _client.RenameChannelAsync(id, name));
    public Task<bool> DeleteChannel(string id) => EditLayout(() => _client.DeleteChannelAsync(id));
    public Task<bool> CreateMix(string name) => EditLayout(() => _client.CreateMixAsync(name));
    public Task<bool> RenameMix(string id, string name) => EditLayout(() => _client.RenameMixAsync(id, name));
    public Task<bool> DeleteMix(string id) => EditLayout(() => _client.DeleteMixAsync(id));

    private bool _layoutBusy;
    public bool LayoutBusy
    {
        get => _layoutBusy;
        private set { if (Set(ref _layoutBusy, value)) Raise(nameof(CanEditLayout)); }
    }

    private bool _supportsLayout;
    public bool SupportsLayout
    {
        get => _supportsLayout;
        private set
        {
            if (Set(ref _supportsLayout, value))
            {
                Raise(nameof(CanEditLayout));
                Raise(nameof(LayoutNote));
            }
        }
    }

    public bool CanEditLayout => DaemonConnected && HasMixer && SupportsLayout && !LayoutBusy;

    private string _layoutNote = "";
    public string LayoutNote => !DaemonConnected ? "Daemon disconnected; waiting for automatic reconnection."
        : !SupportsLayout ? "This daemon does not support editable layouts. Install the matching build and restart it."
        : !HasMixer ? "Enable the submixer in Options first." : _layoutNote;

    private async Task<bool> EditLayout(Func<Task<string?>> action)
    {
        if (!CanEditLayout) return false;
        LayoutBusy = true;
        _layoutNote = "Applying layout to PipeWire…";
        Raise(nameof(LayoutNote));
        try
        {
            string? error = await action();
            _layoutNote = error ?? "Layout saved.";
            Raise(nameof(LayoutNote));
            if (error is not null) Status = error;
            return error is null;
        }
        finally { LayoutBusy = false; }
    }

    // --- device selection (any sink/source, real or virtual) ---

    public ObservableCollection<AudioDeviceItem> Outputs { get; } = [];

    /// <summary>All capture devices, virtual included (for the Options pickers).</summary>
    public ObservableCollection<AudioDeviceItem> Inputs { get; } = [];

    /// <summary>
    /// Sinks the Monitor mix can play on, multi-selectable: everything except
    /// OpenXLR's own nodes (routing the monitor into its own mixer is a
    /// feedback loop).
    /// </summary>
    public ObservableCollection<MonitorOutputItem> MonitorOutputs { get; } = [];

    /// <summary>Dropdown button text: the selected outputs, or a placeholder.</summary>
    public string MonitorSummary
    {
        get
        {
            var picked = MonitorOutputs.Where(o => o.IsSelected).Select(o => o.Label).ToList();
            return picked.Count == 0 ? "not routed"
                 : picked.Count <= 2 ? string.Join(" + ", picked)
                 : $"{picked[0]} + {picked.Count - 1} more";
        }
    }

    private void OnMonitorSelectionChanged()
    {
        Raise(nameof(MonitorSummary));
        if (_applying) return;
        var names = MonitorOutputs.Where(o => o.IsSelected).Select(o => o.Name).ToList();
        _ = _client.SetMonitorOutputsAsync(names);
    }

    /// <summary>Enforced defaults as reported by the daemon (for the Options window).</summary>
    public string? EnforcedDefaultSink { get; private set; }
    public string? EnforcedDefaultSource { get; private set; }

    /// <summary>Mirrors the ui.json preference; MainWindow consults it on close.</summary>
    public bool MinimizeToTray { get; set; } = UiSettings.Load().MinimizeToTray;


    private double _outputVolume;
    public double OutputVolume
    {
        get => _outputVolume;
        set
        {
            if (Set(ref _outputVolume, value) && !_applying)
            {
                double v = value;
                SliderSync.Touch("outvol");
                SliderSync.Send("outvol", () => _ = _client.SetOutputVolumeAsync(v));
            }
            Raise(nameof(OutputVolumeText));
        }
    }
    public string OutputVolumeText => $"{_outputVolume * 100:0}%";

    private bool _hasMixer;
    public bool HasMixer
    {
        get => _hasMixer;
        private set
        {
            if (Set(ref _hasMixer, value))
            {
                Raise(nameof(MixerPlaceholder));
                Raise(nameof(CanEditLayout));
                Raise(nameof(LayoutNote));
            }
        }
    }

    private bool SetAndRaiseMismatch(ref bool field, bool value)
    {
        bool changed = Set(ref field, value);
        if (changed) Raise(nameof(DaemonVersionMismatch));
        return changed;
    }

    private string? _daemonVersion;
    /// <summary>The running daemon's version, or null for one older than 0.1.13.</summary>
    public string? DaemonVersion
    {
        get => _daemonVersion;
        private set { if (Set(ref _daemonVersion, value)) { Raise(nameof(DaemonVersionMismatch)); Raise(nameof(DaemonVersionNote)); } }
    }

    /// <summary>
    /// True when the daemon is not the same build as this window: a package
    /// upgrade leaves the old daemon running, and the controls it offers are
    /// those of the old version.
    /// </summary>
    public bool DaemonVersionMismatch => DaemonConnected && (DaemonVersion ?? "") != AppVersion.Current;

    public string DaemonVersionNote =>
        $"The daemon is {(DaemonVersion is null ? "an older version" : "v" + DaemonVersion)}; this window is v{AppVersion.Current}. " +
        "Restart the daemon to run the installed version.";

    /// <summary>Apply a state push from the daemon without echoing it back.</summary>
    private void Apply(JsonNode node)
    {
        _applying = true;
        try
        {
            DaemonConnected = true;
            DaemonVersion = node["daemonVersion"]?.GetValue<string>();
            var features = (node["features"] as JsonArray)?.Select(f => f?.GetValue<string>()).ToHashSet();
            SupportsLayout = features?.Contains("editableLayout") == true && features.Contains("commandResults");
            DeviceConnected = node["connected"]?.GetValue<bool>() ?? false;
            if (node["device"] is JsonNode dev)
                DeviceName = $"{dev["vendor"]?.GetValue<string>()} {dev["model"]?.GetValue<string>()}".Trim();

            if (node["capabilities"] is JsonNode caps)
            {
                bool Cap(string k) => caps[k]?.GetValue<bool>() ?? false;
                HasXlr2 = (caps["xlrInputs"]?.GetValue<int>() ?? 1) > 1;
                HasHp2 = (caps["hpOutputs"]?.GetValue<int>() ?? 1) > 1;
                CapLowCut = Cap("lowCut");
                CapExpander = Cap("expander");
                CapVoiceTune = Cap("voiceTune");
                CapPhantom = Cap("phantom");
                CapClipGuard = Cap("clipGuard");
                CapCompressor = Cap("compressor");
                CapCrossfade = Cap("crossfade");
                CapLowImpedance = Cap("lowImpedance");
                CapAuxInput = Cap("auxInput");
                CapOutputRouting = Cap("outputRouting");
                CapPhysicalControls = Cap("physicalControls");
            }

            if (node["state"] is JsonNode s)
            {
                if (!SliderSync.RecentlyTouched("gain")) GainDb = s["gainDb"]?.GetValue<int>() ?? 0;
                GainLocked = s["gainLocked"]?.GetValue<bool>() ?? false;
                Mute = s["mute"]?.GetValue<bool>() ?? false;
                LowCut = s["lowCut"]?.GetValue<bool>() ?? false;
                Expander = s["expander"]?.GetValue<bool>() ?? false;
                VoiceTune = s["voiceTune"]?.GetValue<bool>() ?? false;
                LowImpedance = s["lowImpedance"]?.GetValue<bool>() ?? false;
                if (!SliderSync.RecentlyTouched("vts")) VoiceTuneStrength = s["voiceTuneStrength"]?.GetValue<int>() ?? 0;
                if (!SliderSync.RecentlyTouched("vts2")) VoiceTuneStrength2 = s["voiceTuneStrength2"]?.GetValue<int>() ?? 0;
                if (!SliderSync.RecentlyTouched("xfade")) Crossfade = s["crossfade"]?.GetValue<int>() ?? 0;
                if (!SliderSync.RecentlyTouched("hp")) HpVolumeDb = s["hpVolumeDb"]?.GetValue<double>() ?? 0;
                if (!SliderSync.RecentlyTouched("hp2")) Hp2VolumeDb = s["hp2VolumeDb"]?.GetValue<double>() ?? 0;
                Phantom = s["phantom"]?.GetValue<bool>() ?? false;
                PhantomSettling = s["phantomSettling"]?.GetValue<bool>() ?? false;
                PhantomSettling2 = s["phantomSettling2"]?.GetValue<bool>() ?? false;
                PhantomSettleSeconds = s["phantomSettleSeconds"]?.GetValue<int>() ?? 0;
                PhantomSettleSeconds2 = s["phantomSettleSeconds2"]?.GetValue<int>() ?? 0;
                ClipGuard = s["clipGuard"]?.GetValue<bool>() ?? false;
                Compressor = s["compressor"]?.GetValue<bool>() ?? false;
                if (!SliderSync.RecentlyTouched("gain2")) Gain2Db = s["gain2Db"]?.GetValue<int>() ?? 0;
                Mute2 = s["mute2"]?.GetValue<bool>() ?? false;
                LowCut2 = s["lowCut2"]?.GetValue<bool>() ?? false;
                Expander2 = s["expander2"]?.GetValue<bool>() ?? false;
                VoiceTune2 = s["voiceTune2"]?.GetValue<bool>() ?? false;
                Phantom2 = s["phantom2"]?.GetValue<bool>() ?? false;
                ClipGuard2 = s["clipGuard2"]?.GetValue<bool>() ?? false;
                Compressor2 = s["compressor2"]?.GetValue<bool>() ?? false;
                OutHp1 = s["outHp1"]?.GetValue<bool>() ?? false;
                OutHp2 = s["outHp2"]?.GetValue<bool>() ?? false;
                OutUsbAux = s["outUsbAux"]?.GetValue<bool>() ?? false;
                OutLineOut = s["outLineOut"]?.GetValue<bool>() ?? false;
                if (!SliderSync.RecentlyTouched("aux")) AuxLevelDb = s["auxLevelDb"]?.GetValue<double>() ?? 0;
                AuxLevelLock = s["auxLevelLock"]?.GetValue<bool>() ?? false;
            }

            ApplyDetected(node["detected"]);
            ApplyProfiles(node["profiles"]);
            ApplyRecallOnConnect(node["recallOnConnect"]);
            ApplyDevices(node["devices"], node["mixer"]);
            ApplyMixer(node["mixer"]);
            ApplyStreams(node["mixer"]);
            ShowSoftLowCut = DeviceConnected && !CapLowCut && HasMixer;
            ShowSoftClipGuard = DeviceConnected && !CapClipGuard && HasMixer;
            ShowGainLock = DeviceConnected && !CapPhysicalControls;
            Status = DeviceConnected ? "ready" : "no device";
        }
        finally { _applying = false; }
        StateApplied?.Invoke();
    }

    /// <summary>Attached supported interfaces; a picker shows when there are two.</summary>
    public ObservableCollection<DetectedDeviceItem> DetectedDevices { get; } = [];

    private bool _hasMultipleDevices;
    public bool HasMultipleDevices { get => _hasMultipleDevices; set => Set(ref _hasMultipleDevices, value); }

    private string _activeDeviceName = "";
    public string ActiveDeviceName { get => _activeDeviceName; set => Set(ref _activeDeviceName, value); }

    /// <summary>
    /// Explicit pick from the device menu. Only a click reaches this, never a
    /// binding, so a list rebuild can no longer switch devices on its own.
    /// </summary>
    public void SelectDevice(DetectedDeviceItem device)
    {
        if (!device.Active) _ = _client.SetActiveDeviceAsync(device.UsbId);
    }

    private void ApplyDetected(JsonNode? detected)
    {
        var items = (detected as JsonArray)?.Select(n => new DetectedDeviceItem(
            n?["usbId"]?.GetValue<string>() ?? "",
            n?["name"]?.GetValue<string>() ?? "",
            n?["active"]?.GetValue<bool>() ?? false)).ToList() ?? [];
        if (!items.SequenceEqual(DetectedDevices))
        {
            DetectedDevices.Clear();
            foreach (DetectedDeviceItem d in items) DetectedDevices.Add(d);
        }
        HasMultipleDevices = items.Count > 1;
        ActiveDeviceName = DetectedDevices.FirstOrDefault(d => d.Active)?.Name ?? "select device";
    }

    /// <summary>Saved profile names from the daemon, newest list wins.</summary>
    public ObservableCollection<string> Profiles { get; } = [];

    private void ApplyProfiles(JsonNode? profiles)
    {
        var names = (profiles as JsonArray)?.Select(n => n?.GetValue<string>())
            .Where(n => n is not null).Cast<string>().ToList() ?? [];
        if (names.SequenceEqual(Profiles)) return;
        Profiles.Clear();
        foreach (string n in names) Profiles.Add(n);
    }

    /// <summary>The "none" entry of the recall picker; parentheses cannot occur in a profile name.</summary>
    public const string NoRecall = "(none)";

    /// <summary>Recall picker entries: "(none)" then every saved profile.</summary>
    public ObservableCollection<string> RecallChoices { get; } = [NoRecall];

    private string _recallChoice = NoRecall;

    /// <summary>
    /// The profile the daemon recalls when this device connects. Set by the
    /// picker (which sends the command) and by state pushes (silently).
    /// </summary>
    public string RecallOnConnectChoice
    {
        get => _recallChoice;
        set
        {
            if (value is null || !Set(ref _recallChoice, value) || _applying) return;
            _ = _client.SetRecallOnConnectAsync(value == NoRecall ? null : value);
        }
    }

    private void ApplyRecallOnConnect(JsonNode? recall)
    {
        var choices = new List<string> { NoRecall };
        choices.AddRange(Profiles);
        if (!choices.SequenceEqual(RecallChoices))
        {
            RecallChoices.Clear();
            foreach (string c in choices) RecallChoices.Add(c);
            _recallChoice = "";   // the combo lost its selection with the items; force the re-set below
        }
        string want = recall?.GetValue<string>() is string r && Profiles.Contains(r) ? r : NoRecall;
        RecallOnConnectChoice = want;
    }

    public void SaveProfile(string name) => _ = _client.SaveProfileAsync(name);
    public void LoadProfile(string name) => _ = _client.LoadProfileAsync(name);
    public void DeleteProfile(string name) => _ = _client.DeleteProfileAsync(name);

    /// <summary>Raised after a daemon state push has been applied (UI thread).</summary>
    public event Action? StateApplied;

    /// <summary>
    /// Refresh the output/input pickers. The lists are rebuilt only when the set
    /// of devices actually changes, so an open dropdown is not disturbed by the
    /// state pushes that arrive on every fader move.
    /// </summary>
    private void ApplyDevices(JsonNode? devices, JsonNode? mixer)
    {
        if (devices is not JsonArray arr) { Outputs.Clear(); Inputs.Clear(); return; }

        var sinks = new List<AudioDeviceItem>();
        var sources = new List<AudioDeviceItem>();
        foreach (JsonNode? d in arr)
        {
            if (d is null) continue;
            string? name = d["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;
            string desc = d["description"]?.GetValue<string>() ?? name;
            bool own = d["isOwn"]?.GetValue<bool>() ?? false;
            bool physical = d["isPhysical"]?.GetValue<bool>() ?? false;
            var item = new AudioDeviceItem(name, desc, own, physical);
            (IsSink(d["kind"]) ? sinks : sources).Add(item);
        }
        Replace(Outputs, sinks);
        Replace(Inputs, sources);

        // Rebuild the multi-select list only when membership changed, so open
        // dropdowns and checkbox states survive routine pushes.
        var wanted = sinks.Where(s => !s.IsOwn).ToList();
        if (!wanted.Select(w => w.Name).SequenceEqual(MonitorOutputs.Select(m => m.Name)))
        {
            MonitorOutputs.Clear();
            foreach (AudioDeviceItem s in wanted)
                MonitorOutputs.Add(new MonitorOutputItem(s.Name, s.Label, OnMonitorSelectionChanged));
        }

        EnforcedDefaultSink = mixer?["enforcedDefaultSink"]?.GetValue<string>();
        EnforcedDefaultSource = mixer?["enforcedDefaultSource"]?.GetValue<string>();

        if (!SliderSync.RecentlyTouched("outvol"))
            OutputVolume = mixer?["outputVolume"]?.GetValue<double>() ?? 0;

        var current = new HashSet<string>(
            (mixer?["monitorOutputs"] as JsonArray)?.Select(n => n?.GetValue<string>())
                .Where(n => n is not null).Select(n => n!) ?? []);
        foreach (MonitorOutputItem item in MonitorOutputs) item.Sync(current.Contains(item.Name));
        Raise(nameof(MonitorSummary));
    }

    /// <summary>AudioNodeKind arrives as the enum's number (0 = Sink) or name.</summary>
    private static bool IsSink(JsonNode? kind) => kind switch
    {
        null => false,
        _ => kind.ToJsonString().Trim('"') is "0" or "Sink",
    };

    private static void Replace(ObservableCollection<AudioDeviceItem> target, List<AudioDeviceItem> fresh)
    {
        if (target.Count == fresh.Count && target.Select(x => x.Name).SequenceEqual(fresh.Select(x => x.Name)))
            return;   // unchanged, leave the collection (and any open dropdown) alone
        target.Clear();
        foreach (AudioDeviceItem d in fresh) target.Add(d);
    }

    /// <summary>Route each peak to its channel strip or mix.</summary>
    private void ApplyMeters(JsonNode levels)
    {
        if (levels is not JsonObject obj) return;
        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            double l = 0, r = 0;
            if (kv.Value is JsonArray arr && arr.Count >= 2)
            {
                l = arr[0]?.GetValue<double>() ?? 0;
                r = arr[1]?.GetValue<double>() ?? 0;
            }
            if (kv.Key.StartsWith("ch:", StringComparison.Ordinal))
            {
                ChannelViewModel? c = Channels.FirstOrDefault(x => x.Id == kv.Key[3..]);
                if (c is not null) { c.MeterL = l; c.MeterR = r; }
            }
            else if (kv.Key.StartsWith("mix:", StringComparison.Ordinal))
            {
                MixViewModel? m = Mixes.FirstOrDefault(x => x.Id == kv.Key[4..]);
                if (m is not null) { m.MeterL = l; m.MeterR = r; }
            }
        }
    }

    /// <summary>Applications currently playing, with the channel each landed in.</summary>
    public ObservableCollection<AppStreamViewModel> Apps { get; } = [];

    private void ApplyStreams(JsonNode? mixer)
    {
        if (mixer?["streams"] is not JsonArray arr) { Apps.Clear(); return; }
        var fresh = new List<(string Identity, string Label, string Channel, bool Active, bool Running)>();
        foreach (JsonNode? s in arr)
        {
            if (s is null) continue;
            fresh.Add((s["identity"]?.GetValue<string>() ?? "?",
                       s["label"]?.GetValue<string>() ?? "?",
                       s["channelId"]?.GetValue<string>() ?? "",
                       s["active"]?.GetValue<bool>() ?? true,
                       s["running"]?.GetValue<bool>() ?? true));
        }
        // Update in place so an open dropdown is not closed by a state push.
        foreach (var f in fresh)
        {
            AppStreamViewModel? existing = Apps.FirstOrDefault(a =>
                string.Equals(a.Identity, f.Identity, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                Apps.Add(new AppStreamViewModel(_client, f.Identity, f.Label,
                    [.. Channels.Where(c => c.AcceptsApps).Select(c => new ChannelOption(c.Id, c.Name))])
                { ChannelId = f.Channel, Active = f.Active, Running = f.Running });
            else
                existing.ApplyFromDaemon(f.Channel, f.Active, f.Running, f.Label);
        }
        for (int i = Apps.Count - 1; i >= 0; i--)
            if (!fresh.Any(f => string.Equals(f.Identity, Apps[i].Identity, StringComparison.OrdinalIgnoreCase))) Apps.RemoveAt(i);

        // Maintain the running-apps view without disturbing open dropdowns.
        foreach (AppStreamViewModel a in Apps)
        {
            bool listed = ActiveApps.Contains(a);
            bool wanted = a.Active || a.Running;
            if (wanted && !listed) ActiveApps.Add(a);
            else if (!wanted && listed) ActiveApps.Remove(a);
        }
        for (int i = ActiveApps.Count - 1; i >= 0; i--)
            if (!Apps.Contains(ActiveApps[i])) ActiveApps.RemoveAt(i);
        Raise(nameof(HasApps));
    }

    /// <summary>Plugin insert chains of the two XLR channels.</summary>
    public InsertsViewModel Inserts { get; }
    public InsertsViewModel Inserts2 { get; }

    private void ApplyMixer(JsonNode? mixer)
    {
        if (mixer is null) { HasMixer = false; Channels.Clear(); Mixes.Clear(); return; }
        HasMixer = true;
        SoftLowCutHz = mixer["lowCutHz"]?.GetValue<int>() ?? 0;
        SoftClipGuardAvailable = mixer["softClipGuardAvailable"]?.GetValue<bool>() ?? false;
        SoftClipGuardError = mixer["softClipGuardError"]?.GetValue<string>();
        SoftClipGuard = mixer["softClipGuard"]?.GetValue<bool>() ?? false;
        Inserts.Apply(mixer["inserts"]?["xlr1"]);
        Inserts2.Apply(mixer["inserts"]?["xlr2"]);

        if (mixer["mixes"] is JsonArray mixes)
        {
            SyncList(Mixes, mixes, m => m["id"]!.GetValue<string>(),
                (m, vm) => vm.ApplyFromDaemon(m),
                m => new MixViewModel(_client, m["id"]!.GetValue<string>(), m["name"]!.GetValue<string>()));
            bool auxOn = mixer["auxPortEnabled"]?.GetValue<bool>() ?? true;
            foreach (MixViewModel mv in Mixes.Where(mv => mv.IsAuxPort)) mv.ApplyAuxPort(auxOn);
            // The Aux mix only exists to feed the device's USB Aux port; hide
            // it on hardware without that port (its send rows follow below,
            // once the channels have synced).
            foreach (MixViewModel mv in Mixes.Where(mv => mv.IsAuxPort))
                mv.Visible = !DeviceConnected || CapOutputRouting;
            foreach (MixViewModel mv in Mixes)
            {
                mv.Inserts.Apply(mixer["inserts"]?[$"mix:{mv.Id}"]);
                mv.Inserts.EnsurePluginsLoaded();   // shared fetch; cheap after the first
            }
        }

        if (mixer["channels"] is JsonArray channels)
        {
            var mixChoices = Mixes.Select(m => (m.Id, m.Name)).ToList();
            SyncList(Channels, channels, c => c["id"]!.GetValue<string>(),
                (c, vm) => { vm.SyncMixes(mixChoices); vm.ApplyFromDaemon(c); },
                c => new ChannelViewModel(_client, c["id"]!.GetValue<string>(), c["name"]!.GetValue<string>(),
                    mixChoices));
            // Hardware input tiles only make sense for jacks the active
            // device has; without a device, show everything as before.
            foreach (ChannelViewModel c in Channels)
            {
                c.Visible = c.Id switch
                {
                    "xlr2" => !DeviceConnected || HasXlr2,
                    "aux" => !DeviceConnected || CapAuxInput,
                    _ => true,
                };
                foreach (SendViewModel send in c.Sends.Where(s => s.MixId == "auxout"))
                    send.Visible = !DeviceConnected || CapOutputRouting;
            }
            IReadOnlyList<ChannelOption> appChannels = [.. Channels.Where(c => c.AcceptsApps)
                .Select(c => new ChannelOption(c.Id, c.Name))];
            foreach (AppStreamViewModel app in Apps) app.SyncChannels(appChannels);
        }
    }

    /// <summary>Update in place by id so bindings survive; add/remove as needed.</summary>
    private static void SyncList<T>(ObservableCollection<T> target, JsonArray source,
        Func<JsonNode, string> idOf, Action<JsonNode, T> update, Func<JsonNode, T> create)
        where T : class, IHasId
    {
        var seen = new HashSet<string>();
        foreach (JsonNode? item in source)
        {
            if (item is null) continue;
            string id = idOf(item);
            seen.Add(id);
            T? existing = target.FirstOrDefault(x => x.Id == id);
            if (existing is null) { T made = create(item); update(item, made); target.Add(made); }
            else update(item, existing);
        }
        for (int i = target.Count - 1; i >= 0; i--)
            if (!seen.Contains(target[i].Id)) target.RemoveAt(i);
    }
}

public interface IHasId { string Id { get; } }

public sealed record ChannelOption(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>An application that is playing, and the channel it is routed to.</summary>
public sealed class AppStreamViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private bool _applying;

    public AppStreamViewModel(DaemonClient client, string identity, string label, IReadOnlyList<ChannelOption> channels)
    {
        _client = client; Identity = identity; _label = label;
        foreach (ChannelOption c in channels) Channels.Add(c);
    }

    public string Identity { get; }

    private string _label;
    public string Label { get => _label; private set => Set(ref _label, value); }
    public ObservableCollection<ChannelOption> Channels { get; } = [];

    public void SyncChannels(IReadOnlyList<ChannelOption> channels)
    {
        if (Channels.SequenceEqual(channels)) return;
        Channels.Clear();
        foreach (ChannelOption channel in channels) Channels.Add(channel);
        Raise(nameof(SelectedChannel));
    }

    private bool _active = true;
    public bool Active
    {
        get => _active;
        set { if (Set(ref _active, value)) { Raise(nameof(Opacity)); Raise(nameof(StatusText)); } }
    }

    private bool _running = true;
    public bool Running
    {
        get => _running;
        set { if (Set(ref _running, value)) { Raise(nameof(Opacity)); Raise(nameof(StatusText)); } }
    }

    /// <summary>Playing full, running slightly dimmed, remembered-only half.</summary>
    public double Opacity => _active ? 1.0 : _running ? 0.8 : 0.5;

    private string _channelId = "";
    public string ChannelId
    {
        get => _channelId;
        set
        {
            if (!Set(ref _channelId, value)) return;
            Raise(nameof(SelectedChannel));
            if (!_applying && value.Length > 0) _ = _client.AssignAppAsync(Identity, value);
        }
    }

    public ChannelOption? SelectedChannel
    {
        get => Channels.FirstOrDefault(c => c.Id == ChannelId);
        set { if (value is not null) ChannelId = value.Id; }
    }

    public void ApplyFromDaemon(string channelId, bool active, bool running, string? label = null)
    {
        _applying = true;
        try
        {
            ChannelId = channelId; Active = active; Running = running;
            if (label is { Length: > 0 }) Label = label;   // labels heal (e.g. ".exe" dropped)
        }
        finally { _applying = false; }
    }

    /// <summary>"playing" / "running" / "not running", for the manage dialog.</summary>
    public string StatusText => Active ? "playing" : Running ? "running" : "not running";

    public void Forget() => _ = _client.ForgetAppAsync(Identity);
}

/// <summary>One selectable sink or source. Own nodes are OpenXLR's own.</summary>
public sealed record AudioDeviceItem(string Name, string Description, bool IsOwn, bool IsPhysical = false)
{
    public string Label => IsOwn ? $"{Description} (OpenXLR)" : Description;
}

/// <summary>
/// One entry in the multi-select Monitor dropdown: a sink plus a checkbox
/// state. Toggling notifies the owning view model, which sends the full
/// selection to the daemon.
/// </summary>
public sealed class MonitorOutputItem : ViewModelBase
{
    private readonly Action _changed;
    public MonitorOutputItem(string name, string label, Action changed)
    {
        Name = name;
        Label = label;
        _changed = changed;
    }

    public string Name { get; }
    public string Label { get; }

    /// <summary>Set without notifying the daemon (state pushes).</summary>
    public void Sync(bool selected) { if (Set(ref _isSelected, selected, nameof(IsSelected))) { } }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) _changed(); }
    }
}

/// <summary>A structural or user-created mix: master level, mute, and kind.</summary>
public sealed class MixViewModel : ViewModelBase, IHasId
{
    private readonly DaemonClient _client;
    private bool _applying;

    public MixViewModel(DaemonClient client, string id, string name)
    {
        _client = client; Id = id; _name = name;
        Inserts = new InsertsViewModel(client, $"mix:{id}", channels: 2, title: $"{name} mix");
    }

    public string Id { get; }
    private string _name;
    public string Name { get => _name; private set => Set(ref _name, value); }

    private bool _isMonitor;
    public bool IsMonitor { get => _isMonitor; private set => Set(ref _isMonitor, value); }
    private bool _isVirtualMic;
    public bool IsVirtualMic { get => _isVirtualMic; private set => Set(ref _isVirtualMic, value); }
    private bool _isAuxPort;
    public bool IsAuxPort { get => _isAuxPort; private set => Set(ref _isAuxPort, value); }
    private bool _canDelete;
    public bool CanDelete { get => _canDelete; private set => Set(ref _canDelete, value); }

    /// <summary>This mix's stereo plugin insert chain.</summary>
    public InsertsViewModel Inserts { get; }

    private bool _visible = true;
    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    private double _volume = 1.0;
    public double Volume
    {
        get => _volume;
        set
        {
            if (Set(ref _volume, value) && !_applying)
            {
                double v = value;
                SliderSync.Touch($"mixvol:{Id}");
                SliderSync.Send($"mixvol:{Id}", () => _ = _client.SetMixVolumeAsync(Id, v));
            }
            Raise(nameof(VolumeText));
        }
    }
    public string VolumeText => $"{_volume * 100:0}%";

    private bool _muted;
    public bool Muted { get => _muted; set { if (Set(ref _muted, value) && !_applying) _ = _client.SetMixMutedAsync(Id, value); } }

    private bool _auxPortEnabled = true;
    public bool AuxPortEnabled
    {
        get => _auxPortEnabled;
        set { if (Set(ref _auxPortEnabled, value) && !_applying) _ = _client.SetAuxPortEnabledAsync(value); }
    }

    private double _meterL;
    public double MeterL { get => _meterL; set => Set(ref _meterL, Math.Min(value, 1.0)); }
    private double _meterR;
    public double MeterR { get => _meterR; set => Set(ref _meterR, Math.Min(value, 1.0)); }

    public void ApplyFromDaemon(JsonNode n)
    {
        _applying = true;
        try
        {
            Name = n["name"]?.GetValue<string>() ?? Name;
            Inserts.SetTitle($"{Name} mix");
            if (!SliderSync.RecentlyTouched($"mixvol:{Id}"))
                Volume = n["volume"]?.GetValue<double>() ?? 1.0;
            Muted = n["muted"]?.GetValue<bool>() ?? false;
            IsMonitor = n["isMonitor"]?.GetValue<bool>() ?? Id == "monitor";
            IsVirtualMic = n["isVirtualMic"]?.GetValue<bool>() ?? Id is "stream" or "chat";
            IsAuxPort = n["isAuxPort"]?.GetValue<bool>() ?? Id == "auxout";
            CanDelete = n["canDelete"]?.GetValue<bool>() ?? IsVirtualMic;
        }
        finally { _applying = false; }
    }

    public void ApplyAuxPort(bool enabled)
    {
        _applying = true;
        try { AuxPortEnabled = enabled; }
        finally { _applying = false; }
    }
}

/// <summary>A channel with one send (level + mute) per mix.</summary>
public sealed class ChannelViewModel : ViewModelBase, IHasId
{
    public ChannelViewModel(DaemonClient client, string id, string name,
        IReadOnlyList<(string Id, string Name)> mixes)
    {
        Id = id; _name = name;
        _client = client;
        SyncMixes(mixes);
    }

    private readonly DaemonClient _client;

    public string Id { get; }
    private string _name;
    public string Name { get => _name; private set => Set(ref _name, value); }
    public ObservableCollection<SendViewModel> Sends { get; } = [];

    private bool _isHardware;
    public bool IsHardware { get => _isHardware; private set => Set(ref _isHardware, value); }
    private bool _acceptsApps = true;
    public bool AcceptsApps { get => _acceptsApps; private set => Set(ref _acceptsApps, value); }
    private bool _canDelete;
    public bool CanDelete { get => _canDelete; private set => Set(ref _canDelete, value); }

    private bool _visible = true;
    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    private double _meterL;
    public double MeterL { get => _meterL; set => Set(ref _meterL, Math.Min(value, 1.0)); }
    private double _meterR;
    public double MeterR { get => _meterR; set => Set(ref _meterR, Math.Min(value, 1.0)); }

    public void ApplyFromDaemon(JsonNode n)
    {
        Name = n["name"]?.GetValue<string>() ?? Name;
        IsHardware = n["isHardware"]?.GetValue<bool>() ?? Id is "xlr1" or "xlr2" or "aux";
        AcceptsApps = n["acceptsApps"]?.GetValue<bool>() ?? !IsHardware;
        CanDelete = n["canDelete"]?.GetValue<bool>() ?? !IsHardware;
        var muted = new HashSet<string>();
        if (n["mutedIn"] is JsonArray arr)
            foreach (JsonNode? m in arr) if (m is not null) muted.Add(m.GetValue<string>());

        if (n["levels"] is JsonObject levels)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in levels)
            {
                SendViewModel? send = Sends.FirstOrDefault(s => s.MixId == kv.Key);
                if (send is null) continue;
                send.ApplyFromDaemon(kv.Value?.GetValue<double>() ?? 0.0, muted.Contains(kv.Key));
            }
        }
    }

    public void SyncMixes(IReadOnlyList<(string Id, string Name)> mixes)
    {
        foreach ((string id, string name) in mixes)
        {
            SendViewModel? existing = Sends.FirstOrDefault(s => s.MixId == id);
            if (existing is null) Sends.Add(new SendViewModel(_client, Id, id, name));
            else existing.MixName = name;
        }
        for (int i = Sends.Count - 1; i >= 0; i--)
            if (!mixes.Any(m => m.Id == Sends[i].MixId)) Sends.RemoveAt(i);
    }
}

/// <summary>One channel's send into one mix, the fader cell.</summary>
public sealed class SendViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private readonly string _channelId;
    private bool _applying;

    public SendViewModel(DaemonClient client, string channelId, string mixId, string mixName)
    {
        _client = client; _channelId = channelId; MixId = mixId; _mixName = mixName;
    }

    public string MixId { get; }
    private string _mixName;
    public string MixName { get => _mixName; set => Set(ref _mixName, value); }

    private bool _visible = true;
    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    private double _level;
    public double Level
    {
        get => _level;
        set
        {
            if (Set(ref _level, value) && !_applying)
            {
                double v = value;
                string key = $"lvl:{_channelId}:{MixId}";
                SliderSync.Touch(key);
                SliderSync.Send(key, () => _ = _client.SetLevelAsync(_channelId, MixId, v));
            }
            Raise(nameof(LevelText));
        }
    }
    public string LevelText => $"{_level * 100:0}";

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set { if (Set(ref _muted, value) && !_applying) _ = _client.SetChannelMutedAsync(_channelId, MixId, value); }
    }

    public void ApplyFromDaemon(double level, bool muted)
    {
        _applying = true;
        try
        {
            if (!SliderSync.RecentlyTouched($"lvl:{_channelId}:{MixId}")) Level = level;
            Muted = muted;
        }
        finally { _applying = false; }
    }
}


/// <summary>
/// Keeps sliders responsive against the daemon's echo. Dragging a slider fires
/// its setter for every pixel; unthrottled, each send triggers a state push that
/// snaps the slider to a stale value mid-drag (rubber-banding). Send() batches
/// to a trailing edge every 80 ms, and RecentlyTouched() suppresses applying
/// echoes to a slider for 800 ms after the user moved it. UI-thread only.
/// </summary>
internal static class SliderSync
{
    private static readonly System.Collections.Generic.Dictionary<string, DateTime> Touched = [];
    private static readonly System.Collections.Generic.Dictionary<string, Action> Pending = [];
    private static DispatcherTimer? _timer;

    public static void Touch(string key) => Touched[key] = DateTime.UtcNow;

    public static bool RecentlyTouched(string key)
        => Touched.TryGetValue(key, out DateTime t) && DateTime.UtcNow - t < TimeSpan.FromMilliseconds(800);

    public static void Send(string key, Action send)
    {
        Pending[key] = send;
        _timer ??= CreateTimer();
        if (!_timer.IsEnabled) _timer.Start();
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            foreach (string key in System.Linq.Enumerable.ToList(Pending.Keys))
            {
                if (Pending.Remove(key, out Action? send)) send();
            }
            if (Pending.Count == 0) timer.Stop();
        };
        return timer;
    }
}

/// <summary>One attached supported interface in the device picker.</summary>
public sealed record DetectedDeviceItem(string UsbId, string Name, bool Active)
{
    public override string ToString() => Name;
}
