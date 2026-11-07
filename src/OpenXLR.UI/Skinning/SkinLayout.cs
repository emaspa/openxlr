using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OpenXLR.UI.Skinning;

/// <summary>Which way a stack lays its children out.</summary>
public enum SkinLayoutOrientation
{
    /// <summary>One above the other, the classic window.</summary>
    Vertical,
    /// <summary>Side by side, in one row that scrolls when the window is narrower.</summary>
    Horizontal,
}

/// <summary>The edge of a dock a child sits against, or the rest of the dock.</summary>
public enum SkinLayoutEdge
{
    Top,
    Bottom,
    Left,
    Right,
    /// <summary>What the other children leave; only the last child fills.</summary>
    Fill,
}

/// <summary>How a section starts out under a skin.</summary>
public enum SkinRegionVisibility
{
    /// <summary>Shown and open, as every section is without a skin.</summary>
    Visible,
    /// <summary>Shown with its contents folded the first time the window wears the skin.</summary>
    Collapsed,
    /// <summary>Left out of the window until the user shows it from Arrange mode.</summary>
    Hidden,
}

/// <summary>How a grid track is sized.</summary>
public enum SkinTrackUnit
{
    /// <summary>As large as what it holds.</summary>
    Auto,
    /// <summary>A share of the room the other tracks leave.</summary>
    Star,
    /// <summary>A fixed number of device-independent pixels, raised to what it holds.</summary>
    Pixel,
}

/// <summary>One row or column of a grid.</summary>
/// <param name="Value">The star weight or the pixel size; 0 for <see cref="SkinTrackUnit.Auto"/>.</param>
public readonly record struct SkinLayoutTrack(SkinTrackUnit Unit, double Value)
{
    public static SkinLayoutTrack Auto { get; } = new(SkinTrackUnit.Auto, 0);

    public override string ToString() => Unit switch
    {
        SkinTrackUnit.Star => Value.ToString(CultureInfo.InvariantCulture) + "*",
        SkinTrackUnit.Pixel => Value.ToString(CultureInfo.InvariantCulture) + "px",
        _ => "auto",
    };
}

/// <summary>
/// The regions a skin's layout may place. Each id names one part of the
/// window the application owns and builds; a skin places it and never
/// supplies it.
/// </summary>
public static class SkinLayoutRegions
{
    public const string Header = "header";
    public const string Inputs = "inputs";
    public const string Headphones = "headphones";
    public const string Monitor = "monitor";
    public const string Applications = "applications";
    public const string Submixer = "submixer";

    /// <summary>The five sections, in the classic order.</summary>
    public static IReadOnlyList<string> Sections { get; } = [Inputs, Headphones, Monitor, Applications, Submixer];

    /// <summary>Every region a layout may place: the header, then the five sections.</summary>
    public static IReadOnlyList<string> All { get; } = [Header, .. Sections];

    public static bool Exists(string id) => All.Contains(id, StringComparer.Ordinal);

    public static bool IsSection(string id) => Sections.Contains(id, StringComparer.Ordinal);
}

/// <summary>
/// The header's actions a skin may order or put in the overflow menu, in
/// the order the header shows them without a skin. Options is not one of
/// them: it is always first and always shown, the way back from any skin.
/// </summary>
public static class SkinHeaderActions
{
    public const string Profiles = "profiles";
    public const string Arrange = "arrange";
    public const string Flow = "flow";
    public const string RestartDaemon = "restart-daemon";
    public const string Version = "version";
    public const string About = "about";

    public static IReadOnlyList<string> All { get; } = [Profiles, Arrange, Flow, RestartDaemon, Version, About];

    public static bool Exists(string id) => All.Contains(id, StringComparer.Ordinal);
}

