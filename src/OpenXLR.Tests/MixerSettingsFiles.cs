using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The mixer settings a test leaves in a shared config directory. A save keeps
/// the file it replaces as ".bak", and a load reads that backup when the file
/// itself is missing, so deleting mixer.json alone hands the last test's
/// settings to the next daemon started there.
/// </summary>
internal static class MixerSettingsFiles
{
    /// <summary>Delete mixer.json with its backup and any copy kept of a corrupt file.</summary>
    public static void Delete()
    {
        foreach (string suffix in new[] { "", ".bak", ".corrupt" })
            if (File.Exists(MixerSettings.DefaultPath + suffix)) File.Delete(MixerSettings.DefaultPath + suffix);
    }
}
