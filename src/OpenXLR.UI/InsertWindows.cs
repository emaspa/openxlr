using System.Collections.Generic;
using Avalonia.Controls;

namespace OpenXLR.UI;

/// <summary>
/// The insert windows the UI keeps open: one controls window per insert
/// and one chain window per mix, reused while open so a second click
/// raises the existing window instead of stacking another.
/// </summary>
public static class InsertWindows
{
    private static readonly Dictionary<InsertViewModel, InsertControlsWindow> Controls = new();
    private static readonly Dictionary<string, MixInsertsWindow> Chains = new();

    public static void OpenControls(Window owner, InsertViewModel insert)
    {
        if (Controls.TryGetValue(insert, out InsertControlsWindow? open)) { open.Activate(); return; }
        var w = new InsertControlsWindow { DataContext = insert };
        w.Closed += (_, _) => Controls.Remove(insert);
        Controls[insert] = w;
        w.Show(owner);
    }

    internal static void CloseControls(InsertViewModel insert)
    {
        if (Controls.TryGetValue(insert, out var window)) window.Close();
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
