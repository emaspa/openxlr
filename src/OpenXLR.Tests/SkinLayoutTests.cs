using Avalonia.Media;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// The optional layout block of a skin: what it may say in this version, that
/// it is taken whole or not at all, and that a refused layout never costs the
/// skin its colours or its controls. Like the rest of the format, none of
/// this needs a rendering platform.
/// </summary>
public sealed class SkinLayoutTests
{
    private const string Palette = """
        "controls": { "fader": "console" },
        "tokens": { "Ox.Card.Background": "#123456" }
        """;

    private static readonly string[] Classic = ["inputs", "headphones", "monitor", "applications", "submixer"];

    private static SkinReadResult Read(string layout) =>
        SkinReader.Read("test", "{\"schema\":1,\"name\":\"Test\"," + Palette + ",\"layout\":" + layout + "}",
            SkinOrigin.BuiltIn, null);

    private static string Region(string id, string extra = "") => "{\"kind\":\"region\",\"region\":\"" + id + "\"" + extra + "}";

    private static string Stack(string orientation, IEnumerable<string> regions, string extra = "") =>
        "{\"version\":1,\"main\":" + StackNode("main", orientation, regions.Select(r => Region(r)), extra) + "}";

    private static string StackNode(string id, string orientation, IEnumerable<string> children, string extra = "") =>
        "{\"kind\":\"stack\",\"id\":\"" + id + "\",\"orientation\":\"" + orientation + "\"" + extra
        + ",\"children\":[" + string.Join(",", children) + "]}";

    private static string WithMain(string node) => "{\"version\":1,\"main\":" + node + "}";

    /// <summary>
    /// A two-column grid: the header across the top, INPUTS and the
    /// submixer side by side, the other three under them.
    /// </summary>
    private const string GridLayout = """
        {"version":1,"main":{"kind":"grid","id":"desktop","spacing":12,
          "columns":[{"size":280},{"star":2}],
          "rows":["auto",{"star":1},"auto","auto"],
          "children":[
            {"kind":"region","region":"header","row":0,"column":0,"columnSpan":2},
            {"kind":"region","region":"inputs","row":1,"column":0},
            {"kind":"region","region":"submixer","row":1,"column":1},
            {"kind":"region","region":"headphones","row":2,"column":0},
            {"kind":"region","region":"monitor","row":2,"column":1},
            {"kind":"region","region":"applications","row":3,"column":0,"columnSpan":2}]}}
        """;

    private const string DockLayout = """
        {"version":1,"main":{"kind":"dock","id":"desktop","spacing":10,"children":[
          {"kind":"region","region":"header","dock":"bottom"},
          {"kind":"region","region":"inputs","dock":"left"},
          {"kind":"stack","id":"side","orientation":"vertical","dock":"right","children":[
            {"kind":"region","region":"headphones"},{"kind":"region","region":"monitor"}]},
          {"kind":"region","region":"applications","dock":"top"},
          {"kind":"region","region":"submixer","dock":"fill"}]}}
        """;

    /// <summary>The palette and controls of <see cref="Palette"/>, whatever happened to the layout.</summary>
    private static void PaletteKept(SkinReadResult result)
    {
        SkinPackage package = Assert.IsType<SkinPackage>(result.Package);
        Assert.Equal(Color.Parse("#123456"), Assert.IsType<SkinSolid>(package.Tokens["Ox.Card.Background"]).Color);
        Assert.Equal("console", package.Controls["fader"]);
    }

    /// <summary>One complaint, naming the path, and the classic arrangement kept.</summary>
    private static string Refused(string layout, string path)
    {
        SkinReadResult result = Read(layout);
        PaletteKept(result);
        Assert.Null(result.Package!.Layout);
        string error = Assert.Single(result.Errors);
        Assert.StartsWith(path + ": ", error, StringComparison.Ordinal);
        Assert.EndsWith(SkinLayoutReader.Consequence, error, StringComparison.Ordinal);
        return error;
    }

    private static SkinLayout Taken(string layout)
    {
        SkinReadResult result = Read(layout);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        PaletteKept(result);
        return Assert.IsType<SkinLayout>(result.Package!.Layout);
    }

    [Fact]
    public void ASkinWithoutALayoutHasNoneAndTheClassicDefaultsDescribeTheWindow()
    {
        SkinReadResult result = SkinReader.Read("test", "{\"schema\":1," + Palette + "}", SkinOrigin.BuiltIn, null);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Null(result.Package!.Layout);
        Assert.Null(SkinPackage.Default.Layout);

        SkinLayout classic = SkinLayoutDefaults.Classic;
        Assert.Equal(SkinFormat.LayoutVersion, classic.Version);
        SkinLayoutStack main = Assert.IsType<SkinLayoutStack>(classic.Main);
        Assert.Equal(SkinLayoutDefaults.ClassicId, main.Id);
        Assert.Equal(SkinLayoutOrientation.Vertical, main.Orientation);
        Assert.Equal(8, main.Spacing);
        Assert.Equal(Classic, main.RegionOrder);
        Assert.False(classic.PlacesHeader);
        Assert.Equal(Classic, SkinLayoutRegions.Sections);
        Assert.Equal(["header", .. Classic], SkinLayoutRegions.All);
        Assert.Equal(SkinHeaderActions.All, classic.Header.Row);
        Assert.Empty(classic.Header.Overflow);
    }

    /// <summary>
    /// Broadcast, Wave and Compact Desk rearrange the window, and their
    /// complete layouts are taken. Every other shipped skin is colours and
    /// control appearances only, so it keeps the classic arrangement and the
    /// user's own section order.
    /// </summary>
    [Fact]
    public void ShippedLayoutSkinsDeclareTheirArrangementAndTheOtherPalettesKeepTheClassicOne()
    {
        foreach (SkinEntry entry in SkinCatalog.BuiltIn())
        {
            Assert.Empty(entry.Errors);
            if (entry.Id is "broadcast" or "broadcast-amber" or "broadcast-blue" or "wave" or "compact-desk" or "hifi" or "hifi-amber" or "hifi-classic") Assert.NotNull(entry.Package.Layout);
            else Assert.Null(entry.Package.Layout);
        }
    }

    [Fact]
    public void WaveKeepsTheInputBindingsAndProcessingReachableInItsMatrix()
    {
        SkinEntry wave = Assert.Single(SkinCatalog.BuiltIn(), entry => entry.Id == "wave");
        Assert.Empty(wave.Errors);
        SkinLayout layout = Assert.IsType<SkinLayout>(wave.Package.Layout);
        Assert.True(layout.Submixer.IsMatrix);
        // Broadcast's arrangement: HEADPHONES beside MONITOR, the matrix under both, then INPUTS and APPLICATIONS.
        SkinLayoutStack desk = Assert.IsType<SkinLayoutStack>(layout.Main);
        SkinLayoutGrid mixes = Assert.IsType<SkinLayoutGrid>(desk.Children[0].Node);
        Assert.Equal(new[] { "headphones", "monitor", "submixer" }, mixes.RegionOrder);
        Assert.Equal(new[] { "inputs", "applications" }, desk.RegionOrder);
        // Its cards sit 8 pixels apart, closer than Broadcast's 12, in the stack and in the grid alike.
        Assert.Equal(8, desk.Spacing);
        Assert.Equal(8, mixes.Spacing);
        Assert.Equal(SkinRegionVisibility.Hidden, layout.OptionsFor(SkinLayoutRegions.Inputs).Visibility);
        foreach (string part in new[] { "identity", "binding", "gain", "mute", "meter", "effects" })
            Assert.Contains(part, layout.Submixer.Matrix.RowHead.Shown);
        foreach (string part in new[] { "identity", "outputs", "master", "mute", "effects" })
            Assert.Contains(part, layout.Submixer.Matrix.ColumnHead.Shown);
        // Its applications in columns of four across the window's width, as Broadcast's.
        Assert.Equal(SkinLayoutTemplates.ApplicationColumns, layout.Applications);
        Assert.Equal(4, layout.ApplicationsPerColumn);
    }

    [Fact]
    public void AVerticalLayoutReadsToItsOrderAndSpacing()
    {
        string[] order = ["submixer", "inputs", "monitor", "headphones", "applications"];
        SkinLayout layout = Taken(Stack("vertical", order, ",\"spacing\":12"));
        Assert.Equal(1, layout.Version);
        SkinLayoutStack main = Assert.IsType<SkinLayoutStack>(layout.Main);
        Assert.Equal("main", main.Id);
        Assert.Equal(SkinLayoutOrientation.Vertical, main.Orientation);
        Assert.Equal(12, main.Spacing);
        Assert.Equal(order, main.RegionOrder);
        Assert.Equal(new SkinLayoutRegion("submixer"), main.Children[0].Node);
    }

    [Fact]
    public void AHorizontalLayoutReadsWithTheClassicSpacingWhenItSetsNone()
    {
        SkinLayoutStack main = Assert.IsType<SkinLayoutStack>(Taken(Stack("horizontal", Classic)).Main);
        Assert.Equal(SkinLayoutOrientation.Horizontal, main.Orientation);
        Assert.Equal(SkinLayoutDefaults.Spacing, main.Spacing);
        Assert.Equal(Classic, main.RegionOrder);
        Assert.False(main.SameAs(SkinLayoutDefaults.Classic.Main));
        Assert.True(main.SameAs(main with { Children = [.. main.Children] }));
    }

    /// <summary>
    /// The milestone 1 shape, a stack of the five sections with no header in
    /// it, still reads; the header then heads the window above the tree.
    /// </summary>
    [Fact]
    public void ATreeWithoutTheHeaderLeavesTheHeaderAboveIt()
    {
        SkinLayout layout = Taken(Stack("horizontal", Classic));
        Assert.False(layout.PlacesHeader);
        Assert.DoesNotContain("header", layout.Main.Regions());
        Assert.True(Taken(GridLayout).PlacesHeader);
    }

    [Fact]
    public void ALayoutWithOnlyAVersionTakesTheClassicTree()
    {
        SkinLayout layout = Taken("{\"version\":1}");
        Assert.True(layout.Main.SameAs(SkinLayoutDefaults.Classic.Main));
        Assert.Empty(layout.Regions);
        Assert.Same(SkinHeaderOptions.Default, layout.Header);
    }

