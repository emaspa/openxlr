using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI.Layout;

/// <summary>What an input strip stands for, which decides the parts it has and the template that draws it.</summary>
public enum InputStripKind
{
    /// <summary>The first XLR input: its binding, hardware gain, switches and its plugin chain.</summary>
    Xlr1,
    /// <summary>The second XLR input, on an interface with two or bound to a source.</summary>
    Xlr2,
    /// <summary>The USB Aux input stage: its level and level lock.</summary>
    AuxIn,
    /// <summary>The Wave XLR Pro's analog Line In: its gain.</summary>
    LineIn,
    /// <summary>A channel applications play into: its name and meter.</summary>
    Application,
    /// <summary>A channel bound to another capture device: its name and meter, and the interface's controls when it carries the interface's microphone.</summary>
    Capture,
}

/// <summary>
/// INPUTS as a rack: one strip per channel, in the mixer's channel order. A
/// strip for an XLR input, the Aux In stage or the Line In stands for the hardware
/// controls the INPUTS panel has always shown for it, bound to the same
/// <see cref="MainViewModel"/> properties, so every capability, lock and
/// phantom settling rule there applies here unchanged. The interface's
/// input controls go on the channel that carries the interface's microphone
/// (<see cref="ChannelViewModel.ShownUnitInput"/>): its XLR strip, or a
/// capture channel on the unit's own node while the strip is bound to
/// another source. Every channel also has its own volume, after the
/// interface's gain on a strip that carries one; an application or capture
/// channel otherwise has only its name and its meter. Nothing here holds a
/// value of its own.
/// </summary>
public sealed class InputRackViewModel : IDisposable
{
    private readonly MainViewModel _main;
    private SkinStripOptions _options = SkinStripOptions.InputRowDefault;

    public InputRackViewModel(MainViewModel main)
    {
        _main = main;
        _main.Channels.CollectionChanged += OnChannelsChanged;
        _main.PropertyChanged += OnMainChanged;
        Sync();
    }

    public MainViewModel Main => _main;

    /// <summary>One strip per channel, in the mixer's order.</summary>
    public ObservableCollection<InputStripViewModel> Strips { get; } = [];

    /// <summary>The skin's strip options in force.</summary>
    public SkinStripOptions Options => _options;

    /// <summary>Take a skin's input-row options; strips are drawn again only when they draw differently.</summary>
    public void Configure(SkinStripOptions options)
    {
        if (options.Description() == _options.Description()) return;
        _options = options;
        foreach (InputStripViewModel strip in Strips) strip.Detach();
        Strips.Clear();
        Sync();
    }

    /// <summary>
    /// The parts a strip of this kind can have, in no particular order. A
    /// strip draws the gain and the switches only while it carries the
    /// interface's microphone; see <see cref="InputStripViewModel.Spec"/>.
    /// </summary>
    public static IReadOnlyCollection<string> PartsOf(InputStripKind kind) => kind switch
    {
        InputStripKind.Xlr1 or InputStripKind.Xlr2 => SkinStripParts.InputRow,
        InputStripKind.AuxIn => [SkinStripParts.Identity, SkinStripParts.Meter, SkinStripParts.Gain, SkinStripParts.HardwareControls],
        InputStripKind.LineIn => [SkinStripParts.Identity, SkinStripParts.Meter, SkinStripParts.Gain, SkinStripParts.HardwareControls],
        // A capture channel has its source chip and its chain, and the interface's
        // gain and switches while it carries the interface's microphone.
        InputStripKind.Capture => SkinStripParts.InputRow,
        // An application channel's gain part holds its channel volume alone.
        _ => [SkinStripParts.Identity, SkinStripParts.Meter, SkinStripParts.Gain],
    };

