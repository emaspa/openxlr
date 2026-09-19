using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class StereoLinkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADelayedRightPortRetriesAndFailedAttemptsRemoveTheLeftLink(bool permanent)
    {
        if (OperatingSystem.IsWindows()) return;
        string directory = Directory.CreateTempSubdirectory("openxlr-stereo-link-").FullName;
        string? previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), $$"""
                #!/bin/sh
                directory=${0%/*}
                case "$1" in
                  -o) printf 'source:monitor_FL\nsource:monitor_FR\n'; exit 0;;
                  -i) printf 'target:playback_FL\ntarget:playback_FR\n'; exit 0;;
                  -d) printf '0' > "$directory/left"; printf 'cleaned\n' >> "$directory/cleanup"; exit 0;;
                  source:monitor_FL) printf '1' > "$directory/left"; exit 0;;
                  source:monitor_FR)
                    if [ ! -f "$directory/attempted" ] || [ '{{permanent}}' = True ]; then
                      printf '1' > "$directory/attempted"; exit 1
                    fi
                    exit 0;;
                esac
                exit 1
                """);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + previous);
            var adapter = new PipeWireAdapter();
            if (permanent)
            {
                Assert.Throws<InvalidOperationException>(() => adapter.LinkStereoNodes("source", "monitor", "target", "playback", TimeSpan.FromMilliseconds(100)));
                Assert.Equal("0", File.ReadAllText(Path.Combine(directory, "left")));
                Assert.NotEmpty(File.ReadAllLines(Path.Combine(directory, "cleanup")));
            }
            else
            {
                var link = adapter.LinkStereoNodes("source", "monitor", "target", "playback");
                Assert.Equal(2, link.Pairs.Count);
                Assert.Equal("1", File.ReadAllText(Path.Combine(directory, "left")));
                Assert.Single(File.ReadAllLines(Path.Combine(directory, "cleanup")));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
            Directory.Delete(directory, true);
        }
    }
}
