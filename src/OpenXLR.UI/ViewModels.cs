using OpenXLR.UI.Localization;
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
public sealed partial class MainViewModel : ViewModelBase
{
    public DaemonRestartViewModel DaemonRestart { get; }
    public UpdatesViewModel Updates { get; } = new();

    /// <summary>The ducking setup the Ducking window edits; reported by every daemon that has it, on any device.</summary>
    public DuckingViewModel Ducking { get; }

    private readonly DaemonClient _client;
    private bool _applying;

    public MainViewModel(DaemonClient client) : this(client, new DaemonRestartViewModel()) { }

    internal MainViewModel(DaemonClient client, DaemonRestartViewModel daemonRestart)
    {
        _client = client;
        DaemonRestart = daemonRestart;
        Ducking = new DuckingViewModel(client);
        LoadMixerView();
        OutputVolumeRange = new VolumeRangeViewModel(() => OutputVolume = Math.Min(OutputVolume, 1));
        OutputVolumeRange.Changed += RequestDesktopVolumeBoost;
        Inserts = new InsertsViewModel(client, "xlr1", 1, "XLR 1");
        Inserts2 = new InsertsViewModel(client, "xlr2", 1, "XLR 2");
        _client.StateReceived += node => Dispatcher.UIThread.Post(() => Apply(node));
        _client.ConnectionChanged += up => Dispatcher.UIThread.Post(() =>
        {
            DaemonConnected = up;
            DaemonRestart.ConnectionChanged(up);
            if (!up)
            {
                DeviceConnected = false; RefreshBindings(); Status = Localizer.Text("StatusDaemonNotRunning");
                ResetWaveInterfaces();
                ResetBrowserTabs();
                Inserts.ResetForNewConnection(); Inserts2.ResetForNewConnection();
                foreach (MixViewModel mv in Mixes) mv.Inserts.ResetForNewConnection();
                foreach (ChannelViewModel channel in Channels) channel.Inserts?.ResetForNewConnection();
                foreach (MonitorOutputItem output in MonitorOutputs) output.CancelFeedEdit();
                CancelMonitorSelectionEdit();
                CancelMonitorMicrophoneEdit();
            }
            else { Inserts.EnsurePluginsLoaded(); Inserts2.EnsurePluginsLoaded(); }
        });
        _client.ErrorReceived += msg => Dispatcher.UIThread.Post(() => Status = msg);
        _client.NativeEditorRulesChanged += () => Dispatcher.UIThread.Post(() => InsertsViewModel.ReloadAll?.Invoke());
        var meters = new MeterUpdates(action => Dispatcher.UIThread.Post(action), ApplyMeters);
        _client.MetersReceived += meters.Publish;
        InsertsViewModel.ReloadAll = () =>
        {
            InsertsViewModel.ForgetCatalogue();
            Inserts.Refetch(); Inserts2.Refetch();
            foreach (MixViewModel mv in Mixes) mv.Inserts.Refetch();
            foreach (ChannelViewModel channel in Channels) channel.Inserts?.Refetch();
        };
    }

    // --- connection / device identity ---

    private bool _daemonConnected;
    public bool DaemonConnected { get => _daemonConnected; private set { if (SetAndRaiseMismatch(ref _daemonConnected, value)) { Raise(nameof(StatusLine)); Raise(nameof(MixerPlaceholder)); Raise(nameof(CanEditLayout)); } } }

    /// <summary>What the empty SUBMIXER tile says: the three reasons differ.</summary>
    public string MixerPlaceholder => !DaemonConnected
        ? Localizer.Text("DaemonNotRunningSentence")
        : MixerWaiting ? Localizer.Text("MixerWaitingForAudioServer")
        : Localizer.Text("SubmixerIsOffTurnItOnInOptions");

    private bool _mixerWaiting;
    /// <summary>The submixer is on, but the audio server did not answer when the daemon built it.</summary>
    public bool MixerWaiting { get => _mixerWaiting; private set { if (Set(ref _mixerWaiting, value)) Raise(nameof(MixerPlaceholder)); } }

    private bool _deviceConnected;
    /// <summary>The primary interface is driven; the header's LED says this and nothing else.</summary>
    public bool DeviceConnected { get => _deviceConnected; private set { if (Set(ref _deviceConnected, value)) Raise(nameof(StatusLine)); } }

    private string _deviceName = "none";
    public string DeviceName { get => _deviceName; private set { if (Set(ref _deviceName, value)) Raise(nameof(StatusLine)); } }

