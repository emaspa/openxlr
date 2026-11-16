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

    [Theory]
    [InlineData("https://github.com/emaspa/openxlr")]
    [InlineData("https://discord.gg/4bswtnGPW4")]
    [InlineData("https://www.reddit.com/r/OpenXLR/")]
    [InlineData("https://buymeacoffee.com/emaspa")]
    [InlineData("https://ko-fi.com/emaspa")]
    public void TheAboutWindowsLinksAreHandedOut(string link)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-link-").FullName;
        string prev = ExternalLink.Opener;
        try
        {
            ExternalLink.Opener = ExecutableScript.Write(Path.Combine(dir, "opener"), "exit 0\n");
            Assert.True(ExternalLink.Open(link));
        }
        finally
        {
            ExternalLink.Opener = prev;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TheLinkReachesTheOpenerAndAMissingOpenerIsReported()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-link-").FullName;
        string log = Path.Combine(dir, "log");
        // The line is written beside the log and renamed onto it, so the log appears with its
        // line in it: a shell redirect creates the file before printf writes, and the test,
        // waiting only for the file, could read it empty.
        string opener = ExecutableScript.Write(Path.Combine(dir, "opener"), $"printf '%s\\n' \"$1\" > '{log}.part' && mv '{log}.part' '{log}'\n");
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
