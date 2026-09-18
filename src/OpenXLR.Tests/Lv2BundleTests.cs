using System.Runtime.InteropServices;
using System.Text;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class Lv2BundleTests
{
    [Fact]
    public void Lv2ControlsHaveOrderedBoundsAndDefaultsWithinTheirRange()
    {
        if (!NativeLibrary.TryLoad("liblilv-0.so.0", out IntPtr library)) return;
        NativeLibrary.Free(library);
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-bounds-").FullName;
        try
        {
            string bundle = Directory.CreateDirectory(Path.Combine(directory, "bounds.lv2")).FullName;
            File.WriteAllText(Path.Combine(bundle, "manifest.ttl"),
                "@prefix lv2: <http://lv2plug.in/ns/lv2core#> .\n@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .\n"
                + "<urn:openxlr:test:bounds> a lv2:Plugin ; rdfs:seeAlso <bounds.ttl> .\n");
            string[] ranges =
            [
                "lv2:default 1.0 ; lv2:minimum 2.0 ; lv2:maximum 1.0",
                "lv2:default 3.0 ; lv2:minimum 3.0 ; lv2:maximum 3.0",
                "lv2:default -6.0 ; lv2:minimum -5.0 ; lv2:maximum -1.0",
                "lv2:default 21.0 ; lv2:minimum 10.0 ; lv2:maximum 20.0",
                "lv2:minimum 10.0 ; lv2:maximum 20.0",
            ];
            string text = Bundle("bounds", "urn:openxlr:test:bounds", ranges.Length);
            for (int i = 0; i < ranges.Length; i++)
                text = text.Replace($"lv2:name \"Control {i}\" ; lv2:default 0.0 ; lv2:minimum 0.0 ; lv2:maximum 1.0",
                    $"lv2:name \"Control {i}\" ; {ranges[i]}", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(bundle, "bounds.ttl"), text);
            IReadOnlyList<PluginParam> controls = Assert.Single(Lv2Catalog.ScanNow(directory)).Params;
            Assert.Equal(["c1", "c2", "c3", "c4"], controls.Select(p => p.Symbol));
            Assert.Equal([3.0, -5.0, 20.0, 10.0], controls.Select(p => p.Default));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void NonFiniteLv2MetadataCannotBreakTheCatalogueReply()
    {
        if (!NativeLibrary.TryLoad("liblilv-0.so.0", out IntPtr library)) return;
        NativeLibrary.Free(library);
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-ranges-").FullName;
        try
        {
            string bundle = Directory.CreateDirectory(Path.Combine(directory, "ranges.lv2")).FullName;
            File.WriteAllText(Path.Combine(bundle, "manifest.ttl"),
                "@prefix lv2: <http://lv2plug.in/ns/lv2core#> .\n@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .\n"
                + "<urn:openxlr:test:ranges> a lv2:Plugin ; rdfs:seeAlso <ranges.ttl> .\n");
            string text = Bundle("ranges", "urn:openxlr:test:ranges", 3)
                .Replace("lv2:maximum 1.0", "lv2:maximum 1e999", StringComparison.Ordinal);
            // Keep one useful control; its invalid scale point is optional
            // metadata and must not make the whole plugin disappear.
            text = text.Replace("lv2:symbol \"c2\" ; lv2:name \"Control 2\" ; lv2:default 0.0 ; lv2:minimum 0.0 ; lv2:maximum 1e999",
                "lv2:symbol \"c2\" ; lv2:name \"Control 2\" ; lv2:default 0.0 ; lv2:minimum 0.0 ; lv2:maximum 1.0 ; "
                + "lv2:scalePoint [ <http://www.w3.org/2000/01/rdf-schema#label> \"bad\" ; <http://www.w3.org/1999/02/22-rdf-syntax-ns#value> 1e999 ]",
                StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(bundle, "ranges.ttl"), text);
            PluginInfo plugin = Assert.Single(Lv2Catalog.ScanNow(directory));
            PluginParam control = Assert.Single(plugin.Params);
            Assert.Equal("c2", control.Symbol);
            Assert.Empty(control.ScalePoints);
            Assert.NotEmpty(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(plugin));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Bundle(string name, string uri, int extraControls)
    {
        var sb = new StringBuilder();
        sb.Append($"""
            @prefix lv2: <http://lv2plug.in/ns/lv2core#> .
            @prefix doap: <http://usefulinc.com/ns/doap#> .
            <{uri}> a lv2:Plugin, lv2:AmplifierPlugin ;
              doap:name "{name}" ;
              lv2:binary <{name}.so> ;
              lv2:port [ a lv2:AudioPort, lv2:InputPort ; lv2:index 0 ; lv2:symbol "in" ; lv2:name "In" ] ,
                       [ a lv2:AudioPort, lv2:OutputPort ; lv2:index 1 ; lv2:symbol "out" ; lv2:name "Out" ]
            """);
        for (int i = 0; i < extraControls; i++)
            sb.Append($" ,\n    [ a lv2:ControlPort, lv2:InputPort ; lv2:index {i + 2} ; lv2:symbol \"c{i}\" ; lv2:name \"Control {i}\" ; lv2:default 0.0 ; lv2:minimum 0.0 ; lv2:maximum 1.0 ]");
        sb.Append(" .\n");
        return sb.ToString();
    }

    [Fact]
    public void ABundleDeclaringThousandsOfPortsIsSkippedAndAnOrdinaryOneIsKept()
    {
        if (!NativeLibrary.TryLoad("liblilv-0.so.0", out IntPtr lib)) return;   // no lilv here (CI): nothing to scan with
        NativeLibrary.Free(lib);
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-lv2-" + Guid.NewGuid().ToString("N"));
        try
        {
            string good = Path.Combine(dir, "good.lv2"), monster = Path.Combine(dir, "monster.lv2");
            Directory.CreateDirectory(good); Directory.CreateDirectory(monster);
            File.WriteAllText(Path.Combine(good, "manifest.ttl"),
                "@prefix lv2: <http://lv2plug.in/ns/lv2core#> .\n@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .\n<urn:openxlr:test:good> a lv2:Plugin ; lv2:binary <good.so> ; rdfs:seeAlso <good.ttl> .\n");
            File.WriteAllText(Path.Combine(good, "good.ttl"), Bundle("good", "urn:openxlr:test:good", 3));
            File.WriteAllText(Path.Combine(monster, "manifest.ttl"),
                "@prefix lv2: <http://lv2plug.in/ns/lv2core#> .\n@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .\n<urn:openxlr:test:monster> a lv2:Plugin ; lv2:binary <monster.so> ; rdfs:seeAlso <monster.ttl> .\n");
            File.WriteAllText(Path.Combine(monster, "monster.ttl"), Bundle("monster", "urn:openxlr:test:monster", Lv2Catalog.MaxPorts + 10));
            // lilv is told the path directly: native code never sees a .NET environment change.
            IReadOnlyList<PluginInfo> found = Lv2Catalog.ScanNow(dir);
            Assert.Single(found);   // only this directory was scanned
            Assert.Contains(found, p => p.Plugin == "urn:openxlr:test:good" && p.Params.Count == 3 && p.AudioIns == 1 && p.AudioOuts == 1);
            Assert.DoesNotContain(found, p => p.Plugin == "urn:openxlr:test:monster");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void AnLv2ScanBiggerThanOneMessageIsKeptWholeAndOnlyTheClientListIsCut()
    {
        if (!NativeLibrary.TryLoad("liblilv-0.so.0", out IntPtr lib)) return;   // no lilv here (CI): nothing to scan with
        NativeLibrary.Free(lib);
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-lv2-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Plugins at the control limit adding up to more than a client
            // reads in one message, which used to end the scan short.
            const int count = 120;
            for (int i = 0; i < count; i++)
            {
                string bundle = Path.Combine(dir, $"big{i}.lv2");
                Directory.CreateDirectory(bundle);
                File.WriteAllText(Path.Combine(bundle, "manifest.ttl"),
                    "@prefix lv2: <http://lv2plug.in/ns/lv2core#> .\n@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .\n"
                    + $"<urn:openxlr:test:big{i}> a lv2:Plugin ; lv2:binary <big{i}.so> ; rdfs:seeAlso <big{i}.ttl> .\n");
                File.WriteAllText(Path.Combine(bundle, $"big{i}.ttl"),
                    Bundle($"big{i}", $"urn:openxlr:test:big{i}", Lv2Catalog.MaxControls));
            }
            IReadOnlyList<PluginInfo> found = Lv2Catalog.ScanNow(dir);
            Assert.Equal(count, found.Count);
            Assert.True(found.Sum(p => (long)Lv2Catalog.Footprint(p)) > Lv2Catalog.CatalogBudgetBytes);

            // The message is what gets cut, and a plugin it cut is listed
            // again as soon as a chain uses it.
            IReadOnlyList<PluginInfo> idle = ClientCatalog.ForClient(found, []);
            Assert.True(idle.Count < found.Count);
            Assert.True(idle.Sum(p => (long)Lv2Catalog.Footprint(p)) <= Lv2Catalog.CatalogBudgetBytes);
            PluginInfo dropped = found.First(p => !idle.Contains(p));
            IReadOnlyList<PluginInfo> sent = ClientCatalog.ForClient(found, [("lv2", dropped.Plugin)]);
            Assert.Contains(sent, p => p.Plugin == dropped.Plugin);
            Assert.True(sent.Sum(p => (long)Lv2Catalog.Footprint(p)) <= Lv2Catalog.CatalogBudgetBytes);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