/// <summary>
/// The templates a region or a strip can take. Each one is drawn by markup
/// the application owns; a skin names it and never supplies it.
/// </summary>
public static class SkinLayoutTemplates
{
    /// <summary>INPUTS as it has always been: the gain, the switches and the inserts in one panel.</summary>
    public const string HardwarePanel = "hardware-panel";
    /// <summary>INPUTS as one strip per input channel, each with its own parts.</summary>
    public const string InputRack = "input-rack";
    /// <summary>The submixer as it has always been: one tile per channel, the mix masters under them.</summary>
    public const string ChannelTiles = "channel-tiles";
    /// <summary>The submixer by mix: banks, each showing every channel's send into the mix its picker chose.</summary>
    public const string MixBanks = "mix-banks";
    /// <summary>The submixer as a matrix: a row per channel, a column per mix, and the channel's send into the mix where they cross.</summary>
    public const string MixMatrix = "mix-matrix";
    /// <summary>An input rack's strip: one row of parts for one input channel.</summary>
    public const string InputRow = "input-row";
    /// <summary>A bank's strip: the parts of one send stacked in a column, the strips side by side.</summary>
    public const string VerticalStrip = "vertical-strip";
    /// <summary>A bank's strip: the parts of one send in a row, the rows one above the other.</summary>
    public const string SendList = "send-list";
    /// <summary>A bank's master as the mix tile the window has always drawn.</summary>
    public const string MasterCard = "master-card";
    /// <summary>A bank's master drawn like one more strip at the end of the bank.</summary>
    public const string MasterStrip = "master-strip";

    /// <summary>MONITOR as it has always been: the output picker and the volume, each with its label.</summary>
    public const string OutputPanel = "output-panel";
    /// <summary>MONITOR as one compact row: the output picker, the volume and its readout, for a footer or a side slot.</summary>
    public const string OutputToolbar = "output-toolbar";
    /// <summary>APPLICATIONS as it has always been: one chip per running application, wrapping across the section.</summary>
    public const string ApplicationChips = "application-chips";
    /// <summary>APPLICATIONS as a list, one application per line with its channel picker, for a narrow column.</summary>
    public const string ApplicationList = "application-list";
    /// <summary>
    /// APPLICATIONS as columns of the chips: a column holds a skin's number of
    /// chips top to bottom, the next application starts the next column, and
    /// columns that do not fit the section's width go on below, whole.
    /// </summary>
    public const string ApplicationColumns = "application-columns";

    /// <summary>What <c>regions.inputs.template</c> may name; the first is the default.</summary>
    public static IReadOnlyList<string> Inputs { get; } = [HardwarePanel, InputRack];

    /// <summary>What <c>regions.submixer.template</c> may name; the first is the default.</summary>
    public static IReadOnlyList<string> Submixer { get; } = [ChannelTiles, MixBanks, MixMatrix];

    /// <summary>What <c>regions.monitor.template</c> may name; the first is the default.</summary>
    public static IReadOnlyList<string> Monitor { get; } = [OutputPanel, OutputToolbar];

    /// <summary>What <c>regions.applications.template</c> may name; the first is the default.</summary>
    public static IReadOnlyList<string> Applications { get; } = [ApplicationChips, ApplicationList, ApplicationColumns];

    /// <summary>What an input rack's <c>strip.template</c> may name.</summary>
    public static IReadOnlyList<string> InputStrips { get; } = [InputRow];

    /// <summary>What a bank's <c>strip.template</c> may name; the first is the default.</summary>
    public static IReadOnlyList<string> BankStrips { get; } = [VerticalStrip, SendList];

    /// <summary>What a bank's <c>master.template</c> may name; the first is the default.</summary>
    public static IReadOnlyList<string> Masters { get; } = [MasterCard, MasterStrip];

    /// <summary>A bank picks its mix from a dropdown that lists every mix.</summary>
    public const string PickerDropdown = "dropdown";
    /// <summary>A bank picks its mix with a row of keys, one per mix, the shown mix's key lit.</summary>
    public const string PickerButtons = "buttons";

    /// <summary>What <c>regions.submixer.picker</c> may name; the first is the default.</summary>
    public static IReadOnlyList<string> BankPickers { get; } = [PickerDropdown, PickerButtons];
}

