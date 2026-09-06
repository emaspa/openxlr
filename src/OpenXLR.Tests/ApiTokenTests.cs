using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using OpenXLR.Core;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

// Redirects XDG_RUNTIME_DIR, so it joins the serial store collection.
[Collection("xdg-config")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]   // file modes are a Unix matter; the daemon only runs there
public sealed class ApiTokenTests
{
    private static ReadOnlySpan<byte> Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void TheTokenIsPrivateFreshAndOnlyAnAuthMessageWithItIsAccepted()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string? prev = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", dir);
        try
        {
            string path = ApiToken.Initialize();
            Assert.Equal(Path.Combine(dir, "openxlr", "token"), path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(path)!));
            string token = ApiToken.Current!;
            Assert.Equal(64, token.Length);
            Assert.Equal(token, OpenXlrPaths.ReadToken());   // what the clients read

            Assert.True(ApiToken.Accepts(Bytes($"{{\"cmd\":\"auth\",\"token\":\"{token}\"}}")));
            Assert.False(ApiToken.Accepts(Bytes($"{{\"cmd\":\"getState\",\"token\":\"{token}\"}}")));
            Assert.False(ApiToken.Accepts(Bytes("{\"cmd\":\"auth\",\"token\":\"" + new string('0', 64) + "\"}")));
            Assert.False(ApiToken.Accepts(Bytes("{\"cmd\":\"auth\"}")));
            Assert.False(ApiToken.Accepts(Bytes("{\"cmd\":\"auth\",\"token\":\"")));
            Assert.False(ApiToken.Accepts(Bytes("[]")));

            string second = ApiToken.Initialize();
            Assert.Equal(path, second);
            Assert.NotEqual(token, ApiToken.Current);   // every start makes a new one
            Assert.False(ApiToken.Accepts(Bytes($"{{\"cmd\":\"auth\",\"token\":\"{token}\"}}")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", prev);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TheTokenIsPublishedOnlyOnceTheHostListensAndAStaleOneIsRemovedFirst()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string? prev = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", dir);
        try
        {
            string path = Path.Combine(dir, "openxlr", "token");
            OpenXlrPaths.WriteAtomic(path, "stale-token-from-an-earlier-run\n");
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            await using var app = builder.Build();
            ApiToken.PublishWhenListening(app.Lifetime, app.Logger);
            Assert.False(File.Exists(path));            // the stale token is gone before anything listens
            Assert.Null(ApiToken.Current);
            await app.StartAsync();
            Assert.True(File.Exists(path));             // and the new one exists only now
            Assert.Equal(ApiToken.Current, OpenXlrPaths.ReadToken());
            await app.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", prev);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void NothingMatchesBeforeATokenExists() => Assert.False(ApiToken.Matches(null, Bytes("{\"cmd\":\"auth\",\"token\":\"\"}")));
}
