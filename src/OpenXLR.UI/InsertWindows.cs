using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace OpenXLR.UI;

/// <summary>
/// The insert windows the UI keeps open: one controls window per insert
/// and one chain window per mix, reused while open so a second click
/// raises the existing window instead of stacking another.
/// </summary>
public static class InsertWindows
{
    private static readonly Dictionary<string, InsertControlsWindow> Controls = new();
    private static readonly Dictionary<string, MixInsertsWindow> Chains = new();

    public static void OpenControls(Window owner, InsertViewModel insert)
    {
        if (Controls.TryGetValue(insert.Id, out InsertControlsWindow? open)) { open.Activate(); return; }
        var w = new InsertControlsWindow { DataContext = insert };
        w.Closed += (_, _) => Controls.Remove(insert.Id);
        Controls[insert.Id] = w;
        w.Show(owner);
    }

    internal static void CloseChain(InsertsViewModel chain)
    {
        // A layout item can disappear while its chain or controls are open.
        // Clear its session and queued edits before its ID can be reused.
        chain.ResetForNewConnection();
        foreach (var window in Controls.Values.Where(w => w.DataContext is InsertViewModel insert
            && ReferenceEquals(insert.Owner, chain)).ToArray()) window.Close();
        foreach (var window in Chains.Values.Where(w => ReferenceEquals(w.DataContext, chain)).ToArray()) window.Close();
        chain.Apply(null);
    }

    public static void OpenChain(Window owner, InsertsViewModel chain, string key)
    {
        if (Chains.TryGetValue(key, out MixInsertsWindow? open)) { open.Activate(); return; }
        var w = new MixInsertsWindow { DataContext = chain };
        w.Closed += (_, _) => Chains.Remove(key);
        Chains[key] = w;
        w.Show(owner);
    }
}
