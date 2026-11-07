using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace OpenXLR.UI.Skinning;

/// <summary>
/// Reads the optional <c>layout</c> block of a skin. Like the rest of the
/// reader it touches no rendering platform, so every rule is an ordinary
/// unit test.
///
/// The block is read by hand into typed records: each object's properties
/// are matched against a fixed list, each value against its own type and
/// bounds, and nothing names a type, a binding, a resource or a file. A
/// layout is taken whole or not at all. The first problem rejects the block,
/// names the JSON path it found it at, and leaves the skin's tokens and
/// controls alone, so the window keeps the skin's colours in the classic
/// arrangement.
/// </summary>
public static class SkinLayoutReader
{
    /// <summary>What follows every complaint, so a reader knows what the window did about it.</summary>
    internal const string Consequence = "The skin's layout is ignored and the classic arrangement is used.";

    private sealed class LayoutException(string path, string message) : Exception(message)
    {
        public string Path { get; } = path;
    }

    private const string Stack = "stack";
    private const string Grid = "grid";
    private const string Dock = "dock";
    private const string Region = "region";
    private static readonly string[] Kinds = [Stack, Grid, Dock, Region];
    private static readonly string[] Edges = ["top", "bottom", "left", "right", "fill"];
    private static readonly string[] SectionOptions = ["visibility", "width", "height"];
    private static readonly string[] InputsOptions = [.. SectionOptions, "template", "strip"];
    private static readonly string[] SubmixerOptions =
        [.. SectionOptions, "template", "orientation", "visibleBanks", "selectionDefaults", "strip", "master", "picker", "matrix", "popOut"];
    private static readonly string[] TemplateOptions = [.. SectionOptions, "template"];
    private static readonly string[] ApplicationsOptions = [.. TemplateOptions, "perColumn"];
    private static readonly string[] BankOnlyOptions = ["orientation", "visibleBanks", "selectionDefaults", "strip", "master", "picker"];
    private static readonly string[] MatrixOnlyOptions = ["matrix"];
    private static readonly string[] MatrixOptions = ["rowHead", "columnHead", "cell", "meter"];
    private static readonly string[] MatrixMuted = ["tint", "dim"];
    private static readonly string[] MatrixValues = ["beside", "over-fader"];
    private static readonly string[] MatrixMeters = ["under-fader", "none"];
    private static readonly string[] Orientations = ["horizontal", "vertical"];
    private static readonly string[] Placements = ["below-label", "beside-label", "beside-fader"];
    private static readonly string[] Directions = ["normal", "reversed"];
    private static readonly string[] LabelStyles = ["text", "icon-and-text", "icon"];
    private static readonly string[] HeaderOptions = ["actions", "overflow"];

    /// <summary>What the reader has seen so far in one tree: nodes, container ids and placed regions.</summary>
    private sealed class Tree
    {
        public int Nodes;
        public readonly Dictionary<string, string> Ids = new(StringComparer.Ordinal);
        public readonly HashSet<string> Placed = new(StringComparer.Ordinal);
    }

    /// <summary>The container a child sits in, which decides the placement properties it takes.</summary>
    private sealed record Parent(string Kind, int Rows = 0, int Columns = 0);

    /// <summary>
    /// The layout this block describes, or null when it breaks a rule; the
    /// reason, with its path, is then added to <paramref name="errors"/>.
    /// </summary>
    public static SkinLayout? Read(JsonElement element, List<string> errors)
    {
        try
        {
            return ReadLayout(element);
        }
        catch (LayoutException ex)
        {
            errors.Add($"{ex.Path}: {ex.Message} {Consequence}");
            return null;
        }
    }

