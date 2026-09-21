using System.Reflection;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class LayoutRollbackTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedDeletionPreservesPendingSendWrites(bool removeMix, bool throws)
    {
        using var mixer = new Mixer();
        Set("_config", new MixerConfig
        {
            Channels = [new("game", "Game"), new("music", "Music")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)],
        });
        var cells = Field<HashSet<string>>("_cells");
        cells.UnionWith(["game|monitor", "game|chat", "music|monitor", "music|chat"]);
        var pending = Field<HashSet<string>>("_pendingCells");
        pending.UnionWith(["game|chat", "music|monitor"]);
        var levels = Field<Dictionary<string, double>>("_levels");
        foreach (string cell in cells) levels[cell] = .4;
        Field<HashSet<string>>("_muted").Add("game|chat");
        Set("_built", true);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Assert.Throws<IOException>(() =>
                {
                    if (removeMix) mixer.DeleteMix("chat", Persist);
                    else mixer.DeleteApplicationChannel("game", Persist);
                });
                Assert.Equal(new[] { "game|chat", "music|monitor" }, pending.Order());
                Assert.Equal(4, cells.Count);
                Assert.Equal(.4, levels["game|chat"]);
                Assert.Contains("game|chat", Field<HashSet<string>>("_muted"));
            }
        }
        finally { Set("_built", false); } // The fixture never created an audio graph.

        string? Persist(MixerSettings settings)
        {
            Assert.DoesNotContain("game|chat", settings.Levels.Keys);
            if (throws) throw new IOException("disk full");
            return "disk full";
        }
        T Field<T>(string name) => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
        void Set(string name, object value) => typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, value);
    }
}
