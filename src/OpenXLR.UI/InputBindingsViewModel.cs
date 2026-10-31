using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>
/// What feeds a channel, from the state's <c>binding</c>: <c>unit</c>, a
/// hardware strip on the driven interface's capture pair; <c>source</c>, an
/// exact capture node; <c>apps</c>, an application channel.
/// </summary>
public sealed record ChannelBinding(string Kind, string? Source = null, int Pair = 0, int? MonoChannel = null, string? Unit = null)
{
    /// <summary>The zero-based port a source binding takes.</summary>
    public int Port => MonoChannel ?? 0;

    internal static ChannelBinding? From(JsonNode? node)
    {
        if (node is not JsonObject value || value["kind"] is not JsonValue kind || !kind.TryGetValue(out string? name)) return null;
        return new ChannelBinding(name,
            value["source"] is JsonValue source && source.TryGetValue(out string? s) ? s : null,
            value["pair"] is JsonValue pair && pair.TryGetValue(out int p) ? p : 0,
            value["monoChannel"] is JsonValue mono && mono.TryGetValue(out int m) ? m : null,
            value["unit"] is JsonValue unit && unit.TryGetValue(out string? u) ? u : null);
    }
}

/// <summary>Which input stage of which driven unit a channel carries, from the state's <c>unitInput</c>.</summary>
public sealed record UnitInputInfo(string Unit, int Input, bool Primary)
{
    internal static UnitInputInfo? From(JsonNode? node)
    {
        if (node is not JsonObject value || value["input"] is not JsonValue input || !input.TryGetValue(out int number)) return null;
        string unit = value["unit"] is JsonValue id && id.TryGetValue(out string? text) ? text : "";
        bool primary = value["primary"] is JsonValue flag && flag.TryGetValue(out bool p) && p;
        return new UnitInputInfo(unit, number, primary);
    }
}

/// <summary>Where the window draws one of the primary unit's input stages.</summary>
public enum UnitInputPlacement
{
    /// <summary>On its own hardware strip, XLR 1 or XLR 2, as it always was.</summary>
    OnStrip,
    /// <summary>On another channel, a capture channel on the unit's own node.</summary>
    OnChannel,
    /// <summary>On no channel: the strip is bound elsewhere and nothing carries the unit's microphone.</summary>
    NotInMixer,
}

