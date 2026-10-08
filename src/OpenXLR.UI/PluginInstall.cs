using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace OpenXLR.UI;

/// <summary>
/// Installing a plugin from the desktop: the user picks a file or a
/// folder, the daemon puts it where the catalogues look (or hands a
/// Windows plugin to yabridge), and every open chain fetches the
/// catalogue again. Shared by the picker and the Options window.
/// </summary>
public static class PluginInstall
{
    /// <summary>A bundle of two hundred plugins takes a quarter of a minute to describe; yabridge's sync is quick.</summary>
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(4);

    /// <summary>
    /// The manual on Windows plugins: the button sits beside the yabridge
    /// row, and that is the part of installing a plugin that needs reading.
    /// Installing plugins in general is the paragraph above it.
    /// </summary>
    public const string Manual = "https://github.com/emaspa/openxlr/blob/main/docs/manual.md#windows-plugins";

    /// <summary>Let the user pick plugin files: .clap, a single-file .vst3, or a Windows plugin.</summary>
    public static async Task<IReadOnlyList<string>> PickFilesAsync(Window owner)
    {
        IStorageFolder? start = await owner.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads);
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Text("InstallPluginFiles"),
            AllowMultiple = true,
            SuggestedStartLocation = start,
            FileTypeFilter =
            [
                new FilePickerFileType(Localizer.Text("PluginFileType")) { Patterns = ["*.clap", "*.vst3", "*.dll"] },
                FilePickerFileTypes.All,
            ],
        });
        return files.Select(LocalPath).OfType<string>().ToList();
    }

    /// <summary>Let the user pick a folder: a .vst3 or .lv2 bundle, or a folder holding plugins.</summary>
    public static async Task<IReadOnlyList<string>> PickFolderAsync(Window owner,
        string? title = null, bool allowMultiple = true)
    {
        IStorageFolder? start = await owner.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads);
        IReadOnlyList<IStorageFolder> folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title ?? Localizer.Text("InstallAPluginFolder"),
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = start,
        });
        return folders.Select(LocalPath).OfType<string>().ToList();
    }

    private static string? LocalPath(IStorageItem item)
    {
        try { return item.TryGetLocalPath(); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Install each path in turn and say how it went in a sentence or two.
    /// Then every chain fetches the catalogue again.
    /// </summary>
    public static async Task<string> InstallAsync(DaemonClient client, IReadOnlyList<string> paths)
    {
        var lines = new List<string>();
        int added = 0;
        foreach (string path in paths)
        {
            JsonNode? reply = await client.InstallPluginAsync(path, InstallTimeout);
            if (reply is null) { lines.Add(Localizer.Format("NoAnswerForPath", System.IO.Path.GetFileName(path.TrimEnd('/')))); continue; }
            lines.Add(reply["message"]?.GetValue<string>() ?? "");
            added += reply["added"]?.GetValue<int>() ?? 0;
        }
        Reload();
        return Summarise(lines, added);
    }

    /// <summary>Report the outcome of a sync or rescan the same way.</summary>
    public static string Describe(JsonNode? reply, string what)
    {
        if (reply is null) return Localizer.Format("NoAnswerMayStillRun", what);
        Reload();
        int added = reply["added"]?.GetValue<int>() ?? 0;
        int total = reply["total"]?.GetValue<int>() ?? 0;
        string message = reply["message"]?.GetValue<string>() ?? "";
        string gained = added switch { 0 => Localizer.Format("CatalogueNothingNew", total), 1 => Localizer.Text("CatalogueOneNew"), _ => Localizer.Format("CatalogueNew", added) };
        return message.Length == 0 ? gained : $"{message} {gained}";
    }

    private static string Summarise(List<string> lines, int added)
    {
        string text = string.Join(" ", lines.Where(l => l.Length > 0));
        if (added > 0) text += " " + (added == 1 ? Localizer.Text("PickerOneNew") : Localizer.Format("PickerNew", added));
        return text.Trim();
    }

    private static void Reload() => Dispatcher.UIThread.Post(() => InsertsViewModel.ReloadAll?.Invoke());
}
