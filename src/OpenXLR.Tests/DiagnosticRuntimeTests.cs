using System.Text.Json;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class DiagnosticRuntimeTests
{
    [Fact]
    public void TheGraphReportsPlaybackAndCaptureWithTheirActualEndpoints()
    {
        var report = Diagnostics.ReadAudioGraph("""
            [
              {"id":0,"type":"PipeWire:Interface:Core","info":{"version":"1.6.10"}},
              {"id":10,"type":"PipeWire:Interface:Node","info":{"state":"running","props":{
                "media.class":"Stream/Output/Audio","client.id":20,"media.name":"private track title",
                "target.object":"wrong-target","application.process.id":123456},
                "params":{"Tag":[{"info":{"title":"one private tag"},"info":{"title":"another private tag"}}]}}},
              {"id":11,"type":"PipeWire:Interface:Node","info":{"state":"idle","props":{
                "media.class":"Stream/Input/Audio","client.id":20,"application.name":"Recorder","application.process.binary":"obs"}}},
              {"id":12,"type":"PipeWire:Interface:Node","info":{"state":"running","props":{
                "media.class":"Stream/Input/Audio","node.link-group":"openxlr-meter","application.name":"Internal meter"}}},
              {"id":13,"type":"PipeWire:Interface:Node","info":{"state":"running","props":{
                "media.class":"Stream/Output/Audio","node.name":"OpenXLR_player_sfx"}}},
              {"id":20,"type":"PipeWire:Interface:Client","info":{"props":{"application.name":"Player",
                "application.process.binary":"/private/player","application.process.args":"do not export"}}},
              {"id":30,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_ch_music"}}},
              {"id":31,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_chat"}}},
              {"id":40,"type":"PipeWire:Interface:Link","info":{"output-node-id":10,"input-node-id":30}},
              {"id":41,"type":"PipeWire:Interface:Link","info":{"output-node-id":10,"input-node-id":30}},
              {"id":42,"type":"PipeWire:Interface:Link","info":{"output-node-id":31,"input-node-id":11}},
              {"id":43,"type":"PipeWire:Interface:Link","info":{"output-node-id":10,"input-node-id":99}},
              {"id":50,"type":"PipeWire:Interface:Metadata","props":{"metadata.name":"settings"},"metadata":[
                {"subject":0,"key":"clock.rate","value":48000},{"subject":0,"key":"clock.force-quantum","value":128},
                {"subject":0,"key":"clock.quantum","value":1024},{"subject":50,"key":"clock.rate","value":44100},
                {"subject":0,"key":"unrelated","value":55}]}
            ]
            """);
        Assert.True(report.Available);
        Assert.False(report.Truncated);
        Assert.Equal("1.6.10", report.ServerVersion);
        Assert.Equal(48000, report.Clock["clock.rate"]);
        Assert.Equal(128, report.Clock["clock.force-quantum"]);
        Assert.Equal(1024, report.Clock["clock.quantum"]);
        Assert.Equal(3, report.Clock.Count);
        Assert.Collection(report.Streams,
            playback =>
            {
                Assert.Equal("Player", playback.Name);
                Assert.Equal("player", playback.Executable);
                Assert.Equal("playback", playback.Direction);
                Assert.Equal("running", playback.State);
                Assert.Equal(["OpenXLR_ch_music"], playback.ConnectedNodes);
            },
            capture =>
            {
                Assert.Equal("Recorder", capture.Name);
                Assert.Equal("obs", capture.Executable);
                Assert.Equal("capture", capture.Direction);
                Assert.Equal("idle", capture.State);
                Assert.Equal(["OpenXLR_chat"], capture.ConnectedNodes);
            });
        string json = JsonSerializer.Serialize(report);
        foreach (string secret in new[] { "/private/", "private track title", "private tag", "wrong-target", "123456", "do not export" })
            Assert.DoesNotContain(secret, json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[")]
    [InlineData("{}")]
    public void AFailedDumpIsDistinguishedFromAnEmptyGraph(string? json)
    {
        var report = Diagnostics.ReadAudioGraph(json);
        Assert.False(report.Available);
        Assert.Empty(report.Streams);
        Assert.True(Diagnostics.ReadAudioGraph("[]").Available);
    }

    [Fact]
    public void RunningStreamsAreRetainedFirstWhenTheReportIsBounded()
    {
        string graph = JsonSerializer.Serialize(Enumerable.Range(1, 513).Select(id => new
        {
            id,
            type = "PipeWire:Interface:Node",
            info = new
            {
                state = id == 513 ? "running" : "idle",
                props = new Dictionary<string, string>
                {
                    ["media.class"] = "Stream/Output/Audio",
                    ["application.name"] = new string('A', 300),
                },
            },
        }));
        var report = Diagnostics.ReadAudioGraph(graph);
        Assert.True(report.Truncated);
        Assert.Equal(512, report.Streams.Length);
        Assert.Equal("running", report.Streams[0].State);
        Assert.Equal(256, report.Streams[0].Name!.Length);
    }
}