/// <summary>
/// The named parts of a strip. A skin orders them and may leave some out;
/// it cannot add one, and a part only ever shows the control the template
/// gives it.
/// </summary>
public static class SkinStripParts
{
    /// <summary>The channel's icon and name. Never hidden: a strip always says whose it is.</summary>
    public const string Identity = "identity";
    /// <summary>
    /// A microphone strip's input binding: a chip naming the interface's jack
    /// or the capture source that feeds XLR 1 or XLR 2, with its connection
    /// light, which opens the binding dialog.
    /// </summary>
    public const string Binding = "binding";
    /// <summary>The channel's own level meter, left and right.</summary>
    public const string Meter = "meter";
    /// <summary>The input's hardware gain, where the interface has one.</summary>
    public const string Gain = "gain";
    /// <summary>The input's hardware switches: mute, low cut, phantom power and the rest the interface has.</summary>
    public const string HardwareControls = "hardware-controls";
    /// <summary>The input's plugin chain: its button and the chain at a glance.</summary>
    public const string Effects = "effects";
    /// <summary>The send's level into the bank's mix.</summary>
    public const string Fader = "fader";
    /// <summary>That level as a number.</summary>
    public const string Value = "value";
    /// <summary>The send's mute in the bank's mix.</summary>
    public const string Mute = "mute";

    /// <summary>An input row's parts, in their default order.</summary>
    public static IReadOnlyList<string> InputRow { get; } = [Identity, Binding, Meter, Gain, HardwareControls, Effects];

    /// <summary>A send strip's parts, in their default order.</summary>
    public static IReadOnlyList<string> Send { get; } = [Identity, Meter, Fader, Value, Mute];
}

/// <summary>
/// The named parts of a mix matrix: its row heads, its column heads and its
/// cells. As with a strip, a skin orders them and leaves some out; it cannot
/// add one, and a part only ever shows the control the matrix gives it.
/// </summary>
public static class SkinMatrixParts
{
    /// <summary>A column head's mix master: its level as a short fader and that level as a number.</summary>
    public const string Master = "master";
    /// <summary>A column head's outputs line: how many monitor outputs hear the mix, or what kind of mix it is.</summary>
    public const string Outputs = "outputs";
    /// <summary>
    /// A row head's microphone switches: one button, lit while one of them is
    /// on and marked while phantom power is, that opens the INPUTS panel's
    /// low cut, expander, Voice Tune, phantom power, ClipGuard and compressor
    /// for the input the row carries, with XLR 1's software low cut and
    /// ClipGuard, in a flyout.
    /// </summary>
    public const string Processing = "processing";

    /// <summary>
    /// A row head's parts, in their default order: the channel's icon and
    /// name, a microphone strip's input binding, its own meter, the input's
    /// hardware gain or Aux In's level over the channel's own volume (the
    /// volume alone on a channel without one), the input's hardware mute, its
    /// microphone switches and the input's plugin chain. A channel without one
    /// of them, an application channel without mute, switches or chain,
    /// simply does not draw it. The binding stands under the name, in the
    /// name column, wherever the order puts it.
    /// </summary>
    public static IReadOnlyList<string> RowHead { get; } =
        [SkinStripParts.Identity, SkinStripParts.Binding, SkinStripParts.Meter, SkinStripParts.Gain, SkinStripParts.Mute, Processing,
         SkinStripParts.Effects];

    /// <summary>
    /// A column head's parts, in their default order: the mix's icon and
    /// name, its master, its mute, the button that opens its plugin chain and
    /// its outputs line.
    /// </summary>
    public static IReadOnlyList<string> ColumnHead { get; } =
        [SkinStripParts.Identity, Master, SkinStripParts.Mute, SkinStripParts.Effects, Outputs];

    /// <summary>
    /// A cell's parts, in their default order: the send's mute in the
    /// column's mix, its level as a short fader, and that level as a number.
    /// </summary>
    public static IReadOnlyList<string> Cell { get; } = [SkinStripParts.Mute, SkinStripParts.Fader, SkinStripParts.Value];

    /// <summary>The part a row or column head can never hide: it always says whose it is.</summary>
    public const string HeadKept = SkinStripParts.Identity;

    /// <summary>The part a cell can never hide: it always shows its send's level.</summary>
    public const string CellKept = SkinStripParts.Fader;
}

/// <summary>How a matrix cell shows that its send is muted.</summary>
public enum SkinMatrixMuted
{
    /// <summary>The cell's background takes <c>Ox.Matrix.MutedBackground</c> and its mute <c>Ox.Matrix.MutedForeground</c>.</summary>
    Tint,
    /// <summary>The cell fades to <c>Ox.Control.DisabledOpacity</c>.</summary>
    Dim,
}

