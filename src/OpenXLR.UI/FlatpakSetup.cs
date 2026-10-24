using System;
using System.IO;

namespace OpenXLR.UI;

/// <summary>Install only the known session-manager rules, never replace a user's file.</summary>
internal static class FlatpakSetup
{
    internal static readonly string[] Rules = ["50-xlr-dock-capture-hold.conf", "51-openxlr-pro-raw-names.conf", "52-openxlr-mk1-capture-hold.conf", "53-openxlr-wave3-capture-hold.conf"];

    internal static void InstallRules(string source, string hostConfig)
    {
        string directory = Path.Combine(hostConfig, "wireplumber", "wireplumber.conf.d");
        if (new DirectoryInfo(directory).LinkTarget is not null || InstallationReceipt.HasLinkedParent(directory, hostConfig))
            throw new IOException("The WirePlumber configuration directory is a symbolic link.");
        Directory.CreateDirectory(directory);
        foreach (string rule in Rules)
        {
            string target = Path.Combine(directory, rule);
            string text = File.ReadAllText(Path.Combine(source, rule));
            if (new FileInfo(target).LinkTarget is not null) throw new IOException("WirePlumber rule is a symbolic link: " + target);
            if (File.Exists(target))
            {
                if (RegularFile.ReadText(target, 64 * 1024) != text)
                    throw new IOException("A different WirePlumber rule already exists: " + target);
                continue;
            }
            string temporary = Path.Combine(directory, ".openxlr-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Readable like the packaged copies, and the user's directory keeps its own mode.
                OpenXlrPaths.WriteAtomic(temporary, System.Text.Encoding.UTF8.GetBytes(text), privatePermissions: false,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                // An intervening user write wins, including a newly made link.
                File.Move(temporary, target, overwrite: false);
            }
            finally { File.Delete(temporary); }
        }
    }

    internal static string Prepare()
    {
        try
        {
            string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "share", "openxlr"));
            string host = Environment.GetEnvironmentVariable("OPENXLR_HOST_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            InstallRules(source, host);
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Localization.Localizer.Format("FlatpakRulesFailed", ex.Message); }
    }
}