/// <summary>
/// One of the primary unit's two input stages: where the window draws its
/// controls and, for a strip template that draws them on whichever channel
/// carries the input, those controls themselves. Every control is the
/// window's own property for that input, read and written through
/// <see cref="MainViewModel"/>, so the gain lock, phantom settling and
/// capability rules apply wherever it is drawn.
/// </summary>
public sealed class UnitInputViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly Dictionary<string, string> _names;

    internal UnitInputViewModel(MainViewModel main, int input)
    {
        _main = main;
        Input = input;
        _names = input == 1
            ? new(StringComparer.Ordinal)
            {
                [nameof(MainViewModel.GainDb)] = nameof(GainDb), [nameof(MainViewModel.GainText)] = nameof(GainText),
                [nameof(MainViewModel.Mute)] = nameof(Mute), [nameof(MainViewModel.MuteLabel)] = nameof(MuteLabel),
                [nameof(MainViewModel.PhantomSettling)] = nameof(PhantomSettling), [nameof(MainViewModel.LowCut)] = nameof(LowCut),
                [nameof(MainViewModel.Expander)] = nameof(Expander), [nameof(MainViewModel.VoiceTune)] = nameof(VoiceTune),
                [nameof(MainViewModel.VoiceTuneStrength)] = nameof(VoiceTuneStrength),
                [nameof(MainViewModel.VoiceTuneStrengthText)] = nameof(VoiceTuneStrengthText),
                [nameof(MainViewModel.Phantom)] = nameof(Phantom), [nameof(MainViewModel.ClipGuard)] = nameof(ClipGuard),
                [nameof(MainViewModel.Compressor)] = nameof(Compressor),
            }
            : new(StringComparer.Ordinal)
            {
                [nameof(MainViewModel.Gain2Db)] = nameof(GainDb), [nameof(MainViewModel.Gain2Text)] = nameof(GainText),
                [nameof(MainViewModel.Mute2)] = nameof(Mute), [nameof(MainViewModel.Mute2Label)] = nameof(MuteLabel),
                [nameof(MainViewModel.PhantomSettling2)] = nameof(PhantomSettling), [nameof(MainViewModel.LowCut2)] = nameof(LowCut),
                [nameof(MainViewModel.Expander2)] = nameof(Expander), [nameof(MainViewModel.VoiceTune2)] = nameof(VoiceTune),
                [nameof(MainViewModel.VoiceTuneStrength2)] = nameof(VoiceTuneStrength),
                [nameof(MainViewModel.VoiceTuneStrength2Text)] = nameof(VoiceTuneStrengthText),
                [nameof(MainViewModel.Phantom2)] = nameof(Phantom), [nameof(MainViewModel.ClipGuard2)] = nameof(ClipGuard),
                [nameof(MainViewModel.Compressor2)] = nameof(Compressor),
            };
        foreach (string shared in new[] { nameof(MainViewModel.GainMaxDb), nameof(MainViewModel.GainLocked), nameof(MainViewModel.ShowGainLock),
                     nameof(MainViewModel.CapLowCut), nameof(MainViewModel.CapExpander), nameof(MainViewModel.CapVoiceTune),
                     nameof(MainViewModel.CapPhantom), nameof(MainViewModel.CapClipGuard), nameof(MainViewModel.CapCompressor) })
            _names[shared] = shared;
        main.PropertyChanged += OnMainChanged;
    }

    /// <summary>1 or 2: the unit's first or second microphone input.</summary>
    public int Input { get; }

    // --- where the window draws it ---

    private UnitInputPlacement _placement;
    public UnitInputPlacement Placement
    {
        get => _placement;
        private set
        {
            if (!Set(ref _placement, value)) return;
            Raise(nameof(OnStrip)); Raise(nameof(Elsewhere)); Raise(nameof(CanFold)); Raise(nameof(BlockRow));
        }
    }

    /// <summary>On its own XLR strip, where the INPUTS panel draws it in the strip's place.</summary>
    public bool OnStrip => _placement == UnitInputPlacement.OnStrip;

    /// <summary>On another channel or on none: the INPUTS panel draws it under a heading of its own.</summary>
    public bool Elsewhere => _placement != UnitInputPlacement.OnStrip;

    /// <summary>
    /// The row of its XLR strip's section the INPUTS panel draws it in: with
    /// the strip, before the strip's software processing and inserts, or under
    /// them with its own heading when another channel carries it or none does.
    /// </summary>
    public int BlockRow => OnStrip ? 1 : 3;

    private ChannelViewModel? _carrier;
    /// <summary>The channel the controls are drawn on, or null when none carries the input.</summary>
    public ChannelViewModel? Carrier { get => _carrier; private set => Set(ref _carrier, value); }

    private string _heading = "";
    /// <summary>The INPUTS panel's heading while the input is not on its own strip.</summary>
    public string Heading { get => _heading; private set => Set(ref _heading, value); }

    private bool _expanded = true;
    /// <summary>
    /// The INPUTS panel shows the controls under the heading. They start
    /// folded while no channel carries the input, so they are reachable
    /// without filling the panel.
    /// </summary>
    public bool Expanded
    {
        get => _expanded;
        set { if (Set(ref _expanded, value)) Raise(nameof(ControlsShown)); }
    }

    /// <summary>Only the controls of an input no channel carries can be folded.</summary>
    public bool CanFold => _placement == UnitInputPlacement.NotInMixer;

    /// <summary>The INPUTS panel draws the controls themselves.</summary>
    public bool ControlsShown => _placement != UnitInputPlacement.NotInMixer || _expanded;

    /// <summary>The gain row's label: the strip's own, or a plain one under the heading.</summary>
    public string GainLabel => OnStrip ? GainName : Localizer.Text("UnitInputGain");

    /// <summary>The gain slider's name for a screen reader: XLR 1's or XLR 2's, or Mic 1's on a capsule.</summary>
    public string GainName => !_main.UnitXlrJacks ? Localizer.Text("Mic1Gain")
        : Input == 1 ? Localizer.Text("XLR1Gain") : Localizer.Text("XLR2Gain");

    internal void Place(UnitInputPlacement placement, ChannelViewModel? carrier, string heading)
    {
        bool folds = placement == UnitInputPlacement.NotInMixer && _placement != UnitInputPlacement.NotInMixer;
        Placement = placement;
        Carrier = carrier;
        Heading = heading;
        if (folds) Expanded = false;
        Raise(nameof(ControlsShown));
        Raise(nameof(GainLabel));
    }

    // --- the controls, the window's own properties for this input ---

    public int GainMaxDb => _main.GainMaxDb;
    public bool GainLocked => _main.GainLocked;
    public bool ShowGainLock => Input == 1 && _main.ShowGainLock;
    public int GainDb { get => Input == 1 ? _main.GainDb : _main.Gain2Db; set { if (Input == 1) _main.GainDb = value; else _main.Gain2Db = value; } }
    public string GainText => Input == 1 ? _main.GainText : _main.Gain2Text;
    public bool Mute { get => Input == 1 ? _main.Mute : _main.Mute2; set { if (Input == 1) _main.Mute = value; else _main.Mute2 = value; } }
    public string MuteLabel => Input == 1 ? _main.MuteLabel : _main.Mute2Label;
    public bool PhantomSettling => Input == 1 ? _main.PhantomSettling : _main.PhantomSettling2;
    public bool LowCut { get => Input == 1 ? _main.LowCut : _main.LowCut2; set { if (Input == 1) _main.LowCut = value; else _main.LowCut2 = value; } }
    public bool Expander { get => Input == 1 ? _main.Expander : _main.Expander2; set { if (Input == 1) _main.Expander = value; else _main.Expander2 = value; } }
    public bool VoiceTune { get => Input == 1 ? _main.VoiceTune : _main.VoiceTune2; set { if (Input == 1) _main.VoiceTune = value; else _main.VoiceTune2 = value; } }
    public int VoiceTuneStrength
    {
        get => Input == 1 ? _main.VoiceTuneStrength : _main.VoiceTuneStrength2;
        set { if (Input == 1) _main.VoiceTuneStrength = value; else _main.VoiceTuneStrength2 = value; }
    }
    public string VoiceTuneStrengthText => Input == 1 ? _main.VoiceTuneStrengthText : _main.VoiceTuneStrength2Text;
    public bool Phantom { get => Input == 1 ? _main.Phantom : _main.Phantom2; set { if (Input == 1) _main.Phantom = value; else _main.Phantom2 = value; } }
    public bool ClipGuard { get => Input == 1 ? _main.ClipGuard : _main.ClipGuard2; set { if (Input == 1) _main.ClipGuard = value; else _main.ClipGuard2 = value; } }
    public bool Compressor { get => Input == 1 ? _main.Compressor : _main.Compressor2; set { if (Input == 1) _main.Compressor = value; else _main.Compressor2 = value; } }
    public bool CapLowCut => _main.CapLowCut;
    public bool CapExpander => _main.CapExpander;
    public bool CapVoiceTune => _main.CapVoiceTune;
    public bool CapPhantom => _main.CapPhantom;
    public bool CapClipGuard => _main.CapClipGuard;
    public bool CapCompressor => _main.CapCompressor;

    // --- the switches at a glance, for a part that keeps them behind one button ---

    /// <summary>The interface has at least one of the switches the INPUTS panel shows beside the mute.</summary>
    public bool HasSwitches => CapLowCut || CapExpander || CapVoiceTune || CapPhantom || CapClipGuard || CapCompressor;

    /// <summary>One of the switches the interface has is on. Phantom power is power, not processing, so <see cref="PhantomShown"/> says it.</summary>
    public bool SwitchesOn => CapLowCut && LowCut || CapExpander && Expander || CapVoiceTune && VoiceTune
        || CapClipGuard && ClipGuard || CapCompressor && Compressor;

    /// <summary>Phantom power is on, or settling, on an interface that has it.</summary>
    public bool PhantomShown => CapPhantom && Phantom || PhantomSettling;

    private static readonly HashSet<string> SummaryNames = new(StringComparer.Ordinal)
    {
        nameof(LowCut), nameof(Expander), nameof(VoiceTune), nameof(Phantom), nameof(ClipGuard), nameof(Compressor),
        nameof(PhantomSettling), nameof(CapLowCut), nameof(CapExpander), nameof(CapVoiceTune), nameof(CapPhantom),
        nameof(CapClipGuard), nameof(CapCompressor),
    };

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && _names.TryGetValue(name, out string? mine))
        {
            Raise(mine);
            if (SummaryNames.Contains(mine)) { Raise(nameof(HasSwitches)); Raise(nameof(SwitchesOn)); Raise(nameof(PhantomShown)); }
        }
        if (e.PropertyName == nameof(MainViewModel.UnitXlrJacks)) { Raise(nameof(GainName)); Raise(nameof(GainLabel)); }
    }
}

