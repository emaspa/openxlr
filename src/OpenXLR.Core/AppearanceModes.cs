#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core;
#endif

/// <summary>
/// The saved Material mode, shared by the window and the terminal mixer as a
/// linked source so both read the one <c>appearanceMode</c> key in ui.json
/// the same way.
/// </summary>
public static class AppearanceModes
{
    /// <summary>Follow the desktop's light or dark preference.</summary>
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static bool IsValid(string? value) => value is System or Light or Dark;

    /// <summary>An absent or unknown value follows the desktop.</summary>
    public static string Normalize(string? value) => IsValid(value) ? value! : System;
}