    private static SkinLayout ReadLayout(JsonElement element)
    {
        const string path = "layout";
        Dictionary<string, JsonElement> fields = Fields(element, path, "the layout");

        // The version first: a newer layout is expected to carry properties
        // this one does not know, and the version is what to tell the user.
        if (!fields.TryGetValue("version", out JsonElement versionElement))
            throw new LayoutException(path, $"the layout has no \"version\". Add \"version\": {SkinFormat.LayoutVersion}.");
        if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out int version))
            throw new LayoutException(path + ".version", "expected a whole number.");
        if (version > SkinFormat.LayoutVersion)
            throw new LayoutException(path + ".version",
                $"layout version {version} is newer than this OpenXLR reads, which is {SkinFormat.LayoutVersion}. Update OpenXLR to use it.");
        if (version != SkinFormat.LayoutVersion)
            throw new LayoutException(path + ".version", $"layout version {version} is not a version this format ever had.");

        Only(fields, path, "the layout", "version", "main", "regions");
        SkinLayoutContainer main = SkinLayoutDefaults.Classic.Main;
        if (fields.TryGetValue("main", out JsonElement mainElement))
        {
            var tree = new Tree();
            if (ReadChild(mainElement, path + ".main", 1, null, tree).Node is not SkinLayoutContainer container)
                throw new InvalidOperationException("main is read as a container or refused.");
            main = container;
            string[] missing = [.. SkinLayoutRegions.Sections.Where(id => !tree.Placed.Contains(id))];
            if (missing.Length > 0)
                throw new LayoutException(path + ".main.children",
                    $"{Quoted(missing)} {(missing.Length == 1 ? "is" : "are")} not placed; "
                    + "the tree places each of " + Quoted(SkinLayoutRegions.Sections) + " once, and the header at most once.");
        }

        var regions = new Dictionary<string, SkinRegionOptions>(StringComparer.Ordinal);
        SkinHeaderOptions header = SkinHeaderOptions.Default;
        SkinInputsOptions inputs = SkinInputsOptions.Default;
        SkinSubmixerOptions submixer = SkinSubmixerOptions.Default;
        string monitor = SkinLayoutTemplates.OutputPanel;
        string applications = SkinLayoutTemplates.ApplicationChips;
        int perColumn = SkinFormat.DefaultApplicationsPerColumn;
        SkinMixerPlacement popOut = SkinMixerPlacement.Docked;
        if (fields.TryGetValue("regions", out JsonElement regionsElement))
        {
            string regionsPath = path + ".regions";
            foreach ((string id, JsonElement value) in Fields(regionsElement, regionsPath, "\"regions\""))
            {
                string at = $"{regionsPath}.{Printable(id)}";
                if (!SkinLayoutRegions.Exists(id))
                    throw new LayoutException(at,
                        $"\"{Printable(id)}\" is not a region this OpenXLR has; use one of {Quoted(SkinLayoutRegions.All)}.");
                if (id == SkinLayoutRegions.Header) header = ReadHeader(value, at);
                else
                {
                    Dictionary<string, JsonElement> section = Fields(value, at, $"the options of \"{id}\"");
                    regions[id] = ReadSection(section, at, id);
                    if (id == SkinLayoutRegions.Inputs) inputs = ReadInputs(section, at);
                    if (id == SkinLayoutRegions.Submixer)
                    {
                        submixer = ReadSubmixer(section, at);
                        if (section.TryGetValue("popOut", out JsonElement popOutElement)) popOut = ReadPopOut(popOutElement, at + ".popOut");
                    }
                    if (id == SkinLayoutRegions.Monitor && section.TryGetValue("template", out JsonElement monitorTemplate))
                        monitor = Name(monitorTemplate, at + ".template", SkinLayoutTemplates.Monitor, "a MONITOR template");
                    if (id == SkinLayoutRegions.Applications) (applications, perColumn) = ReadApplications(section, at);
                }
            }
        }
        return new SkinLayout(version, main)
        {
            Regions = regions, Header = header, Inputs = inputs, Submixer = submixer,
            Monitor = monitor, Applications = applications, ApplicationsPerColumn = perColumn, PopOut = popOut,
        };
    }

    /// <summary>
    /// One node with its place in <paramref name="parent"/>: a region, or a
    /// container read with its children. <paramref name="parent"/> is null for
    /// <c>main</c>, which is a container and takes no place of its own.
    /// </summary>
    private static SkinLayoutChild ReadChild(JsonElement element, string path, int depth, Parent? parent, Tree tree)
    {
        Dictionary<string, JsonElement> fields = Fields(element, path, parent is null ? "\"main\"" : "a child");
        string kind = Kind(fields, path);
        if (!Kinds.Contains(kind, StringComparer.Ordinal)) throw UnknownKind(path, kind);
        if (kind == Region && parent is null)
            throw new LayoutException(path + ".kind", "\"main\" is a container, a \"stack\", \"grid\" or \"dock\", not a region of its own.");
        if (++tree.Nodes > SkinFormat.MaxLayoutNodes)
            throw new LayoutException(path, $"the tree holds at most {SkinFormat.MaxLayoutNodes} nodes, containers and regions together.");

        string[] placement = parent?.Kind switch
        {
            Grid => ["row", "column", "rowSpan", "columnSpan"],
            Dock => ["dock"],
            _ => [],
        };
        string what = kind == Region ? "a region" : $"a {kind}";
        string[] own = kind switch
        {
            Stack => ["kind", "id", "orientation", "spacing", "children"],
            Grid => ["kind", "id", "rows", "columns", "spacing", "children"],
            Dock => ["kind", "id", "spacing", "children"],
            _ => ["kind", "region"],
        };
        Only(fields, path, what, [.. own, .. placement]);
        var child = new SkinLayoutChild(new SkinLayoutRegion(""));
        if (parent?.Kind == Grid) child = ReadCell(fields, path, parent, child);
        if (parent?.Kind == Dock)
        {
            if (!fields.TryGetValue("dock", out JsonElement edge))
                throw new LayoutException(path, "a child of a dock needs a \"dock\" edge: " + Quoted(Edges, "or") + ".");
            child = child with
            {
                Edge = (edge.ValueKind == JsonValueKind.String ? edge.GetString() : null) switch
                {
                    "top" => SkinLayoutEdge.Top,
                    "bottom" => SkinLayoutEdge.Bottom,
                    "left" => SkinLayoutEdge.Left,
                    "right" => SkinLayoutEdge.Right,
                    "fill" => SkinLayoutEdge.Fill,
                    _ => throw new LayoutException(path + ".dock", "expected " + Quoted(Edges, "or") + "."),
                },
            };
        }

        if (kind == Region) return child with { Node = ReadRegion(fields, path, tree) };

        if (depth > SkinFormat.MaxLayoutDepth)
            throw new LayoutException(path, $"containers nest at most {SkinFormat.MaxLayoutDepth} deep, \"main\" included.");
        string id = ReadId(fields, path, tree);
        double spacing = fields.TryGetValue("spacing", out JsonElement spacingElement)
            ? Number(spacingElement, path + ".spacing", SkinFormat.MinLayoutSpacing, SkinFormat.MaxLayoutSpacing)
            : SkinLayoutDefaults.Spacing;
        SkinLayoutContainer container = kind switch
        {
            Stack => ReadStack(fields, path, id, spacing, depth, tree),
            Grid => ReadGrid(fields, path, id, spacing, depth, tree),
            _ => ReadDock(fields, path, id, spacing, depth, tree),
        };
        return child with { Node = container };
    }

    private static SkinLayoutStack ReadStack(Dictionary<string, JsonElement> fields, string path, string id,
        double spacing, int depth, Tree tree)
    {
        if (!fields.TryGetValue("orientation", out JsonElement orientationElement))
            throw new LayoutException(path, "a stack needs an \"orientation\" of \"vertical\" or \"horizontal\".");
        SkinLayoutOrientation orientation = (orientationElement.ValueKind == JsonValueKind.String
            ? orientationElement.GetString() : null) switch
        {
            "vertical" => SkinLayoutOrientation.Vertical,
            "horizontal" => SkinLayoutOrientation.Horizontal,
            _ => throw new LayoutException(path + ".orientation", "expected \"vertical\" or \"horizontal\"."),
        };
        return new SkinLayoutStack(id, orientation, spacing,
            ReadChildren(fields, path, "a stack", depth, new Parent(Stack), tree));
    }

    private static SkinLayoutGrid ReadGrid(Dictionary<string, JsonElement> fields, string path, string id,
        double spacing, int depth, Tree tree)
    {
        SkinLayoutTrack[] rows = ReadTracks(fields, path, "rows");
        SkinLayoutTrack[] columns = ReadTracks(fields, path, "columns");
        IReadOnlyList<SkinLayoutChild> children =
            ReadChildren(fields, path, "a grid", depth, new Parent(Grid, rows.Length, columns.Length), tree);

        // Each cell holds at most one child; an empty cell is allowed.
        var owner = new int?[rows.Length, columns.Length];
        for (int index = 0; index < children.Count; index++)
        {
            SkinLayoutChild child = children[index];
            for (int row = child.Row; row < child.Row + child.RowSpan; row++)
                for (int column = child.Column; column < child.Column + child.ColumnSpan; column++)
                {
                    if (owner[row, column] is int other)
                        throw new LayoutException($"{path}.children[{index}]",
                            $"covers row {row}, column {column}, which {path}.children[{other}] already covers; grid children do not overlap.");
                    owner[row, column] = index;
                }
        }
        return new SkinLayoutGrid(id, rows, columns, spacing, children);
    }

    private static SkinLayoutDock ReadDock(Dictionary<string, JsonElement> fields, string path, string id,
        double spacing, int depth, Tree tree)
    {
        IReadOnlyList<SkinLayoutChild> children = ReadChildren(fields, path, "a dock", depth, new Parent(Dock), tree);
        if (children.Count == 0)
            throw new LayoutException(path + ".children", "a dock needs a last child with \"dock\": \"fill\".");
        for (int index = 0; index < children.Count - 1; index++)
            if (children[index].Edge == SkinLayoutEdge.Fill)
                throw new LayoutException($"{path}.children[{index}].dock",
                    "only the last child of a dock fills; give this one \"top\", \"bottom\", \"left\" or \"right\".");
        if (children[^1].Edge != SkinLayoutEdge.Fill)
            throw new LayoutException($"{path}.children[{children.Count - 1}].dock",
                "the last child of a dock fills what the others leave; give it \"dock\": \"fill\".");
        return new SkinLayoutDock(id, spacing, children);
    }

    private static IReadOnlyList<SkinLayoutChild> ReadChildren(Dictionary<string, JsonElement> fields, string path,
        string what, int depth, Parent parent, Tree tree)
    {
        if (!fields.TryGetValue("children", out JsonElement childrenElement))
            throw new LayoutException(path, $"{what} needs a \"children\" array.");
        if (childrenElement.ValueKind != JsonValueKind.Array)
            throw new LayoutException(path + ".children", "expected an array of regions and containers.");
        if (childrenElement.GetArrayLength() > SkinFormat.MaxLayoutChildren)
            throw new LayoutException(path + ".children",
                $"a container holds at most {SkinFormat.MaxLayoutChildren} children.");
        var children = new List<SkinLayoutChild>();
        int index = 0;
        foreach (JsonElement child in childrenElement.EnumerateArray())
            children.Add(ReadChild(child, $"{path}.children[{index++}]", depth + 1, parent, tree));
        return children;
    }

    /// <summary>A grid child's cell and spans, all inside the grid's tracks.</summary>
    private static SkinLayoutChild ReadCell(Dictionary<string, JsonElement> fields, string path, Parent grid,
        SkinLayoutChild child)
    {
        if (!fields.ContainsKey("row") || !fields.ContainsKey("column"))
            throw new LayoutException(path, "a child of a grid needs a \"row\" and a \"column\", counted from 0.");
        int row = Whole(fields["row"], path + ".row", 0, grid.Rows - 1,
            $"the grid has {grid.Rows} {(grid.Rows == 1 ? "row" : "rows")}, counted from 0");
        int column = Whole(fields["column"], path + ".column", 0, grid.Columns - 1,
            $"the grid has {grid.Columns} {(grid.Columns == 1 ? "column" : "columns")}, counted from 0");
        int rowSpan = fields.TryGetValue("rowSpan", out JsonElement rs)
            ? Whole(rs, path + ".rowSpan", 1, grid.Rows - row, "a span stays inside the grid") : 1;
        int columnSpan = fields.TryGetValue("columnSpan", out JsonElement cs)
            ? Whole(cs, path + ".columnSpan", 1, grid.Columns - column, "a span stays inside the grid") : 1;
        return child with { Row = row, Column = column, RowSpan = rowSpan, ColumnSpan = columnSpan };
    }

    private static SkinLayoutTrack[] ReadTracks(Dictionary<string, JsonElement> fields, string path, string name)
    {
        string at = $"{path}.{name}";
        if (!fields.TryGetValue(name, out JsonElement element))
            throw new LayoutException(path, $"a grid needs \"rows\" and \"columns\", each an array of 1 to {SkinFormat.MaxLayoutTracks} tracks.");
        if (element.ValueKind != JsonValueKind.Array)
            throw new LayoutException(at, "expected an array of tracks.");
        int count = element.GetArrayLength();
        if (count < 1 || count > SkinFormat.MaxLayoutTracks)
            throw new LayoutException(at, $"a grid has 1 to {SkinFormat.MaxLayoutTracks} {name}; this one has {count}.");
        var tracks = new SkinLayoutTrack[count];
        int index = 0;
        foreach (JsonElement track in element.EnumerateArray())
        {
            string trackPath = $"{at}[{index}]";
            const string shape = "a track is \"auto\" or an object with one of \"star\" and \"size\".";
            if (track.ValueKind == JsonValueKind.String)
            {
                if (track.GetString() != "auto") throw new LayoutException(trackPath, shape);
                tracks[index++] = SkinLayoutTrack.Auto;
                continue;
            }
            if (track.ValueKind != JsonValueKind.Object) throw new LayoutException(trackPath, shape);
            Dictionary<string, JsonElement> trackFields = Fields(track, trackPath, "a track");
            Only(trackFields, trackPath, "a track", "star", "size");
            if (trackFields.Count != 1) throw new LayoutException(trackPath, shape);
            tracks[index++] = trackFields.TryGetValue("star", out JsonElement star)
                ? new SkinLayoutTrack(SkinTrackUnit.Star,
                    Number(star, trackPath + ".star", SkinFormat.MinLayoutStar, SkinFormat.MaxLayoutStar))
                : new SkinLayoutTrack(SkinTrackUnit.Pixel,
                    Number(trackFields["size"], trackPath + ".size", SkinFormat.MinLayoutTrackSize, SkinFormat.MaxLayoutTrackSize));
        }
        return tracks;
    }

    private static SkinLayoutRegion ReadRegion(Dictionary<string, JsonElement> fields, string path, Tree tree)
    {
        if (!fields.TryGetValue("region", out JsonElement regionElement))
            throw new LayoutException(path, "a region needs a \"region\" id.");
        if (regionElement.ValueKind != JsonValueKind.String)
            throw new LayoutException(path + ".region", "expected a region id such as \"inputs\".");
        string id = regionElement.GetString()!;
        if (!SkinLayoutRegions.Exists(id))
            throw new LayoutException(path + ".region",
                $"\"{Printable(id)}\" is not a region this OpenXLR has; use one of {Quoted(SkinLayoutRegions.All)}.");
        if (!tree.Placed.Add(id))
            throw new LayoutException(path + ".region", $"\"{id}\" is placed twice; each region appears once.");
        return new SkinLayoutRegion(id);
    }

    /// <summary>A container's id: short, plain and unique in the tree, so ui.json can name its places.</summary>
    private static string ReadId(Dictionary<string, JsonElement> fields, string path, Tree tree)
    {
        if (!fields.TryGetValue("id", out JsonElement element))
            throw new LayoutException(path, "every container needs an \"id\", so your own arrangement can name its places.");
        string? id = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        if (id is null || id.Length is 0 or > SkinFormat.MaxLayoutIdLength
            || !id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            throw new LayoutException(path + ".id",
                $"expected 1 to {SkinFormat.MaxLayoutIdLength} lowercase letters, digits and hyphens.");
        if (tree.Ids.TryGetValue(id, out string? other))
            throw new LayoutException(path + ".id", $"\"{id}\" is already the id of {other}; container ids are unique.");
        tree.Ids[id] = path;
        return id;
    }

    private static SkinRegionOptions ReadSection(Dictionary<string, JsonElement> fields, string path, string region)
    {
        Only(fields, path, $"\"{region}\"", region switch
        {
            SkinLayoutRegions.Inputs => InputsOptions,
            SkinLayoutRegions.Submixer => SubmixerOptions,
            SkinLayoutRegions.Monitor => TemplateOptions,
            SkinLayoutRegions.Applications => ApplicationsOptions,
            _ => SectionOptions,
        });
        var options = SkinRegionOptions.Default;
        if (fields.TryGetValue("visibility", out JsonElement visibility))
            options = options with
            {
                Visibility = (visibility.ValueKind == JsonValueKind.String ? visibility.GetString() : null) switch
                {
                    "visible" => SkinRegionVisibility.Visible,
                    "collapsed" => SkinRegionVisibility.Collapsed,
                    "hidden" => SkinRegionVisibility.Hidden,
                    _ => throw new LayoutException(path + ".visibility", "expected \"visible\", \"collapsed\" or \"hidden\"."),
                },
            };
        if (fields.TryGetValue("width", out JsonElement width))
            options = options with { Width = Number(width, path + ".width", SkinFormat.MinRegionSize, SkinFormat.MaxRegionSize) };
        if (fields.TryGetValue("height", out JsonElement height))
            options = options with { Height = Number(height, path + ".height", SkinFormat.MinRegionSize, SkinFormat.MaxRegionSize) };
        return options;
    }

    /// <summary>APPLICATIONS' template, and how many applications a column of <c>"application-columns"</c> holds.</summary>
    private static (string Template, int PerColumn) ReadApplications(Dictionary<string, JsonElement> fields, string path)
    {
        string template = fields.TryGetValue("template", out JsonElement element)
            ? Name(element, path + ".template", SkinLayoutTemplates.Applications, "an APPLICATIONS template")
            : SkinLayoutTemplates.ApplicationChips;
        if (!fields.TryGetValue("perColumn", out JsonElement perColumn)) return (template, SkinFormat.DefaultApplicationsPerColumn);
        if (template != SkinLayoutTemplates.ApplicationColumns)
            throw new LayoutException(path + ".perColumn",
                $"\"perColumn\" belongs to the \"{SkinLayoutTemplates.ApplicationColumns}\" template; set \"template\": \"{SkinLayoutTemplates.ApplicationColumns}\" or leave it out.");
        return (template, Whole(perColumn, path + ".perColumn", SkinFormat.MinApplicationsPerColumn, SkinFormat.MaxApplicationsPerColumn,
            "a column holds at least one application, and more columns follow for the rest"));
    }

    /// <summary>INPUTS' template, and an input rack's strip.</summary>
    private static SkinInputsOptions ReadInputs(Dictionary<string, JsonElement> fields, string path)
    {
        string template = fields.TryGetValue("template", out JsonElement element)
            ? Name(element, path + ".template", SkinLayoutTemplates.Inputs, "an INPUTS template")
            : SkinLayoutTemplates.HardwarePanel;
        if (template != SkinLayoutTemplates.InputRack)
        {
            if (fields.ContainsKey("strip"))
                throw new LayoutException(path + ".strip",
                    $"\"strip\" belongs to the \"{SkinLayoutTemplates.InputRack}\" template; set \"template\": \"{SkinLayoutTemplates.InputRack}\" or leave it out.");
            return SkinInputsOptions.Default;
        }
        SkinStripOptions strip = fields.TryGetValue("strip", out JsonElement stripElement)
            ? ReadStrip(stripElement, path + ".strip", SkinLayoutTemplates.InputStrips, SkinStripParts.InputRow, fader: false)
            : SkinStripOptions.InputRowDefault;
        return new SkinInputsOptions(template, strip);
    }

    /// <summary>The submixer's template, and the banks, strips and masters of one drawn by mix.</summary>
    private static SkinSubmixerOptions ReadSubmixer(Dictionary<string, JsonElement> fields, string path)
    {
        string template = fields.TryGetValue("template", out JsonElement element)
            ? Name(element, path + ".template", SkinLayoutTemplates.Submixer, "a submixer template")
            : SkinLayoutTemplates.ChannelTiles;
        if (template != SkinLayoutTemplates.MixBanks && BankOnlyOptions.FirstOrDefault(fields.ContainsKey) is { } bankOnly)
            throw new LayoutException($"{path}.{bankOnly}",
                $"\"{bankOnly}\" belongs to the \"{SkinLayoutTemplates.MixBanks}\" template; set \"template\": \"{SkinLayoutTemplates.MixBanks}\" or leave it out.");
        if (template != SkinLayoutTemplates.MixMatrix && MatrixOnlyOptions.FirstOrDefault(fields.ContainsKey) is { } matrixOnly)
            throw new LayoutException($"{path}.{matrixOnly}",
                $"\"{matrixOnly}\" belongs to the \"{SkinLayoutTemplates.MixMatrix}\" template; set \"template\": \"{SkinLayoutTemplates.MixMatrix}\" or leave it out.");
        if (template == SkinLayoutTemplates.MixMatrix)
            return SkinSubmixerOptions.Default with
            {
                Template = template,
                Matrix = fields.TryGetValue("matrix", out JsonElement matrixElement)
                    ? ReadMatrix(matrixElement, path + ".matrix")
                    : SkinMatrixOptions.Default,
            };
        if (template != SkinLayoutTemplates.MixBanks) return SkinSubmixerOptions.Default;
        SkinLayoutOrientation orientation = fields.TryGetValue("orientation", out JsonElement orientationElement)
            ? Orientation(orientationElement, path + ".orientation")
            : SkinLayoutOrientation.Horizontal;
        int banks = fields.TryGetValue("visibleBanks", out JsonElement banksElement)
            ? Whole(banksElement, path + ".visibleBanks", SkinFormat.MinVisibleBanks, SkinFormat.MaxVisibleBanks,
                "a submixer shows 1 to 4 banks, and every mix stays reachable through each bank's picker")
            : SkinSubmixerOptions.DefaultVisibleBanks;
        IReadOnlyList<string> defaults = fields.TryGetValue("selectionDefaults", out JsonElement defaultsElement)
            ? ReadSelectionDefaults(defaultsElement, path + ".selectionDefaults")
            : [];
        SkinStripOptions strip = fields.TryGetValue("strip", out JsonElement stripElement)
            ? ReadStrip(stripElement, path + ".strip", SkinLayoutTemplates.BankStrips, SkinStripParts.Send, fader: true)
            : SkinStripOptions.BankDefault;
        SkinMasterOptions master = fields.TryGetValue("master", out JsonElement masterElement)
            ? ReadMaster(masterElement, path + ".master")
            : SkinMasterOptions.Default;
        string picker = fields.TryGetValue("picker", out JsonElement pickerElement)
            ? Name(pickerElement, path + ".picker", SkinLayoutTemplates.BankPickers, "a bank picker")
            : SkinLayoutTemplates.PickerDropdown;
        return new SkinSubmixerOptions(template, orientation, banks, defaults, strip, master) { Picker = picker };
    }

    /// <summary>
    /// A matrix: its row heads, its column heads, its cells and where the
    /// channel's meter goes. Every one is optional, and what it leaves out
    /// keeps its default.
    /// </summary>
    private static SkinMatrixOptions ReadMatrix(JsonElement element, string path)
    {
        Dictionary<string, JsonElement> fields = Fields(element, path, "\"matrix\"");
        Only(fields, path, "\"matrix\"", MatrixOptions);
        SkinMatrixOptions options = SkinMatrixOptions.Default;
        if (fields.TryGetValue("rowHead", out JsonElement rowHead))
            options = options with
            {
                RowHead = ReadMatrixParts(rowHead, path + ".rowHead", "\"rowHead\"", "a row head", SkinMatrixParts.RowHead,
                    SkinMatrixParts.HeadKept, label: true, muted: false).Parts,
            };
        if (fields.TryGetValue("columnHead", out JsonElement columnHead))
            options = options with
            {
                ColumnHead = ReadMatrixParts(columnHead, path + ".columnHead", "\"columnHead\"", "a column head",
                    SkinMatrixParts.ColumnHead, SkinMatrixParts.HeadKept, label: true, muted: false).Parts,
            };
        if (fields.TryGetValue("cell", out JsonElement cell))
        {
            (SkinMatrixPartOptions parts, SkinMatrixMuted? muted) = ReadMatrixParts(cell, path + ".cell", "\"cell\"", "a cell",
                SkinMatrixParts.Cell, SkinMatrixParts.CellKept, label: false, muted: true);
            options = options with { Cell = parts, Muted = muted ?? SkinMatrixMuted.Tint };
            if (cell.TryGetProperty("valuePlacement", out JsonElement value))
                options = options with
                {
                    Value = Name(value, path + ".cell.valuePlacement", MatrixValues, "a cell value placement") == "over-fader"
                        ? SkinMatrixValue.OverFader : SkinMatrixValue.Beside,
                };
        }
        if (fields.TryGetValue("meter", out JsonElement meter))
            options = options with
            {
                Meter = Name(meter, path + ".meter", MatrixMeters, "a matrix meter placement") == "none"
                    ? SkinMatrixMeter.None : SkinMatrixMeter.UnderFader,
            };
        return options;
    }

    /// <summary>
    /// One part list of a matrix: <c>parts</c> in the order they are drawn,
    /// <c>hidden</c>, and, where the list has them, <c>label</c> and
    /// <c>muted</c>. A part named in neither list keeps its default place
    /// after the ordered ones; <paramref name="kept"/> can never be hidden.
    /// </summary>
    private static (SkinMatrixPartOptions Parts, SkinMatrixMuted? Muted) ReadMatrixParts(JsonElement element, string path,
        string quoted, string what, IReadOnlyList<string> parts, string kept, bool label, bool muted)
    {
        Dictionary<string, JsonElement> fields = Fields(element, path, quoted);
        Only(fields, path, quoted, label ? ["parts", "hidden", "label"] : ["parts", "hidden", "muted", "valuePlacement"]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<string> Parts(string name)
        {
            var list = new List<string>();
            if (!fields.TryGetValue(name, out JsonElement array)) return list;
            string at = $"{path}.{name}";
            if (array.ValueKind != JsonValueKind.Array)
                throw new LayoutException(at, $"expected an array of part names: {Quoted(parts)}.");
            if (array.GetArrayLength() > parts.Count)
                throw new LayoutException(at, $"{what} has {parts.Count} parts, so \"{name}\" lists at most {parts.Count}.");
            int index = 0;
            foreach (JsonElement item in array.EnumerateArray())
            {
                string itemPath = $"{at}[{index++}]";
                string? part = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (part is null || !parts.Contains(part, StringComparer.Ordinal))
                    throw new LayoutException(itemPath,
                        $"{(part is null ? "this" : $"\"{Printable(part)}\"")} is not a part of {what}; use one of {Quoted(parts)}.");
                if (!seen.Add(part))
                    throw new LayoutException(itemPath,
                        $"\"{part}\" is listed twice; a part appears once across \"parts\" and \"hidden\".");
                if (name == "hidden" && part == kept)
                    throw new LayoutException(itemPath, kept == SkinMatrixParts.CellKept
                        ? $"a cell always shows its send's level, so \"{kept}\" cannot be hidden."
                        : $"{what} always shows whose it is, so \"{kept}\" cannot be hidden.");
                list.Add(part);
            }
            return list;
        }
        List<string> ordered = Parts("parts");
        List<string> hidden = Parts("hidden");
        string[] order = [.. ordered, .. parts.Where(p => !ordered.Contains(p, StringComparer.Ordinal))];
        SkinLabelStyle style = label && fields.TryGetValue("label", out JsonElement labelElement)
            ? ReadLabel(labelElement, path + ".label")
            : SkinLabelStyle.Text;
        SkinMatrixMuted? mutedStyle = muted && fields.TryGetValue("muted", out JsonElement mutedElement)
            ? Name(mutedElement, path + ".muted", MatrixMuted, "a muted cell style") == "dim" ? SkinMatrixMuted.Dim : SkinMatrixMuted.Tint
            : null;
        return (new SkinMatrixPartOptions(order, hidden, style)
        {
            ExplicitOrder = parts == SkinMatrixParts.RowHead && fields.ContainsKey("parts") ? ordered : null,
        }, mutedStyle);
    }

    /// <summary>
    /// The submixer's pop-out default: an object whose one property,
    /// <c>default</c>, says where the submixer starts out. Whatever the user
    /// chose since wins over it; that is the window's to decide.
    /// </summary>
    private static SkinMixerPlacement ReadPopOut(JsonElement element, string path)
    {
        Dictionary<string, JsonElement> fields = Fields(element, path, "\"popOut\"");
        Only(fields, path, "\"popOut\"", "default");
        if (!fields.TryGetValue("default", out JsonElement value))
            throw new LayoutException(path, "\"popOut\" needs a \"default\": " + Quoted(SkinPopOutDefaults.All, "or") + ".");
        return Name(value, path + ".default", SkinPopOutDefaults.All, "a pop-out default") == SkinPopOutDefaults.Detached
            ? SkinMixerPlacement.Detached : SkinMixerPlacement.Docked;
    }

    /// <summary>
    /// A strip: its template, the order of its parts, the parts it leaves out,
    /// its label, its meter and, for a send strip, its fader. A part named in
    /// neither list keeps its default place after the ordered ones.
    /// </summary>
    private static SkinStripOptions ReadStrip(JsonElement element, string path, IReadOnlyList<string> templates,
        IReadOnlyList<string> parts, bool fader)
    {
        Dictionary<string, JsonElement> fields = Fields(element, path, "\"strip\"");
        Only(fields, path, "\"strip\"", fader
            ? ["template", "partsOrder", "hidden", "label", "meter", "fader"]
            : ["template", "partsOrder", "hidden", "label", "meter"]);
        string template = fields.TryGetValue("template", out JsonElement templateElement)
            ? Name(templateElement, path + ".template", templates, "a strip template")
            : templates[0];
        // A vertical strip stands its fader and meter upright unless told otherwise; every other strip lies them flat.
        bool upright = template == SkinLayoutTemplates.VerticalStrip;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<string> Parts(string name)
        {
            var list = new List<string>();
            if (!fields.TryGetValue(name, out JsonElement array)) return list;
            string at = $"{path}.{name}";
            if (array.ValueKind != JsonValueKind.Array)
                throw new LayoutException(at, $"expected an array of part names: {Quoted(parts)}.");
            if (array.GetArrayLength() > parts.Count)
                throw new LayoutException(at, $"this strip has {parts.Count} parts, so \"{name}\" lists at most {parts.Count}.");
            int index = 0;
            foreach (JsonElement item in array.EnumerateArray())
            {
                string itemPath = $"{at}[{index++}]";
                string? part = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (part is null || !parts.Contains(part, StringComparer.Ordinal))
                    throw new LayoutException(itemPath,
                        $"{(part is null ? "this" : $"\"{Printable(part)}\"")} is not a part of this strip; use one of {Quoted(parts)}.");
                if (!seen.Add(part))
                    throw new LayoutException(itemPath,
                        $"\"{part}\" is listed twice; a part appears once across \"partsOrder\" and \"hidden\".");
                if (name == "hidden" && part == SkinStripParts.Identity)
                    throw new LayoutException(itemPath, "a strip always shows whose it is, so \"identity\" cannot be hidden.");
                list.Add(part);
            }
            return list;
        }
        List<string> ordered = Parts("partsOrder");
        List<string> hidden = Parts("hidden");
        string[] order = [.. ordered, .. parts.Where(p => !ordered.Contains(p, StringComparer.Ordinal))];
        SkinLabelStyle label = fields.TryGetValue("label", out JsonElement labelElement)
            ? ReadLabel(labelElement, path + ".label")
            : SkinLabelStyle.Text;
        SkinMeterOptions meter = fields.TryGetValue("meter", out JsonElement meterElement)
            ? ReadMeter(meterElement, path + ".meter", upright)
            : upright ? new SkinMeterOptions(SkinLayoutOrientation.Vertical, SkinMeterPlacement.BesideFader) : SkinMeterOptions.Default;
        SkinFaderOptions faderOptions = fields.TryGetValue("fader", out JsonElement faderElement)
            ? ReadFader(faderElement, path + ".fader", upright)
            : upright ? new SkinFaderOptions(SkinLayoutOrientation.Vertical) : SkinFaderOptions.Default;
        return new SkinStripOptions(template, order, hidden, label, meter, faderOptions)
        {
            ExplicitOrder = !fader && fields.ContainsKey("partsOrder") ? ordered : null,
        };
    }

    private static SkinMasterOptions ReadMaster(JsonElement element, string path)
    {
        Dictionary<string, JsonElement> fields = Fields(element, path, "\"master\"");
        Only(fields, path, "\"master\"", "template", "label", "meter", "fader");
        string template = fields.TryGetValue("template", out JsonElement templateElement)
            ? Name(templateElement, path + ".template", SkinLayoutTemplates.Masters, "a master template")
            : SkinLayoutTemplates.MasterCard;
        bool upright = template == SkinLayoutTemplates.MasterStrip;
        SkinLabelStyle label = fields.TryGetValue("label", out JsonElement labelElement)
            ? ReadLabel(labelElement, path + ".label")
            : SkinLabelStyle.Text;
        SkinMeterOptions meter = fields.TryGetValue("meter", out JsonElement meterElement)
            ? ReadMeter(meterElement, path + ".meter", upright)
            : upright ? new SkinMeterOptions(SkinLayoutOrientation.Vertical, SkinMeterPlacement.BesideFader) : SkinMeterOptions.Default;
        SkinFaderOptions fader = fields.TryGetValue("fader", out JsonElement faderElement)
            ? ReadFader(faderElement, path + ".fader", upright)
            : upright ? new SkinFaderOptions(SkinLayoutOrientation.Vertical) : SkinFaderOptions.Default;
        return new SkinMasterOptions(template, label, meter, fader);
    }

    private static SkinLabelStyle ReadLabel(JsonElement element, string path)
    {
        Dictionary<string, JsonElement> fields = Fields(element, path, "\"label\"");
        Only(fields, path, "\"label\"", "style");
        if (!fields.TryGetValue("style", out JsonElement style)) return SkinLabelStyle.Text;
        return Name(style, path + ".style", LabelStyles, "a label style") switch
        {
            "icon-and-text" => SkinLabelStyle.IconAndText,
            "icon" => SkinLabelStyle.Icon,
            _ => SkinLabelStyle.Text,
        };
    }

    /// <summary>A meter's orientation and placement; what it leaves out follows the strip's own default.</summary>
    private static SkinMeterOptions ReadMeter(JsonElement element, string path, bool upright)
    {
        Dictionary<string, JsonElement> fields = Fields(element, path, "\"meter\"");
        Only(fields, path, "\"meter\"", "orientation", "placement");
        SkinLayoutOrientation orientation = fields.TryGetValue("orientation", out JsonElement o)
            ? Orientation(o, path + ".orientation")
            : upright ? SkinLayoutOrientation.Vertical : SkinLayoutOrientation.Horizontal;
        SkinMeterPlacement placement = fields.TryGetValue("placement", out JsonElement p)
            ? Name(p, path + ".placement", Placements, "a meter placement") switch
            {
                "beside-label" => SkinMeterPlacement.BesideLabel,
                "beside-fader" => SkinMeterPlacement.BesideFader,
                _ => SkinMeterPlacement.BelowLabel,
            }
            : upright ? SkinMeterPlacement.BesideFader : SkinMeterPlacement.BelowLabel;
        return new SkinMeterOptions(orientation, placement);
    }

    private static SkinFaderOptions ReadFader(JsonElement element, string path, bool upright)
    {
        Dictionary<string, JsonElement> fields = Fields(element, path, "\"fader\"");
        Only(fields, path, "\"fader\"", "orientation", "direction");
        SkinLayoutOrientation orientation = fields.TryGetValue("orientation", out JsonElement o)
            ? Orientation(o, path + ".orientation")
            : upright ? SkinLayoutOrientation.Vertical : SkinLayoutOrientation.Horizontal;
        SkinFaderDirection direction = fields.TryGetValue("direction", out JsonElement d)
            && Name(d, path + ".direction", Directions, "a fader direction") == "reversed"
            ? SkinFaderDirection.Reversed : SkinFaderDirection.Normal;
        return new SkinFaderOptions(orientation, direction);
    }

    /// <summary>The mix ids the banks start on: short, plain, unique. Whether a mix has the id is for the window to find out.</summary>
    private static IReadOnlyList<string> ReadSelectionDefaults(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new LayoutException(path, "expected an array of mix ids such as \"monitor\" and \"stream\".");
        if (element.GetArrayLength() > SkinFormat.MaxSelectionDefaults)
            throw new LayoutException(path, $"lists at most {SkinFormat.MaxSelectionDefaults} mix ids.");
        var ids = new List<string>();
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string itemPath = $"{path}[{index++}]";
            string? id = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (!ValidMixId(id))
                throw new LayoutException(itemPath,
                    $"expected a mix id of 1 to {SkinFormat.MaxMixIdLength} lowercase letters, digits, hyphens and underscores.");
            if (ids.Contains(id!, StringComparer.Ordinal))
                throw new LayoutException(itemPath, $"\"{id}\" is listed twice; each mix starts in one bank.");
            ids.Add(id!);
        }
        return ids;
    }

    /// <summary>True for a mix id as <c>selectionDefaults</c> and a saved bank choice may hold it.</summary>
    public static bool ValidMixId(string? id) =>
        id is { Length: > 0 and <= SkinFormat.MaxMixIdLength }
        && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static SkinLayoutOrientation Orientation(JsonElement element, string path) =>
        Name(element, path, Orientations, "an orientation") == "vertical" ? SkinLayoutOrientation.Vertical : SkinLayoutOrientation.Horizontal;

    /// <summary>A string from a fixed list; anything else names the list.</summary>
    private static string Name(JsonElement element, string path, IReadOnlyList<string> names, string what)
    {
        string? name = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        if (name is null || !names.Contains(name, StringComparer.Ordinal))
            throw new LayoutException(path,
                $"{(name is null ? "expected" : $"\"{Printable(name)}\" is not {what} this OpenXLR has; use")} {Quoted(names, "or")}.");
        return name;
    }

    private static SkinHeaderOptions ReadHeader(JsonElement element, string path)
    {
        Dictionary<string, JsonElement> fields = Fields(element, path, "the options of \"header\"");
        Only(fields, path, "\"header\"", HeaderOptions);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<string> Actions(string name)
        {
            var actions = new List<string>();
            if (!fields.TryGetValue(name, out JsonElement list)) return actions;
            string at = $"{path}.{name}";
            if (list.ValueKind != JsonValueKind.Array)
                throw new LayoutException(at, "expected an array of header action ids.");
            int index = 0;
            foreach (JsonElement item in list.EnumerateArray())
            {
                string itemPath = $"{at}[{index++}]";
                string? id = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (id == "options")
                    throw new LayoutException(itemPath,
                        "Options is always first and always shown, so no skin places it; leave it out.");
                if (id is null || !SkinHeaderActions.Exists(id))
                    throw new LayoutException(itemPath,
                        $"{(id is null ? "this" : $"\"{Printable(id)}\"")} is not a header action; use one of {Quoted(SkinHeaderActions.All)}.");
                if (!seen.Add(id))
                    throw new LayoutException(itemPath,
                        $"\"{id}\" is listed twice; an action appears once across \"actions\" and \"overflow\".");
                actions.Add(id);
            }
            return actions;
        }
        List<string> row = Actions("actions");
        return new SkinHeaderOptions(row, Actions("overflow"));
    }

    /// <summary>A finite number inside its bounds.</summary>
    private static double Number(JsonElement element, string path, double minimum, double maximum)
    {
        if (element.ValueKind != JsonValueKind.Number)
            throw new LayoutException(path, "expected a number.");
        if (!element.TryGetDouble(out double value) || !double.IsFinite(value))
            throw new LayoutException(path, "expected a finite number.");
        if (value < minimum || value > maximum)
            throw new LayoutException(path,
                $"{value.ToString(CultureInfo.InvariantCulture)} is outside the allowed range "
                + $"{minimum.ToString(CultureInfo.InvariantCulture)} to {maximum.ToString(CultureInfo.InvariantCulture)}.");
        return value;
    }

    /// <summary>A whole number inside its bounds; <paramref name="why"/> says where the bounds come from.</summary>
    private static int Whole(JsonElement element, string path, int minimum, int maximum, string why)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
            throw new LayoutException(path, "expected a whole number.");
        if (value < minimum || value > maximum)
            throw new LayoutException(path, $"{value} is outside {minimum} to {maximum}: {why}.");
        return value;
    }

    /// <summary>An object's properties by name. A name given twice has no single meaning, so it is refused.</summary>
    private static Dictionary<string, JsonElement> Fields(JsonElement element, string path, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new LayoutException(path, $"{what} must be a JSON object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!fields.TryAdd(property.Name, property.Value))
                throw new LayoutException($"{path}.{Printable(property.Name)}", "is given twice.");
        return fields;
    }

    /// <summary>Refuse any property the object's kind does not have.</summary>
    private static void Only(Dictionary<string, JsonElement> fields, string path, string what, params string[] allowed)
    {
        foreach (string name in fields.Keys)
            if (!allowed.Contains(name, StringComparer.Ordinal))
                throw new LayoutException($"{path}.{Printable(name)}",
                    $"{what} has no such property; it takes {Quoted(allowed)}.");
    }

    private static string Kind(Dictionary<string, JsonElement> fields, string path)
    {
        if (!fields.TryGetValue("kind", out JsonElement kind))
            throw new LayoutException(path, "every node needs a \"kind\".");
        if (kind.ValueKind != JsonValueKind.String)
            throw new LayoutException(path + ".kind", "expected " + Quoted(Kinds, "or") + ".");
        return kind.GetString()!;
    }

    private static LayoutException UnknownKind(string path, string kind) =>
        new(path + ".kind", $"\"{Printable(kind)}\" is not a kind this OpenXLR has; use {Quoted(Kinds, "or")}.");

    private static string Quoted(IEnumerable<string> names, string last = "and")
    {
        string[] quoted = [.. names.Select(n => $"\"{n}\"")];
        return quoted.Length <= 1 ? string.Concat(quoted)
            : string.Join(", ", quoted[..^1]) + $" {last} " + quoted[^1];
    }

    /// <summary>A name from the file, short and without control characters, for a message.</summary>
    private static string Printable(string text)
    {
        string clean = new([.. text.Where(c => !char.IsControl(c))]);
        return clean.Length > 64 ? clean[..64] : clean;
    }
}