public sealed partial class MainViewModel
{
    private UnitInputViewModel? _unitInput1, _unitInput2;

    /// <summary>The primary unit's first input stage: where it is drawn and its controls.</summary>
    public UnitInputViewModel UnitInput1 => _unitInput1 ??= new UnitInputViewModel(this, 1);

    /// <summary>The primary unit's second input stage, on a unit with two.</summary>
    public UnitInputViewModel UnitInput2 => _unitInput2 ??= new UnitInputViewModel(this, 2);

    /// <summary>The controls of one of the primary unit's inputs, 1 or 2.</summary>
    public UnitInputViewModel UnitInputOf(int input) => input == 2 ? UnitInput2 : UnitInput1;

    /// <summary>XLR 1 and XLR 2, the strips Options lists under Input bindings, present or not.</summary>
    public ObservableCollection<ChannelViewModel> BindableStrips { get; } = [];

    private ChannelViewModel? _xlr1Strip, _xlr2Strip;
    /// <summary>XLR 1's channel while the mixer has one, for the INPUTS panel's chip.</summary>
    public ChannelViewModel? Xlr1Strip { get => _xlr1Strip; private set { if (Set(ref _xlr1Strip, value)) Raise(nameof(HasXlr1Strip)); } }
    /// <summary>XLR 2's channel while the mixer has one.</summary>
    public ChannelViewModel? Xlr2Strip { get => _xlr2Strip; private set { if (Set(ref _xlr2Strip, value)) Raise(nameof(HasXlr2Strip)); } }
    public bool HasXlr1Strip => _xlr1Strip is not null;
    public bool HasXlr2Strip => _xlr2Strip is not null;