/// <summary>Where a matrix cell draws its send's level as a number.</summary>
public enum SkinMatrixValue
{
    /// <summary>Beside the fader, in the place the cell's part order gives it.</summary>
    Beside,
    /// <summary>At the top centre of the cell while the fader is being moved, so the fader keeps the cell's width.</summary>
    OverFader,
}

/// <summary>Where a matrix draws a channel's meter beside its faders.</summary>
public enum SkinMatrixMeter
{
    /// <summary>As the track of every fader in the channel's row: the source fader and each send's.</summary>
    UnderFader,
    /// <summary>Not under the faders. The row head's meter part still shows it unless the skin hides that part.</summary>
    None,
}

/// <summary>
/// One part list of a matrix: the order of its parts, the parts left out
/// and how it names its channel or mix. <see cref="Order"/> is complete: the
/// parts the skin ordered, then the rest in their default order.
/// </summary>
public sealed record SkinMatrixPartOptions(IReadOnlyList<string> Order, IReadOnlyList<string> Hidden, SkinLabelStyle Label)
{
    /// <summary>The order as written, before omitted parts get their defaults; null when no order was given.</summary>
    public IReadOnlyList<string>? ExplicitOrder { get; init; }

    /// <summary>The parts drawn, in the order they are drawn.</summary>
    public IReadOnlyList<string> Shown => [.. Order.Where(p => !Hidden.Contains(p, StringComparer.Ordinal))];

    /// <summary>True when this part is drawn.</summary>
    public bool Shows(string part) => Order.Contains(part, StringComparer.Ordinal) && !Hidden.Contains(part, StringComparer.Ordinal);

    internal string Description() => $"[{string.Join(',', Shown)}]{Label}:{(ExplicitOrder is null ? "*" : string.Join(',', ExplicitOrder))}";
}

/// <summary>
/// A mix matrix: how its row heads, column heads and cells are drawn, how a
/// muted cell shows it, and whether the channel's meter runs under the
/// faders. Which rows and columns show is never the skin's to say: every
/// channel and every mix the view shows has one.
/// </summary>
public sealed record SkinMatrixOptions(
    SkinMatrixPartOptions RowHead,
    SkinMatrixPartOptions ColumnHead,
    SkinMatrixPartOptions Cell,
    SkinMatrixMuted Muted = SkinMatrixMuted.Tint,
    SkinMatrixMeter Meter = SkinMatrixMeter.UnderFader,
    SkinMatrixValue Value = SkinMatrixValue.Beside)
{
    /// <summary>Every part in its default order, muted cells tinted and the meter under the faders.</summary>
    public static SkinMatrixOptions Default { get; } = new(
        new SkinMatrixPartOptions(SkinMatrixParts.RowHead, [], SkinLabelStyle.Text),
        new SkinMatrixPartOptions(SkinMatrixParts.ColumnHead, [], SkinLabelStyle.Text),
        new SkinMatrixPartOptions(SkinMatrixParts.Cell, [], SkinLabelStyle.Text));

    internal string Description() =>
        $"{RowHead.Description()}/{ColumnHead.Description()}/{Cell.Description()}/{Muted}/{Meter}/{Value}";
}

/// <summary>How a strip names its channel.</summary>
public enum SkinLabelStyle
{
    /// <summary>The name alone.</summary>
    Text,
    /// <summary>The icon chosen in the layout editor, then the name.</summary>
    IconAndText,
    /// <summary>The icon alone, or the name when the channel has no icon. A screen reader still hears the whole name.</summary>
    Icon,
}

/// <summary>Where a strip's meter goes.</summary>
public enum SkinMeterPlacement
{
    /// <summary>A part of its own, where <c>partsOrder</c> puts it; by default straight under the name.</summary>
    BelowLabel,
    /// <summary>Beside the name, in the name's place.</summary>
    BesideLabel,
    /// <summary>Beside the fader, or the gain on an input row, in that part's place.</summary>
    BesideFader,
}

/// <summary>Which end of a fader is the top of its range.</summary>
public enum SkinFaderDirection
{
    /// <summary>The maximum at the top or at the right, as every fader has always been.</summary>
    Normal,
    /// <summary>The maximum at the bottom or at the left.</summary>
    Reversed,
}

