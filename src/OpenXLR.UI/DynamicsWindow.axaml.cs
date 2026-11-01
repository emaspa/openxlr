using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Threading;

namespace OpenXLR.UI;

/// <summary>
/// One microphone's compressor or expander on this computer. While it is open
/// it keeps asking the daemon for the processor's levels, which go out only
/// while some window shows them, and lets levels that stopped coming go.
/// </summary>
public partial class DynamicsWindow : Window
{
    /// <summary>How often the levels are asked for again; the daemon's lease is three times as long.</summary>
    internal static readonly TimeSpan KeepEvery = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _ticks;

    public DynamicsWindow()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Tick();
        Opened += (_, _) =>
        {
            KeepLevels();
            _tick.Start();
        };
        Closed += (_, _) => _tick.Stop();
        // Touch sizing, or a skin's own control minimum, enlarges the targets here as in the mixer.
        UpdateControlSizing();
        Skinning.SkinService.Changed += UpdateControlSizing;
        Closed += (_, _) => Skinning.SkinService.Changed -= UpdateControlSizing;
    }

    private void UpdateControlSizing() => Classes.Set("large-targets", Skinning.SkinService.LargeTargets);

    private MicDynamicsViewModel? Processor => DataContext as MicDynamicsViewModel;

    /// <summary>The graph, for the tests.</summary>
    internal DynamicsGraph Graph => this.FindControl<DynamicsGraph>("DynamicsGraph")!;

    private void Tick()
    {
        Processor?.CheckLevels();
        if (++_ticks % (int)KeepEvery.TotalSeconds == 0) KeepLevels();
    }

    private void KeepLevels() { if (Processor is { } vm) _ = vm.KeepLevelsAsync(); }
}

/// <summary>
/// The compressor and expander windows open now: one per processor of a
/// microphone, reused while open, so a second press raises the window rather
/// than stacking another.
/// </summary>
public static class DynamicsWindows
{
    private static readonly Dictionary<MicDynamicsViewModel, DynamicsWindow> Open = new();

    public static DynamicsWindow Show(Window owner, MicDynamicsViewModel processor)
    {
        if (Open.TryGetValue(processor, out DynamicsWindow? open))
        {
            // A window's owner cannot change while it is shown: reopen under the window that asked.
            if (open.Owner == owner)
            {
                if (!open.IsVisible) open.Show(owner);
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                open.Activate();
                return open;
            }
            open.Close();
        }
        var window = new DynamicsWindow { DataContext = processor };
        window.Closed += (_, _) => { if (Open.TryGetValue(processor, out DynamicsWindow? kept) && kept == window) Open.Remove(processor); };
        Open[processor] = window;
        window.Show(owner);
        return window;
    }

    /// <summary>The window open for this processor, or null.</summary>
    public static DynamicsWindow? Find(MicDynamicsViewModel processor) => Open.GetValueOrDefault(processor);

    /// <summary>Close the window of a processor the daemon no longer reports.</summary>
    internal static void Close(MicDynamicsViewModel processor)
    {
        if (Open.TryGetValue(processor, out DynamicsWindow? window)) window.Close();
    }
}
