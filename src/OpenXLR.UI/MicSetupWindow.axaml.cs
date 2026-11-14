using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OpenXLR.UI;

/// <summary>
/// Mic setup for one microphone at a time. While it is open it keeps the
/// daemon's meter before the chain running. Closing it stops the session; a
/// close the user asked for waits for that stop, with the window disabled, so
/// nothing can be queued behind it. One is open at a time: opening it again,
/// from the mixer's header or another input's chain, moves the open window to
/// that microphone.
/// </summary>
public partial class MicSetupWindow : Window
{
    /// <summary>How often the meter's lease is renewed: well inside the daemon's fifteen seconds.</summary>
    internal static readonly TimeSpan MeterRenewal = TimeSpan.FromSeconds(5);

    private static MicSetupWindow? _open;

    private readonly DispatcherTimer _meter = new() { Interval = MeterRenewal };
    // Suggest gain's and Measure room noise's countdown, and their end when the meter sends nothing.
    private readonly DispatcherTimer _measure = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _closing, _closed, _moving;
    private MicSetupViewModel? _watched;

    public MicSetupWindow()
    {
        InitializeComponent();
        _meter.Tick += (_, _) => KeepMeter();
        _measure.Tick += (_, _) =>
        {
            Setup?.MeasureTick();
            if (Setup is not ({ Measuring: true } or { MeasuringRoom: true })) _measure.Stop();
        };
        FitScreen();
        Opened += (_, _) => { FitScreen(); KeepMeter(); _meter.Start(); };
        Closed += (_, _) =>
        {
            _meter.Stop();
            _measure.Stop();
            // A normal close already ended its measurement before waiting.
            // Another window may now be measuring the same input.
            if (!_closing) Setup?.CancelMeasures();
            Watch(null);
            if (_open == this) _open = null;
        };
        Closing += async (_, e) =>
        {
            if (_closed || DataContext is not MicSetupViewModel vm) return;
            if (e.CloseReason != WindowCloseReason.WindowClosing)
            {
                _ = vm.StopOnCloseAsync();
                return;
            }
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            if (_open == this) _open = null;
            _meter.Stop();
            _measure.Stop();
            vm.CancelMeasures();
            IsEnabled = false; // Do not queue a new recording behind the pending stop.
            await vm.StopOnCloseAsync();
            _closed = true;
            Close();
        };
        DataContextChanged += (_, _) => { Watch(Setup); _refit = true; };
        LayoutUpdated += (_, _) => FitContent();
        // A size the window took for its content keeps its bottom on the screen; one the user dragged is left alone.
        Resized += (_, e) => { if (e.Reason != WindowResizeReason.User) KeepOnScreen(); };
    }

    /// <summary>
    /// Open Mic setup on <paramref name="setup"/>'s microphone, or move the
    /// window already open to it. The window that is closing does not count:
    /// it waits for its stop, and a new one opens beside it.
    /// </summary>
    public static MicSetupWindow Open(Window owner, MicSetupViewModel setup)
    {
        if (_open is { } open)
        {
            _ = open.MoveToAsync(setup);
            open.Activate();
            return open;
        }
        var window = new MicSetupWindow { DataContext = setup };
        _open = window;
        window.Show(owner);
        return window;
    }

    private MicSetupViewModel? Setup => DataContext as MicSetupViewModel;

    /// <summary>The daemon removed this input and its session. Close without stopping a replacement with the same id.</summary>
    internal static void CloseRemovedInput(MicSetupViewModel setup)
    {
        if (_open is not { } window || !ReferenceEquals(window.Setup, setup)) return;
        window._closed = true;
        window.Close();
    }

    private void KeepMeter() { if (Setup is { } vm) _ = vm.KeepMeterAsync(); }

    // --- the microphone picker ---

