using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class DiagnosticFileSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("openxlr-diagnostic-files-").FullName;
    private string Source => Path.Combine(_root, "source");
    private string Output => Path.Combine(_root, "output");

    public DiagnosticFileSafetyTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Output);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private void Copy() => Diagnostics.CopyRedactedIfExists(s => s, Source, "ui.json", Output);
    private string Saved() => File.ReadAllText(Path.Combine(Output, "ui.json"));

    [Fact]
    public void OversizedConfigurationIsOmittedWithoutCopyingIt()
    {
        using (var file = File.Create(Path.Combine(Source, "ui.json"))) file.SetLength(OpenXLR.UI.OpenXlrPaths.MaxPreferenceBytes + 1L);
        Copy();
        Assert.Contains("unavailable", Saved());
        Assert.InRange(new FileInfo(Path.Combine(Output, "ui.json")).Length, 1, 1024);
    }

    [Fact]
    public void ConfigurationLinksDoNotIncludeTheirTargets()
    {
        if (!OperatingSystem.IsLinux()) return;
        string secret = Path.Combine(_root, "private");
        File.WriteAllText(secret, "sensitive target contents");
        File.CreateSymbolicLink(Path.Combine(Source, "ui.json"), secret);
        Copy();
        Assert.Contains("symbolic links", Saved());
        Assert.DoesNotContain("sensitive target", Saved());
    }

    [Fact]
    public async Task ConfigurationFifosDoNotWaitForAWriter()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(OpenXLR.Core.ProcessRunner.Run("mkfifo", [Path.Combine(Source, "ui.json")]).Ok);
        await Task.Run(Copy).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("not a regular file", Saved());
    }

    [Fact]
    public async Task ScanLogFifosAreRejectedBeforeOpeningThem()
    {
        if (!OperatingSystem.IsLinux()) return;
        string logs = Directory.CreateDirectory(Path.Combine(_root, "plugin-scan-logs")).FullName;
        Assert.True(OpenXLR.Core.ProcessRunner.Run("mkfifo", [Path.Combine(logs, "vst3-aaaabbbbccccdddd.log")]).Ok);
        await Task.Run(() => Diagnostics.WriteScanLogsAsync(logs, null, Output, [])).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("not a regular file", File.ReadAllText(Path.Combine(Output, "plugin-scan-logs", "index.txt")));
        Assert.False(File.Exists(Path.Combine(Output, "plugin-scan-logs", "vst3-aaaabbbbccccdddd.log")));
    }

    [Fact]
    public void OrdinaryConfigurationIsRedactedAndMissingFilesAreOptional()
    {
        Copy();
        Assert.Empty(Directory.GetFiles(Output));
        File.WriteAllText(Path.Combine(Source, "ui.json"), "{\"skin\":\"wave\"}");
        Diagnostics.CopyRedactedIfExists(s => s.Replace("wave", "redacted"), Source, "ui.json", Output);
        Assert.Equal("{\"skin\":\"redacted\"}", Saved());
    }
}