    private string _status = Localizer.Text("Connecting");
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(StatusLine)); } }

    public string StatusLine => !DaemonConnected ? Localizer.Text("DaemonNotRunning")
        : DeviceConnected ? DeviceName
        : Localizer.Text("NoDevice");

    // --- hardware controls ---

    private bool _gainLocked;
    public bool GainLocked
    {
        get => _gainLocked;
        set { if (Set(ref _gainLocked, value) && !_applying) _ = _client.SetControlAsync("gainLock", value); }
    }

    private int _gainMaxDb = 80;
    /// <summary>The top of the gain sliders: the device's highest gain.</summary>
    public int GainMaxDb { get => _gainMaxDb; private set => Set(ref _gainMaxDb, value); }

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

    private bool _unitXlrJacks = true;
    /// <summary>The interface's inputs are XLR jacks; false on a Wave:3 or Wave:1, whose input is its built-in capsule.</summary>
    public bool UnitXlrJacks { get => _unitXlrJacks; set => Set(ref _unitXlrJacks, value); }

    private bool _capOutputRouting = true;
    public bool CapOutputRouting { get => _capOutputRouting; set => Set(ref _capOutputRouting, value); }

    private bool _capPhysicalControls = true;
    public bool CapPhysicalControls { get => _capPhysicalControls; set => Set(ref _capPhysicalControls, value); }

    // Interfaces without settings memory (Wave XLR, the first XLR Dock)
    // get their last settings back on connect and can be reset to the
    // firmware defaults; devices that keep their own settings need neither.
    private bool _capRetainsSettings = true;
    public bool CapRetainsSettings { get => _capRetainsSettings; set => Set(ref _capRetainsSettings, value); }

    private bool _capBuiltInDefaults;
    /// <summary>OpenXLR ships a baseline for this model, so a device with its own memory can still be reset.</summary>
    public bool CapBuiltInDefaults { get => _capBuiltInDefaults; set { if (Set(ref _capBuiltInDefaults, value)) Raise(nameof(ResetDescription)); } }

    /// <summary>What the reset button does on this device, for its tooltip and confirmation.</summary>
    public string ResetDescription => CapRetainsSettings
        ? Localizer.Text("ResetToBaselineDescription")
        : Localizer.Text("ResetToFirmwareDescription");

    private bool _showResetDefaults;
    public bool ShowResetDefaults { get => _showResetDefaults; private set => Set(ref _showResetDefaults, value); }

    // The daemon's sentence about a unit driven differently from the usual
    // (an XLR Dock taking a control through its config block), or empty.
    private string _deviceNote = "";
    public string DeviceNote { get => _deviceNote; private set { if (Set(ref _deviceNote, value)) Raise(nameof(ShowInterfaceCard)); } }
    // The firmware version the unit reports, or empty where the daemon does not read it.
    private string _deviceFirmware = "";
    public string DeviceFirmware
    {
        get => _deviceFirmware;
        private set { if (Set(ref _deviceFirmware, value)) { Raise(nameof(FirmwareText)); Raise(nameof(ShowInterfaceCard)); } }
    }
    public string FirmwareText => DeviceFirmware.Length > 0 ? Localizer.Format("DeviceFirmware", DeviceFirmware) : "";
    public bool ShowInterfaceCard => ShowResetDefaults || DeviceNote.Length > 0 || DeviceFirmware.Length > 0 || ShowWaveInterfaces;

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
    public bool ShowSoftClipGuard { get => _showSoftClipGuard; private set { if (Set(ref _showSoftClipGuard, value)) Raise(nameof(ShowSoftwareControls)); } }

    /// <summary>XLR 1's software low cut or ClipGuard applies, so the INPUTS panel has a row for them.</summary>
    public bool ShowSoftwareControls => _showSoftLowCut || _showSoftClipGuard;

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
        ? Localizer.Text("SoftClipGuardTip")
        : SoftClipGuardError ?? Localizer.Text("SoftClipGuardUnavailable");

    // Latency compensation across mixes, a mixer setting the daemon saves.
    private bool _compensateMixLatency;
    public bool CompensateMixLatency
    {
        get => _compensateMixLatency;
        set { if (Set(ref _compensateMixLatency, value) && !_applying) _ = _client.SetMixLatencyCompensationAsync(value); }
    }

    private string? _mixLatencyError;
    public string? MixLatencyError { get => _mixLatencyError; private set => Set(ref _mixLatencyError, value); }

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
    public string SoftLowCutText => _softLowCutHz == 0 ? Localizer.Text("LowCutOff") : Localizer.Format("LowCutFrequency", _softLowCutHz);

    private bool _showSoftLowCut;
    public bool ShowSoftLowCut { get => _showSoftLowCut; private set { if (Set(ref _showSoftLowCut, value)) Raise(nameof(ShowSoftwareControls)); } }

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
    public string CrossfadeText => _crossfade == 100 ? Localizer.Text("CrossfadeCentre")
        : _crossfade < 100 ? Localizer.Format("CrossfadeMic", 100 - _crossfade) : Localizer.Format("CrossfadePc", _crossfade - 100);

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
    public string MuteLabel => PhantomSettling ? $"48V {PhantomSettleSeconds}s" : Localizer.Text("Mute");
    public string Mute2Label => PhantomSettling2 ? $"48V {PhantomSettleSeconds2}s" : Localizer.Text("Mute");

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

    private bool _hpMixMicDirect;
    /// <summary>
    /// The Wave XLR Pro carries its first input straight to its headphone
    /// jacks (its zero-latency direct path), as the device reports it. The
    /// daemon drives the path; the window only reads it, to say on the
    /// carrying channel that its volume does not reach that path.
    /// </summary>
    public bool HpMixMicDirect { get => _hpMixMicDirect; private set => Set(ref _hpMixMicDirect, value); }

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
    /// <summary>Capture inputs in layout order, for their blocks in the classic INPUTS panel.</summary>
    public ObservableCollection<ChannelViewModel> CaptureInputs { get; } = [];
    public ObservableCollection<MixViewModel> Mixes { get; } = [];

    /// <summary>Running audio-capable apps (the main card shows these).</summary>
    public ObservableCollection<AppStreamViewModel> ActiveApps { get; } = [];

    /// <summary>True when the registry holds anything (enables Manage).</summary>
    public bool HasApps => Apps.Count > 0;

    /// <summary>Pre-register an installed app with a channel (Manage dialog).</summary>
    public void AddApp(string identity, string label, string channel)
        => _ = _client.AssignAppAsync(identity, channel, label);

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
            return picked.Count == 0 ? Localizer.Text("MonitorNotRouted")
                 : picked.Count <= 2 ? string.Join(" + ", picked)
                 : Localizer.Format("MonitorOutputsMore", picked[0], picked.Count - 1);
        }
    }

    private void OnMonitorSelectionChanged()
    {
        Raise(nameof(MonitorSummary));
        if (_applying) return;
        // The daemon's order first, so the output the desktop volume drives
        // stays first, then what was just ticked.
        var ticked = MonitorOutputs.Where(o => o.IsSelected).Select(o => o.Name).ToHashSet();
        List<string> previous = _pendingMonitorSelection ?? _monitorSelection;
        var names = previous.Where(ticked.Contains)
            .Concat(MonitorOutputs.Where(o => o.IsSelected && !previous.Contains(o.Name)).Select(o => o.Name))
            .ToList();
        _pendingMonitorSelection = names;
        _ = SubmitMonitorSelectionAsync(names, ++_monitorSelectionVersion);
    }

    private async Task SubmitMonitorSelectionAsync(List<string> names, long version)
    {
        string? error = await _client.SetMonitorOutputsAsync(names);
        if (version != _monitorSelectionVersion) return;
        _pendingMonitorSelection = null;
        if (error is null) return;
        RestoreReportedMonitorSelection();
        Status = error;
    }

    private void RestoreReportedMonitorSelection()
    {
        bool applying = _applying;
        _applying = true;
        try { ApplyDevices(_reportedMonitorDevices, _reportedMonitorMixer); }
        finally { _applying = applying; }
    }

    internal void CancelMonitorSelectionEdit()
    {
        _monitorSelectionVersion++;
        bool pending = _pendingMonitorSelection is not null;
        _pendingMonitorSelection = null;
        if (pending) RestoreReportedMonitorSelection();
    }

    // The monitor outputs as the daemon last reported them, in its order.
    private List<string> _monitorSelection = [];
    private List<string>? _pendingMonitorSelection;
    private long _monitorSelectionVersion;
    private JsonNode? _reportedMonitorDevices, _reportedMonitorMixer;
    // Device descriptions seen this session, to name a selected output while it is missing.
    private readonly Dictionary<string, string> _outputLabels = [];

    /// <summary>Enforced defaults as reported by the daemon (for the Options window).</summary>
    public string? EnforcedDefaultSink { get; private set; }
    public string? EnforcedDefaultSource { get; private set; }

    /// <summary>Mirrors the ui.json preference; MainWindow consults it on close.</summary>
    public bool MinimizeToTray { get; set; } = UiSettings.Load().MinimizeToTray;


    public VolumeRangeViewModel OutputVolumeRange { get; }

    /// <summary>
    /// Raised when a monitor range is changed in the window and the desktop
    /// owns the range (Plasma). Nobody listens on other desktops, where each
    /// control keeps its own range.
    /// </summary>
    internal event Action<bool>? DesktopVolumeBoostRequested;
    private bool? _desktopVolumeBoost;
    private string? _volumeRangeError;
    public string? VolumeRangeError { get => _volumeRangeError; internal set { if (Set(ref _volumeRangeError, value)) Raise(nameof(HasVolumeRangeError)); } }
    public bool HasVolumeRangeError => !string.IsNullOrEmpty(VolumeRangeError);

    /// <summary>The desktop's range, applied to MONITOR and every monitor mix.</summary>
    internal void ApplyDesktopVolumeBoost(bool boost)
    {
        _desktopVolumeBoost = boost;
        OutputVolumeRange.Apply(boost);
        foreach (var mix in Mixes.Where(m => m.IsMonitor)) mix.VolumeRange.Apply(boost);
    }

    private void RequestDesktopVolumeBoost(bool boost)
    {
        if (DesktopVolumeBoostRequested is not { } request) return;
        ApplyDesktopVolumeBoost(boost);
        request(boost);
    }

    private MixViewModel CreateMix(JsonNode node)
    {
        var mix = new MixViewModel(_client, node["id"]!.GetValue<string>(), node["name"]!.GetValue<string>())
            { Kind = node["kind"]?.GetValue<string>() ?? "monitor" };
        if (mix.IsMonitor && _desktopVolumeBoost is bool boost) mix.VolumeRange.Apply(boost);
        mix.VolumeRange.Changed += boost => { if (!mix.Detached && mix.IsMonitor) RequestDesktopVolumeBoost(boost); };
        return mix;
    }

    private double _outputVolume;
    public double OutputVolume
    {
        get => _outputVolume;
        set
        {
            OutputVolumeRange.Include(value);
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

    private bool _monitorMuted;
    /// <summary>Monitoring is paused; the optional microphone pause leaves saved sends and mutes unchanged.</summary>
    public bool MonitorMuted
    {
        get => _monitorMuted;
        set { if (Set(ref _monitorMuted, value) && !_applying) _ = _client.SetMonitorMutedAsync(value); }
    }

    private bool _monitorMuteMicrophones;
    private bool _reportedMonitorMuteMicrophones;
    private bool? _pendingMonitorMuteMicrophones;
    private long _monitorMicrophoneEditVersion;
    /// <summary>Also pause microphone sends while monitoring is paused; the daemon saves this opt-in.</summary>
    public bool MonitorMuteMicrophones
    {
        get => _monitorMuteMicrophones;
        set
        {
            if (!Set(ref _monitorMuteMicrophones, value) || _applying) return;
            MonitorMicrophoneError = null;
            _pendingMonitorMuteMicrophones = value;
            _ = SubmitMonitorMicrophoneEditAsync(value, ++_monitorMicrophoneEditVersion);
        }
    }

    private async Task SubmitMonitorMicrophoneEditAsync(bool value, long version)
    {
        string? error = await _client.SetMonitorMuteMicrophonesAsync(value);
        if (version != _monitorMicrophoneEditVersion) return;
        MonitorMicrophoneError = error;
        if (error is not null)
        {
            Set(ref _monitorMuteMicrophones, _reportedMonitorMuteMicrophones, nameof(MonitorMuteMicrophones));
            Status = error;
        }
        _pendingMonitorMuteMicrophones = null;
    }

    private void CancelMonitorMicrophoneEdit()
    {
        _monitorMicrophoneEditVersion++;
        _pendingMonitorMuteMicrophones = null;
        MonitorMicrophoneError = null;
        Set(ref _monitorMuteMicrophones, _reportedMonitorMuteMicrophones, nameof(MonitorMuteMicrophones));
    }

    private string? _monitorMicrophoneError;
    public string? MonitorMicrophoneError
    {
        get => _monitorMicrophoneError;
        private set { if (Set(ref _monitorMicrophoneError, value)) Raise(nameof(MonitorMicrophoneNote)); }
    }

    private bool _microphonePausePending;
    /// <summary>The daemon has not confirmed the microphone sends' requested audio state.</summary>
    public bool MicrophonePausePending
    {
        get => _microphonePausePending;
        private set { if (Set(ref _microphonePausePending, value)) Raise(nameof(MonitorMicrophoneNote)); }
    }
    public string? MonitorMicrophoneNote => MonitorMicrophoneError
        ?? (MicrophonePausePending ? Localizer.Text("MicrophonePausePendingNote") : null);

    private bool _hasMixer;
    public bool HasMixer { get => _hasMixer; private set { if (Set(ref _hasMixer, value)) { Raise(nameof(MixerPlaceholder)); Raise(nameof(CanEditLayout)); Raise(nameof(ShowApplications)); } } }

    /// <summary>The layout editor needs a live daemon with a built graph.</summary>
    public bool CanEditLayout => DaemonConnected && HasMixer;

    private bool _renamedSinceStart;
    /// <summary>
    /// A virtual microphone was renamed while the daemon runs. Other apps
    /// keep listing the old device name until the daemon restarts.
    /// </summary>
    public bool RenamedSinceStart { get => _renamedSinceStart; private set => Set(ref _renamedSinceStart, value); }

    private string _layoutWarning = "";
    /// <summary>pipewire-pulse near its open-file limit, or empty; shown in the layout editor.</summary>
    public string LayoutWarning { get => _layoutWarning; private set => Set(ref _layoutWarning, value); }

    // --- layout editing: the daemon answers after the new layout is saved ---

    public Task<string?> CreateCaptureChannel(string name, string source, int pair, int? monoChannel = null)
        => Edit(_client.CreateCaptureChannelAsync(name, source, pair, monoChannel));
    public Task<string?> CreateChannel(string name, string? icon = null) => Edit(_client.CreateChannelAsync(name, icon));
    public Task<string?> RenameChannel(string id, string name) => Edit(_client.RenameChannelAsync(id, name));
    public Task<string?> DeleteChannel(string id) => Edit(_client.DeleteChannelAsync(id));
    public Task<string?> CreateMix(string name, string kind = "virtualMic", string? icon = null) => Edit(_client.CreateMixAsync(name, kind, icon));
    public Task<string?> RenameMix(string id, string name) => Edit(_client.RenameMixAsync(id, name));
    public Task<string?> DeleteMix(string id) => Edit(_client.DeleteMixAsync(id));

    /// <summary>The exclusive groups in the last state, for the groups editor.</summary>
    public IReadOnlyList<ExclusiveGroupItem> ExclusiveGroups { get; private set; } = [];
    public Task<string?> SetExclusiveGroup(string? id, string name, IReadOnlyList<string> channels)
        => Edit(_client.SetExclusiveGroupAsync(id, name, channels));
    public Task<string?> DeleteExclusiveGroup(string id) => Edit(_client.DeleteExclusiveGroupAsync(id));

    /// <summary>Move one editable channel or mix one step; the whole order is sent.</summary>
    public Task<string?> MoveChannel(string id, int delta) => Reorder(id, delta, isMix: false);
    public Task<string?> MoveMix(string id, int delta) => Reorder(id, delta, isMix: true);

    /// <summary>
    /// Put one editable channel or mix before or after another, as a drag in
    /// the window does, and send the whole order with <c>setLayoutOrder</c>.
    /// Hidden channels keep their place in the list.
    /// </summary>
    public Task<string?> PlaceInLayoutOrder(string source, string target, bool after, bool isMix)
    {
        List<string> channels = [.. Channels.Where(c => c.IsEditable).Select(c => c.Id)];
        List<string> mixes = [.. Mixes.Where(m => m.IsEditable).Select(m => m.Id)];
        List<string> list = isMix ? mixes : channels;
        if (source == target || !list.Contains(source) || !list.Contains(target)) return Task.FromResult<string?>(null);
        List<string> before = [.. list];
        list.Remove(source);
        list.Insert(list.IndexOf(target) + (after ? 1 : 0), source);
        if (list.SequenceEqual(before)) return Task.FromResult<string?>(null);
        return Edit(_client.SetLayoutOrderAsync(channels, mixes));
    }

    private Task<string?> Reorder(string id, int delta, bool isMix)
    {
        List<string> channels = [.. Channels.Where(c => c.IsEditable).Select(c => c.Id)];
        List<string> mixes = [.. Mixes.Where(m => m.IsEditable).Select(m => m.Id)];
        List<string> list = isMix ? mixes : channels;
        int from = list.IndexOf(id), to = from + delta;
        if (from < 0 || to < 0 || to >= list.Count) return Task.FromResult<string?>(null);
        (list[from], list[to]) = (list[to], list[from]);
        return Edit(_client.SetLayoutOrderAsync(channels, mixes));
    }

    private async Task<string?> Edit(Task<string?> result)
    {
        string? error = await result;
        if (error is not null) Status = error;
        return error;
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
        DaemonVersion is null
            ? Localizer.Format("DaemonOlderVersionNote", AppVersion.Current)
            : Localizer.Format("DaemonVersionNote", DaemonVersion, AppVersion.Current);

    private string _daemonWarning = "";
    /// <summary>A condition the daemon wants the user to see (settings not being saved), or empty.</summary>
    public string DaemonWarning { get => _daemonWarning; private set => Set(ref _daemonWarning, value); }

    /// <summary>Apply a state push from the daemon without echoing it back.</summary>
    private void Apply(JsonNode node)
    {
        _applying = true;
        DaemonWarning = node["warning"]?.GetValue<string>() ?? "";
        MixerWaiting = node["mixerWaiting"]?.GetValue<bool>() ?? false;
        try
        {
            DaemonConnected = true;
            DaemonVersion = node["daemonVersion"]?.GetValue<string>();
            DeviceConnected = node["connected"]?.GetValue<bool>() ?? false;
            if (node["device"] is JsonNode dev)
            {
                DeviceName = $"{dev["vendor"]?.GetValue<string>()} {dev["model"]?.GetValue<string>()}".Trim();
                DeviceNote = dev["note"]?.GetValue<string>() ?? "";
                DeviceFirmware = dev["firmware"]?.GetValue<string>() ?? "";
            }
            else { DeviceNote = ""; DeviceFirmware = ""; }

            if (node["capabilities"] is JsonNode caps)
            {
                bool Cap(string k) => caps[k]?.GetValue<bool>() ?? false;
                HasXlr2 = (caps["xlrInputs"]?.GetValue<int>() ?? 1) > 1;
                HasHp2 = (caps["hpOutputs"]?.GetValue<int>() ?? 1) > 1;
                // Before the state below, so a slider the new range shortens
                // is coerced while the state is being applied and sends nothing.
                GainMaxDb = caps["gainMaxDb"]?.GetValue<int>() ?? 80;
                CapLowCut = Cap("lowCut");
                CapExpander = Cap("expander");
                CapVoiceTune = Cap("voiceTune");
                CapPhantom = Cap("phantom");
                CapClipGuard = Cap("clipGuard");
                CapCompressor = Cap("compressor");
                CapCrossfade = Cap("crossfade");
                CapLowImpedance = Cap("lowImpedance");
                CapAuxInput = Cap("auxInput");
                UnitXlrJacks = caps["xlrJacks"]?.GetValue<bool>() ?? true;
                CapOutputRouting = Cap("outputRouting");
                CapPhysicalControls = Cap("physicalControls");
                CapRetainsSettings = caps["retainsSettings"]?.GetValue<bool>() ?? true;
                CapBuiltInDefaults = caps["builtInDefaults"]?.GetValue<bool>() ?? false;
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

            // Read outside the state block, so a unit that left takes its path with it.
            HpMixMicDirect = node["state"]?["hpMixMicDirect"]?.GetValue<bool>() ?? false;

            ApplyDetected(node["detected"]);
            ApplyProfiles(node["profiles"]);
            ApplyRecallOnConnect(node["recallOnConnect"]);
            ApplyDevices(node["devices"], node["mixer"]);
            ApplyWaveInterfaces(node["waveInterfaces"]);
            ApplyMixer(node["mixer"]);
            ApplyStreams(node["mixer"]);
            ApplyBrowserTabs(node["mixer"]);
            Ducking.Apply(node["ducking"], node["mixer"]);
            // The daemon says whether XLR 1's signal has a hardware low cut and
            // ClipGuard in front of it; an older daemon leaves the window to
            // work it out from the interface's capabilities.
            JsonNode? mixerNode = node["mixer"];
            ShowSoftLowCut = HasMixer && (mixerNode?["softLowCutApplicable"]?.GetValue<bool>() ?? (DeviceConnected && !CapLowCut));
            ShowSoftClipGuard = HasMixer && (mixerNode?["softClipGuardApplicable"]?.GetValue<bool>() ?? (DeviceConnected && !CapClipGuard));
            ShowGainLock = DeviceConnected && !CapPhysicalControls;
            ShowResetDefaults = DeviceConnected && (!CapRetainsSettings || CapBuiltInDefaults);
            Raise(nameof(ResetDescription));
            Raise(nameof(ShowInterfaceCard));
            RefreshBindings();
            Raise(nameof(StatusLine));
            Status = DeviceConnected ? Localizer.Text("StatusReady") : Localizer.Text("StatusNoDevice");
        }
        finally { _applying = false; }
        StateApplied?.Invoke();
    }

    /// <summary>Attached supported interfaces; a picker shows when there are two.</summary>
    public ObservableCollection<DetectedDeviceItem> DetectedDevices { get; } = [];

    private bool _hasMultipleDevices;
    public bool HasMultipleDevices { get => _hasMultipleDevices; set { if (Set(ref _hasMultipleDevices, value)) Raise(nameof(ShowDeviceStatus)); } }

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
        ActiveDeviceName = DetectedDevices.FirstOrDefault(d => d.Active)?.Name ?? Localizer.Text("SelectDevice");
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

    /// <summary>
    /// The "no profile" entry of the recall picker; parentheses cannot occur
    /// in a profile name. An interface without settings memory gets its last
    /// settings back in that case, so the entry says so there.
    /// </summary>
    public const string NoRecall = "(none)";
    public const string LastSettings = "(last settings)";

    private string NoRecallLabel => CapRetainsSettings ? NoRecall : LastSettings;
    private static bool IsNoRecall(string value) => value is NoRecall or LastSettings;

    /// <summary>Recall picker entries: the "no profile" entry then every saved profile.</summary>
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
            _ = _client.SetRecallOnConnectAsync(IsNoRecall(value) ? null : value);
        }
    }

    private void ApplyRecallOnConnect(JsonNode? recall)
    {
        var choices = new List<string> { NoRecallLabel };
        choices.AddRange(Profiles);
        if (!choices.SequenceEqual(RecallChoices))
        {
            RecallChoices.Clear();
            foreach (string c in choices) RecallChoices.Add(c);
            _recallChoice = "";   // the combo lost its selection with the items; force the re-set below
        }
        string want = recall?.GetValue<string>() is string r && Profiles.Contains(r) ? r : NoRecallLabel;
        RecallOnConnectChoice = want;
    }

    /// <summary>Write the firmware defaults back to an interface without settings memory.</summary>
    public void ResetDevice() => _ = _client.ResetDeviceAsync();

    public void SaveProfile(string name)
    {
        SliderSync.FlushPending();
        _ = _client.SaveProfileAsync(name);
    }

    public void LoadProfile(string name)
    {
        SliderSync.FlushPending();
        SliderSync.ReleaseTouchGuards();
        _ = _client.LoadProfileAsync(name);
    }
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
        // Retain the latest report for a rejected selection, without copying
        // the state or guessing the feed of an output that was missing.
        _reportedMonitorDevices = devices;
        _reportedMonitorMixer = mixer;
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
            int? channels = d["channels"] is JsonValue c && c.TryGetValue(out int n) && n > 0 ? n : null;
            string? positions = d["positions"] is JsonArray p && p.Count > 0
                ? string.Join(',', p.Select(x => x is JsonValue v && v.TryGetValue(out string? s) ? s.Replace(",", "") : "")) : null;
            string? driven = d["drivenUnit"] is JsonValue u && u.TryGetValue(out string? unit) ? unit : null;
            var item = new AudioDeviceItem(name, desc, own, physical, channels, positions, driven);
            (IsSink(d["kind"]) ? sinks : sources).Add(item);
        }
        // Output commands identify sinks by exact name. A transient duplicate
        // must not place the same output row in the collection twice.
        sinks = sinks.DistinctBy(sink => sink.Name, StringComparer.Ordinal).ToList();
        Replace(Outputs, sinks);
        Replace(Inputs, sources);

        _monitorSelection = (mixer?["monitorOutputs"] as JsonArray)?.Select(n => n?.GetValue<string>())
            .Where(n => n is not null).Select(n => n!).Distinct().ToList() ?? [];
        List<string> selection = _pendingMonitorSelection ?? _monitorSelection;
        var current = new HashSet<string>(selection);
        foreach (AudioDeviceItem s in sinks) _outputLabels[s.Name] = s.Label;

        // A selected output whose device is unplugged, asleep or not yet
        // probed stays in the list, ticked and named as missing. The daemon
        // keeps it selected and routes to it again when it returns; a list
        // without it would send a selection that drops it at the next tick.
        var wanted = sinks.Where(s => !s.IsOwn).Select(s => (s.Name, s.Label, Present: true)).ToList();
        foreach (string name in selection.Where(n => !sinks.Any(s => s.Name == n)))
            wanted.Add((name, Localizer.Format("MonitorOutputMissing", _outputLabels.GetValueOrDefault(name) ?? name), false));

        // Rebuild the multi-select list only when membership changed, so open
        // dropdowns and checkbox states survive routine pushes. A row is the
        // output's by name alone: unplugged or back, it is the same row, so a
        // feed edit still waiting keeps its answer, error included.
        if (!wanted.Select(w => w.Name).SequenceEqual(MonitorOutputs.Select(m => m.Name)))
        {
            var items = wanted.Select(w => MonitorOutputs.FirstOrDefault(output => output.Name == w.Name)
                ?? new MonitorOutputItem(w.Name, w.Label, OnMonitorSelectionChanged, OnMonitorFeedChanged,
                    w.Present, error => Status = error)).ToList();
            // Adding or losing another device must not cancel this output's
            // pending feed edit or replace its open picker rows.
            foreach (MonitorOutputItem output in MonitorOutputs.Where(output => !items.Contains(output))) output.CancelFeedEdit();
            for (int i = 0; i < items.Count; i++)
            {
                int existing = MonitorOutputs.IndexOf(items[i]);
                if (existing < 0) MonitorOutputs.Insert(i, items[i]);
                else if (existing != i) MonitorOutputs.Move(existing, i);
            }
            while (MonitorOutputs.Count > items.Count) MonitorOutputs.RemoveAt(MonitorOutputs.Count - 1);
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            MonitorOutputs[i].SyncLabel(wanted[i].Label);
            MonitorOutputs[i].SyncPresent(wanted[i].Present);
        }

        EnforcedDefaultSink = mixer?["enforcedDefaultSink"]?.GetValue<string>();
        EnforcedDefaultSource = mixer?["enforcedDefaultSource"]?.GetValue<string>();

        if (!SliderSync.RecentlyTouched("outvol"))
            OutputVolume = mixer?["outputVolume"]?.GetValue<double>() ?? 0;

        foreach (MonitorOutputItem item in MonitorOutputs) item.Sync(current.Contains(item.Name));

        var allMixes = (mixer?["mixes"] as JsonArray)?.Where(m => m is not null).ToList() ?? [];
        var mixOptions = allMixes.Select(m => new MixOption(m!["id"]!.GetValue<string>(),
            m["name"]?.GetValue<string>() ?? m["id"]!.GetValue<string>())).ToList();
        var monitors = allMixes.Where(m => m!["kind"]?.GetValue<string>() == "monitor"
            || m["kind"] is null && m["id"]?.GetValue<string>() is "monitor" or "monitor2")
            .Select(m => m!["id"]!.GetValue<string>()).ToList();
        string? primaryMonitor = mixer?["defaultMonitorMix"]?.GetValue<string>();
        if (!monitors.Contains(primaryMonitor ?? ""))
            primaryMonitor = monitors.FirstOrDefault(); // Older daemons use layout order for the implicit feed.
        var feeds = mixer?["monitorFeeds"] as JsonObject;
        DefaultMonitorMix = primaryMonitor ?? "monitor";
        foreach (MonitorOutputItem item in MonitorOutputs)
        {
            var included = (feeds?[item.Name]?.GetValue<string>()?.Split('+',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? []).ToHashSet(StringComparer.Ordinal);
            string feed = string.Join('+', mixOptions.Where(m => included.Contains(m.Id)).Select(m => m.Id));
            item.SyncFeed(mixOptions, feed.Length > 0 ? feed : primaryMonitor);
        }
        Raise(nameof(MonitorSummary));
    }

    /// <summary>"Monitor A" and "Monitor B" read "Monitor A+B"; names without a shared first word are joined whole.</summary>
    internal static string SummedName(IEnumerable<string> names)
    {
        List<string> list = [.. names];
        string[] firstWords = [.. list.Select(n => n.Split(' ', 2)[0])];
        if (list.Count > 1 && firstWords.Distinct().Count() == 1 && list.All(n => n.Contains(' ')))
            return firstWords[0] + " " + string.Join("+", list.Select(n => n.Split(' ', 2)[1]));
        return string.Join("+", list);
    }

    /// <summary>The daemon's implicit monitor feed; old daemons use their first monitor.</summary>
    public string DefaultMonitorMix { get; private set; } = "monitor";

    private Task<string?> OnMonitorFeedChanged(string output, string mixId)
        => _applying ? Task.FromResult<string?>(null) : _client.SetMonitorFeedAsync(output, mixId);

    /// <summary>AudioNodeKind arrives as the enum's number (0 = Sink) or name.</summary>
    private static bool IsSink(JsonNode? kind) => kind switch
    {
        null => false,
        _ => kind.ToJsonString().Trim('"') is "0" or "Sink",
    };

    private static void Replace(ObservableCollection<AudioDeviceItem> target, List<AudioDeviceItem> fresh)
    {
        // Compared by value: a source whose ports or owning unit became known
        // later is replaced, so a picker never offers what the daemon refuses.
        if (target.SequenceEqual(fresh))
            return;   // unchanged, leave the collection (and any open dropdown) alone
        target.Clear();
        foreach (AudioDeviceItem d in fresh) target.Add(d);
    }

    /// <summary>Route each peak to its channel strip or mix.</summary>
    private void ApplyMeters(JsonNode levels)
    {
        if (levels is not JsonObject obj) return;
        Ducking.ApplyMeters(obj);
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
        if (mixer?["streams"] is not JsonArray arr)
        {
            Apps.Clear();
            ActiveApps.Clear();
            Raise(nameof(HasApps));
            return;
        }
        // Apps route to application channels only; "not managed" leaves them to the desktop.
        List<ChannelChoice> choices = [.. Channels.Where(c => c.IsApplication).Select(c => new ChannelChoice(c.Id, c.Name)),
            new ChannelChoice(AppStreamViewModel.Ignore, Localizer.Text("NotManaged"))];
        // Update in place so an open dropdown is not closed by a state push.
        var byIdentity = Apps.ToDictionary(a => a.Identity, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? stream in arr)
        {
            if (stream is null) continue;
            string identity = stream["identity"]?.GetValue<string>() ?? "?";
            string label = stream["label"]?.GetValue<string>() ?? "?";
            string channel = stream["channelId"]?.GetValue<string>() ?? "";
            bool active = stream["active"]?.GetValue<bool>() ?? true;
            bool running = stream["running"]?.GetValue<bool>() ?? true;
            string? icon = stream["icon"] is JsonValue iconValue && iconValue.TryGetValue(out string? named) ? named : null;
            bool browserHeld = stream["browserHeld"] is JsonValue heldValue && heldValue.TryGetValue(out bool held) && held;
            string? output = stream["outputChannelId"] is JsonValue outputValue && outputValue.TryGetValue(out string? outputId)
                ? choices.FirstOrDefault(c => c.Id == outputId && c.Id != AppStreamViewModel.Ignore)?.Name : null;
            seen.Add(identity);
            if (!byIdentity.TryGetValue(identity, out AppStreamViewModel? existing))
            {
                existing = new AppStreamViewModel(_client, identity, label, choices);
                // Initial state is a daemon update too, not a user routing edit.
                existing.ApplyFromDaemon(channel, active, running);
                existing.Icon = icon;
                byIdentity.Add(identity, existing);
                Apps.Add(existing);
            }
            else
            {
                existing.SyncChannels(choices);
                existing.ApplyFromDaemon(channel, active, running, label);
                existing.Icon = icon;
            }
            existing.ApplyBrowserHold(browserHeld, output);
        }
        for (int i = Apps.Count - 1; i >= 0; i--)
            if (!seen.Contains(Apps[i].Identity)) Apps.RemoveAt(i);

        // Maintain the running-apps view without disturbing open dropdowns.
        var listed = new HashSet<AppStreamViewModel>(ActiveApps);
        var wanted = new HashSet<AppStreamViewModel>();
        foreach (AppStreamViewModel a in Apps)
        {
            if (!a.Active && !a.Running) continue;
            wanted.Add(a);
            if (listed.Add(a)) ActiveApps.Add(a);
        }
        for (int i = ActiveApps.Count - 1; i >= 0; i--)
            if (!wanted.Contains(ActiveApps[i])) ActiveApps.RemoveAt(i);
        Raise(nameof(HasApps));
    }

    /// <summary>Plugin insert chains of the two XLR channels.</summary>
    public InsertsViewModel Inserts { get; }
    public InsertsViewModel Inserts2 { get; }

    private void ApplyMixer(JsonNode? mixer)
    {
        MonitorMuted = mixer?["monitorMuted"]?.GetValue<bool>() ?? false;
        _reportedMonitorMuteMicrophones = mixer?["monitorMuteMicrophones"]?.GetValue<bool>() ?? false;
        if (mixer is null) CancelMonitorMicrophoneEdit();
        Set(ref _monitorMuteMicrophones, _pendingMonitorMuteMicrophones ?? _reportedMonitorMuteMicrophones, nameof(MonitorMuteMicrophones));
        MicrophonePausePending = mixer?["microphonePausePending"]?.GetValue<bool>() ?? false;
        if (mixer is null)
        {
            Inserts.SoundCheck.Apply(null); Inserts2.SoundCheck.Apply(null);
            foreach (ChannelViewModel channel in Channels) channel.Inserts?.SoundCheck.Apply(null);
            HasMixer = false; RenamedSinceStart = false; LayoutWarning = ""; ExclusiveGroups = []; return;
        }
        HasMixer = true;
        ApplyOutputAppearance(mixer["outputAppearance"]);
        ExclusiveGroups = [.. (mixer["exclusiveGroups"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(g => g["id"] is JsonValue && g["name"] is JsonValue && g["channels"] is JsonArray)
            .Select(g => new ExclusiveGroupItem(g["id"]!.GetValue<string>(), g["name"]!.GetValue<string>(),
                [.. g["channels"]!.AsArray().OfType<JsonValue>().Select(ch => ch.GetValue<string>())]))];
        RenamedSinceStart = mixer["renamedSinceStart"]?.GetValue<bool>() ?? false;
        LayoutWarning = mixer["layoutWarning"]?.GetValue<string>() ?? "";
        SoftLowCutHz = mixer["lowCutHz"]?.GetValue<int>() ?? 0;
        SoftClipGuardAvailable = mixer["softClipGuardAvailable"]?.GetValue<bool>() ?? false;
        SoftClipGuardError = mixer["softClipGuardError"]?.GetValue<string>();
        SoftClipGuard = mixer["softClipGuard"]?.GetValue<bool>() ?? false;
        CompensateMixLatency = mixer["compensateMixLatency"]?.GetValue<bool>() ?? false;
        MixLatencyError = mixer["mixLatencyError"]?.GetValue<string>();
        Inserts.Apply(mixer["inserts"]?["xlr1"]);
        Inserts2.Apply(mixer["inserts"]?["xlr2"]);
        Inserts.SoundCheck.Apply(mixer["soundCheck"]);
        Inserts2.SoundCheck.Apply(mixer["soundCheck"]);
        bool auxAudible = mixer["monitorFeeds"] is JsonObject monitorFeeds && monitorFeeds.Any(
            feed => (feed.Value?.GetValue<string>() ?? "").Split('+').Contains("auxout"));

        if (mixer["mixes"] is JsonArray mixes)
        {
            SyncList(Mixes, mixes, m => m["id"]!.GetValue<string>(),
                (m, vm) => vm.ApplyFromDaemon(m, MonitorMuted),
                CreateMix, removed =>
                {
                    removed.Detach();
                    removed.Inserts.Detach();
                    InsertWindows.CloseChain(removed.Inserts, $"mix:{removed.Id}");
                });
            bool auxOn = mixer["auxPortEnabled"]?.GetValue<bool>() ?? true;
            foreach (MixViewModel mv in Mixes.Where(mv => mv.IsAuxPort)) mv.ApplyAuxPort(auxOn);
            // Aux can feed a selected output even without a USB Aux port.
            // Hide its unused controls on other hardware; send rows follow
            // the same rule below, once the channels have synced.
            foreach (MixViewModel mv in Mixes.Where(mv => mv.IsAuxPort))
            {
                mv.AuxPortAvailable = !DeviceConnected || CapOutputRouting;
                mv.Visible = mv.AuxPortAvailable || auxAudible;
            }

            foreach (MixViewModel mv in Mixes)
            {
                mv.Inserts.Apply(mixer["inserts"]?[$"mix:{mv.Id}"]);
                mv.Inserts.EnsurePluginsLoaded();   // shared fetch; cheap after the first
            }
        }

        if (mixer["channels"] is JsonArray channels)
        {
            string[] mixIds = [.. Mixes.Select(m => m.Id)];
            SyncList(Channels, channels, c => c["id"]!.GetValue<string>(),
                (c, vm) =>
                {
                    vm.SyncSends(mixIds);
                    vm.ApplyFromDaemon(c);
                    vm.ApplyEq(c["eq"], mixer["eqLimits"]);
                    vm.Inserts?.Apply(mixer["inserts"]?[vm.Id]);
                    vm.Inserts?.SoundCheck.Apply(mixer["soundCheck"]);
                    vm.Inserts?.EnsurePluginsLoaded();
                },
                c => new ChannelViewModel(_client, c["id"]!.GetValue<string>(), c["name"]!.GetValue<string>(), mixIds),
                removed => { removed.SyncSends([]); removed.Detach(); });
            // Send rows carry the mix's name, not its id.
            foreach (ChannelViewModel c in Channels)
                foreach (SendViewModel send in c.Sends)
                    send.MixName = Mixes.FirstOrDefault(m => m.Id == send.MixId)?.Name ?? send.MixId;
            // Hardware input tiles only make sense for jacks the active
            // device has. The daemon works that out and says so per channel,
            // so the window, the terminal mixer and the bar agree.
            foreach (ChannelViewModel c in Channels)
            {
                c.Visible = c.Present;
                foreach (SendViewModel send in c.Sends.Where(s => s.MixId == "auxout"))
                    send.Visible = !DeviceConnected || CapOutputRouting || auxAudible;
            }
            RefreshChannelPresentation();
            RefreshBindings();
            ChannelViewModel[] captures = [.. Channels.Where(c => c.CaptureSource is not null)];
            for (int i = CaptureInputs.Count - 1; i >= 0; i--)
                if (!captures.Contains(CaptureInputs[i])) CaptureInputs.RemoveAt(i);
            for (int i = 0; i < captures.Length; i++)
            {
                int at = CaptureInputs.IndexOf(captures[i]);
                if (at < 0) CaptureInputs.Insert(i, captures[i]);
                else if (at != i) CaptureInputs.Move(at, i);
            }
        }
    }

    /// <summary>Follow daemon order while retaining existing objects and their bindings.</summary>
    private static void SyncList<T>(ObservableCollection<T> target, JsonArray source,
        Func<JsonNode, string> idOf, Action<JsonNode, T> update, Func<JsonNode, T> create,
        Action<T>? removed = null)
        where T : class, IHasId
    {
        var seen = new HashSet<string>();
        int position = 0;
        foreach (JsonNode? item in source)
        {
            if (item is null) continue;
            string id = idOf(item);
            if (!seen.Add(id)) continue;
            // State pushes normally keep the order, so avoid a list search
            // for every control on every refresh.
            T? existing = position < target.Count && target[position].Id == id
                ? target[position] : target.FirstOrDefault(x => x.Id == id);
            if (existing is null)
            {
                T made = create(item);
                update(item, made);
                target.Insert(position, made);
            }
            else
            {
                update(item, existing);
                if (!ReferenceEquals(target[position], existing))
                    target.Move(target.IndexOf(existing), position);
            }
            position++;
        }
        for (int i = target.Count - 1; i >= 0; i--)
            if (!seen.Contains(target[i].Id))
            {
                removed?.Invoke(target[i]);
                target.RemoveAt(i);
            }
    }
}

public interface IHasId { string Id { get; } }

/// <summary>An application that is playing, and the channel it is routed to.</summary>
public sealed class AppStreamViewModel : ViewModelBase
{
    /// <summary>The daemon's "not managed" pseudo-channel: the app keeps the desktop's routing.</summary>
    public const string Ignore = "ignore";

    private readonly DaemonClient _client;
    private bool _applying;

    public AppStreamViewModel(DaemonClient client, string identity, string label, IReadOnlyList<ChannelChoice> channels)
    {
        _client = client; Identity = identity; _label = label;
        foreach (ChannelChoice c in channels) Channels.Add(c);
    }

    public string Identity { get; }

    private string _label;
    public string Label { get => _label; private set => Set(ref _label, value); }

    private string? _icon;
    /// <summary>The daemon's icon for the app: a freedesktop icon name or an image path, or null.</summary>
    public string? Icon { get => _icon; set => Set(ref _icon, value); }

    /// <summary>The application channels by name, plus "not managed".</summary>
    public ObservableCollection<ChannelChoice> Channels { get; } = [];

    /// <summary>Replace the choices when the layout changed; the selection is re-pointed by id.</summary>
    public void SyncChannels(IReadOnlyList<ChannelChoice> channels)
    {
        if (Channels.Select(c => (c.Id, c.Name)).SequenceEqual(channels.Select(c => (c.Id, c.Name)))) return;
        _applying = true;
        try
        {
            Channels.Clear();
            foreach (ChannelChoice c in channels) Channels.Add(c);
            Raise(nameof(SelectedChannel));
        }
        finally { _applying = false; }
    }

    /// <summary>The picker's selection, mapped onto <see cref="ChannelId"/>.</summary>
    public ChannelChoice? SelectedChannel
    {
        get => Channels.FirstOrDefault(c => c.Id == _channelId);
        set { if (value is not null) ChannelId = value.Id; }
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
            if (Set(ref _channelId, value))
            {
                Raise(nameof(SelectedChannel));
                if (!_applying && value.Length > 0) _ = _client.AssignAppAsync(Identity, value);
            }
        }
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

    private string? _browserHeldNote;
    /// <summary>
    /// Null unless browser output selection holds this browser. Then it says
    /// where the browser plays meanwhile, and that the chosen channel is the
    /// one that applies when the option is off.
    /// </summary>
    public string? BrowserHeldNote { get => _browserHeldNote; private set => Set(ref _browserHeldNote, value); }

    /// <summary>The daemon's report for a browser held by browser output selection.</summary>
    public void ApplyBrowserHold(bool held, string? outputChannel)
        => BrowserHeldNote = !held ? null
            : !Active ? Localizer.Text("BrowserOutputHeld")
            : outputChannel is not null ? Localizer.Format("BrowserOutputHeldOnChannel", outputChannel)
            : Localizer.Text("BrowserOutputHeldElsewhere");

    /// <summary>"playing" / "running" / "not running", for the manage dialog.</summary>
    public string StatusText => Active ? Localizer.Text("Playing") : Running ? Localizer.Text("Running") : Localizer.Text("NotRunning");

    public void Forget() => _ = _client.ForgetAppAsync(Identity);
}

/// <summary>An application channel as a picker entry: stable id, current name.</summary>
public sealed record ChannelChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// One selectable sink or source. Own nodes are OpenXLR's own. A source
/// carries its channel count and its channel positions, comma-joined so the
/// record compares by value; <paramref name="DrivenUnit"/> names the attached
/// supported interface the node belongs to, or is null.
/// </summary>
public sealed record AudioDeviceItem(string Name, string Description, bool IsOwn, bool IsPhysical = false,
    int? Channels = null, string? Positions = null, string? DrivenUnit = null)
{
    public string Label => IsOwn ? Localizer.Format("OwnDeviceLabel", Description) : Description;

    /// <summary>The position of a zero-based port ("MONO", "FR", "AUX2"), or null when unknown.</summary>
    public string? PositionOf(int port)
        => Positions?.Split(',') is { } names && port >= 0 && port < names.Length ? names[port] : null;
}

/// <summary>A mix (monitor/stream/chat): master level and mute.</summary>
public sealed class MixViewModel : ViewModelBase, IHasId
{
    internal bool Detached { get; private set; }
    internal void Detach()
    {
        Detached = true;
        SliderSync.Forget($"mixvol:{Id}");
    }

    public LayoutAppearanceViewModel Appearance { get; } = new();

    private bool _displayVisible = true;
    /// <summary>The submixer draws this mix's master; see MainViewModel.RefreshChannelPresentation.</summary>
    public bool DisplayVisible { get => _displayVisible; set => Set(ref _displayVisible, value); }

    private bool _revealed;
    /// <summary>Hidden in the layout editor and drawn faded because Show hidden is on.</summary>
    public bool Revealed { get => _revealed; set => Set(ref _revealed, value); }
    private readonly DaemonClient _client;
    private bool _applying;

    public MixViewModel(DaemonClient client, string id, string name)
    {
        _client = client; Id = id; _name = name;
        VolumeRange = new VolumeRangeViewModel(() => Volume = Math.Min(Volume, 1));
        Inserts = new InsertsViewModel(client, $"mix:{id}", channels: 2, title: Localizer.Format("MixInsertsTitle", name));
    }

    public string Id { get; }

    private string _name;
    /// <summary>Display name; the daemon renames virtual microphones live.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value)) Inserts.Title = Localizer.Format("MixInsertsTitle", value);
        }
    }

    private bool? _editable;
    /// <summary>The daemon owns editability; older daemons only allow virtual microphones.</summary>
    public bool IsEditable => _editable ?? Kind == "virtualMic";

    private bool? _deletable;
    public bool IsDeletable => _deletable ?? IsEditable;

    /// <summary>What a structural mix is, for the layout editor.</summary>
    public string KindLabel => Kind switch { "monitor" => Localizer.Text("MonitorMixKind"), "auxPort" => Localizer.Text("UsbAuxPortKind"), _ => "" };

    /// <summary>This mix's stereo plugin insert chain.</summary>
    public InsertsViewModel Inserts { get; }

    private bool _visible = true;
    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    private string _kind = "monitor";
    /// <summary>"monitor", "virtualMic" or "auxPort", as the daemon reports it.</summary>
    public string Kind { get => _kind; set { if (Set(ref _kind, value)) { Raise(nameof(IsEditable)); Raise(nameof(IsDeletable)); Raise(nameof(KindLabel)); Raise(nameof(IsMonitor)); } } }
    public bool IsMonitor => Kind == "monitor";
    public VolumeRangeViewModel VolumeRange { get; }

    private double _volume = 1.0;
    public double Volume
    {
        get => _volume;
        set
        {
            if (Detached) return;
            VolumeRange.Include(value);
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
    public bool Muted { get => _muted; set { if (Set(ref _muted, value) && !_applying && !Detached) _ = _client.SetMixMutedAsync(Id, value); } }

    /// <summary>Only the Aux mix carries the port toggle.</summary>
    public bool IsAuxPort => Id == "auxout";

    private bool _auxPortAvailable = true;
    public bool AuxPortAvailable
    {
        get => _auxPortAvailable;
        set { if (Set(ref _auxPortAvailable, value)) Raise(nameof(ShowAuxPortToggle)); }
    }
    public bool ShowAuxPortToggle => IsAuxPort && AuxPortAvailable;

    private bool _auxPortEnabled = true;
    public bool AuxPortEnabled
    {
        get => _auxPortEnabled;
        set { if (Set(ref _auxPortEnabled, value) && !_applying && !Detached) _ = _client.SetAuxPortEnabledAsync(value); }
    }

    private double _meterL;
    public double MeterL { get => _meterL; set => Set(ref _meterL, Math.Min(value, 1.0)); }
    private double _meterR;
    public double MeterR { get => _meterR; set => Set(ref _meterR, Math.Min(value, 1.0)); }

    public void ApplyFromDaemon(JsonNode n, bool monitorMuted = false)
    {
        Appearance.Apply(n["appearance"]);
        bool? editable = n["editable"]?.GetValue<bool>();
        if (_editable != editable) { _editable = editable; Raise(nameof(IsEditable)); Raise(nameof(IsDeletable)); }
        bool? deletable = n["deletable"]?.GetValue<bool>();
        if (_deletable != deletable) { _deletable = deletable; Raise(nameof(IsDeletable)); }
        _applying = true;
        try
        {
            if (!SliderSync.RecentlyTouched($"mixvol:{Id}"))
                Volume = n["volume"]?.GetValue<double>() ?? 1.0;
            Kind = n["kind"]?.GetValue<string>() ?? Kind;
            Muted = (n["muted"]?.GetValue<bool>() ?? false) || IsMonitor && monitorMuted;
            if (n["name"]?.GetValue<string>() is { Length: > 0 } name) Name = name;
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
    public LayoutAppearanceViewModel Appearance { get; } = new();

    public ChannelViewModel(DaemonClient client, string id, string name, IReadOnlyList<string> mixIds)
    {
        _client = client; Id = id; _name = name;
        foreach (string mixId in mixIds) Sends.Add(new SendViewModel(client, id, mixId));
    }

    private readonly DaemonClient _client;
    public string Id { get; }

    private string _name;
    /// <summary>Display name; the daemon renames application channels live.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            Raise(nameof(BindingChipName));
            if (_eq is not null) _eq.ChannelName = value;
            if (_inserts is not null) _inserts.Title = value;
        }
    }

    private MicEqViewModel? _eq;
    /// <summary>
    /// The microphone EQ, on a channel the daemon reports one for (XLR 1,
    /// XLR 2 and capture inputs on one mono port); null on every other
    /// channel and from a daemon without the EQ,
    /// so no client draws an EQ button there.
    /// </summary>
    public MicEqViewModel? Eq { get => _eq; private set { if (Set(ref _eq, value)) Raise(nameof(HasEq)); } }

    public bool HasEq => _eq is not null;

    private InsertsViewModel? _inserts;
    /// <summary>A capture input's chain, only while the daemon reports its width.</summary>
    public InsertsViewModel? Inserts
    {
        get => _inserts;
        private set { if (Set(ref _inserts, value)) Raise(nameof(HasInserts)); }
    }
    public bool HasInserts => _inserts is not null;
    private int? _chainWidth;

    private bool _microphone;
    public bool Microphone { get => _microphone; private set { if (Set(ref _microphone, value)) Raise(nameof(MonoMeter)); } }

    /// <summary>
    /// A capture microphone's meter is one bar. XLR 1 and XLR 2 keep the pair
    /// they have always drawn; the spare bar keeps its room, so rows and tiles
    /// still line up with their stereo neighbours.
    /// </summary>
    public bool MonoMeter => _microphone && _captureSource is not null;

    private void ApplyChainWidth(int? width)
    {
        if (_chainWidth == width) return;
        if (_inserts is { } gone)
        {
            Inserts = null;
            gone.Detach();
            InsertWindows.CloseChain(gone, Id);
        }
        _chainWidth = width;
        if (width is 1 or 2) Inserts = new InsertsViewModel(_client, Id, width.Value, Name);
    }

    /// <summary>Take the channel's <c>eq</c> and the mixer's <c>eqLimits</c> from a state push.</summary>
    internal void ApplyEq(JsonNode? eq, JsonNode? limits)
    {
        if (eq is not JsonObject)
        {
            if (_eq is { } gone)
            {
                Eq = null;
                EqWindows.Close(gone);
                gone.Dispose();
            }
            return;
        }
        if (_eq is null) Eq = new MicEqViewModel(_client, Id, Name);
        _eq!.Apply(eq, limits);
    }

    private bool _isHardware;
    /// <summary>A hardware input (XLR 1, XLR 2, Aux In): structural, not editable.</summary>
    public bool IsHardware
    {
        get => _isHardware;
        set
        {
            if (!Set(ref _isHardware, value)) return;
            Raise(nameof(IsEditable)); Raise(nameof(IsApplication)); Raise(nameof(IsBindable));
            Raise(nameof(IsSourceBound)); Raise(nameof(IsUnitBound));
        }
    }
    public bool IsEditable => !IsHardware;
    private string? _captureSource;
    public string? CaptureSource { get => _captureSource; set { if (Set(ref _captureSource, value)) { Raise(nameof(IsApplication)); Raise(nameof(MonoMeter)); } } }
    public bool IsApplication => !IsHardware && CaptureSource is null;
    private bool _captureConnected;
    public bool CaptureConnected { get => _captureConnected; private set => Set(ref _captureConnected, value); }
    public int CapturePair { get; private set; }
    public int? CaptureMonoChannel { get; private set; }

    /// <summary>False when the active device has no jack behind this channel.</summary>
    public bool Present { get; private set; } = true;

    private ChannelBinding? _binding;
    /// <summary>What feeds the channel, as the daemon reports it; null from a daemon that predates input bindings.</summary>
    public ChannelBinding? Binding
    {
        get => _binding;
        private set
        {
            if (!Set(ref _binding, value)) return;
            Raise(nameof(IsSourceBound));
            Raise(nameof(IsUnitBound));
        }
    }

    private bool _bindingConnected = true;
    /// <summary>The channel's feed exists: the interface's pair is linked, or the bound source is.</summary>
    public bool BindingConnected { get => _bindingConnected; private set { if (Set(ref _bindingConnected, value)) Raise(nameof(BindingTip)); } }

    private string? _bindingWarning;
    /// <summary>Why the channel's source is silent, or null.</summary>
    public string? BindingWarning
    {
        get => _bindingWarning;
        private set { if (Set(ref _bindingWarning, value)) { Raise(nameof(HasBindingWarning)); Raise(nameof(BindingTip)); } }
    }
    public bool HasBindingWarning => !string.IsNullOrEmpty(_bindingWarning);

    private UnitInputInfo? _unitInput;
    /// <summary>Which input stage of which driven unit this channel carries, as the daemon reports it, or null.</summary>
    public UnitInputInfo? UnitInput { get => _unitInput; private set => Set(ref _unitInput, value); }

    /// <summary>XLR 1 or XLR 2: a hardware strip that can be bound to another capture source.</summary>
    public bool IsBindable => IsHardware && Id is "xlr1" or "xlr2";

    /// <summary>A hardware strip fed by another capture source rather than the interface.</summary>
    public bool IsSourceBound => IsHardware && _binding?.Kind == "source";

    /// <summary>A hardware strip on the interface's own capture pair, which is also what an older daemon means.</summary>
    public bool IsUnitBound => IsHardware && (_binding is null || _binding.Kind == "unit");

    private int _shownUnitInput;
    /// <summary>
    /// The primary unit's input (1 or 2) whose controls the window draws on
    /// this channel, or 0. Set by <see cref="MainViewModel"/> from
    /// <see cref="UnitInput"/>, with the strips on the interface standing in
    /// while no unit reports one.
    /// </summary>
    public int ShownUnitInput { get => _shownUnitInput; internal set => Set(ref _shownUnitInput, value); }

    private WaveUnitInputViewModel? _extraUnitInput;
    /// <summary>
    /// An additional unit's input whose controls the window draws on this
    /// channel, or null: the daemon names a driven additional unit in
    /// <see cref="UnitInput"/>, and the controls come from that unit's entry
    /// in <c>waveInterfaces</c>.
    /// </summary>
    public WaveUnitInputViewModel? ExtraUnitInput
    {
        get => _extraUnitInput;
        internal set { if (Set(ref _extraUnitInput, value)) Raise(nameof(CarriesExtraUnit)); }
    }

    /// <summary>The channel draws an additional unit's gain and switches.</summary>
    public bool CarriesExtraUnit => _extraUnitInput is not null;

    private string _bindingLabel = "";
    /// <summary>The chip's text: the interface's jack, or the bound source's description.</summary>
    public string BindingLabel { get => _bindingLabel; internal set { if (Set(ref _bindingLabel, value)) { Raise(nameof(BindingTip)); Raise(nameof(BindingChipName)); } } }

    /// <summary>The chip's tooltip: what feeds the strip, and why it is silent when it is.</summary>
    public string BindingTip => HasBindingWarning ? $"{_bindingLabel}\n{_bindingWarning}" : _bindingLabel;

    /// <summary>The chip's name for a screen reader: whose input, what it is, and that it opens the picker.</summary>
    public string BindingChipName => Localizer.Format("InputBindingChipName", Name, _bindingLabel);

    private string _captureLabel = "";
    public string CaptureLabel { get => _captureLabel; set => Set(ref _captureLabel, value); }

    public ObservableCollection<SendViewModel> Sends { get; } = [];

    private bool _applyingVolume;
    private bool _hasVolume;
    /// <summary>
    /// The daemon reports a channel volume for this channel. Every channel
    /// has one, hardware inputs too, except on a daemon that predates channel
    /// volumes, or one that gave them to application and capture channels
    /// only.
    /// </summary>
    public bool HasVolume { get => _hasVolume; private set => Set(ref _hasVolume, value); }

    private bool _directMonitored;
    /// <summary>
    /// The Wave XLR Pro's direct microphone path carries this channel's input
    /// to the headphone jacks: the device reports the path on and this channel
    /// carries the unit's first input. The headphones then hear the
    /// microphone through the hardware, which the channel volume does not
    /// reach; the volume still changes every mix. Set by
    /// <see cref="MainViewModel"/>.
    /// </summary>
    public bool DirectMonitored
    {
        get => _directMonitored;
        internal set { if (Set(ref _directMonitored, value)) Raise(nameof(VolumeTip)); }
    }

    /// <summary>The volume fader's tooltip, which says what the volume does not reach while the direct path is on.</summary>
    public string VolumeTip => _directMonitored ? Localizer.Text("ChannelVolumeDirectTip") : Localizer.Text("ChannelVolumeTip");

    private double _volume = 1.0;
    /// <summary>The channel's own volume, which scales every send it makes (0..1).</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (Set(ref _volume, value) && !_applyingVolume && !_detached && HasVolume)
            {
                double v = value;
                string key = $"chvol:{Id}";
                SliderSync.Touch(key);
                SliderSync.Send(key, () => _ = _client.SetChannelVolumeAsync(Id, v));
            }
            Raise(nameof(VolumeText));
        }
    }
    public string VolumeText => $"{_volume * 100:0}%";

    private bool _detached;
    /// <summary>The channel left the mixer; its volume fader sends nothing more.</summary>
    internal void Detach()
    {
        _detached = true;
        SliderSync.Forget($"chvol:{Id}");
        ApplyEq(null, null);
        ApplyChainWidth(null);
    }

    /// <summary>Keep existing send controls in the current mix order.</summary>
    public void SyncSends(IReadOnlyList<string> mixIds)
    {
        var wanted = new HashSet<string>(mixIds);
        for (int i = Sends.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Sends[i].MixId))
            {
                Sends[i].Detach();
                Sends.RemoveAt(i);
            }
        int position = 0;
        foreach (string mixId in mixIds)
        {
            if (!wanted.Remove(mixId)) continue;
            if (position >= Sends.Count || Sends[position].MixId != mixId)
            {
                SendViewModel? existing = Sends.FirstOrDefault(s => s.MixId == mixId);
                if (existing is null) Sends.Insert(position, new SendViewModel(_client, Id, mixId));
                else Sends.Move(Sends.IndexOf(existing), position);
            }
            position++;
        }
    }

    private bool _visible = true;
    /// <summary>The active device can feed this channel.</summary>
    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    private bool _displayVisible = true;
    /// <summary>The mixer draws this channel's strip; see MainViewModel.RefreshChannelPresentation.</summary>
    public bool DisplayVisible { get => _displayVisible; set => Set(ref _displayVisible, value); }

    private bool _revealed;
    /// <summary>Hidden in the layout editor and drawn faded because Show hidden is on.</summary>
    public bool Revealed { get => _revealed; set => Set(ref _revealed, value); }

    private double _meterL;
    public double MeterL { get => _meterL; set => Set(ref _meterL, Math.Min(value, 1.0)); }
    private double _meterR;
    public double MeterR { get => _meterR; set => Set(ref _meterR, Math.Min(value, 1.0)); }

    public void ApplyFromDaemon(JsonNode n)
    {
        Appearance.Apply(n["appearance"]);
        if (n["name"]?.GetValue<string>() is { Length: > 0 } name) Name = name;
        IsHardware = n["hardware"]?.GetValue<bool>() ?? false;
        Present = n["present"]?.GetValue<bool>() ?? true;
        Binding = ChannelBinding.From(n["binding"]);
        BindingConnected = n["bindingConnected"]?.GetValue<bool>() ?? true;
        BindingWarning = n["bindingWarning"] is JsonValue warning && warning.TryGetValue(out string? text) && text.Length > 0 ? text : null;
        UnitInput = UnitInputInfo.From(n["unitInput"]);
        CaptureSource = n["captureSource"]?.GetValue<string>();
        CaptureConnected = n["captureConnected"]?.GetValue<bool>() ?? false;
        CapturePair = n["capturePair"]?.GetValue<int>() ?? 0;
        CaptureMonoChannel = n["captureMonoChannel"]?.GetValue<int>();
        Microphone = n["microphone"]?.GetValue<bool>() ?? Id is "xlr1" or "xlr2";
        ApplyChainWidth(CaptureSource is not null && n["chainWidth"]?.GetValue<int>() is 1 or 2
            ? n["chainWidth"]!.GetValue<int>() : null);
        if (Inserts is not null) Inserts.CanSoundCheck = Microphone;
        int capturePair = CapturePair + 1;
        CaptureLabel = CaptureSource is null ? ""
            : n["captureMonoChannel"]?.GetValue<int>() is int port
                ? CaptureConnected ? Localizer.Format("CaptureConnectedPort", port + 1) : Localizer.Format("CaptureOfflinePort", port + 1)
                : CaptureConnected ? Localizer.Format("CaptureConnectedPair", capturePair) : Localizer.Format("CaptureOfflinePair", capturePair);
        double? volume = n["volume"]?.GetValue<double>();
        HasVolume = volume is not null;
        _applyingVolume = true;
        try
        {
            if (!SliderSync.RecentlyTouched($"chvol:{Id}")) Volume = volume ?? 1.0;
        }
        finally { _applyingVolume = false; }
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
}

