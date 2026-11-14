using System.Diagnostics;
using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class MeterSampleTests
{
    [Fact]
    public void InvalidSamplesDoNotPoisonEitherChannelOrTheFollowingFrames()
    {
        byte[] input = Bytes(float.NaN, 0.25f, float.PositiveInfinity, 0.5f,
            float.NegativeInfinity, -0.5f, 0.5f, float.NaN);
        var result = MeterReader.AccumulateFrames(input, input.Length);
        Assert.Equal(0.25, result.SumL);
        Assert.Equal(0.5625, result.SumR);
        Assert.Equal(4, result.Frames);
        Assert.Equal(0, result.Carry);
        byte[] loud = Bytes(float.MaxValue, -float.MaxValue);
        var extremes = MeterReader.AccumulateFrames(loud, loud.Length);
        Assert.True(double.IsFinite(extremes.SumL));
        Assert.Equal((double)float.MaxValue * float.MaxValue, extremes.SumL);
        Assert.Equal(extremes.SumL, extremes.SumR);
    }

    [Fact]
    public void MeterMessagesRemainSerializableAfterInvalidAudio()
    {
        string path = Path.Combine(Path.GetTempPath(), "openxlr-meter-" + Guid.NewGuid());
        File.WriteAllBytes(path, Bytes(float.NaN, 0.5f, float.PositiveInfinity, 0.5f));
        try
        {
            using var meters = new MeterReader();
            var process = new ProcessStartInfo("cat") { RedirectStandardOutput = true, RedirectStandardError = true };
            process.ArgumentList.Add(path);
            meters.Add("test", process);
            IReadOnlyDictionary<string, double[]>? snapshot = null;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                snapshot = meters.Read();
                return snapshot.TryGetValue("test", out var levels) && levels[1] > 0;
            }, TimeSpan.FromSeconds(5)));
            Assert.Equal(0, snapshot!["test"][0]);
            Assert.All(snapshot["test"], level => Assert.InRange(level, 0, 1));
            Assert.Contains("test", JsonSerializer.Serialize(snapshot));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0f, false)]      // the zeros a running mix delivers in silence
    [InlineData(5e-5f, false)]   // -86 dBFS
    [InlineData(2e-4f, true)]    // -74 dBFS
    public void SoundStartsAboveMinus80Dbfs(float sample, bool sound)
    {
        byte[] input = Bytes([.. Enumerable.Repeat(sample, 400)]);
        var result = MeterReader.AccumulateFrames(input, input.Length);
        Assert.Equal(sound, MeterReader.IsSound(result.SumL, result.SumR, result.Frames));
    }

    [Fact]
    public void LastSoundMovesWhenTheMeterReadsSound()
    {
        string path = Path.Combine(Path.GetTempPath(), "openxlr-meter-" + Guid.NewGuid());
        File.WriteAllBytes(path, Bytes([.. Enumerable.Repeat(1e-3f, 400)]));
        try
        {
            using var meters = new MeterReader();
            Assert.Null(meters.LastSound("test"));
            // The samples arrive 200 ms after the process starts, which is a
            // little before the meter notes its own start.
            var process = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-c", "sleep 0.2; exec cat \"$1\"", "sh", path }) process.ArgumentList.Add(arg);
            meters.Add("test", process);
            long started = meters.LastSound("test")!.Value;
            Assert.True(SpinWait.SpinUntil(() => meters.LastSound("test") >= started + 100, TimeSpan.FromSeconds(5)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(1024, 48000, 22)]
    [InlineData(256, 48000, 6)]
    [InlineData(2048, 48000, 43)]
    [InlineData(1024, 44100, 24)]
    [InlineData(8192, 48000, 50)]
    [InlineData(null, null, 22)]
    public void MeterLatencyAsksForTheGraphDefault(int? quantum, int? rate, int expected)
        => Assert.Equal(expected, MeterReader.LatencyMs(quantum, rate));

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(96000)]
    public void MeterLatencyRoundsBackToTheSameQuantum(int rate)
    {
        // PipeWire scales node.latency to the graph rate and rounds down to a
        // power of two; the meter's request must land on the default itself.
        for (int quantum = 64; quantum <= 2048; quantum *= 2)
        {
            long frames = (long)MeterReader.LatencyMs(quantum, rate) * rate / 1000;
            Assert.Equal(quantum, 1 << (int)Math.Log2(frames));
        }
    }

    private static byte[] Bytes(params float[] samples)
        => samples.SelectMany(BitConverter.GetBytes).ToArray();
}