    /// <summary>What a channel is, as the rack draws it.</summary>
    public static InputStripKind KindOf(ChannelViewModel channel) => channel switch
    {
        { IsHardware: true, Id: "xlr1" } => InputStripKind.Xlr1,
        { IsHardware: true, Id: "xlr2" } => InputStripKind.Xlr2,
        { IsHardware: true, Id: "aux" } => InputStripKind.AuxIn,
        { IsHardware: true, Id: "line" } => InputStripKind.LineIn,
        { CaptureSource: not null } => InputStripKind.Capture,
        _ => InputStripKind.Application,
    };

    private void OnChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        // XLR 1's software low cut and ClipGuard keep its switches part while it is bound elsewhere.
        if (e.PropertyName is nameof(MainViewModel.ShowSoftLowCut) or nameof(MainViewModel.ShowSoftClipGuard))
            foreach (InputStripViewModel strip in Strips.Where(s => s.Kind == InputStripKind.Xlr1)) strip.RefreshSpec();
    }

    /// <summary>
    /// Follow the channels: keep a channel's strip while it is the same kind,
    /// make a new one when the kind changed, and keep the mixer's order.
    /// </summary>
    internal void Sync()
    {
        ChannelViewModel[] wanted = [.. _main.Channels];
        for (int i = Strips.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Strips[i].Channel) || Strips[i].Kind != KindOf(Strips[i].Channel))
            {
                Strips[i].Detach();
                Strips.RemoveAt(i);
            }
        for (int i = 0; i < wanted.Length; i++)
        {
            int at = -1;
            for (int j = 0; j < Strips.Count; j++)
                if (ReferenceEquals(Strips[j].Channel, wanted[i])) { at = j; break; }
            if (at == i) continue;
            if (at >= 0) Strips.Move(at, i);
            else Strips.Insert(i, new InputStripViewModel(this, wanted[i], KindOf(wanted[i])));
        }
    }

    public void Dispose()
    {
        _main.Channels.CollectionChanged -= OnChannelsChanged;
        _main.PropertyChanged -= OnMainChanged;
        foreach (InputStripViewModel strip in Strips) strip.Detach();
        Strips.Clear();
    }
}

/// <summary>One channel in the input rack.</summary>
public sealed class InputStripViewModel : ViewModelBase
{
    private readonly InputRackViewModel _rack;
    private bool _detached;

    internal InputStripViewModel(InputRackViewModel rack, ChannelViewModel channel, InputStripKind kind)
    {
        _rack = rack;
        Channel = channel;
        Kind = kind;
        Spec = CreateSpec();
        channel.PropertyChanged += OnChannelChanged;
        channel.Appearance.PropertyChanged += OnAppearanceChanged;
        rack.Main.PropertyChanged += OnMainChanged;
    }

    public ChannelViewModel Channel { get; }

    /// <summary>The item's appearance, where the <c>layoutAccent</c> style looks for the colour the user chose.</summary>
    public LayoutAppearanceViewModel Appearance => Channel.Appearance;

    public InputStripKind Kind { get; }

    /// <summary>The window's model: the hardware controls bind to its properties, exactly as the INPUTS panel does.</summary>
    public MainViewModel Main => _rack.Main;

    /// <summary>How the strip is drawn: the skin's parts, less the ones this kind of channel does not have.</summary>
    public StripSpec Spec { get; private set; }

    private StripSpec CreateSpec() => StripSpec.From(_rack.Options, Orientation.Vertical,
        [.. InputRackViewModel.PartsOf(Kind).Where(Available)], SkinStripParts.Gain);

