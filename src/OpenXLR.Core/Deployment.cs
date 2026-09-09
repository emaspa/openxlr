using System;
using System.IO;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

/// <summary>Capabilities and paths of the separately packaged Flatpak variant.</summary>
public static class Deployment
{
    public const string FlatpakId = "io.github.emaspa.OpenXLR";
    public static bool IsFlatpak => Environment.GetEnvironmentVariable("FLATPAK_ID") == FlatpakId;
    public static string DataDirectory => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "openxlr");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string Lv2Directory => Path.Combine(DataDirectory, "lv2");
}