    private bool _showXlr2Section = true;
    /// <summary>
    /// XLR 2's part of the INPUTS panel: on a unit with a second input, or
    /// while XLR 2 is bound to a source, which gives a one-input unit a second
    /// microphone strip.
    /// </summary>
    public bool ShowXlr2Section { get => _showXlr2Section; private set => Set(ref _showXlr2Section, value); }

    private string _xlr1InputLabel = "", _xlr2InputLabel = "", _chooseMicrophoneText = "";
    /// <summary>The INPUTS panel's label for XLR 1's binding, by the strip's name: "XLR 1 input" or "Mic 1 input".</summary>
    public string Xlr1InputLabel { get => _xlr1InputLabel; private set => Set(ref _xlr1InputLabel, value); }
    public string Xlr2InputLabel { get => _xlr2InputLabel; private set => Set(ref _xlr2InputLabel, value); }
    /// <summary>The header link's text, by XLR 1's name.</summary>
    public string ChooseMicrophoneText { get => _chooseMicrophoneText; private set => Set(ref _chooseMicrophoneText, value); }

    private bool _showChooseMicrophone;
    /// <summary>
    /// The header offers the binding dialog: no supported interface is
    /// attached and XLR 1 still waits for one.
    /// </summary>
    public bool ShowChooseMicrophone
    {
        get => _showChooseMicrophone;
        private set { if (Set(ref _showChooseMicrophone, value)) Raise(nameof(ShowDeviceStatus)); }
    }

    /// <summary>The header's plain status line, whenever the binding link is not in its place.</summary>
    public bool ShowDeviceStatus => !HasMultipleDevices && !_showChooseMicrophone;

    private string _crossfadeNote = "";
    /// <summary>
    /// The line under the crossfade while the unit's own microphone is not on
    /// XLR 1: the blend still mixes it into the unit's headphone jack.
    /// </summary>
    public string CrossfadeNote { get => _crossfadeNote; private set { if (Set(ref _crossfadeNote, value)) Raise(nameof(HasCrossfadeNote)); } }
    public bool HasCrossfadeNote => _crossfadeNote.Length > 0;

    /// <summary>The primary unit's name for a heading, or a plain word while none is connected.</summary>
    internal string UnitName => DeviceConnected && DeviceName.Trim().Length > 0 ? DeviceName : Localizer.Text("TheInterface");