    /// <summary>
    /// Whether this channel has the part now. The gain and the switches belong
    /// to the interface's input, so a strip bound to another source has
    /// neither, except XLR 1's software low cut and ClipGuard, and a capture
    /// channel carrying the interface's microphone has both. A capture input
    /// with a gain of its own keeps the switches part for its source's mute,
    /// where a carried unit's mute would be, and any capture input keeps it
    /// for its Own clock switch. The gain part
    /// also holds the channel's own volume, which every channel has once the
    /// daemon reports one, so a bound strip keeps the part for its volume.
    /// </summary>
    private bool Available(string part) => (Kind, part) switch
    {
        (InputStripKind.Xlr1, SkinStripParts.HardwareControls) => CarriesUnit || CarriesExtraUnit || Main.ShowSoftLowCut || Main.ShowSoftClipGuard,
        (InputStripKind.Xlr1 or InputStripKind.Xlr2, SkinStripParts.Gain) => CarriesUnit || CarriesExtraUnit || Channel.HasVolume,
        (InputStripKind.Xlr2, SkinStripParts.HardwareControls) => CarriesUnit || CarriesExtraUnit,
        (InputStripKind.Capture, SkinStripParts.HardwareControls) => CarriesUnit || CarriesExtraUnit || ShowsCaptureMute || ShowsOwnClock,
        (InputStripKind.Capture or InputStripKind.Application, SkinStripParts.Gain) => CarriesUnit || CarriesExtraUnit || Channel.HasVolume
            || ShowsCaptureGain,
        (InputStripKind.LineIn, SkinStripParts.Gain) => Main.CapLineInput || Channel.HasVolume,
        (InputStripKind.LineIn, SkinStripParts.HardwareControls) => Main.CapLineInput,
        (InputStripKind.Capture, SkinStripParts.Binding) => CapturePartOrdered(part),
        (InputStripKind.Capture, SkinStripParts.Effects) => Channel.HasInserts && CapturePartOrdered(part),
        _ => true,
    };

    private bool CapturePartOrdered(string part) => _rack.Options.ExplicitOrder is not { } order || order.Contains(part);

    /// <summary>The interface's input this strip carries, whose controls it draws, or null.</summary>
    public UnitInputViewModel? Unit => Channel.ShownUnitInput is 1 or 2 ? Main.UnitInputOf(Channel.ShownUnitInput) : null;

    /// <summary>The strip draws the interface's gain and switches.</summary>
    public bool CarriesUnit => Unit is not null;

    /// <summary>An additional interface's input this strip carries, whose gain and switches it draws, or null.</summary>
    public WaveUnitInputViewModel? ExtraUnit => Channel.ExtraUnitInput;

    /// <summary>The strip draws an additional interface's gain and switches.</summary>
    public bool CarriesExtraUnit => Channel.CarriesExtraUnit;

    /// <summary>The strip draws the channel's own volume, under the interface's gain, Aux In's level, Line In's gain or a capture input's gain where it has one.</summary>
    public bool ShowsVolume => Channel.HasVolume;

    /// <summary>
    /// The strip draws a capture input's own gain, its source's volume: the
    /// daemon reports one, which it does only while the channel carries no
    /// Wave unit's input, whose gain the strip draws instead.
    /// </summary>
    public bool ShowsCaptureGain => Kind == InputStripKind.Capture && Channel.ShowsCaptureGain;

    /// <summary>The strip draws a capture input's own mute, its source's, beside that gain.</summary>
    public bool ShowsCaptureMute => Kind == InputStripKind.Capture && Channel.ShowsCaptureMute;

    /// <summary>The strip draws a capture input's Own clock switch: the daemon reports one for it.</summary>
    public bool ShowsOwnClock => Kind == InputStripKind.Capture && Channel.HasOwnClock;

    internal void RefreshSpec()
    {
        Spec = CreateSpec();
        Raise(nameof(Spec));
        Raise(nameof(ShowsCaptureLabel));
    }

    /// <summary>A capture strip whose skin leaves the source chip out keeps the connection line it had before.</summary>
    public bool ShowsCaptureLabel => Kind == InputStripKind.Capture && !Spec.Parts.Contains(SkinStripParts.Binding, StringComparer.Ordinal);

    /// <summary>The input's plugin chain, when the daemon supports one.</summary>
    public InsertsViewModel? Inserts => Kind switch
    {
        InputStripKind.Xlr1 => _rack.Main.Inserts,
        InputStripKind.Xlr2 => _rack.Main.Inserts2,
        InputStripKind.Capture => Channel.Inserts,
        _ => null,
    };

