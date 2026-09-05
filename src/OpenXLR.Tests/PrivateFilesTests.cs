using OpenXLR.Core;

namespace OpenXLR.Tests;

// Redirects XDG_CONFIG_HOME like the other store tests, so it shares their serial collection.
[Collection("xdg-config")]
public sealed class PrivateFilesTests
{
    [Fact]
    public void StoresWritePrivateFilesAndTreatAnEmptyXdgVariableAsUnset()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string? prevXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(dir, "xdg"));
        try
        {
            const string dev = "0fd9:00a6";
            ProfileStore.Save(dev, "Night", new Profile());
            string profile = Path.Combine(dir, "xdg", "openxlr", "profiles", "0fd9-00a6", "Night.json");
            Assert.True(File.Exists(profile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(profile));
            foreach (string d in new[] { "openxlr", "openxlr/profiles", "openxlr/profiles/0fd9-00a6" })
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.Combine(dir, "xdg", d)));

            // A directory an older version left world-readable is tightened on the next write.
            string profDir = Path.Combine(dir, "xdg", "openxlr", "profiles", "0fd9-00a6");
            File.SetUnixFileMode(profDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                          UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            ProfileStore.SetRecallOnConnect(dev, "Night");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(profDir));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(profDir, "recall-on-connect")));

            // An empty XDG_CONFIG_HOME means ~/.config, not the working directory.
            // (The runtime caches the home directory, so only the rule is checked, nothing is written.)
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "");
            Assert.EndsWith(Path.Combine(".config", "openxlr"), OpenXlrPaths.ConfigDir);
            Assert.True(Path.IsPathRooted(OpenXlrPaths.ConfigDir));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prevXdg);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