    /// <summary>
    /// Work out, after a state push, which channel carries each of the
    /// primary unit's inputs, what each binding chip says and what the header
    /// and the crossfade line show. The daemon names the carrier in
    /// <c>unitInput</c>. While no channel names one, which is the case with no
    /// unit connected and with a daemon that predates bindings, a strip still
    /// on the interface stands for its input as it always has, so the window
    /// looks as it did before bindings existed.
    /// </summary>
    internal void RefreshBindings()
    {
        // With the mixer off there are no strips, whatever the last state held.
        ChannelViewModel[] channels = HasMixer ? [.. Channels] : [];
        Xlr1Strip = channels.FirstOrDefault(c => c.IsHardware && c.Id == "xlr1");
        Xlr2Strip = channels.FirstOrDefault(c => c.IsHardware && c.Id == "xlr2");
        ChannelViewModel[] bindable = [.. channels.Where(c => c.IsBindable)];
        if (!bindable.SequenceEqual(BindableStrips))
        {
            BindableStrips.Clear();
            foreach (ChannelViewModel strip in bindable) BindableStrips.Add(strip);
        }

        ChannelViewModel? carrier1 = Carrier(channels, 1, Xlr1Strip), carrier2 = Carrier(channels, 2, Xlr2Strip);
        foreach (ChannelViewModel channel in Channels)
        {
            channel.ShownUnitInput = ReferenceEquals(channel, carrier1) ? 1 : ReferenceEquals(channel, carrier2) ? 2 : 0;
            // Each of the Pro's direct paths follows the channel that carries its input.
            channel.DirectMonitored = DeviceConnected
                && (HpMixMicDirect && ReferenceEquals(channel, carrier1) || HpMixMic2Direct && ReferenceEquals(channel, carrier2));
            // An additional unit's input, while the daemon drives that unit.
            channel.ExtraUnitInput = channel.UnitInput is { Primary: false } extra
                && WaveInterfaces.FirstOrDefault(w => w.Id == extra.Unit && !w.Active && w.Connected) is { } unit
                    ? unit.InputOf(extra.Input) : null;
            channel.BindingLabel = BindingLabelOf(channel);
        }
        // Mic setup shows the controls of the input its channel carries: the
        // gain on Level, phantom power on Position, low cut, ClipGuard, noise
        // removal and the EQ on Shape, and the unit's kind for the Position tips.
        foreach (MicSetupViewModel setup in MicSetups())
        {
            ChannelViewModel? carrier = Channels.FirstOrDefault(c => c.Id == setup.Channel);
            setup.ObserveInput(carrier);
            setup.Unit = carrier?.ShownUnitInput is 1 or 2 ? UnitInputOf(carrier.ShownUnitInput) : null;
            setup.ExtraUnit = carrier?.ExtraUnitInput;
            setup.ProDirect = CapOutputRouting && carrier?.ShownUnitInput == 1;
            setup.Eq = carrier?.Eq;
            setup.Noise = carrier?.Noise;
            setup.Software = setup.Channel == "xlr1" && carrier is not null ? this : null;
            if (setup.Unit is { } unit)
                setup.Place(UnitXlrJacks ? MicSetupUnitKind.XlrJack : MicSetupUnitKind.Capsule, DeviceName, unit.Input);
            else if (setup.ExtraUnit is { } extra)
                setup.Place(extra.Unit.XlrJacks ? MicSetupUnitKind.XlrJack : MicSetupUnitKind.Capsule, extra.Unit.Name, extra.Input);
            else setup.Place(MicSetupUnitKind.None, null, 1);
            setup.RaiseCarrier();
        }
        RefreshMicSetupInputs(channels);
        Xlr1InputLabel = Localizer.Format("StripInputLabel", Xlr1Strip?.Name ?? "XLR 1");
        Xlr2InputLabel = Localizer.Format("StripInputLabel", Xlr2Strip?.Name ?? "XLR 2");
        ChooseMicrophoneText = Localizer.Format("ChooseMicrophoneForStrip", Xlr1Strip?.Name ?? "XLR 1");
        Place(UnitInput1, Xlr1Strip, carrier1);
        Place(UnitInput2, Xlr2Strip, carrier2);
        ShowXlr2Section = HasXlr2 || Xlr2Strip is { IsSourceBound: true };

        ShowChooseMicrophone = DaemonConnected && HasMixer && !DeviceConnected && DetectedDevices.Count == 0
            && Xlr1Strip is { IsUnitBound: true };
        CrossfadeNote = !CapCrossfade || UnitInput1.OnStrip ? ""
            : UnitInput1.Carrier is { } elsewhere ? Localizer.Format("CrossfadeMicPlaysOn", elsewhere.Name)
            : Localizer.Text("CrossfadeMicNotInMixer");
        Raise(nameof(ShowDeviceStatus));
        RefreshInputStrips();
    }