/// <summary>A strip's meter: which way it runs and where it sits.</summary>
public sealed record SkinMeterOptions(
    SkinLayoutOrientation Orientation = SkinLayoutOrientation.Horizontal,
    SkinMeterPlacement Placement = SkinMeterPlacement.BelowLabel)
{
    public static SkinMeterOptions Default { get; } = new();

    internal string Description() => $"{Orientation}/{Placement}";
}

/// <summary>A strip's fader: which way it runs and which end is the top.</summary>
public sealed record SkinFaderOptions(
    SkinLayoutOrientation Orientation = SkinLayoutOrientation.Horizontal,
    SkinFaderDirection Direction = SkinFaderDirection.Normal)
{
    public static SkinFaderOptions Default { get; } = new();

    internal string Description() => $"{Orientation}/{Direction}";
}

/// <summary>
/// One strip template and how it is drawn: the order of its parts, the parts
/// left out, the label, the meter and the fader. <see cref="Order"/> is
/// complete: the parts the skin ordered, then the rest in their default order.
/// </summary>
public sealed record SkinStripOptions(
    string Template,
    IReadOnlyList<string> Order,
    IReadOnlyList<string> Hidden,
    SkinLabelStyle Label,
    SkinMeterOptions Meter,
    SkinFaderOptions Fader)
{
    /// <summary>The order as written, before omitted parts get their defaults; null when no order was given.</summary>
    public IReadOnlyList<string>? ExplicitOrder { get; init; }

    /// <summary>An input row with every part in its default order.</summary>
    public static SkinStripOptions InputRowDefault { get; } = new(SkinLayoutTemplates.InputRow, SkinStripParts.InputRow, [],
        SkinLabelStyle.Text, SkinMeterOptions.Default, SkinFaderOptions.Default);

    /// <summary>A vertical send strip with every part in its default order and its fader and meter upright.</summary>
    public static SkinStripOptions BankDefault { get; } = new(SkinLayoutTemplates.VerticalStrip, SkinStripParts.Send, [],
        SkinLabelStyle.Text, new SkinMeterOptions(SkinLayoutOrientation.Vertical, SkinMeterPlacement.BesideFader),
        new SkinFaderOptions(SkinLayoutOrientation.Vertical));

    /// <summary>The parts the strip draws, in the order it draws them.</summary>
    public IReadOnlyList<string> Shown => [.. Order.Where(p => !Hidden.Contains(p, StringComparer.Ordinal))];

    internal string Description() =>
        $"{Template}[{string.Join(',', Shown)}]{Label}:{Meter.Description()}:{Fader.Description()}:{(ExplicitOrder is null ? "*" : string.Join(',', ExplicitOrder))}";
}

/// <summary>A bank's master: its template and how its label, meter and fader are drawn.</summary>
public sealed record SkinMasterOptions(
    string Template,
    SkinLabelStyle Label,
    SkinMeterOptions Meter,
    SkinFaderOptions Fader)
{
    public static SkinMasterOptions Default { get; } = new(SkinLayoutTemplates.MasterCard, SkinLabelStyle.Text,
        SkinMeterOptions.Default, SkinFaderOptions.Default);

    internal string Description() => $"{Template}:{Label}:{Meter.Description()}:{Fader.Description()}";
}

/// <summary>INPUTS: the panel the window has always drawn, or a rack of input strips.</summary>
public sealed record SkinInputsOptions(string Template, SkinStripOptions Strip)
{
    public static SkinInputsOptions Default { get; } = new(SkinLayoutTemplates.HardwarePanel, SkinStripOptions.InputRowDefault);

    public bool IsRack => Template == SkinLayoutTemplates.InputRack;

    internal string Description() => IsRack ? $"{Template}({Strip.Description()})" : Template;
}

