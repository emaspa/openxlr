using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>The main window's mixer panel, moved here without creating another client or mixer.</summary>
public partial class MixerWindow : Window
{
    private Control? _panel;
    private double _minimum;
    private Size _decoration;
    private Screen? _screen;
    /// <summary>The size chosen: <see cref="MixerScale.Fit"/> or a whole percentage, as <see cref="MixerScale.Known"/> writes it.</summary>
    private string _scaleChoice;
    /// <summary>True while the picker shows Custom and the percentage box under it.</summary>
    private bool _custom;
    /// <summary>The factor the panel is drawn at now; for Fit, the one it found last.</summary>
    private double _scale = 1;
    /// <summary>True while the panel is being sized, so the sizes it tries do not start another round.</summary>
    private bool _sizing;
    private bool _fitPending;
    /// <summary>The panel's height, before enlarging, when Fit last sized it.</summary>
    private double _fittedHeight = double.NaN;

    public MixerWindow()
    {
        InitializeComponent();
        _scaleChoice = MixerScale.Known(UiSettings.Load().MixerScale) ?? MixerScale.Default;
        // A saved size the picker does not list is a custom one.
        _custom = !MixerScale.Listed(_scaleChoice);
        MixerScalePicker.ItemsSource = MixerScale.Choices.Select(c => c == MixerScale.Fit
                ? Localizer.Text("MixerScaleFit")
                : Localizer.Format("MixerScalePercent", int.Parse(c, CultureInfo.InvariantCulture)))
            .Append(Localizer.Text("MixerScaleCustom")).ToArray();
        MixerScalePicker.SelectedIndex = _custom ? CustomIndex : IndexOf(_scaleChoice);
        MixerScalePicker.SelectionChanged += OnScaleChosen;
        MixerScaleCustom.Minimum = MixerScale.MinimumPercent;
        MixerScaleCustom.Maximum = MixerScale.MaximumPercent;
        MixerScaleCustom.Increment = MixerScale.FitStepPercent;
        MixerScaleCustom.Value = MixerScale.Percent(_scaleChoice) ?? MixerScale.MinimumPercent;
        MixerScaleCustomRow.IsVisible = _custom;
        MixerScaleCustom.ValueChanged += OnCustomValueChanged;
        // Enter and leaving the box settle what was typed, whether or not the box handled the key.
        MixerScaleCustom.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) CommitCustom();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        MixerScaleCustom.LostFocus += (_, _) => CommitCustom();
        // The main window's Ctrl+G, for a mixer that was popped out of it.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!MainWindow.IsGlitchKey(e) || DataContext is not MainViewModel model) return;
            e.Handled = true;
            _ = model.MarkGlitchAsync();
        }, RoutingStrategies.Tunnel);
        UpdateControlSizing();
        SkinService.Changed += UpdateControlSizing;
        MixerScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty) UpdatePanelWidth();
        };
        // Fit follows the panel too: a channel added or removed, or a view or a
        // skin that draws it taller or shorter. The scroll viewer's extent
        // cannot say so, since it never shrinks below the viewport, so this
        // compares the panel's own height with the one Fit last sized it at.
        PanelScale.LayoutUpdated += (_, _) =>
        {
            if (_scaleChoice != MixerScale.Fit || _sizing || _fitPending || _panel is not { IsMeasureValid: true } panel
                || Math.Abs(panel.DesiredSize.Height - _fittedHeight) <= 0.5) return;
            // Once the layout has settled, so the sizes it tries see the panel as it now is.
            _fitPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _fitPending = false;
                UpdatePanelWidth();
            }, DispatcherPriority.Background);
        };
        Closed += (_, _) => SkinService.Changed -= UpdateControlSizing;
        HistoryKeys.Attach(this, redo =>
        {
            if (DataContext is MainViewModel model) _ = model.StepHistoryAsync(redo);
        });
    }

    internal void Attach(Control panel, object? model, Window origin)
    {
        DataContext = model;
        _panel = panel;
        PanelScale.Child = panel;
        _minimum = Minimum();
        UpdatePanelWidth();
        // Start on the originating screen, with enough room for its frame.
        // The window stays unowned, so hiding the main window leaves this one usable.
        if (origin.Screens.ScreenFromWindow(origin) is { } screen)
        {
            Size frame = origin.FrameSize ?? origin.ClientSize;
            var decoration = new Size(Math.Max(0, frame.Width - origin.ClientSize.Width),
                Math.Max(0, frame.Height - origin.ClientSize.Height));
            _screen = screen;
            _decoration = decoration;
            var size = FlowWindow.FitClientSize(new(Width, Height), screen.WorkingArea.Size, screen.Scaling, decoration);
            MinWidth = Math.Min(MinWidth, size.Width);
            MinHeight = Math.Min(MinHeight, size.Height);
            Width = size.Width;
            Height = size.Height;
            Position = screen.WorkingArea.Position;
        }
    }

    /// <summary>
    /// Give the panel back as it came: no width, no scale and none of the
    /// transform the scaling put on it, so the main window draws it at the
    /// skin's own size.
    /// </summary>
    internal Control Detach()
    {
        var panel = PanelScale.Child!;
        PanelScale.Child = null;
        PanelScale.LayoutTransform = null;
        _scale = 1;
        _panel = null;
        if (panel is Views.MixerPanel mixer) mixer.SetMatrixFillWidth(null);
        panel.ClearValue(WidthProperty);
        panel.ClearValue(RenderTransformProperty);
        panel.ClearValue(RenderTransformOriginProperty);
        DataContext = null;
        return panel;
    }

    /// <summary>The panel while it is in this window, for the tests.</summary>
    internal Control? Panel => _panel;

    /// <summary>The size chosen, for the tests.</summary>
    internal string ScaleChoice => _scaleChoice;

    /// <summary>True while the picker shows Custom, for the tests.</summary>
    internal bool CustomScale => _custom;

    /// <summary>The factor the panel is drawn at now, for the tests.</summary>
    internal double Scale => _scale;

    private void UpdateControlSizing()
    {
        Classes.Set("large-targets", SkinService.LargeTargets);
        UpdatePanelWidth();
    }

    /// <summary>
    /// Keep wrapping at the viewport width, which at a scale is the width the
    /// panel has before it is enlarged. A panel that needs more than the
    /// window has, wide tiles or banks side by side, scrolls sideways instead
    /// of clipping a control. How much it needs is the active presentation's
    /// to say: the wider tile for the channel tiles, or what the banks need.
    /// </summary>
    internal void UpdatePanelWidth()
    {
        if (_panel is null || _sizing) return;
        _sizing = true;
        try
        {
            double minimum = Minimum() + 8;
            bool fit = _scaleChoice == MixerScale.Fit;
            if (_panel is Views.MixerPanel mixer)
                mixer.SetMatrixFillWidth(fit && MixerScroll.Viewport.Width >= minimum);
            double scale = fit ? FitScale(_panel, minimum) : MixerScale.Factor(_scaleChoice);
            _panel.Width = Math.Max(WrapWidth(scale), minimum);
            ApplyScale(scale);
            if (fit)
            {
                _panel.Measure(new Size(_panel.Width, double.PositiveInfinity));
                _fittedHeight = _panel.DesiredSize.Height;
            }
        }
        finally { _sizing = false; }
    }

    /// <summary>
    /// The width the panel wraps at for a scale. Enlarged, it stays a unit
    /// short of the viewport, so rounding to the screen's pixels after the
    /// enlargement never leaves a sliver to scroll sideways.
    /// </summary>
    private double WrapWidth(double scale) => scale == 1 ? MixerScroll.Viewport.Width
        : Math.Floor(MixerScroll.Viewport.Width / scale) - 1;

    /// <summary>
    /// The largest scale at which the panel shows whole: as wide as its
    /// presentation needs and, wrapped at that scale's width, no taller than
    /// the viewport. Never under 1, where a window too small for the panel
    /// scrolls as it always has. Before the window has a size it keeps the
    /// scale it has.
    /// </summary>
    private double FitScale(Control panel, double minimum)
    {
        Size viewport = MixerScroll.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0) return _scale;
        return MixerScale.LargestFit(scale =>
        {
            double width = WrapWidth(scale);
            if (width < minimum) return false;
            panel.Width = width;
            panel.Measure(new Size(width, double.PositiveInfinity));
            return panel.DesiredSize.Height * scale <= viewport.Height - 1;
        });
    }

    private void ApplyScale(double scale)
    {
        if (scale == _scale && (PanelScale.LayoutTransform is null) == (scale == 1)) return;
        _scale = scale;
        // At 1 there is no transform at all, so the panel lays out as it did before sizes existed.
        PanelScale.LayoutTransform = scale == 1 ? null : new ScaleTransform(scale, scale);
    }

    private static int IndexOf(string choice) => Math.Max(0, MixerScale.Choices.ToList().IndexOf(choice));

    /// <summary>Custom, which follows the fixed choices in the picker.</summary>
    private static int CustomIndex => MixerScale.Choices.Count;

    /// <summary>
    /// The user picked a size: draw it and keep it for the next time the
    /// mixer pops out. Custom starts at the size drawn now, Fit's included,
    /// and shows the box to type another.
    /// </summary>
    private void OnScaleChosen(object? sender, SelectionChangedEventArgs e)
    {
        int index = MixerScalePicker.SelectedIndex;
        if (index < 0 || index > CustomIndex) return;
        if (index == CustomIndex)
        {
            if (_custom) return;
            _custom = true;
            int percent = MixerScale.Clamp((decimal)Math.Round(_scale * 100));
            MixerScaleCustom.Value = percent;
            MixerScaleCustomRow.IsVisible = true;
            Choose(percent.ToString(CultureInfo.InvariantCulture));
            return;
        }
        _custom = false;
        MixerScaleCustomRow.IsVisible = false;
        Choose(MixerScale.Choices[index]);
    }

    /// <summary>
    /// The box's value changed: from its arrows, the arrow keys or the wheel
    /// at once, and from typing once the text reads as that value. While a
    /// size is typed its first digits read as less than the smallest size
    /// and the box holds them to it, and digits past the largest are held
    /// too; drawing those would make the panel jump about under the typing,
    /// so they wait for Enter or for leaving the box.
    /// </summary>
    private void OnCustomValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_custom || e.NewValue is not decimal value) return;
        if (!decimal.TryParse(MixerScaleCustom.Text, NumberStyles.Number, MixerScaleCustom.NumberFormat, out decimal typed)
            || typed != value) return;
        Choose(MixerScale.Clamp(value).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Settle the box: what was typed, rounded to a whole percent and held to
    /// the range, shows as the size drawn. An empty box goes back to the
    /// size drawn.
    /// </summary>
    private void CommitCustom()
    {
        if (!_custom) return;
        int percent = MixerScaleCustom.Value is decimal value ? MixerScale.Clamp(value)
            : MixerScale.Percent(_scaleChoice) ?? MixerScale.MinimumPercent;
        string text = percent.ToString(CultureInfo.InvariantCulture);
        if (MixerScaleCustom.Value != percent) MixerScaleCustom.Value = percent;
        else if (MixerScaleCustom.Text != text) MixerScaleCustom.Text = text;
        Choose(text);
    }

    /// <summary>Draw a size, and keep it for the next time the mixer pops out.</summary>
    private void Choose(string choice)
    {
        if (choice == _scaleChoice) return;
        _scaleChoice = choice;
        UpdatePanelWidth();
        string? error = (UiSettings.Load() with { MixerScale = _scaleChoice }).Save();
        MixerScaleNote.Text = error is null ? null : Localizer.Format("MixerScaleNotSaved", error);
        MixerScaleNote.IsVisible = error is not null;
    }

    private double Minimum() => _panel is Views.MixerPanel mixer ? mixer.MinimumWidth()
        : Math.Max((double)this.FindResource("Ox.Mixer.MixWidth")!, (double)this.FindResource("Ox.Mixer.ChannelWidth")!);

    /// <summary>
    /// The panel shows another presentation: wrap it at the new width, and
    /// widen the window when the presentation needs more than the one before
    /// it did and more than the window has, never past the screen it opened
    /// on. A presentation that needs less leaves the window as the user
    /// sized it, and so does one that needs more than a window the user made
    /// narrower on purpose, until the need grows again; the panel scrolls
    /// sideways meanwhile. At a fixed size the need is the enlarged one; Fit
    /// needs what the skin's own size does, and shrinks to the window from
    /// there.
    /// </summary>
    internal void FitPresentation()
    {
        if (_panel is null) return;
        double minimum = Minimum();
        bool grew = minimum > _minimum + 0.5;
        _minimum = minimum;
        UpdatePanelWidth();
        // The panel, its own gap, and the window's margin on both sides;
        // enlarged, also the unit WrapWidth leaves free.
        double scale = MixerScale.Factor(_scaleChoice);
        double need = scale == 1 ? minimum + 8 + 16 : Math.Ceiling((Math.Ceiling(minimum + 8) + 1) * scale) + 16;
        if (!grew || ClientSize.Width >= need) return;
        double most = _screen is { } screen
            ? FlowWindow.FitClientSize(new(need, Height), screen.WorkingArea.Size, screen.Scaling, _decoration).Width
            : need;
        Width = Math.Max(ClientSize.Width, Math.Min(need, most));
    }

    /// <summary>The narrowest the panel's presentation was drawn at when the window last fitted it, for the tests.</summary>
    internal double FittedMinimum => _minimum;

    private void OnDockMixer(object? sender, RoutedEventArgs e) => Close();
}