/// <summary>One channel's send into one mix, the fader cell.</summary>
public sealed class SendViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private readonly string _channelId;
    private bool _applying;
    private bool _detached;

    internal void Detach()
    {
        _detached = true;
        SliderSync.Forget($"lvl:{_channelId}:{MixId}");
    }

    public SendViewModel(DaemonClient client, string channelId, string mixId)
    {
        _client = client; _channelId = channelId; MixId = mixId; _mixName = mixId;
    }

    public string MixId { get; }

    /// <summary>The channel whose send this is.</summary>
    internal string ChannelId => _channelId;

    private string _mixName;
    /// <summary>The mix's display name for the row header (the id until the mixes are known).</summary>
    public string MixName { get => _mixName; set => Set(ref _mixName, value); }

    private bool _visible = true;
    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    private bool _displayVisible = true;
    /// <summary>The strip draws this send row; see MainViewModel.RefreshChannelPresentation.</summary>
    public bool DisplayVisible { get => _displayVisible; set => Set(ref _displayVisible, value); }

    private bool _revealed;
    /// <summary>
    /// Its channel or its mix is hidden and drawn faded because Show hidden
    /// is on: how a cell that stands on its own fades.
    /// </summary>
    public bool Revealed { get => _revealed; set => Set(ref _revealed, value); }

    private bool _mixRevealed;
    /// <summary>
    /// Its mix is hidden and drawn faded while its channel is not: how a send
    /// row inside the channel's own strip fades, so a faded strip does not
    /// fade the row a second time.
    /// </summary>
    public bool MixRevealed { get => _mixRevealed; set => Set(ref _mixRevealed, value); }

    private double _level;
    public double Level
    {
        get => _level;
        set
        {
            if (Set(ref _level, value) && !_applying && !_detached)
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
        set { if (Set(ref _muted, value) && !_applying && !_detached) _ = _client.SetChannelMutedAsync(_channelId, MixId, value); }
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
    private static readonly System.Collections.Generic.Dictionary<string, long> Touched = [];   // monotonic ms
    private static readonly System.Collections.Generic.Dictionary<string, Action> Pending = [];
    private static DispatcherTimer? _timer;

    public static void Touch(string key) => Touched[key] = Environment.TickCount64;

    public static void Forget(string key)
    {
        Pending.Remove(key);
        Touched.Remove(key);
    }

    /// <summary>Send earlier edits before a snapshot or recall on the same connection.</summary>
    public static void FlushPending()
    {
        foreach (string key in System.Linq.Enumerable.ToList(Pending.Keys)) Flush(key);
    }

    public static void Flush(string key)
    {
        if (Pending.Remove(key, out Action? send)) send();
    }

    /// <summary>A recalled state must replace values touched before the recall.</summary>
    public static void ReleaseTouchGuards() => Touched.Clear();

    public static bool RecentlyTouched(string key)
        => Touched.TryGetValue(key, out long t) && Environment.TickCount64 - t < 800;

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
            FlushPending();
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