/// <summary>
/// The submixer: the channel tiles the window has always drawn, mix banks or
/// a mix matrix. A bank always has a picker that lists every mix; the skin
/// only says how many banks show, which way they run, which mixes they start
/// on and how their strips and masters are drawn. A matrix always shows every
/// mix the view shows, so it has nothing to pick; the skin says how its heads
/// and cells are drawn (<see cref="Matrix"/>).
/// </summary>
/// <param name="SelectionDefaults">
/// Mix ids as mixer.json names them, which the banks show first when the
/// user has chosen none. An id with no mix behind it is skipped.
/// </param>
public sealed record SkinSubmixerOptions(
    string Template,
    SkinLayoutOrientation Orientation,
    int VisibleBanks,
    IReadOnlyList<string> SelectionDefaults,
    SkinStripOptions Strip,
    SkinMasterOptions Master)
{
    /// <summary>How many banks show when a skin with banks does not say.</summary>
    public const int DefaultVisibleBanks = 2;

    public static SkinSubmixerOptions Default { get; } = new(SkinLayoutTemplates.ChannelTiles, SkinLayoutOrientation.Horizontal,
        DefaultVisibleBanks, [], SkinStripOptions.BankDefault, SkinMasterOptions.Default);

    public bool IsBanks => Template == SkinLayoutTemplates.MixBanks;

    public bool IsMatrix => Template == SkinLayoutTemplates.MixMatrix;

    /// <summary>How a <c>mix-matrix</c> is drawn; <see cref="SkinMatrixOptions.Default"/> with any other template.</summary>
    public SkinMatrixOptions Matrix { get; init; } = SkinMatrixOptions.Default;

    /// <summary>
    /// How each bank picks its mix: a dropdown, the default, or a row of keys,
    /// one per mix. Either lists every mix the view shows and makes the same
    /// pick, so the choice is only how the picker looks.
    /// </summary>
    public string Picker { get; init; } = SkinLayoutTemplates.PickerDropdown;

    /// <summary>True when each bank picks its mix with a row of keys.</summary>
    public bool PickerButtons => IsBanks && Picker == SkinLayoutTemplates.PickerButtons;

    internal string Description() => IsBanks
        ? $"{Template}({Orientation},{VisibleBanks},{string.Join(',', SelectionDefaults)},{Strip.Description()},{Master.Description()},{Picker})"
        : IsMatrix ? $"{Template}({Matrix.Description()})" : Template;
}

/// <summary>Where the submixer starts out under a skin, until the user docks it or pops it out.</summary>
public enum SkinMixerPlacement
{
    /// <summary>In the main window, as it has always been.</summary>
    Docked,
    /// <summary>In its own window, opened once when the skin is put on.</summary>
    Detached,
}

/// <summary>
/// The values <c>regions.submixer.popOut.default</c> may take, in the order
/// of <see cref="SkinMixerPlacement"/>; the first is the default.
/// </summary>
public static class SkinPopOutDefaults
{
    public const string Docked = "docked";
    public const string Detached = "detached";

    public static IReadOnlyList<string> All { get; } = [Docked, Detached];
}

/// <summary>A node of the layout tree: a region or a container.</summary>
public abstract record SkinLayoutNode
{
    internal abstract void Describe(StringBuilder text);
}

/// <summary>A region placed in a container: one of <see cref="SkinLayoutRegions.All"/>.</summary>
public sealed record SkinLayoutRegion(string Id) : SkinLayoutNode
{
    internal override void Describe(StringBuilder text) => text.Append('<').Append(Id).Append('>');
}

/// <summary>
/// A child of a container and its place there. A grid child has a cell and
/// spans; a dock child has an edge; a stack child has neither, and the
/// fields keep their defaults.
/// </summary>
public sealed record SkinLayoutChild(SkinLayoutNode Node)
{
    public int Row { get; init; }
    public int Column { get; init; }
    public int RowSpan { get; init; } = 1;
    public int ColumnSpan { get; init; } = 1;
    public SkinLayoutEdge Edge { get; init; } = SkinLayoutEdge.Fill;
}

/// <summary>A container: a stack, a grid or a dock, with an id unique in its tree.</summary>
/// <param name="Spacing">The gap between children, in device-independent pixels.</param>
public abstract record SkinLayoutContainer(string Id, double Spacing, IReadOnlyList<SkinLayoutChild> Children) : SkinLayoutNode
{
    /// <summary>
    /// The children in the order the window places them and focus visits
    /// them: a stack's and a dock's in the file's order, a grid's by row and
    /// then by column.
    /// </summary>
    public virtual IReadOnlyList<SkinLayoutChild> Placed => Children;

    /// <summary>This container and every one inside it, outermost first.</summary>
    public IEnumerable<SkinLayoutContainer> Containers()
    {
        yield return this;
        foreach (SkinLayoutChild child in Placed)
            if (child.Node is SkinLayoutContainer inner)
                foreach (SkinLayoutContainer nested in inner.Containers())
                    yield return nested;
    }