    /// <summary>The chain's key, as the chain window names it.</summary>
    public string? InsertsKey => Kind switch
    {
        InputStripKind.Xlr1 => "xlr1",
        InputStripKind.Xlr2 => "xlr2",
        InputStripKind.Capture when Channel.HasInserts => Channel.Id,
        _ => null,
    };

    /// <summary>True when the strip draws this part.</summary>
    public bool Has(string part) => Spec.Parts.Contains(part, StringComparer.Ordinal);

    /// <summary>
    /// True while the view draws the channel. The second XLR input also needs
    /// an interface with two, which is the rule the INPUTS panel shows it by,
    /// or a source of its own.
    /// </summary>
    public bool Shown => Channel.DisplayVisible && (Kind != InputStripKind.Xlr2 || _rack.Main.HasXlr2 || Channel.IsSourceBound);

    /// <summary>True when the strip shows no Arrange handle: the channel keeps its place.</summary>
    public bool HandleHidden => !Channel.IsEditable;

    public bool ShowsIcon => Spec.WantsIcon && Channel.Appearance.HasIcon;

    public bool ShowsName => Spec.WantsName || !Channel.Appearance.HasIcon;

    public string LeftLevelName => Localizer.Format("ChannelLevelLeft", Channel.Name);

    public string RightLevelName => Localizer.Format("ChannelLevelRight", Channel.Name);

    private void OnChannelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChannelViewModel.DisplayVisible): Raise(nameof(Shown)); break;
            case nameof(ChannelViewModel.IsEditable): Raise(nameof(HandleHidden)); break;
            case nameof(ChannelViewModel.Name): Raise(nameof(LeftLevelName)); Raise(nameof(RightLevelName)); break;
            case nameof(ChannelViewModel.HasVolume): RefreshSpec(); Raise(nameof(ShowsVolume)); break;
            case nameof(ChannelViewModel.ShowsCaptureGain): RefreshSpec(); Raise(nameof(ShowsCaptureGain)); break;
            case nameof(ChannelViewModel.ShowsCaptureMute): RefreshSpec(); Raise(nameof(ShowsCaptureMute)); break;
            case nameof(ChannelViewModel.HasOwnClock): RefreshSpec(); Raise(nameof(ShowsOwnClock)); break;
            case nameof(ChannelViewModel.Inserts): Raise(nameof(Inserts)); Raise(nameof(InsertsKey)); RefreshSpec(); break;
            case nameof(ChannelViewModel.ShownUnitInput):
                Raise(nameof(Unit)); Raise(nameof(CarriesUnit)); Raise(nameof(ShowsCaptureGain)); Raise(nameof(ShowsCaptureMute)); RefreshSpec();
                break;
            case nameof(ChannelViewModel.ExtraUnitInput):
                Raise(nameof(ExtraUnit)); Raise(nameof(CarriesExtraUnit)); Raise(nameof(ShowsCaptureGain)); Raise(nameof(ShowsCaptureMute)); RefreshSpec();
                break;
            case nameof(ChannelViewModel.IsSourceBound): Raise(nameof(Shown)); break;
            case nameof(ChannelViewModel.IsHardware) or nameof(ChannelViewModel.CaptureSource):
                if (!_detached && InputRackViewModel.KindOf(Channel) != Kind) _rack.Sync();
                break;
        }
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HasXlr2) && Kind == InputStripKind.Xlr2) Raise(nameof(Shown));
        if (e.PropertyName is nameof(MainViewModel.CapLineInput) && Kind == InputStripKind.LineIn) RefreshSpec();
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        Raise(nameof(ShowsIcon));
        Raise(nameof(ShowsName));
    }

    internal void Detach()
    {
        if (_detached) return;
        _detached = true;
        Channel.PropertyChanged -= OnChannelChanged;
        Channel.Appearance.PropertyChanged -= OnAppearanceChanged;
        _rack.Main.PropertyChanged -= OnMainChanged;
    }
}
