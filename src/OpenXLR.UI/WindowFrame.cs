using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>How the main window is drawn at a given moment.</summary>
internal enum WindowOutline
{
    /// <summary>The platform's own window and title bar, exactly as the UI has always been.</summary>
    System,
    /// <summary>OpenXLR's frame with the skin's rounded corners, transparent outside them.</summary>
    Rounded,
    /// <summary>OpenXLR's frame with square corners, for a maximised or full screen window.</summary>
    Square,
}

/// <summary>
/// Gives a window the outline the skin in force asks for in its
/// <c>window</c> section. The rectangle, which every shipped skin keeps, is
/// the window untouched: its own content, the platform's title bar and
/// borders, and no transparency asked of the platform.
///
/// The rounded outline needs a window the platform composites with
/// transparency, which is asked for only while a skin wants it. Where the
/// platform reports none (X11 without a compositor, or any platform that
/// refuses) the window stays the rectangle, silently: the skin's other values
/// still apply and nothing is reported, since a desktop without a compositor
/// is not the skin's fault. When it is drawn, the platform's title bar goes,
/// so the frame carries its own caption row (drag to move, double click to
/// maximise, and minimise, maximise and close buttons) and resize edges on
/// all four sides and corners. The window's own content is moved into the
/// frame unchanged and moved back when the outline goes, so no control is
/// rebuilt and every view keeps its state.
///
/// A maximised or full screen window drops the corners and the resize edges
/// so it fills the screen, and gets them back when it is restored.
/// </summary>
internal sealed class WindowFrame : IDisposable
{
    /// <summary>The width of the resize edges along each side.</summary>
    internal const double EdgeSize = 6;

    /// <summary>The size of the resize squares in the corners.</summary>
    internal const double CornerSize = 14;

    /// <summary>
    /// Stands in for the platform's answer about transparency, for the tests:
    /// Xvfb has no compositor, so the shaped path is reached through this.
    /// Null asks the platform.
    /// </summary>
    internal static Func<Window, bool>? TransparencyProbe { get; set; }

    /// <summary>
    /// Stands in for the window's state, for the tests: a window under Xvfb
    /// has no window manager to maximise it, so the platform keeps it normal.
    /// Null reads the window.
    /// </summary>
    internal static Func<Window, WindowState>? StateProbe { get; set; }

    private WindowState State => StateProbe?.Invoke(_window) ?? _window.WindowState;

    private static readonly Geometry MinimizeGlyph = Geometry.Parse("M0,8.5 L10,8.5");
    private static readonly Geometry MaximizeGlyph = Geometry.Parse("M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z");
    private static readonly Geometry RestoreGlyph = Geometry.Parse("M2.5,2.5 L2.5,0.5 L9.5,0.5 L9.5,7.5 L7.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z");
    private static readonly Geometry CloseGlyph = Geometry.Parse("M0.5,0.5 L9.5,9.5 M9.5,0.5 L0.5,9.5");

    private readonly Window _window;
    private Border? _frame;
    private Grid? _caption;
    private Decorator? _host;
    private Panel? _edges;
    private Button? _maximize;
    private Path? _maximizeGlyph;
    private Control? _content;
    private bool _hinted;
    private bool _wrapped;
    private bool _updating;

    public WindowFrame(Window window)
    {
        _window = window;
        SkinService.Changed += Update;
        _window.PropertyChanged += OnWindowPropertyChanged;
        Update();
    }

    /// <summary>How the window is drawn now.</summary>
    public WindowOutline Outline { get; private set; } = WindowOutline.System;

    /// <summary>The frame while it is drawn, for the tests.</summary>
    internal Border? Frame => _wrapped ? _frame : null;

    internal Grid? Caption => _wrapped ? _caption : null;

    internal Panel? Edges => _wrapped ? _edges : null;

    /// <summary>
    /// The outline a window gets: the skin's shape where the platform can
    /// draw it, square corners while the window fills the screen, and the
    /// platform's own window otherwise.
    /// </summary>
    internal static WindowOutline Decide(string shape, bool transparent, WindowState state) =>
        shape != SkinWindowShapes.Rounded || !transparent ? WindowOutline.System
        : state is WindowState.Maximized or WindowState.FullScreen ? WindowOutline.Square
        : WindowOutline.Rounded;

