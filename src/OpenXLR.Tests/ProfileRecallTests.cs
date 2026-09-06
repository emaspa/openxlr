using OpenXLR.Core;

namespace OpenXLR.Tests;

// Both stores read XDG_CONFIG_HOME, which these tests redirect, so they must not run in parallel.
[Collection("xdg-config")]
public sealed class ProfileRecallTests
{
    [Fact]
    public void RecallOnConnectRoundTripsAndFollowsTheProfile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            const string dev = "0fd9:007d";
            Assert.Null(ProfileStore.RecallOnConnect(dev));

            ProfileStore.Save(dev, "Streaming", new Profile());
            ProfileStore.SetRecallOnConnect(dev, "Streaming");
            Assert.Equal("Streaming", ProfileStore.RecallOnConnect(dev));
            Assert.Equal(["Streaming"], ProfileStore.List(dev));   // the marker is not listed as a profile

            ProfileStore.SetRecallOnConnect(dev, null);
            Assert.Null(ProfileStore.RecallOnConnect(dev));

            ProfileStore.SetRecallOnConnect(dev, "Streaming");
            Assert.True(ProfileStore.Delete(dev, "Streaming"));
            Assert.Null(ProfileStore.RecallOnConnect(dev));   // deleting the profile clears the marker
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