    /// <summary>Every region the tree places, in placement order, a container's regions where the container stands.</summary>
    public IEnumerable<string> Regions()
    {
        foreach (SkinLayoutChild child in Placed)
        {
            if (child.Node is SkinLayoutRegion region) yield return region.Id;
            else if (child.Node is SkinLayoutContainer inner)
                foreach (string nested in inner.Regions())
                    yield return nested;
        }
    }

    /// <summary>The region ids this container places directly, in placement order.</summary>
    public IEnumerable<string> RegionOrder => Placed.Select(c => c.Node).OfType<SkinLayoutRegion>().Select(r => r.Id);

    /// <summary>Equal in what the window shows: kinds, ids, tracks, places, gaps and order.</summary>
    public bool SameAs(SkinLayoutContainer other) => Description() == other.Description();

    /// <summary>The tree written out in one line, equal for two trees that place everything alike.</summary>
    public string Description()
    {
        var text = new StringBuilder();
        Describe(text);
        return text.ToString();
    }

    private protected void DescribeChildren(StringBuilder text)
    {
        text.Append('[');
        foreach (SkinLayoutChild child in Placed)
        {
            text.Append(child.Row).Append(',').Append(child.Column).Append(',').Append(child.RowSpan).Append(',')
                .Append(child.ColumnSpan).Append(',').Append(child.Edge).Append(':');
            child.Node.Describe(text);
            text.Append(';');
        }
        text.Append(']');
    }
}

/// <summary>A stack: its children one after another, in one direction.</summary>
public sealed record SkinLayoutStack(
    string Id,
    SkinLayoutOrientation Orientation,
    double Spacing,
    IReadOnlyList<SkinLayoutChild> Children) : SkinLayoutContainer(Id, Spacing, Children)
{
    internal override void Describe(StringBuilder text)
    {
        text.Append("stack ").Append(Id).Append(' ').Append(Orientation).Append(' ')
            .Append(Spacing.ToString(CultureInfo.InvariantCulture));
        DescribeChildren(text);
    }
}

/// <summary>A grid: rows and columns of tracks, each child in a cell with its spans.</summary>
public sealed record SkinLayoutGrid(
    string Id,
    IReadOnlyList<SkinLayoutTrack> Rows,
    IReadOnlyList<SkinLayoutTrack> Columns,
    double Spacing,
    IReadOnlyList<SkinLayoutChild> Children) : SkinLayoutContainer(Id, Spacing, Children)
{
    public override IReadOnlyList<SkinLayoutChild> Placed =>
        [.. Children.OrderBy(c => c.Row).ThenBy(c => c.Column)];

    internal override void Describe(StringBuilder text)
    {
        text.Append("grid ").Append(Id).Append(' ').Append(string.Join(' ', Rows)).Append(" / ")
            .Append(string.Join(' ', Columns)).Append(' ').Append(Spacing.ToString(CultureInfo.InvariantCulture));
        DescribeChildren(text);
    }
}

/// <summary>A dock: children against its edges in order, the last one filling the rest.</summary>
public sealed record SkinLayoutDock(
    string Id,
    double Spacing,
    IReadOnlyList<SkinLayoutChild> Children) : SkinLayoutContainer(Id, Spacing, Children)
{
    internal override void Describe(StringBuilder text)
    {
        text.Append("dock ").Append(Id).Append(' ').Append(Spacing.ToString(CultureInfo.InvariantCulture));
        DescribeChildren(text);
    }
}

/// <summary>What a skin says about one section beyond where it goes.</summary>
/// <param name="Width">A preferred width, never below what the section needs; null for none.</param>
/// <param name="Height">A preferred height, a floor the section grows past; null for none.</param>
public sealed record SkinRegionOptions(
    SkinRegionVisibility Visibility = SkinRegionVisibility.Visible,
    double? Width = null,
    double? Height = null)
{
    public static SkinRegionOptions Default { get; } = new();
}

/// <summary>
/// The header's actions as a skin orders them: <see cref="Actions"/> first
/// in the row, then the ones it names nowhere in their usual order, and
/// <see cref="Overflow"/> in the menu at the end of the row.
/// </summary>
public sealed record SkinHeaderOptions(IReadOnlyList<string> Actions, IReadOnlyList<string> Overflow)
{
    public static SkinHeaderOptions Default { get; } = new([], []);