    private async void OnPickInput(object? sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is MicSetupInput { } pick) await MoveToAsync(pick.Setup);
    }

    /// <summary>
    /// Move the window to another microphone: what runs on this one stops
    /// first, with the window disabled meanwhile, then the window shows the
    /// other one on the same step and keeps its meter running.
    /// </summary>
    internal async Task MoveToAsync(MicSetupViewModel next)
    {
        if (Setup is not { } current || ReferenceEquals(current, next) || _moving || _closing) return;
        _moving = true;
        _measure.Stop();
        IsEnabled = false;
        bool moved;
        try { moved = await current.HandOverAsync(next); }
        finally
        {
            _moving = false;
            if (!_closing && !_closed) IsEnabled = true;
        }
        if (_closing || _closed) return;
        if (!moved) { current.RaiseInputs(); return; }
        DataContext = next;
        KeepMeter();
    }

    // --- size ---

    // The content's height when last looked at, at the width it had then.
    private (double Width, double Need)? _seen;
    // A new step or microphone: grow to the content whatever size the user left.
    private bool _refit = true;

    private void Watch(MicSetupViewModel? setup)
    {
        if (_watched is not null) _watched.PropertyChanged -= OnSetupChanged;
        _watched = setup;
        if (setup is not null) setup.PropertyChanged += OnSetupChanged;
    }

    private void OnSetupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MicSetupViewModel.Step)) _refit = true;
    }

    /// <summary>
    /// Keep the window inside the screen it opens on: no taller or wider than
    /// its working area less the frame, and its minimum no larger than that.
    /// Past that height the steps scroll.
    /// </summary>
    private void FitScreen()
    {
        var screen = Screens.ScreenFromWindow(Owner as Window ?? this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        Size frame = FrameSize ?? ClientSize;
        var decoration = new Size(Math.Max(0, frame.Width - ClientSize.Width), Math.Max(0, frame.Height - ClientSize.Height));
        Size limit = FlowWindow.FitClientSize(Size.Infinity, screen.WorkingArea.Size, screen.Scaling, decoration);
        MaxWidth = limit.Width;
        MaxHeight = limit.Height;
        MinWidth = Math.Min(420, limit.Width);
        MinHeight = Math.Min(400, limit.Height);
    }

    /// <summary>Move the window up as far as its frame needs to end inside the working area, never above its top.</summary>
    private void KeepOnScreen()
    {
        if (!IsVisible || Screens.ScreenFromWindow(this) is not { } screen) return;
        PixelRect area = screen.WorkingArea;
        Size frame = FrameSize ?? ClientSize;
        int height = (int)Math.Ceiling(frame.Height * screen.Scaling);
        if (Position.Y + height <= area.Bottom) return;
        Position = Position.WithY(Math.Max(area.Y, area.Bottom - height));
    }

    /// <summary>The client height that shows the whole step without scrolling, at the width the window has now.</summary>
    internal double NeededHeight
        => ClientSize.Height - MicSetupScroll.Viewport.Height + MicSetupContent.DesiredSize.Height;

    /// <summary>
    /// Until the user resizes it the window follows its content by itself
    /// (SizeToContent), growing and shrinking with the step. A size the user
    /// chose stays: the window only grows from it, when another step or
    /// microphone is shown or the content gets taller at the same width, and
    /// never past the screen. A width the user changes is the user's too, so
    /// the taller wrapping it brings is left to the scroll bar.
    /// </summary>
    private void FitContent()
    {
        if (!IsVisible || _closing) return;
        double width = ClientSize.Width, need = NeededHeight;
        bool grew = _seen is { } seen && Math.Abs(seen.Width - width) < 0.5 && need > seen.Need + 0.5;
        bool refit = _refit;
        _refit = false;
        _seen = (width, need);
        if (SizeToContent != SizeToContent.Manual || !(grew || refit)) return;
        if (need > ClientSize.Height + 0.5 && ClientSize.Height < MaxHeight - 0.5)
            Height = Math.Min(need, MaxHeight);
    }

    // --- the steps ---

    private void OnStep(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MicSetupStepItem item) item.IsSelected = true;
    }

    private void OnClearClip(object? sender, RoutedEventArgs e) => Setup?.ClearClip();

    private void OnSuggestGain(object? sender, RoutedEventArgs e)
    {
        if (Setup is not { } vm) return;
        vm.StartGainMeasure();
        _measure.Start();
    }

    private void OnMeasureRoom(object? sender, RoutedEventArgs e)
    {
        if (Setup is not { } vm) return;
        vm.StartRoomMeasure();
        if (vm.MeasuringRoom) _measure.Start();
    }

    private void OnApplyRoom(object? sender, RoutedEventArgs e) => Setup?.ApplyRoom();
    private void OnDiscardRoom(object? sender, RoutedEventArgs e) => Setup?.DiscardRoom();

    /// <summary>The expander's own window, with its levels, graph and the rest of its settings.</summary>
    private void OnExpanderWindow(object? sender, RoutedEventArgs e)
    {
        if (Setup?.Expander is { } expander) DynamicsWindows.Show(this, expander);
    }

    private void OnAcceptGain(object? sender, RoutedEventArgs e) => Setup?.AcceptGain();
    private void OnDiscardGain(object? sender, RoutedEventArgs e) => Setup?.DiscardGain();
    private async void OnRecord(object? sender, RoutedEventArgs e) { if (Setup is { } vm) await vm.RunAsync("record"); }
    private async void OnLoop(object? sender, RoutedEventArgs e) { if (Setup is { } vm) await vm.RunAsync("loop"); }
    private async void OnLive(object? sender, RoutedEventArgs e) { if (Setup is { } vm) await vm.RunAsync("live"); }
    private async void OnStop(object? sender, RoutedEventArgs e) { if (Setup is { } vm) await vm.RunAsync("stop"); }
    private void OnProcessed(object? sender, RoutedEventArgs e) { if (Setup is { } vm) vm.ListenProcessed = true; }
    private void OnRaw(object? sender, RoutedEventArgs e) { if (Setup is { } vm) vm.ListenRaw = true; }
    private void OnHearMyself(object? sender, RoutedEventArgs e) { if (Setup is { } vm) vm.HearMyself = !vm.HearMyself; }
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // --- Shape ---

    private void OnPreset(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MicSetupPresetItem preset) Setup?.ChoosePreset(preset);
    }

    private void OnUndoPreset(object? sender, RoutedEventArgs e) => Setup?.UndoPreset();

    /// <summary>The full editor, with a row of fields per band, on the same EQ.</summary>
    private void OnEqEditor(object? sender, RoutedEventArgs e)
    {
        if (Setup?.Eq is { } eq) EqWindows.Show(this, eq);
    }

    private void OnCycleSoftLowCut(object? sender, RoutedEventArgs e) => Setup?.Software?.CycleSoftLowCut();

    private async void OnAddEffect(object? sender, RoutedEventArgs e)
    {
        if (Setup?.Chain is not { CanEditEffects: true } chain) return;
        PluginChoice? choice = await new PluginPickerWindow { DataContext = chain }.ShowDialog<PluginChoice?>(this);
        if (choice is not null) chain.Add(choice);
    }

    /// <summary>
    /// As the chain's cog does: a plugin running in its own process opens its
    /// own editor, anything else the generated controls.
    /// </summary>
    private void OnInsertRemove(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel insert) insert.Owner.Remove(insert);
    }

    private async void OnInsertEditor(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not InsertViewModel insert) return;
        if (insert.NativeEditorAvailable) await insert.Owner.ShowNativeEditorAsync(insert);
        else InsertWindows.OpenControls(this, insert);
    }
}