    public void Dispose()
    {
        SkinService.Changed -= Update;
        _window.PropertyChanged -= OnWindowPropertyChanged;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == TopLevel.ActualTransparencyLevelProperty)
            Update();
    }

    /// <summary>Wear the outline the skin and the platform call for now.</summary>
    public void Update()
    {
        // Asking for transparency can answer at once, which comes back here
        // through the property change; the outer call reads the answer.
        if (_updating) return;
        _updating = true;
        try
        {
            string shape = SkinService.Current.Package.WindowShape;
            bool wants = shape != SkinWindowShapes.Rectangle;
            if (wants != _hinted)
            {
                _hinted = wants;
                if (wants) _window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
                else _window.ClearValue(TopLevel.TransparencyLevelHintProperty);
            }
            bool transparent = wants && (TransparencyProbe?.Invoke(_window)
                ?? _window.ActualTransparencyLevel == WindowTransparencyLevel.Transparent);
            Show(Decide(shape, transparent, State));
        }
        finally { _updating = false; }
    }

    private void Show(WindowOutline outline)
    {
        Outline = outline;
        if (outline == WindowOutline.System)
        {
            Unwrap();
            return;
        }
        Wrap();

        bool square = outline == WindowOutline.Square;
        CornerRadius radius = square ? default
            : _window.FindResource("Ox.Window.CornerRadius") is CornerRadius r ? r : default;
        Thickness border = square ? default
            : _window.FindResource("Ox.Window.BorderThickness") is Thickness t ? t : default;
        _frame!.CornerRadius = radius;
        _frame.BorderThickness = border;
        // Nothing the UI draws reaches into a rounded corner: the caption's
        // buttons start where the top corners end, and the content keeps a
        // margin at the sides and the foot that the curve does not cut.
        double inset = Math.Ceiling(0.3 * Math.Max(Math.Max(radius.TopLeft, radius.TopRight),
            Math.Max(radius.BottomLeft, radius.BottomRight)));
        _caption!.Margin = new Thickness(Math.Max(radius.TopLeft, 4), 0, Math.Max(radius.TopRight, 4), 0);
        _host!.Margin = new Thickness(inset, 0, inset, inset);
        _edges!.IsVisible = !square;

        bool maximized = State == WindowState.Maximized;
        _maximizeGlyph!.Data = maximized ? RestoreGlyph : MaximizeGlyph;
        string label = maximized ? Localizer.Text("RestoreWindow") : Localizer.Text("MaximizeWindow");
        ToolTip.SetTip(_maximize!, label);
        AutomationProperties.SetName(_maximize!, label);
        _maximize!.IsVisible = _window.CanResize;
    }

    /// <summary>Move the window's content into the frame and take the platform's title bar away.</summary>
    private void Wrap()
    {
        if (_wrapped) return;
        _frame ??= Build();
        _content = _window.Content as Control;
        _window.Content = null;
        _host!.Child = _content;
        _window.Content = _frame;
        _window.WindowDecorations = WindowDecorations.None;
        _window.Background = Brushes.Transparent;
        _wrapped = true;
    }

    /// <summary>Put the content back where it was and give the window back to the platform.</summary>
    private void Unwrap()
    {
        if (!_wrapped) return;
        _window.Content = null;
        _host!.Child = null;
        _window.Content = _content;
        _content = null;
        _window.ClearValue(Window.WindowDecorationsProperty);
        _window.ClearValue(Window.BackgroundProperty);
        _wrapped = false;
    }

    private Border Build()
    {
        var frame = new Border { Name = "WindowFrame", ClipToBounds = true };
        frame.Bind(Border.BackgroundProperty, new DynamicResourceExtension("Ox.Window.Background"));
        frame.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("Ox.Window.BorderBrush"));

        _caption = new Grid { Name = "WindowCaption", ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        DockPanel.SetDock(_caption, Dock.Top);
        // The drag area: the whole row the buttons leave, transparent so it
        // takes the pointer.
        var drag = new Border { Background = Brushes.Transparent, MinHeight = 28 };
        drag.PointerPressed += OnCaptionPressed;
        _caption.Children.Add(drag);

        Button minimize = CaptionButton(MinimizeGlyph, out _);
        ToolTip.SetTip(minimize, Localizer.Text("MinimizeWindow"));
        AutomationProperties.SetName(minimize, Localizer.Text("MinimizeWindow"));
        minimize.Name = "WindowMinimize";
        minimize.Click += (_, _) => _window.WindowState = WindowState.Minimized;
        Grid.SetColumn(minimize, 1);
        _caption.Children.Add(minimize);

        _maximize = CaptionButton(MaximizeGlyph, out _maximizeGlyph);
        _maximize.Name = "WindowMaximize";
        _maximize.Click += (_, _) => ToggleMaximized();
        Grid.SetColumn(_maximize, 2);
        _caption.Children.Add(_maximize);

        Button close = CaptionButton(CloseGlyph, out _);
        ToolTip.SetTip(close, Localizer.Text("Close"));
        AutomationProperties.SetName(close, Localizer.Text("Close"));
        close.Name = "WindowClose";
        // The window's own close, so the tray setting decides what it does
        // exactly as it does for the platform's close button.
        close.Click += (_, _) => _window.Close();
        Grid.SetColumn(close, 3);
        _caption.Children.Add(close);

        _host = new Decorator();
        var body = new DockPanel { LastChildFill = true };
        body.Children.Add(_caption);
        body.Children.Add(_host);

        _edges = BuildEdges();
        frame.Child = new Grid { Children = { body, _edges } };
        return frame;
    }

    private static Button CaptionButton(Geometry glyph, out Path path)
    {
        path = new Path
        {
            Data = glyph, StrokeThickness = 1.2, Width = 10, Height = 10, Stretch = Stretch.None,
            StrokeLineCap = PenLineCap.Square,
        };
        path.Bind(Shape.StrokeProperty, new DynamicResourceExtension("Ox.Text.Primary"));
        var button = new Button
        {
            Content = path, Padding = new Thickness(12, 8), Margin = new Thickness(2, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add("ghost");
        return button;
    }

    private void ToggleMaximized() => _window.WindowState =
        State == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCaptionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2 && _window.CanResize) ToggleMaximized();
        else _window.BeginMoveDrag(e);
    }

    /// <summary>
    /// The resize edges: a thin strip along each side and a square in each
    /// corner, over the content, each handing its edge to the platform's own
    /// resize. The middle is empty, so the pointer reaches the content there.
    /// </summary>
    private Panel BuildEdges()
    {
        var edges = new Panel { Name = "WindowEdges" };
        void Add(WindowEdge edge, HorizontalAlignment h, VerticalAlignment v, double width, double height, StandardCursorType cursor)
        {
            var grip = new Border
            {
                Background = Brushes.Transparent, HorizontalAlignment = h, VerticalAlignment = v,
                Width = width, Height = height, Cursor = new Cursor(cursor), Tag = edge,
            };
            grip.PointerPressed += (_, e) =>
            {
                if (!_window.CanResize || !e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                _window.BeginResizeDrag(edge, e);
            };
            edges.Children.Add(grip);
        }
        const HorizontalAlignment Stretch = HorizontalAlignment.Stretch, Left = HorizontalAlignment.Left, Right = HorizontalAlignment.Right;
        const VerticalAlignment Fill = VerticalAlignment.Stretch, Top = VerticalAlignment.Top, Bottom = VerticalAlignment.Bottom;
        Add(WindowEdge.North, Stretch, Top, double.NaN, EdgeSize, StandardCursorType.TopSide);
        Add(WindowEdge.South, Stretch, Bottom, double.NaN, EdgeSize, StandardCursorType.BottomSide);
        Add(WindowEdge.West, Left, Fill, EdgeSize, double.NaN, StandardCursorType.LeftSide);
        Add(WindowEdge.East, Right, Fill, EdgeSize, double.NaN, StandardCursorType.RightSide);
        Add(WindowEdge.NorthWest, Left, Top, CornerSize, CornerSize, StandardCursorType.TopLeftCorner);
        Add(WindowEdge.NorthEast, Right, Top, CornerSize, CornerSize, StandardCursorType.TopRightCorner);
        Add(WindowEdge.SouthWest, Left, Bottom, CornerSize, CornerSize, StandardCursorType.BottomLeftCorner);
        Add(WindowEdge.SouthEast, Right, Bottom, CornerSize, CornerSize, StandardCursorType.BottomRightCorner);
        return edges;
    }
}