    /// <summary>Whether the version number stands just after the title rather than among the actions.</summary>
    public bool VersionByTitle { get; init; }

    /// <summary>The actions shown in the row, in order.</summary>
    public IReadOnlyList<string> Row =>
        [.. Actions, .. SkinHeaderActions.All.Where(a => !Actions.Contains(a) && !Overflow.Contains(a)
            && !(VersionByTitle && a == SkinHeaderActions.Version))];
}

/// <summary>
/// A skin's validated desktop layout. Every value has passed
/// <see cref="SkinLayoutReader"/>; nothing in it names a type, a binding, a
/// resource or a file.
/// </summary>
/// <param name="Version">The layout format version the skin was written for.</param>
/// <param name="Main">The main window's tree. Without the header in it, the header stays above it.</param>
public sealed record SkinLayout(int Version, SkinLayoutContainer Main)
{
    /// <summary>The sections' options, by region id; a section left out takes <see cref="SkinRegionOptions.Default"/>.</summary>
    public IReadOnlyDictionary<string, SkinRegionOptions> Regions { get; init; } =
        new Dictionary<string, SkinRegionOptions>(StringComparer.Ordinal);

    /// <summary>The header's action order and overflow.</summary>
    public SkinHeaderOptions Header { get; init; } = SkinHeaderOptions.Default;

    /// <summary>The template INPUTS is drawn with, and its strips.</summary>
    public SkinInputsOptions Inputs { get; init; } = SkinInputsOptions.Default;

    /// <summary>The template the submixer is drawn with, its banks, strips and masters.</summary>
    public SkinSubmixerOptions Submixer { get; init; } = SkinSubmixerOptions.Default;

    /// <summary>The template MONITOR is drawn with: one of <see cref="SkinLayoutTemplates.Monitor"/>.</summary>
    public string Monitor { get; init; } = SkinLayoutTemplates.OutputPanel;

    /// <summary>The template APPLICATIONS is drawn with: one of <see cref="SkinLayoutTemplates.Applications"/>.</summary>
    public string Applications { get; init; } = SkinLayoutTemplates.ApplicationChips;

    /// <summary>
    /// How many applications one column holds when APPLICATIONS is drawn as
    /// <see cref="SkinLayoutTemplates.ApplicationColumns"/>; the other templates ignore it.
    /// </summary>
    public int ApplicationsPerColumn { get; init; } = SkinFormat.DefaultApplicationsPerColumn;

    /// <summary>
    /// Where the submixer starts out under this skin. It is a default only:
    /// it applies while ui.json holds no docking choice of the user's, and
    /// then once per skin and layout version.
    /// </summary>
    public SkinMixerPlacement PopOut { get; init; } = SkinMixerPlacement.Docked;

    /// <summary>True when the tree places the header itself rather than leaving it above the tree.</summary>
    public bool PlacesHeader => Main.Regions().Contains(SkinLayoutRegions.Header, StringComparer.Ordinal);

    public SkinRegionOptions OptionsFor(string region) =>
        Regions.TryGetValue(region, out SkinRegionOptions? options) ? options : SkinRegionOptions.Default;

    /// <summary>The whole layout in one line: equal for two layouts the window draws alike.</summary>
    public string Description()
    {
        var text = new StringBuilder(Main.Description());
        foreach (string region in SkinLayoutRegions.Sections)
        {
            SkinRegionOptions options = OptionsFor(region);
            text.Append(' ').Append(region).Append('=').Append(options.Visibility).Append(',')
                .Append(options.Width?.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(options.Height?.ToString(CultureInfo.InvariantCulture));
        }
        text.Append(" header=").Append(string.Join(',', Header.Row)).Append('|').Append(string.Join(',', Header.Overflow));
        text.Append(" inputs=").Append(Inputs.Description()).Append(" submixer=").Append(Submixer.Description());
        text.Append(" monitor=").Append(Monitor).Append(" applications=").Append(Applications);
        if (Applications == SkinLayoutTemplates.ApplicationColumns) text.Append(',').Append(ApplicationsPerColumn);
        text.Append(" popOut=").Append(PopOut);
        return text.ToString();
    }
}
