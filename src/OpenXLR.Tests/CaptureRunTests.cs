using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// A capture counts as started by the kernel's own stream status where the
/// card has one. The Wave:3 MK.2 kept its PipeWire source running while
/// /proc/asound/MK2/stream0 read "Capture: Status: Stop".
/// </summary>
public sealed class CaptureRunTests : IDisposable
{
    // The MK.2's stream file while its capture stalled, trimmed to the parts read.
    private const string Stalled = """
        Elgato Elgato Wave:3 MK.2 at usb-0000:14:00.0-1, high speed : USB Audio

        Playback:
          Status: Running
            Interface = 2
            Altset = 2
          Interface 2
            Altset 1
            Format: FLOAT_LE

        Capture:
          Status: Stop
          Interface 1
            Altset 1
            Format: FLOAT_LE
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("openxlr-asound-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void TheCaptureSectionDecidesNotThePlaybackOne()
    {
        Assert.False(CaptureRun.ParseKernelStatus(Stalled));
        Assert.True(CaptureRun.ParseKernelStatus(Stalled.Replace("Status: Stop", "Status: Running")));
        // A playback-only card says nothing about capture.
        Assert.Null(CaptureRun.ParseKernelStatus(Stalled[..Stalled.IndexOf("Capture:", StringComparison.Ordinal)]));
        Assert.Null(CaptureRun.ParseKernelStatus(""));
        // CRLF and an unknown status word are not taken as running.
        Assert.False(CaptureRun.ParseKernelStatus(Stalled.Replace("\n", "\r\n")));
        Assert.Null(CaptureRun.ParseKernelStatus(Stalled.Replace("Status: Stop", "Status: Draining")));
    }

    [Fact]
    public void TheCardAndStreamComeFromTheNodesOwnAlsaProperties()
    {
        Directory.CreateDirectory(Path.Combine(_root, "card7"));
        File.WriteAllText(Path.Combine(_root, "card7", "stream0"), Stalled);
        const string source = "alsa_input.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.analog-stereo";
        JsonElement[] graph = Graph($$"""
            [
              { "id": 501, "type": "PipeWire:Interface:Node", "info": { "state": "running",
                "props": { "node.name": "alsa_output.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.analog-stereo", "api.alsa.pcm.card": 7 } } },
              { "id": 582, "type": "PipeWire:Interface:Node", "info": { "state": "running",
                "props": { "node.name": "{{source}}", "api.alsa.pcm.card": 7, "alsa.card": 7, "alsa.device": 0 } } },
              { "id": 600, "type": "PipeWire:Interface:Node", "info": { "state": "idle",
                "props": { "node.name": "test_virtual_mic" } } }
            ]
            """);

        CaptureRun stalled = PipeWireAdapter.CaptureRunOf(graph, source, _root);
        Assert.Equal(new CaptureRun(582, Alsa: true, NodeRunning: true, KernelRunning: false), stalled);
        Assert.False(stalled.Started);   // PipeWire runs it, the kernel does not

        File.WriteAllText(Path.Combine(_root, "card7", "stream0"), Stalled.Replace("Status: Stop", "Status: Running"));
        Assert.True(PipeWireAdapter.CaptureRunOf(graph, source, _root).Started);

        // The card went away under the node (a reset, an unplug): PipeWire's word.
        Directory.Delete(Path.Combine(_root, "card7"), recursive: true);
        Assert.Equal(new CaptureRun(582, true, true, null), PipeWireAdapter.CaptureRunOf(graph, source, _root));

        // A source that is not a card counts once it is there; a missing one never.
        Assert.True(PipeWireAdapter.CaptureRunOf(graph, "test_virtual_mic", _root).Started);
        Assert.Equal(CaptureRun.Missing, PipeWireAdapter.CaptureRunOf(graph, "alsa_input.gone", _root));
        Assert.False(CaptureRun.Missing.Started);
    }

    [Fact]
    public void AnAlsaNodeWithoutAKernelAnswerCountsWhenPipeWireRunsIt()
    {
        Assert.True(new CaptureRun(1, Alsa: true, NodeRunning: true, KernelRunning: null).Started);
        Assert.False(new CaptureRun(1, Alsa: true, NodeRunning: false, KernelRunning: null).Started);
        // The kernel's word wins over PipeWire's either way.
        Assert.True(new CaptureRun(1, Alsa: true, NodeRunning: false, KernelRunning: true).Started);
    }

    [Fact]
    public void TheRunningPcmCursorUsesTheActualCardAndIsUnavailableWhilePlaybackIsOpen()
    {
        const string source = "alsa_input.usb-Elgato_Systems_Elgato_XLR_Dock-test";
        const string frozen = """
            state: RUNNING
            owner_pid   : 2053
            trigger_time: 19.095039214
            tstamp      : 4774.932181708
            -----
            hw_ptr      : 228280655
            appl_ptr    : 228280576
            """;
        string card = Path.Combine(_root, "card4");
        string capture = Directory.CreateDirectory(Path.Combine(card, "pcm1c", "sub2")).FullName;
        string playback = Directory.CreateDirectory(Path.Combine(card, "pcm0p", "sub0")).FullName;
        string status = Path.Combine(capture, "status");
        string output = Path.Combine(playback, "status");
        File.WriteAllText(status, frozen);
        File.WriteAllText(output, "closed\n");
        JsonElement[] graph = Graph($$"""
            [{ "id": 46, "type": "PipeWire:Interface:Node", "info": { "state": "running",
               "props": { "node.name": "{{source}}", "api.alsa.pcm.card": "4", "api.alsa.pcm.stream": "capture",
                          "alsa.device": 1, "alsa.subdevice": 2 } } }]
            """);
        CaptureRun Read() => PipeWireAdapter.CaptureRunOf(graph, source, _root, readCursor: true);

        Assert.Equal(new CaptureCursor(4, 1, 2, "19.095039214", 228280655), Read().Cursor);
        File.WriteAllText(output, "state: RUNNING\n");
        Assert.Null(Read().Cursor);
        File.Delete(output);
        Assert.Null(Read().Cursor);
        File.WriteAllText(output, "closed\n");
        File.WriteAllText(status, frozen.Replace("RUNNING", "XRUN"));
        Assert.Null(Read().Cursor);
        File.WriteAllText(status, frozen.Replace("hw_ptr", "unknown"));
        Assert.Null(Read().Cursor);
    }

    private static JsonElement[] Graph(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