    /// <summary>The channel that carries the primary unit's input, by the daemon's word or, failing it, the strip on the interface.</summary>
    private static ChannelViewModel? Carrier(ChannelViewModel[] channels, int input, ChannelViewModel? strip)
    {
        if (channels.FirstOrDefault(c => c.UnitInput is { Primary: true } u && u.Input == input) is { } reported) return reported;
        // The daemon names the other input but not this one: no channel carries it.
        if (channels.Any(c => c.UnitInput is { Primary: true })) return null;
        return strip is { IsUnitBound: true } ? strip : null;
    }

    private void Place(UnitInputViewModel unit, ChannelViewModel? strip, ChannelViewModel? carrier)
    {
        // Without a strip there is no mixer, and the panel draws the unit's
        // controls as it always has.
        if (strip is null || ReferenceEquals(carrier, strip)) unit.Place(UnitInputPlacement.OnStrip, strip, "");
        else if (carrier is not null)
            unit.Place(UnitInputPlacement.OnChannel, carrier, Localizer.Format("UnitInputPlaysOn", UnitName, unit.Input, carrier.Name));
        else unit.Place(UnitInputPlacement.NotInMixer, null, Localizer.Format("UnitInputNotInMixer", UnitName, unit.Input));
    }

    /// <summary>
    /// What a strip's chip says: the interface's jack, or a USB microphone's
    /// built-in capsule, which is no jack; or the source by its description
    /// and, on a source with more than one, its port.
    /// </summary>
    private string BindingLabelOf(ChannelViewModel channel)
    {
        if (channel.CaptureSource is { } capture)
        {
            AudioDeviceItem? captureDevice = BoundDevice(capture, channel.Binding?.Unit);
            string source = captureDevice?.Description is { Length: > 0 } label ? label : capture;
            return channel.CaptureMonoChannel is int port
                ? Localizer.Format("InputBindingSourcePort", source, port + 1)
                : Localizer.Format("CaptureSourcePair", source, channel.CapturePair + 1);
        }
        if (!channel.IsBindable) return "";
        if (channel.Binding is not { Kind: "source", Source: { } name } binding)
            return !UnitXlrJacks ? Localizer.Text("UnitCapsule")
                : channel.Id == "xlr2" ? Localizer.Text("Xlr2Jack") : Localizer.Text("Xlr1Jack");
        AudioDeviceItem? device = BoundDevice(name, binding.Unit);
        string description = device?.Description is { Length: > 0 } text ? text
            : binding.Unit is { } owner && WaveInterfaces.FirstOrDefault(w => w.Id == owner) is { Name.Length: > 0 } known ? known.Name : name;
        bool single = device?.Channels == 1 || device?.Channels is null && binding.Port == 0;
        return single ? description : Localizer.Format("InputBindingSourcePort", description, binding.Port + 1);
    }

    // A supported unit's source can be renamed by its card profile. Both
    // bound strips and capture inputs follow it in their source chips.
    private AudioDeviceItem? BoundDevice(string source, string? unit)
        => Inputs.FirstOrDefault(i => i.Name == source)
            ?? (unit is not null && Inputs.Where(i => !i.IsOwn && i.DrivenUnit == unit).ToArray() is [var renamed] ? renamed : null);

    /// <summary>
    /// Bind XLR 1 or XLR 2 to a source's port, or back to the interface with
    /// a null source. Resolves to null once the daemon has saved it, or to its
    /// refusal.
    /// </summary>
    public Task<string?> SetInputBinding(string channel, string? source, int port) => _client.SetInputBindingAsync(channel, source, port);
    public Task<string?> SetCaptureSource(string channel, string source, int pair, int? monoChannel)
        => _client.SetCaptureSourceAsync(channel, source, pair, monoChannel);

    /// <summary>Drive an attached unit as an additional interface, or stop; resolves to null or the daemon's refusal.</summary>
    public Task<string?> SetWaveInterfaceEnabled(string unit, bool enabled) => _client.SetWaveInterfaceEnabledAsync(unit, enabled);
}
