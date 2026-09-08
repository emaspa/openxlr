using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenXLR.UI;

/// <summary>Live routing with selectable paths. Selection never changes the mixer.</summary>
public partial class FlowWindow : Window
{
    private readonly MainViewModel? _vm;
    private FlowGraph _graph = new([], []);
    private string? _selected;
    private readonly Dictionary<string, Button> _cards = [];
    private readonly List<RouteVisual> _routes = [];
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private sealed record RouteVisual(FlowRoute Route, Path Line, Path Glow, Ellipse Dot, Point Start, Point End);

    public FlowWindow()
    {
        InitializeComponent();
        GraphScroll.SizeChanged += (_, _) => RenderGraph();
        GraphCanvas.PointerPressed += (_, e) =>
        {
            if (e.Source == GraphCanvas) Select(null);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Select(null); e.Handled = true; }
        };
        _animation.Tick += (_, _) => Animate();
        Opened += (_, _) => { ConstrainToScreen(); Rebuild(); };
        Closed += (_, _) => _animation.Stop();
    }

    public FlowWindow(MainViewModel vm) : this()
    {
        _vm = vm;
        vm.StateApplied += Rebuild;
        vm.PropertyChanged += OnStatePropertyChanged;
        ConstrainToScreen();
        Rebuild();
        Closed += (_, _) =>
        {
            vm.StateApplied -= Rebuild;
            vm.PropertyChanged -= OnStatePropertyChanged;
        };
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.DaemonConnected) or nameof(MainViewModel.HasMixer))
            UpdateHint();
    }

    private IBrush Brush(string name) => (IBrush)Resources[name]!;
    private IBrush RouteBrush(FlowStage stage) => Brush(stage switch
    {
        FlowStage.Input => "FlowInput", FlowStage.Channel => "FlowChannel", _ => "FlowOutput",
    });

    private void Rebuild()
    {
        if (_vm is null) return;
        FlowGraph next = FlowGraph.From(_vm);
        UpdateHint();
        if (_graph.Nodes.SequenceEqual(next.Nodes) && _graph.Routes.SequenceEqual(next.Routes)) return;
        _graph = next;
        if (_selected is not null && !_graph.Nodes.Any(n => n.Key == _selected)) _selected = null;
        RenderGraph();
    }

    private void UpdateHint()
    {
        FlowHint.Text = _vm is { DaemonConnected: false } ? "Daemon disconnected. Waiting for live routing."
            : _vm is { HasMixer: false } ? "Turn on the submixer in Options to see audio flow."
            : "Click any element to trace its signal path";
    }

    private void ConstrainToScreen()
    {
        var screen = Screens.ScreenFromWindow(Owner ?? this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        Size frame = FrameSize ?? ClientSize;
        var decoration = new Size(Math.Max(0, frame.Width - ClientSize.Width),
            Math.Max(0, frame.Height - ClientSize.Height));
        Size limit = FitClientSize(Size.Infinity, screen.WorkingArea.Size, screen.Scaling, decoration);
        MinWidth = Math.Min(900, limit.Width);
        MinHeight = Math.Min(500, limit.Height);
        MaxWidth = limit.Width;
        MaxHeight = limit.Height;
    }

    internal static Size FitClientSize(Size desired, PixelSize workingArea, double scaling, Size decoration)
        => new(Math.Min(desired.Width, Math.Max(1, workingArea.Width / scaling - decoration.Width)),
            Math.Min(desired.Height, Math.Max(1, workingArea.Height / scaling - decoration.Height)));

    private void RenderGraph()
    {
        Canvas canvas = GraphCanvas;
        canvas.Children.Clear();
        _cards.Clear();
        _routes.Clear();
        // Auto-size against a fixed natural width. Once the user resizes the
        // window, Avalonia switches SizeToContent to Manual and the graph adapts.
        double width = SizeToContent == SizeToContent.Manual
            ? Math.Max(1040, GraphScroll.Bounds.Width - 16) : 1208;
        double gap = Math.Clamp(width * 0.075, 72, 140);
        double cardWidth = (width - 3 * gap) / 4;
        double X(FlowStage stage) => (int)stage * (cardWidth + gap);
        canvas.Width = width;
        canvas.Height = Math.Max(320, _graph.Nodes.Select(n => n.Y + n.Height + 16).DefaultIfEmpty(320).Max());

        foreach ((FlowStage stage, string title, FlowIcon icon) in new[]
        {
            (FlowStage.Input, "INPUTS", FlowIcon.Microphone),
            (FlowStage.Channel, "CHANNELS", FlowIcon.Channel),
            (FlowStage.Mix, "MIXES", FlowIcon.Mix),
            (FlowStage.Output, "OUTPUTS", FlowIcon.Speaker),
        })
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            label.Children.Add(CreateIcon(icon, 15));
            label.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.SemiBold,
                Foreground = Brush("FlowText"), VerticalAlignment = VerticalAlignment.Center });
            var header = new Border { Background = Brush("FlowCard"), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 8), Child = label };
            header.Measure(Size.Infinity);
            Place(header, X(stage) + (cardWidth - header.DesiredSize.Width) / 2, 8);
        }

        var positions = _graph.Nodes.ToDictionary(n => n.Key,
            n => new Rect(X(n.Stage), n.Y, cardWidth, n.Height));
        foreach (FlowRoute route in _graph.Routes)
        {
            Rect from = positions[route.From], to = positions[route.To];
            var start = new Point(from.Right, from.Y + 29);
            var end = new Point(to.X, to.Y + 29);
            double bend = (end.X - start.X) / 2;
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(start, false);
                context.CubicBezierTo(new Point(start.X + bend, start.Y), new Point(end.X - bend, end.Y), end);
            }
            var glow = new Path { Data = geometry, StrokeThickness = 8, IsHitTestVisible = false };
            var line = new Path { Data = geometry, StrokeThickness = 2, IsHitTestVisible = false };
            var dot = new Ellipse { Width = 6, Height = 6, IsHitTestVisible = false };
            canvas.Children.Add(glow);
            canvas.Children.Add(line);
            canvas.Children.Add(dot);
            _routes.Add(new RouteVisual(route, line, glow, dot, start, end));
        }

        foreach (FlowNode node in _graph.Nodes)
        {
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*") };
            content.Children.Add(CreateIcon(node.Icon, 20));
            var text = new StackPanel { Spacing = 3, Margin = new Thickness(9, 0, 0, 0) };
            Grid.SetColumn(text, 1);
            text.Children.Add(new TextBlock { Text = node.Label, FontSize = 13, FontWeight = FontWeight.Medium,
                Foreground = Brush("FlowText"), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = node.Detail, FontSize = 11, Foreground = Brush("FlowTextDim"),
                TextTrimming = TextTrimming.CharacterEllipsis });
            if (node.Processing.Length > 0)
                text.Children.Add(new TextBlock { Text = "FX  " + node.Processing.Replace("\n", " · "),
                    FontSize = 10, Foreground = Brush("FlowInput"), Margin = new Thickness(0, 4, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(text);
            var card = new Button { Width = cardWidth, Height = node.Height, Content = content };
            card.Classes.Add("flowNode");
            AutomationProperties.SetName(card, $"{node.Label}, {node.Detail}");
            ToolTip.SetTip(card, node.Label + "\n" + node.Detail + (node.Processing.Length == 0 ? ""
                : (node.Stage == FlowStage.Channel ? "\nInput processing:\n" : "\nMix processing:\n") + node.Processing));
            card.Click += (_, _) => Select(_selected == node.Key ? null : node.Key);
            _cards.Add(node.Key, card);
            Place(card, positions[node.Key].X, node.Y);
        }
        Select(_selected);
    }

    private void Place(Control control, double x, double y)
    {
        Canvas.SetLeft(control, x);
        Canvas.SetTop(control, y);
        GraphCanvas.Children.Add(control);
    }

    private void OnClearSelection(object? sender, RoutedEventArgs e) => Select(null);

    private void Select(string? key)
    {
        _selected = key;
        ClearSelection.IsEnabled = key is not null;
        HashSet<FlowRoute>? path = key is null ? null : _graph.Trace(key);
        var related = path?.SelectMany(r => new[] { r.From, r.To }).ToHashSet() ?? [];
        if (key is not null) related.Add(key);
        foreach (FlowNode node in _graph.Nodes)
        {
            Button card = _cards[node.Key];
            card.Opacity = key is not null && !related.Contains(node.Key) ? 0.28 : node.Active ? 1 : 0.65;
            card.Background = Brush(node.Key == key ? "FlowCardSelected" : "FlowCard");
            card.BorderBrush = node.Key == key ? Brush("FlowTextDim") : Brushes.Transparent;
        }
        foreach (RouteVisual visual in _routes)
        {
            bool inPath = path is null || path.Contains(visual.Route);
            IBrush color = inPath && visual.Route.Active ? RouteBrush(visual.Route.Stage) : Brush("FlowMuted");
            visual.Line.Stroke = color;
            visual.Line.Opacity = inPath ? 0.9 : 0.16;
            visual.Line.StrokeDashArray = visual.Route.Active ? null : [3, 4];
            visual.Glow.Stroke = color;
            visual.Glow.Opacity = key is not null && inPath && visual.Route.Active ? 0.13 : 0;
            visual.Dot.Fill = color;
            visual.Dot.IsVisible = key is not null && inPath && visual.Route.Active;
        }
        if (_routes.Any(r => r.Dot.IsVisible)) _animation.Start();
        else _animation.Stop();
        Animate();
    }

    private void Animate()
    {
        double t = _clock.Elapsed.TotalSeconds / 2 % 1, u = 1 - t;
        foreach (RouteVisual route in _routes.Where(r => r.Dot.IsVisible))
        {
            double bend = (route.End.X - route.Start.X) / 2;
            double x = u * u * u * route.Start.X + 3 * u * u * t * (route.Start.X + bend)
                + 3 * u * t * t * (route.End.X - bend) + t * t * t * route.End.X;
            double y = (u * u * u + 3 * u * u * t) * route.Start.Y
                + (3 * u * t * t + t * t * t) * route.End.Y;
            Canvas.SetLeft(route.Dot, x - 3);
            Canvas.SetTop(route.Dot, y - 3);
        }
    }

    private Control CreateIcon(FlowIcon icon, double size)
    {
        string data = icon switch
        {
            FlowIcon.Microphone => "M9,4 C9,0 15,0 15,4 L15,11 C15,15 9,15 9,11 Z M5,10 L5,11 C5,20 19,20 19,11 L19,10 M12,18 L12,23 M8,23 L16,23",
            FlowIcon.Headphones => "M3,14 L3,11 C3,0 21,0 21,11 L21,14 M3,12 L7,12 L7,21 L3,21 Z M17,12 L21,12 L21,21 L17,21 Z",
            FlowIcon.Speaker => "M3,9 L7,9 L12,4 L12,20 L7,15 L3,15 Z M16,8 C20,10 20,14 16,16 M19,4 C25,8 25,16 19,20",
            FlowIcon.Channel => "M5,2 L5,8 M5,13 L5,22 M2,8 L8,8 L8,13 L2,13 Z M17,2 L17,13 M17,18 L17,22 M14,13 L20,13 L20,18 L14,18 Z",
            FlowIcon.Mix => "M3,5 L8,5 L8,10 L3,10 Z M16,5 L21,5 L21,10 L16,10 Z M12,15 L12,10 M6,10 L6,15 L18,15 L18,10 M9,18 L15,18 L15,23 L9,23 Z",
            _ => "M2,3 L22,3 L22,18 L2,18 Z M2,7 L22,7 M8,22 L16,22 M12,18 L12,22",
        };
        return new Viewbox { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
            Child = new Path { Width = 24, Height = 24, Data = Geometry.Parse(data),
                Stroke = Brush("FlowTextDim"), StrokeThickness = 1.6, StrokeLineCap = PenLineCap.Round } };
    }
}
