using System.Text;
using OpenXLR.Core;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

// Redirects XDG_RUNTIME_DIR, so it joins the serial store collection.
[Collection("xdg-config")]
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
    public void NothingMatchesBeforeATokenExists() => Assert.False(ApiToken.Matches(null, Bytes("{\"cmd\":\"auth\",\"token\":\"\"}")));
}
