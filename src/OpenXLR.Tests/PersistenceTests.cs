using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public void AFailedSettingsWriteReportsTheReasonInsteadOfVanishing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new MixerSettings();
            Assert.Null(settings.Save(Path.Combine(dir, "mixer.json")));
            Assert.True(File.Exists(Path.Combine(dir, "mixer.json")));

            // A file where the directory should be: the write cannot land.
            string blocked = Path.Combine(dir, "blocked");
            File.WriteAllText(blocked, "");
            string? err = settings.Save(Path.Combine(blocked, "mixer.json"));
            Assert.NotNull(err);
            Assert.Contains("mixer.json", err);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "system";
        public bool HasMix(string id) => id is "monitor" or "monitor2" or "stream";
        public bool IsMonitorFeed(string feed) => feed is "monitor" or "monitor2" or "monitor+monitor2";
        public bool IsMonitorOutput(string device) => device is "alsa_output.katana" or "alsa_output.pro#";
        public bool IsInsertKey(string key) => key is "xlr1";
        public int OverrideCount => 0;
    }

    private static Command Cmd(string json) => JsonSerializer.Deserialize<Command>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Theory]
    [InlineData("""{"cmd":"setMonitorFeed","device":"alsa_output.katana","mix":"monitor2"}""", null)]
    [InlineData("""{"cmd":"setMonitorFeed","device":"alsa_output.pro#","mix":"monitor"}""", null)]
    [InlineData("""{"cmd":"setMonitorFeed","device":"alsa_output.katana","mix":"stream"}""", "not a monitor mix")]
    [InlineData("""{"cmd":"setMonitorFeed","device":"alsa_output.katana","mix":"monitor+monitor2"}""", null)]
    [InlineData("""{"cmd":"setMonitorFeed","device":"alsa_output.gone","mix":"monitor2"}""", "not a selected monitor output")]
    [InlineData("""{"cmd":"setMonitorFeed","device":"alsa_output.katana","mix":"nope"}""", "not a monitor mix")]
    public void AMonitorFeedCommandIsRejectedBeforeItCouldSilentlyDoNothing(string json, string? expected)
    {
        string? err = CommandValidation.Check(Cmd(json), new Layout(), _ => null);
        if (expected is null) Assert.Null(err);
        else Assert.Contains(expected, err);
    }
}
