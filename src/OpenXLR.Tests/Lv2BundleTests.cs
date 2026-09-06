using System.Runtime.InteropServices;
using System.Text;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class Lv2BundleTests
{
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
            Assert.Equal(1, found.Count);   // only this directory was scanned
            Assert.Contains(found, p => p.Plugin == "urn:openxlr:test:good" && p.Params.Count == 3 && p.AudioIns == 1 && p.AudioOuts == 1);
            Assert.DoesNotContain(found, p => p.Plugin == "urn:openxlr:test:monster");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