    [Fact]
    public void AGridReadsToItsTracksCellsAndSpans()
    {
        SkinLayoutGrid grid = Assert.IsType<SkinLayoutGrid>(Taken(GridLayout).Main);
        Assert.Equal("desktop", grid.Id);
        Assert.Equal(12, grid.Spacing);
        Assert.Equal([new SkinLayoutTrack(SkinTrackUnit.Pixel, 280), new SkinLayoutTrack(SkinTrackUnit.Star, 2)], grid.Columns);
        Assert.Equal([SkinLayoutTrack.Auto, new SkinLayoutTrack(SkinTrackUnit.Star, 1), SkinLayoutTrack.Auto, SkinLayoutTrack.Auto], grid.Rows);
        SkinLayoutChild header = grid.Children[0];
        Assert.Equal(new SkinLayoutRegion("header"), header.Node);
        Assert.Equal((0, 0, 1, 2), (header.Row, header.Column, header.RowSpan, header.ColumnSpan));
        SkinLayoutChild monitor = grid.Children.Single(c => c.Node == new SkinLayoutRegion("monitor"));
        Assert.Equal((2, 1, 1, 1), (monitor.Row, monitor.Column, monitor.RowSpan, monitor.ColumnSpan));
        // Placement, and focus, goes by row and then by column.
        Assert.Equal(["header", "inputs", "submixer", "headphones", "monitor", "applications"], grid.RegionOrder);
    }

    [Fact]
    public void ADockReadsToItsEdgesInOrderWithANestedStack()
    {
        SkinLayout layout = Taken(DockLayout);
        SkinLayoutDock dock = Assert.IsType<SkinLayoutDock>(layout.Main);
        Assert.Equal(10, dock.Spacing);
        Assert.Equal([SkinLayoutEdge.Bottom, SkinLayoutEdge.Left, SkinLayoutEdge.Right, SkinLayoutEdge.Top, SkinLayoutEdge.Fill],
            dock.Children.Select(c => c.Edge));
        SkinLayoutStack side = Assert.IsType<SkinLayoutStack>(dock.Children[2].Node);
        Assert.Equal("side", side.Id);
        Assert.Equal(["headphones", "monitor"], side.RegionOrder);
        Assert.Equal(["header", "inputs", "headphones", "monitor", "applications", "submixer"], layout.Main.Regions());
        Assert.Equal(["desktop", "side"], layout.Main.Containers().Select(c => c.Id));
    }

