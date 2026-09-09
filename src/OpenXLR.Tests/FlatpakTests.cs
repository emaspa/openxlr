using System.Net;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

// Tests changing process-wide XDG or Flatpak variables must not overlap any collection.
[CollectionDefinition("xdg-config", DisableParallelization = true)]
public sealed class XdgConfigCollection;

[Collection("xdg-config")]
public sealed class FlatpakTests
{
    [Fact]
    public void FlatpakRejectsUnsupportedInstallsBeforeCopyingAndDoesNotCreateUserUnits()
    {
        string? old = Environment.GetEnvironmentVariable("FLATPAK_ID");
        string dir = Directory.CreateTempSubdirectory("openxlr-flatpak-test-").FullName;
        try
        {
            Environment.SetEnvironmentVariable("FLATPAK_ID", Deployment.FlatpakId);
            Assert.True(Deployment.IsFlatpak);
            Assert.False(NativePluginHost.HostInstalled);
            Assert.Throws<InvalidOperationException>(() => OpenXLR.UI.StartupIntegration.SetDaemonAtLogin(true));
            Assert.Throws<InvalidOperationException>(() => OpenXLR.UI.StartupIntegration.SetWindowAtLogin(true));
            string source = Path.Combine(dir, "input");
            Directory.CreateDirectory(source);
            string bundle = Path.Combine(source, "Example.lv2");
            Directory.CreateDirectory(bundle);
            File.WriteAllText(Path.Combine(bundle, "manifest.ttl"), "@prefix lv2: <http://lv2plug.in/ns/lv2core#> .");
            File.WriteAllBytes(Path.Combine(source, "Example.clap"), [0x7f, (byte)'E', (byte)'L', (byte)'F']);
            var installer = new PluginInstaller(Path.Combine(dir, "lv2"), Path.Combine(dir, "clap"), Path.Combine(dir, "vst3"), null, null);
            Assert.False(installer.Install(source).Ok);
            Assert.False(Directory.Exists(Path.Combine(dir, "lv2")));
            using var mixer = new Mixer();
            Assert.Throws<InvalidOperationException>(() => mixer.Build(MixerConfig.FromSettings(new MixerSettings
            {
                UserChannels = Enumerable.Range(0, 10).Select(i => new UserChannelDefinition($"app{i}", $"App {i}")).ToList(),
            })));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLATPAK_ID", old);
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FlatpakUpdateOnlyLinksToTheExpectedBundle(bool bundlePresent)
    {
        string assets = bundlePresent
            ? """[{"name":"OpenXLR-x86_64.flatpak","browser_download_url":"https://evil.example/bundle"}]"""
            : """[{"name":"openxlr.rpm"}]""";
        using var handler = new ReleaseHandler("""{"tag_name":"v99.0.0","body":"Changes","assets":ASSETS}""".Replace("ASSETS", assets));
        using var client = new HttpClient(handler);
        var result = await new OpenXLR.UI.UpdateChecker(client, flatpak: true).CheckAsync("0.1.30");
        Assert.True(result.Available);
        Assert.Equal(bundlePresent
            ? "https://github.com/emaspa/openxlr/releases/download/v99.0.0/OpenXLR-x86_64.flatpak"
            : "https://github.com/emaspa/openxlr/releases/tag/v99.0.0", result.Url);
        Assert.Contains(bundlePresent ? "manually" : "does not have a Flatpak", result.Details);
    }

    private sealed class ReleaseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
