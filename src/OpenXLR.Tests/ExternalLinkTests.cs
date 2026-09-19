using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class ExternalLinkTests
{
    [Fact]
    public void OnlyHttpsToANamedHostIsHandedOut()
    {
        Assert.False(ExternalLink.Open("http://github.com/emaspa/openxlr"));
        Assert.False(ExternalLink.Open("https://example.com/emaspa/openxlr"));
        Assert.False(ExternalLink.Open("file:///etc/passwd"));
        Assert.False(ExternalLink.Open("not a link"));
    }

    [Fact]
    public async Task TheLinkReachesTheOpenerAndAMissingOpenerIsReported()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-link-").FullName;
        string log = Path.Combine(dir, "log");
        string opener = ExecutableScript.Write(Path.Combine(dir, "opener"), $"printf '%s\\n' \"$1\" >> '{log}'\n");
        string prev = ExternalLink.Opener;
        const string Link = "https://github.com/emaspa/openxlr/blob/main/docs/manual.md#skins";
        try
        {
            ExternalLink.Opener = opener;
            Assert.True(ExternalLink.Open(Link));
            for (int i = 0; i < 100 && !File.Exists(log); i++) await Task.Delay(50);
            Assert.Equal(Link, (await File.ReadAllTextAsync(log)).Trim());

            ExternalLink.Opener = Path.Combine(dir, "missing");
            Assert.False(ExternalLink.Open(Link));
        }
        finally
        {
            ExternalLink.Opener = prev;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