    [Fact]
    public void ContainersNestFourDeep()
    {
        string deep = WithMain(StackNode("a", "vertical", [
            Region("header"),
            StackNode("b", "horizontal", [
                Region("inputs"),
                "{\"kind\":\"grid\",\"id\":\"c\",\"rows\":[\"auto\",\"auto\"],\"columns\":[\"auto\"],\"children\":["
                + Region("headphones", ",\"row\":0,\"column\":0")
                + ",{\"kind\":\"dock\",\"id\":\"d\",\"row\":1,\"column\":0,\"children\":["
                + Region("monitor", ",\"dock\":\"top\"") + "," + Region("applications", ",\"dock\":\"fill\"") + "]}]}"]),
            Region("submixer")]));
        SkinLayout layout = Taken(deep);
        Assert.Equal(["a", "b", "c", "d"], layout.Main.Containers().Select(c => c.Id));

        string tooDeep = deep.Replace(Region("applications", ",\"dock\":\"fill\""),
            StackNode("e", "vertical", [Region("applications")], ",\"dock\":\"fill\""));
        string error = Refused(tooDeep, "layout.main.children[1].children[1].children[1].children[1]");
        Assert.Contains($"at most {SkinFormat.MaxLayoutDepth} deep", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RegionOptionsReadToTheirValues()
    {
        SkinLayout layout = Taken("""
            {"version":1,"regions":{
              "applications":{"visibility":"hidden"},
              "headphones":{"visibility":"collapsed","height":180},
              "inputs":{"width":720.5},
              "header":{"actions":["flow","profiles"],"overflow":["restart-daemon","about"]}}}
            """);
        Assert.Equal(SkinRegionVisibility.Hidden, layout.OptionsFor("applications").Visibility);
        Assert.Equal(new SkinRegionOptions(SkinRegionVisibility.Collapsed, null, 180), layout.OptionsFor("headphones"));
        Assert.Equal(720.5, layout.OptionsFor("inputs").Width);
        Assert.Same(SkinRegionOptions.Default, layout.OptionsFor("submixer"));
        Assert.Equal(["flow", "profiles"], layout.Header.Actions);
        Assert.Equal(["restart-daemon", "about"], layout.Header.Overflow);
        // The row: the listed actions, then the ones named nowhere in their usual order.
        Assert.Equal(["flow", "profiles", "arrange", "version"], layout.Header.Row);
        Assert.True(layout.Main.SameAs(SkinLayoutDefaults.Classic.Main));
        Assert.NotEqual(SkinLayoutDefaults.Classic.Description(), layout.Description());
    }

    /// <summary>
    /// A skin may stand the version number just after the title: it then
    /// leaves the row, and listing it in either list as well is refused.
    /// </summary>
    [Fact]
    public void TheVersionCanStandByTheTitle()
    {
        Assert.False(Taken("""{"version":1,"regions":{"header":{"version":"row"}}}""").Header.VersionByTitle);
        SkinLayout layout = Taken("""{"version":1,"regions":{"header":{"version":"title","overflow":["about"]}}}""");
        Assert.True(layout.Header.VersionByTitle);
        Assert.Equal(["profiles", "arrange", "flow", "restart-daemon"], layout.Header.Row);
        Refused("""{"version":1,"regions":{"header":{"version":"title","actions":["version"]}}}""", "layout.regions.header.version");
        Refused("""{"version":1,"regions":{"header":{"version":"title","overflow":["version"]}}}""", "layout.regions.header.version");
        Refused("""{"version":1,"regions":{"header":{"version":"beside"}}}""", "layout.regions.header.version");
    }

    [Fact]
    public void TheSpacingBoundsAreTheDocumentedOnes()
    {
        foreach (double spacing in new[] { SkinFormat.MinLayoutSpacing, SkinFormat.MaxLayoutSpacing, 2.5 })
            Assert.Equal(spacing, Taken(Stack("vertical", Classic, ",\"spacing\":"
                + spacing.ToString(System.Globalization.CultureInfo.InvariantCulture))).Main.Spacing);
        Refused(Stack("vertical", Classic, ",\"spacing\":-1"), "layout.main.spacing");
        Refused(Stack("vertical", Classic, ",\"spacing\":64.5"), "layout.main.spacing");
        Refused(Stack("vertical", Classic, ",\"spacing\":1e999"), "layout.main.spacing");
        Refused(Stack("vertical", Classic, ",\"spacing\":\"8\""), "layout.main.spacing");
        Refused(GridLayout.Replace("\"spacing\":12", "\"spacing\":65"), "layout.main.spacing");
        Refused(DockLayout.Replace("\"spacing\":10", "\"spacing\":-0.5"), "layout.main.spacing");
    }

    [Fact]
    public void AnUnknownOrMissingVersionIsRefused()
    {
        Refused(Stack("vertical", Classic).Replace("\"version\":1", "\"version\":2"), "layout.version");
        Refused(Stack("vertical", Classic).Replace("\"version\":1", "\"version\":0"), "layout.version");
        Refused(Stack("vertical", Classic).Replace("\"version\":1", "\"version\":1.5"), "layout.version");
        Refused(Stack("vertical", Classic).Replace("\"version\":1", "\"version\":\"1\""), "layout.version");
        Refused(Stack("vertical", Classic).Replace("\"version\":1,", ""), "layout");

        // A newer version is said as such, before any property it adds.
        SkinReadResult future = Read("{\"version\":2,\"mixer\":{}}");
        Assert.Contains("newer than this OpenXLR reads", Assert.Single(future.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void ALayoutThatIsNotAnObjectIsRefused()
    {
        Refused("[]", "layout");
        Refused("\"classic\"", "layout");
        Refused("{\"version\":1,\"main\":[]}", "layout.main");
        Refused("{\"version\":1,\"regions\":[]}", "layout.regions");
        Refused("{\"version\":1,\"regions\":{\"inputs\":\"hidden\"}}", "layout.regions.inputs");
    }

    [Fact]
    public void AnUnknownKindIsRefused()
    {
        Refused(Stack("vertical", Classic).Replace("\"kind\":\"stack\"", "\"kind\":\"canvas\""), "layout.main.kind");
        Refused(Stack("vertical", Classic).Replace("\"kind\":\"stack\",", ""), "layout.main");
        Refused(Stack("vertical", Classic).Replace("\"kind\":\"stack\"", "\"kind\":\"region\""), "layout.main.kind");
        Refused(Stack("vertical", Classic).Replace("\"kind\":\"stack\"", "\"kind\":7"), "layout.main.kind");
        string widget = Stack("vertical", Classic).Replace(Region("monitor"), "{\"kind\":\"button\",\"region\":\"monitor\"}");
        Refused(widget, "layout.main.children[2].kind");
    }

    [Fact]
    public void AMissingOrBadOrRepeatedIdIsRefused()
    {
        Refused(Stack("vertical", Classic).Replace("\"id\":\"main\",", ""), "layout.main");
        foreach (string id in new[] { "", "Main", "has space", "under_score", "ünïcode", new string('a', SkinFormat.MaxLayoutIdLength + 1) })
            Refused(Stack("vertical", Classic).Replace("\"id\":\"main\"", "\"id\":\"" + id + "\""), "layout.main.id");
        Refused(Stack("vertical", Classic).Replace("\"id\":\"main\"", "\"id\":3"), "layout.main.id");
        Taken(Stack("vertical", Classic).Replace("\"id\":\"main\"", "\"id\":\"" + new string('a', SkinFormat.MaxLayoutIdLength) + "\""));
        Taken(Stack("vertical", Classic).Replace("\"id\":\"main\"", "\"id\":\"left-2\""));

        string twice = DockLayout.Replace("\"id\":\"side\"", "\"id\":\"desktop\"");
        Assert.Contains("already the id of layout.main", Refused(twice, "layout.main.children[2].id"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownOrMissingPropertyIsRefused()
    {
        Refused(Stack("vertical", Classic).Replace("{\"version\":1,", "{\"version\":1,\"mixer\":{},"), "layout.mixer");
        Refused(Stack("vertical", Classic, ",\"rows\":[\"auto\"]"), "layout.main.rows");
        Refused(Stack("vertical", Classic).Replace(Region("inputs"), Region("inputs", ",\"row\":0")),
            "layout.main.children[0].row");
        Refused(Stack("vertical", Classic).Replace(Region("inputs"), Region("inputs", ",\"dock\":\"top\"")),
            "layout.main.children[0].dock");
        Refused(Stack("vertical", Classic).Replace(Region("inputs"), Region("inputs", ",\"binding\":\"Gain\"")),
            "layout.main.children[0].binding");
        Refused(Stack("vertical", Classic).Replace("\"orientation\":\"vertical\",", ""), "layout.main");
        Refused(Stack("diagonal", Classic), "layout.main.orientation");
        Refused("{\"version\":1,\"main\":{\"kind\":\"stack\",\"id\":\"main\",\"orientation\":\"vertical\"}}", "layout.main");
        Refused(Stack("vertical", Classic).Replace(Region("inputs"), "{\"kind\":\"region\"}"), "layout.main.children[0]");
        // A dock child takes an edge, not a cell; a grid child a cell, not an edge.
        Refused(DockLayout.Replace("\"dock\":\"left\"", "\"row\":0"), "layout.main.children[1].row");
        Refused(GridLayout.Replace("\"row\":1,\"column\":0}", "\"row\":1,\"column\":0,\"dock\":\"left\"}"),
            "layout.main.children[1].dock");
        Refused(GridLayout.Replace("\"columns\"", "\"cols\""), "layout.main.cols");
    }

    [Fact]
    public void ARepeatedKeyIsRefusedWhereverItIs()
    {
        Refused(Stack("vertical", Classic).Replace("{\"version\":1,", "{\"version\":1,\"version\":1,"), "layout.version");
        Refused(Stack("vertical", Classic, ",\"orientation\":\"horizontal\""), "layout.main.orientation");
        Refused(Stack("vertical", Classic).Replace("\"region\":\"inputs\"}", "\"region\":\"inputs\",\"region\":\"monitor\"}"),
            "layout.main.children[0].region");
        Refused("{\"version\":1,\"regions\":{\"inputs\":{},\"inputs\":{}}}", "layout.regions.inputs");

        SkinReadResult twice = SkinReader.Read("test", "{\"schema\":1," + Palette + ",\"layout\":{\"version\":1},\"layout\":{\"version\":1}}",
            SkinOrigin.BuiltIn, null);
        PaletteKept(twice);
        Assert.Null(twice.Package!.Layout);
        Assert.StartsWith("layout: ", Assert.Single(twice.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void EverySectionIsPlacedExactlyOnceAndTheHeaderAtMostOnce()
    {
        Refused(Stack("vertical", ["inputs", "headphones", "monitor", "applications"]), "layout.main.children");
        Refused(Stack("vertical", [.. Classic, "inputs"]), "layout.main.children[5].region");
        Refused(Stack("vertical", ["inputs", "headphones", "monitor", "applications", "mixer"]), "layout.main.children[4].region");
        Refused(Stack("vertical", ["header", .. Classic, "header"]), "layout.main.children[6].region");
        Refused(Stack("vertical", []), "layout.main.children");
        Refused(Stack("vertical", Classic).Replace("\"region\":\"inputs\"", "\"region\":3"), "layout.main.children[0].region");
        Assert.True(Taken(Stack("vertical", ["header", .. Classic])).PlacesHeader);
        // A section left out of a nested tree is still left out.
        Refused(DockLayout.Replace(",{\"kind\":\"region\",\"region\":\"monitor\"}", ""), "layout.main.children");
    }

    [Fact]
    public void ABadGridTrackIsRefused()
    {
        foreach ((string track, string path) in new[]
        {
            ("\"fill\"", "layout.main.columns[0]"),
            ("3", "layout.main.columns[0]"),
            ("{}", "layout.main.columns[0]"),
            ("{\"star\":1,\"size\":100}", "layout.main.columns[0]"),
            ("{\"star\":0.05}", "layout.main.columns[0].star"),
            ("{\"star\":10.5}", "layout.main.columns[0].star"),
            ("{\"star\":\"1\"}", "layout.main.columns[0].star"),
            ("{\"size\":39}", "layout.main.columns[0].size"),
            ("{\"size\":2001}", "layout.main.columns[0].size"),
            ("{\"size\":1e999}", "layout.main.columns[0].size"),
            ("{\"min\":10}", "layout.main.columns[0].min"),
        })
            Refused(GridLayout.Replace("{\"size\":280}", track), path);
        foreach (string track in new[] { "{\"star\":0.1}", "{\"star\":10}", "{\"size\":40}", "{\"size\":2000}", "\"auto\"" })
            Taken(GridLayout.Replace("{\"size\":280}", track));

        Refused(GridLayout.Replace("\"columns\":[{\"size\":280},{\"star\":2}]", "\"columns\":[]"), "layout.main.columns");
        Refused(GridLayout.Replace("\"columns\":[{\"size\":280},{\"star\":2}]", "\"columns\":\"auto\""), "layout.main.columns");
        Refused(GridLayout.Replace("\"rows\":[\"auto\",{\"star\":1},\"auto\",\"auto\"],", ""), "layout.main");
        string nine = "\"columns\":[" + string.Join(",", Enumerable.Repeat("\"auto\"", SkinFormat.MaxLayoutTracks + 1)) + "]";
        string error = Refused(GridLayout.Replace("\"columns\":[{\"size\":280},{\"star\":2}]", nine), "layout.main.columns");
        Assert.Contains($"1 to {SkinFormat.MaxLayoutTracks}", error, StringComparison.Ordinal);
        string eight = "\"columns\":[" + string.Join(",", Enumerable.Repeat("\"auto\"", SkinFormat.MaxLayoutTracks)) + "]";
        Taken(GridLayout.Replace("\"columns\":[{\"size\":280},{\"star\":2}]", eight));
    }

    [Fact]
    public void AGridChildStaysInsideTheGridAndOffItsNeighbours()
    {
        Refused(GridLayout.Replace("\"row\":3,\"column\":0", "\"row\":4,\"column\":0"), "layout.main.children[5].row");
        Refused(GridLayout.Replace("\"row\":1,\"column\":0}", "\"row\":1,\"column\":2}"), "layout.main.children[1].column");
        Refused(GridLayout.Replace("\"row\":1,\"column\":0}", "\"row\":-1,\"column\":0}"), "layout.main.children[1].row");
        Refused(GridLayout.Replace("\"row\":1,\"column\":0}", "\"row\":1.5,\"column\":0}"), "layout.main.children[1].row");
        Refused(GridLayout.Replace("\"row\":1,\"column\":0}", "\"column\":0}"), "layout.main.children[1]");
        // A span past the last track, or of none.
        Refused(GridLayout.Replace("\"row\":0,\"column\":0,\"columnSpan\":2", "\"row\":0,\"column\":0,\"columnSpan\":3"),
            "layout.main.children[0].columnSpan");
        Refused(GridLayout.Replace("\"row\":0,\"column\":0,\"columnSpan\":2", "\"row\":0,\"column\":1,\"columnSpan\":2"),
            "layout.main.children[0].columnSpan");
        Refused(GridLayout.Replace("\"row\":0,\"column\":0,\"columnSpan\":2", "\"row\":0,\"column\":0,\"columnSpan\":0"),
            "layout.main.children[0].columnSpan");
        Refused(GridLayout.Replace("\"row\":3,\"column\":0,\"columnSpan\":2", "\"row\":3,\"column\":0,\"rowSpan\":2"),
            "layout.main.children[5].rowSpan");

        // Two children over one cell; the second is the one named.
        string overlap = GridLayout.Replace("\"region\":\"inputs\",\"row\":1,\"column\":0", "\"region\":\"inputs\",\"row\":1,\"column\":0,\"rowSpan\":2");
        string error = Refused(overlap, "layout.main.children[3]");
        Assert.Contains("row 2, column 0", error, StringComparison.Ordinal);
        Assert.Contains("layout.main.children[1]", error, StringComparison.Ordinal);
        Refused(GridLayout.Replace("\"row\":2,\"column\":1", "\"row\":0,\"column\":1"), "layout.main.children[4]");

        // A cell left empty is allowed.
        Taken(GridLayout.Replace("\"rows\":[\"auto\",{\"star\":1},\"auto\",\"auto\"]", "\"rows\":[\"auto\",{\"star\":1},\"auto\",\"auto\",\"auto\"]"));
    }

    [Fact]
    public void ADockFillsWithItsLastChildAlone()
    {
        Refused(DockLayout.Replace("\"dock\":\"fill\"", "\"dock\":\"top\""), "layout.main.children[4].dock");
        Refused(DockLayout.Replace("\"dock\":\"left\"", "\"dock\":\"fill\""), "layout.main.children[1].dock");
        Refused(DockLayout.Replace("\"dock\":\"left\"", "\"dock\":\"middle\""), "layout.main.children[1].dock");
        Refused(DockLayout.Replace("\"dock\":\"left\"", "\"dock\":true"), "layout.main.children[1].dock");
        Refused(DockLayout.Replace(",\"dock\":\"left\"", ""), "layout.main.children[1]");
        Refused(WithMain("{\"kind\":\"dock\",\"id\":\"d\",\"children\":[]}"), "layout.main.children");
    }

    [Fact]
    public void AContainerHoldsAtMostSixteenChildrenAndATreeSixtyFourNodes()
    {
        string seventeen = WithMain(StackNode("main", "vertical", Enumerable.Repeat(Region("inputs"), SkinFormat.MaxLayoutChildren + 1)));
        string error = Refused(seventeen, "layout.main.children");
        Assert.Contains($"at most {SkinFormat.MaxLayoutChildren} children", error, StringComparison.Ordinal);

        // Empty stacks are allowed, so a tree of them reaches the node bound:
        // main, then four stacks of fifteen.
        int count = 0;
        string Empty() => StackNode("s" + count++, "vertical", []);
        string Row() => StackNode("r" + count++, "vertical", Enumerable.Range(0, 15).Select(_ => Empty()));
        string big = WithMain(StackNode("main", "vertical", Enumerable.Range(0, 4).Select(_ => Row())));
        string nodes = Refused(big, "layout.main.children[3].children[14]");
        Assert.Contains($"at most {SkinFormat.MaxLayoutNodes} nodes", nodes, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadRegionOptionIsRefused()
    {
        Refused("{\"version\":1,\"regions\":{\"mixer\":{}}}", "layout.regions.mixer");
        Refused("{\"version\":1,\"regions\":{\"inputs\":{\"template\":\"rack\"}}}", "layout.regions.inputs.template");
        Refused("{\"version\":1,\"regions\":{\"inputs\":{\"actions\":[]}}}", "layout.regions.inputs.actions");
        Refused("{\"version\":1,\"regions\":{\"header\":{\"visibility\":\"hidden\"}}}", "layout.regions.header.visibility");
        Refused("{\"version\":1,\"regions\":{\"header\":{\"width\":400}}}", "layout.regions.header.width");
        Refused("{\"version\":1,\"regions\":{\"inputs\":{\"visibility\":\"gone\"}}}", "layout.regions.inputs.visibility");
        foreach (string size in new[] { "119", "4001", "1e999", "\"300\"" })
        {
            Refused("{\"version\":1,\"regions\":{\"inputs\":{\"width\":" + size + "}}}", "layout.regions.inputs.width");
            Refused("{\"version\":1,\"regions\":{\"monitor\":{\"height\":" + size + "}}}", "layout.regions.monitor.height");
        }
        Taken("{\"version\":1,\"regions\":{\"inputs\":{\"width\":120,\"height\":4000}}}");
    }

    [Fact]
    public void ABadHeaderActionIsRefused()
    {
        string Header(string body) => "{\"version\":1,\"regions\":{\"header\":{" + body + "}}}";
        Refused(Header("\"actions\":[\"settings\"]"), "layout.regions.header.actions[0]");
        Refused(Header("\"actions\":[3]"), "layout.regions.header.actions[0]");
        Refused(Header("\"actions\":\"flow\""), "layout.regions.header.actions");
        Assert.Contains("Options is always first",
            Refused(Header("\"actions\":[\"flow\",\"options\"]"), "layout.regions.header.actions[1]"), StringComparison.Ordinal);
        Refused(Header("\"overflow\":[\"options\"]"), "layout.regions.header.overflow[0]");
        Refused(Header("\"actions\":[\"flow\",\"flow\"]"), "layout.regions.header.actions[1]");
        Refused(Header("\"actions\":[\"flow\"],\"overflow\":[\"about\",\"flow\"]"), "layout.regions.header.overflow[1]");
        SkinLayout every = Taken(Header("\"overflow\":[" + string.Join(",", SkinHeaderActions.All.Select(a => $"\"{a}\"")) + "]"));
        Assert.Empty(every.Header.Row);
        Assert.Equal(SkinHeaderActions.All, every.Header.Overflow);
    }

    [Fact]
    public void AFaultInTheLayoutLeavesTheOtherComplaintsAsTheyWere()
    {
        SkinReadResult result = SkinReader.Read("test",
            "{\"schema\":1,\"tokens\":{\"Ox.Card.Background\":\"#123456\",\"Ox.Nonsense\":\"#fff\"},"
            + "\"layout\":{\"version\":1,\"main\":{\"kind\":\"canvas\"}}}", SkinOrigin.BuiltIn, null);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.StartsWith("\"Ox.Nonsense\"", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.StartsWith("layout.main.kind: ", StringComparison.Ordinal));
        Assert.Single(result.Package!.Tokens);
    }

    [Fact]
    public void AControlCharacterInAPropertyNameIsNotRepeatedInTheMessage()
    {
        SkinReadResult result = Read(Stack("vertical", Classic, ",\"b\\u0007ad\":1"));
        string error = Assert.Single(result.Errors);
        Assert.StartsWith("layout.main.bad: ", error, StringComparison.Ordinal);
        Assert.DoesNotContain(error, char.IsControl);
    }

    private static string Regions(string body) => "{\"version\":1,\"regions\":{" + body + "}}";

    [Fact]
    public void WithoutTemplatesTheSectionsKeepTheClassicOnes()
    {
        SkinLayout layout = Taken(Regions("\"inputs\":{\"width\":300},\"submixer\":{\"visibility\":\"collapsed\"}"));
        Assert.Equal(SkinLayoutTemplates.HardwarePanel, layout.Inputs.Template);
        Assert.False(layout.Inputs.IsRack);
        Assert.Equal(SkinLayoutTemplates.ChannelTiles, layout.Submixer.Template);
        Assert.False(layout.Submixer.IsBanks);
        Assert.Same(SkinInputsOptions.Default, SkinLayoutDefaults.Classic.Inputs);
        Assert.Same(SkinSubmixerOptions.Default, SkinLayoutDefaults.Classic.Submixer);
        Assert.Equal([SkinLayoutTemplates.HardwarePanel, SkinLayoutTemplates.InputRack], SkinLayoutTemplates.Inputs);
        Assert.Equal([SkinLayoutTemplates.ChannelTiles, SkinLayoutTemplates.MixBanks, SkinLayoutTemplates.MixMatrix], SkinLayoutTemplates.Submixer);
        Assert.False(layout.Submixer.IsMatrix);
        Assert.Same(SkinMatrixOptions.Default, layout.Submixer.Matrix);
        Assert.Equal(["identity", "binding", "meter", "gain", "hardware-controls", "effects"], SkinStripParts.InputRow);
        Assert.Equal(["identity", "meter", "fader", "value", "mute"], SkinStripParts.Send);
    }

    [Fact]
    public void AnInputRackReadsItsStripPartsLabelAndMeter()
    {
        SkinLayout layout = Taken(Regions("""
            "inputs":{"template":"input-rack","strip":{"template":"input-row","partsOrder":["gain","identity"],
              "hidden":["effects","meter"],"label":{"style":"icon-and-text"},"meter":{"orientation":"vertical","placement":"beside-fader"}}}
            """));
        Assert.True(layout.Inputs.IsRack);
        SkinStripOptions strip = layout.Inputs.Strip;
        Assert.Equal(SkinLayoutTemplates.InputRow, strip.Template);
        Assert.Equal(["gain", "identity", "binding", "meter", "hardware-controls", "effects"], strip.Order);
        Assert.Equal(["effects", "meter"], strip.Hidden);
        Assert.Equal(["gain", "identity", "binding", "hardware-controls"], strip.Shown);
        Assert.Equal(SkinLabelStyle.IconAndText, strip.Label);
        Assert.Equal(new SkinMeterOptions(SkinLayoutOrientation.Vertical, SkinMeterPlacement.BesideFader), strip.Meter);

        // A rack without a strip takes the default row: every part, flat, the meter under the name.
        SkinStripOptions plain = Taken(Regions("\"inputs\":{\"template\":\"input-rack\"}")).Inputs.Strip;
        Assert.Same(SkinStripOptions.InputRowDefault, plain);
        Assert.Equal(SkinStripParts.InputRow, plain.Shown);
        Assert.Equal(SkinMeterOptions.Default, plain.Meter);
    }

    [Fact]
    public void MixBanksReadTheirOrientationCountDefaultsStripAndMaster()
    {
        SkinLayout layout = Taken(Regions("""
            "submixer":{"template":"mix-banks","orientation":"vertical","visibleBanks":3,"selectionDefaults":["monitor","stream","my_mix-2"],
              "strip":{"template":"send-list","partsOrder":["mute","fader"],"hidden":["value"],"label":{"style":"icon"},
                       "fader":{"orientation":"vertical","direction":"reversed"},"meter":{"placement":"beside-label"}},
              "master":{"template":"master-strip","label":{"style":"text"},"fader":{"direction":"reversed"}}}
            """));
        SkinSubmixerOptions banks = layout.Submixer;
        Assert.True(banks.IsBanks);
        Assert.Equal(SkinLayoutOrientation.Vertical, banks.Orientation);
        Assert.Equal(3, banks.VisibleBanks);
        Assert.Equal(["monitor", "stream", "my_mix-2"], banks.SelectionDefaults);
        Assert.Equal(SkinLayoutTemplates.SendList, banks.Strip.Template);
        Assert.Equal(["mute", "fader", "identity", "meter"], banks.Strip.Shown);
        Assert.Equal(SkinLabelStyle.Icon, banks.Strip.Label);
        Assert.Equal(new SkinFaderOptions(SkinLayoutOrientation.Vertical, SkinFaderDirection.Reversed), banks.Strip.Fader);
        // A send list lies its meter flat unless told otherwise.
        Assert.Equal(new SkinMeterOptions(SkinLayoutOrientation.Horizontal, SkinMeterPlacement.BesideLabel), banks.Strip.Meter);
        Assert.Equal(SkinLayoutTemplates.MasterStrip, banks.Master.Template);
        // A master strip stands its fader and meter up unless told otherwise.
        Assert.Equal(new SkinFaderOptions(SkinLayoutOrientation.Vertical, SkinFaderDirection.Reversed), banks.Master.Fader);
        Assert.Equal(new SkinMeterOptions(SkinLayoutOrientation.Vertical, SkinMeterPlacement.BesideFader), banks.Master.Meter);

        // Banks with nothing else said: two, side by side, upright strips, the master card.
        SkinSubmixerOptions plain = Taken(Regions("\"submixer\":{\"template\":\"mix-banks\"}")).Submixer;
        Assert.Equal(SkinSubmixerOptions.DefaultVisibleBanks, plain.VisibleBanks);
        Assert.Equal(2, plain.VisibleBanks);
        Assert.Equal(SkinLayoutOrientation.Horizontal, plain.Orientation);
        Assert.Empty(plain.SelectionDefaults);
        Assert.Same(SkinStripOptions.BankDefault, plain.Strip);
        Assert.Equal(new SkinFaderOptions(SkinLayoutOrientation.Vertical), plain.Strip.Fader);
        Assert.Same(SkinMasterOptions.Default, plain.Master);
        Assert.Equal(SkinFaderOptions.Default, plain.Master.Fader);
    }

    [Fact]
    public void TheTemplatesAreInTheLayoutDescription()
    {
        string Described(string body) => Taken(Regions(body)).Description();
        string classic = SkinLayoutDefaults.Classic.Description();
        Assert.Equal(classic, Described("\"inputs\":{\"template\":\"hardware-panel\"}"));
        string rack = Described("\"inputs\":{\"template\":\"input-rack\"}");
        Assert.NotEqual(classic, rack);
        Assert.NotEqual(rack, Described("\"inputs\":{\"template\":\"input-rack\",\"strip\":{\"partsOrder\":[\"meter\"]}}"));
        Assert.NotEqual(rack, Described("\"inputs\":{\"template\":\"input-rack\",\"strip\":{\"label\":{\"style\":\"icon\"}}}"));
        string banks = Described("\"submixer\":{\"template\":\"mix-banks\"}");
        Assert.NotEqual(classic, banks);
        foreach (string change in new[]
                 {
                     "\"visibleBanks\":3", "\"orientation\":\"vertical\"", "\"selectionDefaults\":[\"chat\"]",
                     "\"strip\":{\"template\":\"send-list\"}", "\"strip\":{\"hidden\":[\"value\"]}",
                     "\"strip\":{\"fader\":{\"direction\":\"reversed\"}}", "\"strip\":{\"meter\":{\"placement\":\"below-label\"}}",
                     "\"master\":{\"template\":\"master-strip\"}", "\"master\":{\"label\":{\"style\":\"icon\"}}",
                 })
            Assert.NotEqual(banks, Described("\"submixer\":{\"template\":\"mix-banks\"," + change + "}"));
        // The same banks written two ways describe alike.
        Assert.Equal(banks, Described("\"submixer\":{\"template\":\"mix-banks\",\"visibleBanks\":2,\"strip\":{\"template\":\"vertical-strip\"}}"));
    }

    [Fact]
    public void ABadTemplateNameIsRefusedWithItsPath()
    {
        Refused(Regions("\"inputs\":{\"template\":\"mixer\"}"), "layout.regions.inputs.template");
        Refused(Regions("\"inputs\":{\"template\":3}"), "layout.regions.inputs.template");
        Refused(Regions("\"submixer\":{\"template\":\"send-bank\"}"), "layout.regions.submixer.template");
        Refused(Regions("\"headphones\":{\"template\":\"input-rack\"}"), "layout.regions.headphones.template");
        Refused(Regions("\"inputs\":{\"template\":\"input-rack\",\"strip\":{\"template\":\"vertical-strip\"}}"),
            "layout.regions.inputs.strip.template");
        Refused(Regions("\"submixer\":{\"template\":\"mix-banks\",\"strip\":{\"template\":\"input-row\"}}"),
            "layout.regions.submixer.strip.template");
        Refused(Regions("\"submixer\":{\"template\":\"mix-banks\",\"master\":{\"template\":\"master-tile\"}}"),
            "layout.regions.submixer.master.template");
    }

    [Fact]
    public void AnOptionOfAnotherTemplateIsRefused()
    {
        string strip = Refused(Regions("\"inputs\":{\"strip\":{}}"), "layout.regions.inputs.strip");
        Assert.Contains("input-rack", strip, StringComparison.Ordinal);
        Refused(Regions("\"inputs\":{\"template\":\"hardware-panel\",\"strip\":{}}"), "layout.regions.inputs.strip");
        foreach (string option in new[] { "orientation", "visibleBanks", "selectionDefaults", "strip", "master", "picker" })
            Assert.Contains("mix-banks", Refused(Regions("\"submixer\":{\"template\":\"channel-tiles\",\"" + option + "\":{}}"),
                "layout.regions.submixer." + option), StringComparison.Ordinal);
        Refused(Regions("\"monitor\":{\"strip\":{}}"), "layout.regions.monitor.strip");
        Refused(Regions("\"inputs\":{\"template\":\"input-rack\",\"visibleBanks\":2}"), "layout.regions.inputs.visibleBanks");
        // An input row has no fader of its own; its gain is the interface's.
        Refused(Regions("\"inputs\":{\"template\":\"input-rack\",\"strip\":{\"fader\":{}}}"), "layout.regions.inputs.strip.fader");
        Refused(Regions("\"submixer\":{\"template\":\"mix-banks\",\"master\":{\"partsOrder\":[]}}"), "layout.regions.submixer.master.partsOrder");
        Refused(Regions("\"submixer\":{\"template\":\"mix-banks\",\"strip\":{\"label\":{\"wrap\":true}}}"), "layout.regions.submixer.strip.label.wrap");
        Refused(Regions("\"submixer\":{\"template\":\"mix-banks\",\"strip\":{\"meter\":{\"colour\":\"red\"}}}"), "layout.regions.submixer.strip.meter.colour");
        Refused(Regions("\"submixer\":{\"template\":\"mix-banks\",\"strip\":{\"fader\":{\"length\":200}}}"), "layout.regions.submixer.strip.fader.length");
    }

    [Fact]
    public void PartsAreKnownListedOnceAndIdentityIsNeverHidden()
    {
        string Rack(string strip) => Regions("\"inputs\":{\"template\":\"input-rack\",\"strip\":{" + strip + "}}");
        string Bank(string strip) => Regions("\"submixer\":{\"template\":\"mix-banks\",\"strip\":{" + strip + "}}");
        Refused(Rack("\"partsOrder\":[\"fader\"]"), "layout.regions.inputs.strip.partsOrder[0]");
        Refused(Bank("\"partsOrder\":[\"gain\"]"), "layout.regions.submixer.strip.partsOrder[0]");
        Refused(Bank("\"partsOrder\":[\"meter\",3]"), "layout.regions.submixer.strip.partsOrder[1]");
        Refused(Bank("\"partsOrder\":\"meter\""), "layout.regions.submixer.strip.partsOrder");
        Refused(Bank("\"partsOrder\":[\"meter\",\"meter\"]"), "layout.regions.submixer.strip.partsOrder[1]");
        Assert.Contains("identity", Refused(Rack("\"hidden\":[\"identity\"]"), "layout.regions.inputs.strip.hidden[0]"), StringComparison.Ordinal);
        Refused(Bank("\"partsOrder\":[\"value\"],\"hidden\":[\"value\"]"), "layout.regions.submixer.strip.hidden[0]");
        // Never more entries than the strip has parts.
        string six = "[\"identity\",\"meter\",\"fader\",\"value\",\"mute\",\"meter\"]";
        Assert.Contains("at most 5", Refused(Bank("\"partsOrder\":" + six), "layout.regions.submixer.strip.partsOrder"), StringComparison.Ordinal);
        Refused(Bank("\"hidden\":" + six), "layout.regions.submixer.strip.hidden");
        // Every part but identity may be hidden.
        SkinStripOptions bare = Taken(Bank("\"hidden\":[\"meter\",\"fader\",\"value\",\"mute\"]")).Submixer.Strip;
        Assert.Equal(["identity"], bare.Shown);
        SkinStripOptions all = Taken(Bank("\"partsOrder\":[\"mute\",\"value\",\"fader\",\"meter\",\"identity\"]")).Submixer.Strip;
        Assert.Equal(["mute", "value", "fader", "meter", "identity"], all.Shown);
    }

    [Fact]
    public void LabelMeterAndFaderTakeOnlyTheirNames()
    {
        string Bank(string strip) => Regions("\"submixer\":{\"template\":\"mix-banks\",\"strip\":{" + strip + "}}");
        Refused(Bank("\"label\":{\"style\":\"bold\"}"), "layout.regions.submixer.strip.label.style");
        Refused(Bank("\"label\":\"icon\""), "layout.regions.submixer.strip.label");
        Refused(Bank("\"meter\":{\"orientation\":\"diagonal\"}"), "layout.regions.submixer.strip.meter.orientation");
        Refused(Bank("\"meter\":{\"placement\":\"above-label\"}"), "layout.regions.submixer.strip.meter.placement");
        Refused(Bank("\"fader\":{\"orientation\":\"Vertical\"}"), "layout.regions.submixer.strip.fader.orientation");
        Refused(Bank("\"fader\":{\"direction\":\"up\"}"), "layout.regions.submixer.strip.fader.direction");
        Refused(Regions("\"submixer\":{\"template\":\"mix-banks\",\"orientation\":\"grid\"}"), "layout.regions.submixer.orientation");
        Refused(Regions("\"submixer\":{\"template\":\"mix-banks\",\"master\":{\"fader\":{\"direction\":1}}}"),
            "layout.regions.submixer.master.fader.direction");
        Taken(Bank("\"label\":{},\"meter\":{},\"fader\":{}"));
    }

    [Fact]
    public void ABankPicksWithADropdownOrWithKeys()
    {
        string Banks(string option) => Regions("\"submixer\":{\"template\":\"mix-banks\"" + option + "}");
        SkinSubmixerOptions dropdown = Taken(Banks("")).Submixer;
        Assert.Equal(SkinLayoutTemplates.PickerDropdown, dropdown.Picker);
        Assert.False(dropdown.PickerButtons);
        Assert.Equal(SkinLayoutTemplates.PickerDropdown, Taken(Banks(",\"picker\":\"dropdown\"")).Submixer.Picker);
        SkinSubmixerOptions buttons = Taken(Banks(",\"picker\":\"buttons\"")).Submixer;
        Assert.True(buttons.PickerButtons);
        Assert.NotEqual(dropdown.Description(), buttons.Description());
        foreach (string value in new[] { "\"keys\"", "\"Buttons\"", "true", "1", "[\"buttons\"]" })
            Assert.Contains("buttons", Refused(Banks(",\"picker\":" + value), "layout.regions.submixer.picker"), StringComparison.Ordinal);
        Refused(Regions("\"submixer\":{\"template\":\"mix-matrix\",\"picker\":\"buttons\"}"), "layout.regions.submixer.picker");
    }

    [Fact]
    public void TheBankCountAndTheSelectionDefaultsAreBounded()
    {
        string Banks(string option) => Regions("\"submixer\":{\"template\":\"mix-banks\"," + option + "}");
        foreach (string count in new[] { "0", "5", "2.5", "\"2\"" })
            Refused(Banks("\"visibleBanks\":" + count), "layout.regions.submixer.visibleBanks");
        Assert.Equal(SkinFormat.MinVisibleBanks, Taken(Banks("\"visibleBanks\":1")).Submixer.VisibleBanks);
        Assert.Equal(SkinFormat.MaxVisibleBanks, Taken(Banks("\"visibleBanks\":4")).Submixer.VisibleBanks);

        Refused(Banks("\"selectionDefaults\":\"monitor\""), "layout.regions.submixer.selectionDefaults");
        foreach (string id in new[] { "\"\"", "\"Monitor\"", "\"mix one\"", "\"mix.one\"", "3", "\"" + new string('a', 65) + "\"" })
            Refused(Banks("\"selectionDefaults\":[\"monitor\"," + id + "]"), "layout.regions.submixer.selectionDefaults[1]");
        Refused(Banks("\"selectionDefaults\":[\"monitor\",\"monitor\"]"), "layout.regions.submixer.selectionDefaults[1]");
        string many = "[" + string.Join(",", Enumerable.Range(0, SkinFormat.MaxSelectionDefaults + 1).Select(i => $"\"mix{i}\"")) + "]";
        Refused(Banks("\"selectionDefaults\":" + many), "layout.regions.submixer.selectionDefaults");
        string most = "[" + string.Join(",", Enumerable.Range(0, SkinFormat.MaxSelectionDefaults).Select(i => $"\"mix{i}\"")) + "]";
        Assert.Equal(SkinFormat.MaxSelectionDefaults, Taken(Banks("\"selectionDefaults\":" + most)).Submixer.SelectionDefaults.Count);
        // An id may name a mix the user has since deleted; the window skips it.
        Assert.Equal(["gone", new string('a', 64)], Taken(Banks("\"selectionDefaults\":[\"gone\",\"" + new string('a', 64) + "\"]")).Submixer.SelectionDefaults);
        Assert.True(SkinLayoutReader.ValidMixId("monitor2"));
        Assert.False(SkinLayoutReader.ValidMixId(null));
        Assert.False(SkinLayoutReader.ValidMixId("Monitor"));
    }

    [Fact]
    public void ARepeatedKeyInsideATemplateOptionIsRefused()
    {
        Refused(Regions("\"submixer\":{\"template\":\"mix-banks\",\"strip\":{\"label\":{\"style\":\"icon\",\"style\":\"text\"}}}"),
            "layout.regions.submixer.strip.label.style");
        Refused(Regions("\"inputs\":{\"template\":\"input-rack\",\"template\":\"hardware-panel\"}"), "layout.regions.inputs.template");
    }

    [Fact]
    public void ARegionsNarrowestWidthFollowsItsTemplate()
    {
        var sizes = new RegionSizes(StripWidth: 76, MixWidth: 232, BankSpacing: 12, Banks: 2, VisibleChannels: 6);
        // The classic templates, the header and a skin without a layout keep the classic width.
        Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth, SkinLayoutDefaults.RegionMinWidth("inputs", null, sizes));
        Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth, SkinLayoutDefaults.RegionMinWidth("inputs", SkinLayoutDefaults.Classic, sizes));
        Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth, SkinLayoutDefaults.RegionMinWidth("submixer", SkinLayoutDefaults.Classic, sizes));
        SkinLayout rack = Taken(Regions("\"inputs\":{\"template\":\"input-rack\"},\"submixer\":{\"template\":\"mix-banks\"}"));
        Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth, SkinLayoutDefaults.RegionMinWidth("header", rack, sizes));
        Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth, SkinLayoutDefaults.RegionMinWidth("monitor", rack, sizes));
        // An input rack: three strip widths and the card's padding.
        Assert.Equal(3 * 76 + SkinLayoutDefaults.CardPadding, SkinLayoutDefaults.RegionMinWidth("inputs", rack, sizes));
        Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth,
            SkinLayoutDefaults.RegionMinWidth("inputs", rack, sizes with { StripWidth = 200 }));
        // Upright banks with the master card: each as wide as the card, side by side with the gap.
        double card = 232 + SkinLayoutDefaults.BankPadding;
        Assert.Equal(Math.Min(600, 2 * card + 12 + SkinLayoutDefaults.CardPadding), SkinLayoutDefaults.RegionMinWidth("submixer", rack, sizes));
        Assert.Equal(card + SkinLayoutDefaults.CardPadding, SkinLayoutDefaults.RegionMinWidth("submixer", rack, sizes with { Banks = 1 }));
        // Master strips: one channel strip and the master strip, then the rest scroll.
        SkinLayout strips = Taken(Regions("\"submixer\":{\"template\":\"mix-banks\",\"master\":{\"template\":\"master-strip\"}}"));
        double bank = 2 * 76 + SkinLayoutDefaults.StripSpacing + SkinLayoutDefaults.BankPadding;
        Assert.Equal(2 * bank + 12 + SkinLayoutDefaults.CardPadding, SkinLayoutDefaults.RegionMinWidth("submixer", strips, sizes));
        Assert.Equal(76 + SkinLayoutDefaults.BankPadding + SkinLayoutDefaults.CardPadding,
            SkinLayoutDefaults.RegionMinWidth("submixer", strips, sizes with { Banks = 1, VisibleChannels = 0 }));
        // Banks one above the other need one bank's width.
        SkinLayout column = Taken(Regions("\"submixer\":{\"template\":\"mix-banks\",\"orientation\":\"vertical\",\"master\":{\"template\":\"master-strip\"}}"));
        Assert.Equal(bank + SkinLayoutDefaults.CardPadding, SkinLayoutDefaults.RegionMinWidth("submixer", column, sizes with { Banks = 4 }));
        // A send list: three strip widths for its rows.
        SkinLayout list = Taken(Regions("\"submixer\":{\"template\":\"mix-banks\",\"visibleBanks\":1,\"strip\":{\"template\":\"send-list\"},\"master\":{\"template\":\"master-strip\"}}"));
        Assert.Equal(3 * 76 + SkinLayoutDefaults.BankPadding + SkinLayoutDefaults.CardPadding,
            SkinLayoutDefaults.RegionMinWidth("submixer", list, sizes with { Banks = 1 }));
        // Never more than a classic section.
        Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth, SkinLayoutDefaults.RegionMinWidth("submixer", rack, sizes with { Banks = 4 }));
    }

    [Fact]
    public void MonitorAndApplicationsReadTheirTemplates()
    {
        SkinLayout classic = Taken(Regions("\"monitor\":{\"width\":300},\"applications\":{\"visibility\":\"collapsed\"}"));
        Assert.Equal(SkinLayoutTemplates.OutputPanel, classic.Monitor);
        Assert.Equal(SkinLayoutTemplates.ApplicationChips, classic.Applications);
        Assert.Equal(SkinLayoutTemplates.OutputPanel, SkinLayoutDefaults.Classic.Monitor);
        Assert.Equal(SkinLayoutTemplates.ApplicationChips, SkinLayoutDefaults.Classic.Applications);
        Assert.Equal([SkinLayoutTemplates.OutputPanel, SkinLayoutTemplates.OutputToolbar], SkinLayoutTemplates.Monitor);
        Assert.Equal([SkinLayoutTemplates.ApplicationChips, SkinLayoutTemplates.ApplicationList, SkinLayoutTemplates.ApplicationColumns],
            SkinLayoutTemplates.Applications);

        SkinLayout compact = Taken(Regions("""
            "monitor":{"template":"output-toolbar","visibility":"collapsed"},"applications":{"template":"application-list","width":280}
            """));
        Assert.Equal(SkinLayoutTemplates.OutputToolbar, compact.Monitor);
        Assert.Equal(SkinLayoutTemplates.ApplicationList, compact.Applications);
        // The options every section has still apply beside the template.
        Assert.Equal(SkinRegionVisibility.Collapsed, compact.OptionsFor("monitor").Visibility);
        Assert.Equal(280, compact.OptionsFor("applications").Width);
        // Each template is a structural fact: two layouts that differ only in one draw differently.
        Assert.NotEqual(classic.Description(), compact.Description());
        Assert.NotEqual(Taken(Regions("\"monitor\":{\"template\":\"output-toolbar\"}")).Description(),
            Taken(Regions("\"applications\":{\"template\":\"application-list\"}")).Description());
        Assert.Equal(Taken(Regions("\"monitor\":{\"template\":\"output-panel\"}")).Description(),
            Taken(Regions("\"monitor\":{}")).Description());
    }

    [Fact]
    public void ApplicationColumnsReadHowManyApplicationsAColumnHolds()
    {
        SkinLayout columns = Taken(Regions("\"applications\":{\"template\":\"application-columns\",\"perColumn\":4}"));
        Assert.Equal(SkinLayoutTemplates.ApplicationColumns, columns.Applications);
        Assert.Equal(4, columns.ApplicationsPerColumn);
        // Six when the skin leaves it out, and the same for every other template.
        Assert.Equal(SkinFormat.DefaultApplicationsPerColumn,
            Taken(Regions("\"applications\":{\"template\":\"application-columns\"}")).ApplicationsPerColumn);
        Assert.Equal(6, SkinFormat.DefaultApplicationsPerColumn);
        Assert.Equal(SkinFormat.DefaultApplicationsPerColumn, SkinLayoutDefaults.Classic.ApplicationsPerColumn);
        foreach (int bound in new[] { SkinFormat.MinApplicationsPerColumn, SkinFormat.MaxApplicationsPerColumn })
            Assert.Equal(bound, Taken(Regions($"\"applications\":{{\"template\":\"application-columns\",\"perColumn\":{bound}}}")).ApplicationsPerColumn);
        // The length is structure: two lengths draw differently, the chips and the columns too.
        Assert.NotEqual(columns.Description(),
            Taken(Regions("\"applications\":{\"template\":\"application-columns\",\"perColumn\":5}")).Description());
        Assert.NotEqual(columns.Description(), Taken(Regions("\"applications\":{}")).Description());
        // Columns are the chips again, so they need no more room than the chips.
        var sizes = new RegionSizes(StripWidth: 76, MixWidth: 232, BankSpacing: 12, Banks: 2, VisibleChannels: 6);
        Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth, SkinLayoutDefaults.RegionMinWidth("applications", columns, sizes));

        foreach (string value in new[] { "0", "25", "-1", "2.5", "\"6\"", "null" })
            Refused(Regions($"\"applications\":{{\"template\":\"application-columns\",\"perColumn\":{value}}}"),
                "layout.regions.applications.perColumn");
        // perColumn belongs to the columns template alone.
        Assert.Contains("\"perColumn\" belongs to the \"application-columns\" template",
            Refused(Regions("\"applications\":{\"template\":\"application-list\",\"perColumn\":6}"), "layout.regions.applications.perColumn"),
            StringComparison.Ordinal);
        Refused(Regions("\"applications\":{\"perColumn\":6}"), "layout.regions.applications.perColumn");
        Refused(Regions("\"monitor\":{\"template\":\"output-toolbar\",\"perColumn\":6}"), "layout.regions.monitor.perColumn");
    }

    [Fact]
    public void AnUnknownMonitorOrApplicationsTemplateOrOptionIsRefused()
    {
        Assert.Contains("\"output-strip\" is not a MONITOR template",
            Refused(Regions("\"monitor\":{\"template\":\"output-strip\"}"), "layout.regions.monitor.template"), StringComparison.Ordinal);
        Refused(Regions("\"monitor\":{\"template\":2}"), "layout.regions.monitor.template");
        Assert.Contains("\"application-chips\", \"application-list\" or \"application-columns\"",
            Refused(Regions("\"applications\":{\"template\":\"application-grid\"}"), "layout.regions.applications.template"),
            StringComparison.Ordinal);
        // A template of another region is not one of this region's.
        Refused(Regions("\"monitor\":{\"template\":\"application-list\"}"), "layout.regions.monitor.template");
        Refused(Regions("\"applications\":{\"template\":\"input-rack\"}"), "layout.regions.applications.template");
        // An option of another template: neither MONITOR nor APPLICATIONS template takes strips, banks or a pop-out default.
        Refused(Regions("\"monitor\":{\"template\":\"output-toolbar\",\"strip\":{}}"), "layout.regions.monitor.strip");
        Refused(Regions("\"applications\":{\"template\":\"application-list\",\"orientation\":\"vertical\"}"),
            "layout.regions.applications.orientation");
        Refused(Regions("\"monitor\":{\"popOut\":{\"default\":\"detached\"}}"), "layout.regions.monitor.popOut");
        // HEADPHONES has one way of being drawn, so no template at all.
        Refused(Regions("\"headphones\":{\"template\":\"output-panel\"}"), "layout.regions.headphones.template");
        Refused(Regions("\"monitor\":{\"template\":\"output-toolbar\",\"template\":\"output-panel\"}"), "layout.regions.monitor.template");
    }

    [Fact]
    public void TheSubmixerReadsItsPopOutDefault()
    {
        Assert.Equal(SkinMixerPlacement.Docked, SkinLayoutDefaults.Classic.PopOut);
        Assert.Equal(SkinMixerPlacement.Docked, Taken(Regions("\"submixer\":{\"visibility\":\"visible\"}")).PopOut);
        Assert.Equal([SkinPopOutDefaults.Docked, SkinPopOutDefaults.Detached], SkinPopOutDefaults.All);
        SkinLayout detached = Taken(Regions("\"submixer\":{\"popOut\":{\"default\":\"detached\"}}"));
        Assert.Equal(SkinMixerPlacement.Detached, detached.PopOut);
        // The pop-out default belongs to the submixer whatever template it wears.
        Assert.Equal(SkinMixerPlacement.Detached,
            Taken(Regions("\"submixer\":{\"template\":\"mix-banks\",\"popOut\":{\"default\":\"detached\"}}")).PopOut);
        SkinLayout docked = Taken(Regions("\"submixer\":{\"popOut\":{\"default\":\"docked\"}}"));
        Assert.Equal(SkinMixerPlacement.Docked, docked.PopOut);
        Assert.NotEqual(detached.Description(), docked.Description());
        // Bank options stay refused on the classic tiles with a pop-out default beside them.
        Refused(Regions("\"submixer\":{\"popOut\":{\"default\":\"detached\"},\"visibleBanks\":2}"), "layout.regions.submixer.visibleBanks");
    }

    [Fact]
    public void AMalformedPopOutDefaultIsRefused()
    {
        Refused(Regions("\"submixer\":{\"popOut\":\"detached\"}"), "layout.regions.submixer.popOut");
        Assert.Contains("needs a \"default\"",
            Refused(Regions("\"submixer\":{\"popOut\":{}}"), "layout.regions.submixer.popOut"), StringComparison.Ordinal);
        Assert.Contains("\"floating\" is not a pop-out default",
            Refused(Regions("\"submixer\":{\"popOut\":{\"default\":\"floating\"}}"), "layout.regions.submixer.popOut.default"),
            StringComparison.Ordinal);
        Refused(Regions("\"submixer\":{\"popOut\":{\"default\":true}}"), "layout.regions.submixer.popOut.default");
        Refused(Regions("\"submixer\":{\"popOut\":{\"default\":\"docked\",\"screen\":1}}"), "layout.regions.submixer.popOut.screen");
        Refused(Regions("\"submixer\":{\"popOut\":{\"default\":\"docked\",\"default\":\"detached\"}}"),
            "layout.regions.submixer.popOut.default");
    }

    [Fact]
    public void TheCompactTemplatesNeedThreeStripWidthsAndBothMonitorTemplatesStackAtOneWidth()
    {
        var sizes = new RegionSizes(StripWidth: 76, MixWidth: 232, BankSpacing: 12, Banks: 2, VisibleChannels: 6);
        SkinLayout compact = Taken(Regions("\"monitor\":{\"template\":\"output-toolbar\"},\"applications\":{\"template\":\"application-list\"}"));
        Assert.Equal(3 * 76 + SkinLayoutDefaults.CardPadding, SkinLayoutDefaults.RegionMinWidth("monitor", compact, sizes));
        Assert.Equal(3 * 76 + SkinLayoutDefaults.CardPadding, SkinLayoutDefaults.RegionMinWidth("applications", compact, sizes));
        Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth, SkinLayoutDefaults.RegionMinWidth("monitor", SkinLayoutDefaults.Classic, sizes));
        Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth, SkinLayoutDefaults.RegionMinWidth("applications", SkinLayoutDefaults.Classic, sizes));
        Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth,
            SkinLayoutDefaults.RegionMinWidth("monitor", compact, sizes with { StripWidth = 300 }));
        // The stacking width sits below what a classic section leaves its controls, so a panel only stacks in a narrow window.
        Assert.Equal(560, SkinLayoutDefaults.MonitorStackWidth);
        Assert.True(SkinLayoutDefaults.MonitorStackWidth < SkinLayoutDefaults.RowSectionMinWidth - SkinLayoutDefaults.CardPadding);
        Assert.True(SkinLayoutDefaults.RegionMinWidth("monitor", compact, sizes) - SkinLayoutDefaults.CardPadding < SkinLayoutDefaults.MonitorStackWidth);
    }
    // ---- the mix matrix ----

    private static string Matrix(string options = "") =>
        Regions("\"submixer\":{\"template\":\"mix-matrix\"" + (options.Length > 0 ? "," + options : "") + "}");

    [Fact]
    public void AMatrixReadsItsHeadsCellsMutedStyleAndMeter()
    {
        SkinLayout layout = Taken(Matrix("""
            "matrix":{"rowHead":{"parts":["identity","mute","gain"],"hidden":["effects"],"label":{"style":"icon-and-text"}},
                      "columnHead":{"parts":["outputs","identity"],"hidden":["master"],"label":{"style":"icon"}},
                      "cell":{"parts":["fader","mute"],"hidden":["value"],"muted":"dim","valuePlacement":"over-fader"},
                      "meter":"none"}
            """));
        SkinSubmixerOptions submixer = layout.Submixer;
        Assert.True(submixer.IsMatrix);
        Assert.False(submixer.IsBanks);
        SkinMatrixOptions matrix = submixer.Matrix;
        Assert.Equal(["identity", "mute", "gain", "binding", "meter", "processing", "effects"], matrix.RowHead.Order);
        Assert.Equal(["identity", "mute", "gain", "binding", "meter", "processing"], matrix.RowHead.Shown);
        Assert.Equal(SkinLabelStyle.IconAndText, matrix.RowHead.Label);
        Assert.Equal(["outputs", "identity", "master", "mute", "effects"], matrix.ColumnHead.Order);
        Assert.Equal(["outputs", "identity", "mute", "effects"], matrix.ColumnHead.Shown);
        Assert.Equal(SkinLabelStyle.Icon, matrix.ColumnHead.Label);
        Assert.Equal(["fader", "mute"], matrix.Cell.Shown);
        Assert.False(matrix.Cell.Shows("value"));
        Assert.Equal(SkinMatrixMuted.Dim, matrix.Muted);
        Assert.Equal(SkinMatrixMeter.None, matrix.Meter);
        Assert.Equal(SkinMatrixValue.OverFader, matrix.Value);

        // A matrix with nothing else said: every part in its default order, muted cells tinted, the meter under the faders.
        SkinSubmixerOptions plain = Taken(Matrix()).Submixer;
        Assert.True(plain.IsMatrix);
        Assert.Same(SkinMatrixOptions.Default, plain.Matrix);
        Assert.Equal(SkinMatrixParts.RowHead, plain.Matrix.RowHead.Shown);
        Assert.Equal(SkinMatrixParts.ColumnHead, plain.Matrix.ColumnHead.Shown);
        Assert.Equal(SkinMatrixParts.Cell, plain.Matrix.Cell.Shown);
        Assert.Equal(SkinMatrixMuted.Tint, plain.Matrix.Muted);
        Assert.Equal(SkinMatrixMeter.UnderFader, plain.Matrix.Meter);
        Assert.Equal(SkinMatrixValue.Beside, plain.Matrix.Value);
        // An empty block, or empty lists, keep the defaults too.
        Assert.Equal(SkinMatrixOptions.Default.Description(),
            Taken(Matrix("\"matrix\":{\"rowHead\":{},\"columnHead\":{\"parts\":[]},\"cell\":{\"hidden\":[]}}")).Submixer.Matrix.Description());
        Assert.Equal(["identity", "binding", "meter", "gain", "mute", "processing", "effects"], SkinMatrixParts.RowHead);
        // A skin that lists every part a row head had before the microphone switches gets them last.
        Assert.Equal(["identity", "binding", "mute", "gain", "meter", "effects", "processing"],
            Taken(Matrix("\"matrix\":{\"rowHead\":{\"parts\":[\"identity\",\"binding\",\"mute\",\"gain\",\"meter\",\"effects\"]}}"))
                .Submixer.Matrix.RowHead.Shown);
        Assert.Equal(["identity", "master", "mute", "effects", "outputs"], SkinMatrixParts.ColumnHead);
        Assert.Equal(["mute", "fader", "value"], SkinMatrixParts.Cell);
    }

    [Fact]
    public void TheMatrixIsInTheLayoutDescription()
    {
        string Described(string body) => Taken(Regions(body)).Description();
        string matrix = Described("\"submixer\":{\"template\":\"mix-matrix\"}");
        Assert.NotEqual(SkinLayoutDefaults.Classic.Description(), matrix);
        Assert.NotEqual(Described("\"submixer\":{\"template\":\"mix-banks\"}"), matrix);
        foreach (string change in new[]
                 {
                     "\"rowHead\":{\"parts\":[\"mute\"]}", "\"rowHead\":{\"hidden\":[\"meter\"]}", "\"rowHead\":{\"label\":{\"style\":\"icon\"}}",
                     "\"columnHead\":{\"parts\":[\"outputs\"]}", "\"columnHead\":{\"hidden\":[\"effects\"]}",
                     "\"cell\":{\"hidden\":[\"value\"]}", "\"cell\":{\"muted\":\"dim\"}", "\"cell\":{\"valuePlacement\":\"over-fader\"}", "\"meter\":\"none\"",
                 })
            Assert.NotEqual(matrix, Described("\"submixer\":{\"template\":\"mix-matrix\",\"matrix\":{" + change + "}}"));
        // The same matrix written two ways describes alike.
        Assert.Equal(matrix, Described("\"submixer\":{\"template\":\"mix-matrix\",\"matrix\":{\"cell\":{\"muted\":\"tint\"},\"meter\":\"under-fader\"}}"));
    }

    [Fact]
    public void TheOptionsOfBanksAndOfAMatrixAreRefusedOnTheOtherTemplate()
    {
        foreach (string option in new[] { "orientation", "visibleBanks", "selectionDefaults", "strip", "master", "picker" })
            Assert.Contains("mix-banks", Refused(Matrix("\"" + option + "\":{}"), "layout.regions.submixer." + option), StringComparison.Ordinal);
        foreach (string template in new[] { "mix-banks", "channel-tiles" })
            Assert.Contains("mix-matrix", Refused(Regions("\"submixer\":{\"template\":\"" + template + "\",\"matrix\":{}}"),
                "layout.regions.submixer.matrix"), StringComparison.Ordinal);
        Refused(Regions("\"submixer\":{\"matrix\":{}}"), "layout.regions.submixer.matrix");
        Refused(Regions("\"inputs\":{\"matrix\":{}}"), "layout.regions.inputs.matrix");
        // A matrix may still say where the submixer starts out.
        Assert.Equal(SkinMixerPlacement.Detached, Taken(Matrix("\"popOut\":{\"default\":\"detached\"}")).PopOut);
    }

    [Fact]
    public void AMatrixTakesOnlyItsOwnPartsNamesAndValues()
    {
        Refused(Matrix("\"matrix\":[]"), "layout.regions.submixer.matrix");
        Refused(Matrix("\"matrix\":{\"rows\":{}}"), "layout.regions.submixer.matrix.rows");
        Refused(Matrix("\"matrix\":{\"rowHead\":{\"muted\":\"dim\"}}"), "layout.regions.submixer.matrix.rowHead.muted");
        Refused(Matrix("\"matrix\":{\"cell\":{\"label\":{}}}"), "layout.regions.submixer.matrix.cell.label");
        Refused(Matrix("\"matrix\":{\"rowHead\":{\"partsOrder\":[]}}"), "layout.regions.submixer.matrix.rowHead.partsOrder");
        Refused(Matrix("\"matrix\":{\"rowHead\":{\"parts\":[\"fader\"]}}"), "layout.regions.submixer.matrix.rowHead.parts[0]");
        Refused(Matrix("\"matrix\":{\"rowHead\":{\"parts\":[\"hardware-controls\"]}}"), "layout.regions.submixer.matrix.rowHead.parts[0]");
        Refused(Matrix("\"matrix\":{\"columnHead\":{\"parts\":[\"gain\"]}}"), "layout.regions.submixer.matrix.columnHead.parts[0]");
        Refused(Matrix("\"matrix\":{\"cell\":{\"parts\":[\"identity\"]}}"), "layout.regions.submixer.matrix.cell.parts[0]");
        Refused(Matrix("\"matrix\":{\"cell\":{\"parts\":[\"mute\",3]}}"), "layout.regions.submixer.matrix.cell.parts[1]");
        Refused(Matrix("\"matrix\":{\"cell\":{\"parts\":\"mute\"}}"), "layout.regions.submixer.matrix.cell.parts");
        Refused(Matrix("\"matrix\":{\"cell\":{\"parts\":[\"mute\"],\"hidden\":[\"mute\"]}}"), "layout.regions.submixer.matrix.cell.hidden[0]");
        Refused(Matrix("\"matrix\":{\"rowHead\":{\"label\":{\"style\":\"bold\"}}}"), "layout.regions.submixer.matrix.rowHead.label.style");
        Refused(Matrix("\"matrix\":{\"cell\":{\"muted\":\"red\"}}"), "layout.regions.submixer.matrix.cell.muted");
        Refused(Matrix("\"matrix\":{\"cell\":{\"valuePlacement\":\"under\"}}"), "layout.regions.submixer.matrix.cell.valuePlacement");
        Refused(Matrix("\"matrix\":{\"rowHead\":{\"valuePlacement\":\"over-fader\"}}"), "layout.regions.submixer.matrix.rowHead.valuePlacement");
        Refused(Matrix("\"matrix\":{\"cell\":{\"muted\":true}}"), "layout.regions.submixer.matrix.cell.muted");
        Refused(Matrix("\"matrix\":{\"meter\":\"beside-fader\"}"), "layout.regions.submixer.matrix.meter");
        Refused(Matrix("\"matrix\":{\"meter\":{}}"), "layout.regions.submixer.matrix.meter");
        Refused(Matrix("\"matrix\":{\"meter\":\"none\",\"meter\":\"none\"}"), "layout.regions.submixer.matrix.meter");
        // Never more entries than the list has parts.
        Assert.Contains("at most 3", Refused(Matrix("\"matrix\":{\"cell\":{\"parts\":[\"mute\",\"fader\",\"value\",\"mute\"]}}"),
            "layout.regions.submixer.matrix.cell.parts"), StringComparison.Ordinal);
        Refused(Matrix("\"matrix\":{\"rowHead\":{\"hidden\":[\"binding\",\"meter\",\"gain\",\"mute\",\"processing\",\"effects\",\"identity\",\"meter\"]}}"),
            "layout.regions.submixer.matrix.rowHead.hidden");
    }

    [Fact]
    public void AHeadAlwaysSaysWhoseItIsAndACellAlwaysShowsItsLevel()
    {
        Assert.Contains("identity", Refused(Matrix("\"matrix\":{\"rowHead\":{\"hidden\":[\"identity\"]}}"),
            "layout.regions.submixer.matrix.rowHead.hidden[0]"), StringComparison.Ordinal);
        Assert.Contains("identity", Refused(Matrix("\"matrix\":{\"columnHead\":{\"hidden\":[\"master\",\"identity\"]}}"),
            "layout.regions.submixer.matrix.columnHead.hidden[1]"), StringComparison.Ordinal);
        Assert.Contains("fader", Refused(Matrix("\"matrix\":{\"cell\":{\"hidden\":[\"fader\"]}}"),
            "layout.regions.submixer.matrix.cell.hidden[0]"), StringComparison.Ordinal);
        // Everything else may go: a bare row head, a bare column head, a cell that is only its fader.
        SkinMatrixOptions bare = Taken(Matrix("""
            "matrix":{"rowHead":{"hidden":["binding","meter","gain","mute","processing","effects"]},"columnHead":{"hidden":["master","mute","effects","outputs"]},
                      "cell":{"hidden":["mute","value"]}}
            """)).Submixer.Matrix;
        Assert.Equal(["identity"], bare.RowHead.Shown);
        Assert.Equal(["identity"], bare.ColumnHead.Shown);
        Assert.Equal(["fader"], bare.Cell.Shown);
    }

    [Fact]
    public void AMatrixNeedsItsRowHeadsAndOneColumnAndNeverMoreThanAClassicSection()
    {
        var sizes = new RegionSizes(StripWidth: 76, MixWidth: 232, BankSpacing: 12, Banks: 1, VisibleChannels: 6,
            MatrixCellWidth: 168, MatrixNameWidth: 112, MatrixColumns: 6);
        SkinLayout matrix = Taken(Matrix());
        double one = 112 + 168 + 168 + SkinLayoutDefaults.MatrixSpacing;
        Assert.Equal(one + SkinLayoutDefaults.CardPadding, SkinLayoutDefaults.RegionMinWidth("submixer", matrix, sizes));
        Assert.Equal(one, SkinLayoutDefaults.MatrixWidth(sizes, 1));
        // The whole matrix, as its own window draws it: every column beside the row heads.
        Assert.Equal(112 + 168 + 6 * (168 + SkinLayoutDefaults.MatrixSpacing), SkinLayoutDefaults.MatrixWidth(sizes, 6));
        // With no mix to show, the row heads alone.
        Assert.Equal(112 + 168 + SkinLayoutDefaults.CardPadding,
            SkinLayoutDefaults.RegionMinWidth("submixer", matrix, sizes with { MatrixColumns = 0 }));
        // Touch sizing's wider cells, and never past the classic width.
        Assert.Equal(132 + 200 + 200 + SkinLayoutDefaults.MatrixSpacing + SkinLayoutDefaults.CardPadding,
            SkinLayoutDefaults.RegionMinWidth("submixer", matrix, sizes with { MatrixCellWidth = 200, MatrixNameWidth = 132 }));
        Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth,
            SkinLayoutDefaults.RegionMinWidth("submixer", matrix, sizes with { MatrixCellWidth = 400, MatrixNameWidth = 400 }));
        Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth, SkinLayoutDefaults.RegionMinWidth("inputs", matrix, sizes));
    }
}
