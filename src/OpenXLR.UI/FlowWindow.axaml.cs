using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace OpenXLR.UI;

/// <summary>
/// A flow graph of the live routing, rebuilt from the view model on every
/// state push: sources (apps and hardware inputs) into channels, channels
/// into mixes, mixes to physical and virtual outputs, with the filter chains
/// (built-in DSP and LV2 inserts) drawn where they sit in the path.
/// Application source nodes include a channel picker, so routing can be edited
/// where the relationship is visible. Levels and inserts stay in mixer cards.
/// </summary>
public partial class FlowWindow : Window
{
    private const double NodeW = 190, NodeH = 58, ColGap = 130, RowGap = 10, Pad = 8;

    private static readonly IBrush NodeBg = new SolidColorBrush(Color.Parse("#23262f"));
    private static readonly IBrush NodeBgDim = new SolidColorBrush(Color.Parse("#1c1f26"));
    private static readonly IBrush TextFg = new SolidColorBrush(Color.Parse("#e6e9f0"));
    private static readonly IBrush TextDim = new SolidColorBrush(Color.Parse("#7d8496"));
    private static readonly IBrush EdgeMuted = new SolidColorBrush(Color.Parse("#555b68"));

    // One stable hue per channel: every edge touching a channel wears its
    // color, which is what keeps the crossing curves readable.
    private static readonly IBrush[] Palette =
        new[] { "#4fb3d9", "#e0a84a", "#b06ab3", "#3ecf7a", "#e06767",
                "#6a8de0", "#d9cf4f", "#e08bc7", "#7fd9c6", "#c78b5a" }
        .Select(IBrush (h) => new SolidColorBrush(Color.Parse(h))).ToArray();

    private readonly MainViewModel? _vm;

    public FlowWindow() => InitializeComponent();

    public FlowWindow(MainViewModel vm) : this()
    {
        _vm = vm;
        vm.StateApplied += Rebuild;
        Opened += (_, _) => Rebuild();
        Closed += (_, _) => vm.StateApplied -= Rebuild;
    }

    /// <summary>One filter chain: the inserts (and built-in DSP) between a
    /// source and its channel, or between a mix and its outputs.</summary>
    private sealed record ChainNode(string Key, string Owner, IReadOnlyList<(string Label, bool Active, string? Note)> Items);

    private void Rebuild()
    {
        if (_vm is null) return;
        Canvas c = GraphCanvas;
        // Do not tear down an open routing picker on a state push. Once it
        // closes, render the newest model (including changes from other clients).
        if (c.GetVisualDescendants().OfType<ComboBox>().Any(p => p.IsDropDownOpen)) return;
        c.Children.Clear();
        if (!_vm.HasMixer)
        {
            c.Children.Add(new TextBlock { Text = _vm.MixerPlaceholder, Foreground = TextFg });
            c.Width = 650;
            c.Height = 60;
            return;
        }

        // ---- collect the nodes per column ----
        var sources = new List<(string Key, string Label, string Channel, bool Playing)>();
        foreach (string chId in new[] { "xlr1", "xlr2", "aux" })
            if (_vm.Channels.Any(x => x.Id == chId))
                sources.Add(($"hw:{chId}",
                    chId switch { "xlr1" => "XLR 1 jack", "xlr2" => "XLR 2 jack", _ => "Line In / USB Aux" },
                    chId, true));
        foreach (AppStreamViewModel a in _vm.ActiveApps)
            sources.Add(($"app:{a.Identity}", a.Label, a.ChannelId, a.Active));

        var channels = _vm.Channels.ToList();
        var mixes = _vm.Mixes.ToList();

        var outputs = new List<(string Key, string Label, string MixId, bool Active)>();
        string? monitorId = mixes.FirstOrDefault(m => m.IsMonitor)?.Id;
        foreach (MonitorOutputItem o in _vm.MonitorOutputs.Where(o => o.IsSelected))
            if (monitorId is not null) outputs.Add(($"out:{o.Name}", o.Label, monitorId, true));
        foreach (MixViewModel virtualMix in mixes.Where(m => m.IsVirtualMic))
            outputs.Add(($"vm:{virtualMix.Id}", $"OpenXLR {virtualMix.Name} (virtual mic)", virtualMix.Id, true));
        MixViewModel? auxMix = mixes.FirstOrDefault(m => m.IsAuxPort);
        if (auxMix is not null)
            outputs.Add(("aux:port", "USB Aux port (second PC)", auxMix.Id, auxMix.AuxPortEnabled));

        // Filter chains in the path. An input chain sits between the jack and
        // its channel: the built-in low cut and ClipGuard (XLR 1 only, when
        // switched on) followed by the LV2 inserts. A mix chain sits between
        // the mix and everything it feeds. Chains with nothing in them are
        // not drawn, and the columns appear only when a chain exists.
        var inputChains = new List<ChainNode>();
        static IEnumerable<(string, bool, string?)> InsertItems(InsertsViewModel vm)
            => vm.Items.Select(i => (i.Label, i.IsActive, i.HasError ? "problem" : i.Bypass ? "bypassed" : null));
        if (channels.Any(x => x.Id == "xlr1"))
        {
            var items = new List<(string, bool, string?)>();
            if (_vm.ShowSoftLowCut && _vm.SoftLowCutOn) items.Add(($"Low cut {_vm.SoftLowCutHz} Hz", true, null));
            if (_vm.ShowSoftClipGuard && _vm.SoftClipGuard) items.Add(("ClipGuard", _vm.SoftClipGuardAvailable, _vm.SoftClipGuardAvailable ? null : "unavailable"));
            items.AddRange(InsertItems(_vm.Inserts));
            if (items.Count > 0) inputChains.Add(new ChainNode("chain:xlr1", "xlr1", items));
        }
        if (channels.Any(x => x.Id == "xlr2"))
        {
            var items = InsertItems(_vm.Inserts2).ToList();
            if (items.Count > 0) inputChains.Add(new ChainNode("chain:xlr2", "xlr2", items));
        }
        var mixChains = mixes
            .Where(m => m.Inserts.Items.Count > 0)
            .Select(m => new ChainNode($"chain:mix:{m.Id}", m.Id, InsertItems(m.Inserts).ToList()))
            .ToList();

        // ---- layout ----
        // Columns: sources, [input DSP], channels, mixes, [mix DSP], outputs.
        var headers = new List<string> { "SOURCES" };
        int colInputDsp = -1, colMixDsp = -1;
        if (inputChains.Count > 0) { colInputDsp = headers.Count; headers.Add("INPUT DSP"); }
        int colChannels = headers.Count; headers.Add("CHANNELS");
        int colMixes = headers.Count; headers.Add("MIXES");
        if (mixChains.Count > 0) { colMixDsp = headers.Count; headers.Add("MIX DSP"); }
        int colOutputs = headers.Count; headers.Add("OUTPUTS");

        double colX(int col) => Pad + col * (NodeW + ColGap);
        var pos = new Dictionary<string, Rect>();
        void Place(int col, int row, string key)
            => pos[key] = new Rect(colX(col), Pad + row * (NodeH + RowGap), NodeW, NodeH);
        // A chain node is as tall as its list, centred on its owner's row.
        void PlaceChain(int col, Rect owner, ChainNode chain)
        {
            double h = Math.Max(NodeH, 10 + chain.Items.Count * 16);
            pos[chain.Key] = new Rect(colX(col), owner.Center.Y - h / 2, NodeW, h);
        }

        for (int i = 0; i < sources.Count; i++) Place(0, i, sources[i].Key);
        for (int i = 0; i < channels.Count; i++) Place(colChannels, i, $"ch:{channels[i].Id}");
        for (int i = 0; i < mixes.Count; i++) Place(colMixes, i * 2 + 1, $"mix:{mixes[i].Id}");
        for (int i = 0; i < outputs.Count; i++) Place(colOutputs, i * 2 + 1, outputs[i].Key);
        foreach (ChainNode chain in inputChains) PlaceChain(colInputDsp, pos[$"ch:{chain.Owner}"], chain);
        foreach (ChainNode chain in mixChains) PlaceChain(colMixDsp, pos[$"mix:{chain.Owner}"], chain);

        // A tall chain centred on the first row would start above the canvas;
        // push everything down by the overshoot.
        double overshoot = Pad - pos.Values.Min(r => r.Y);
        if (overshoot > 0)
            foreach (string key in pos.Keys.ToList())
                pos[key] = pos[key].Translate(new Vector(0, overshoot));

        c.Width = colX(colOutputs) + NodeW + Pad;
        c.Height = pos.Values.Max(r => r.Bottom) + Pad;

        // ---- edges first (under the nodes) ----
        IBrush ChannelBrush(string chId)
        {
            int idx = channels.FindIndex(x => x.Id == chId);
            return idx < 0 ? EdgeMuted : Palette[idx % Palette.Length];
        }
        IBrush MixBrush(string mixId)
        {
            int idx = mixes.FindIndex(x => x.Id == mixId);
            return idx < 0 ? EdgeMuted : Palette[idx % Palette.Length];
        }
        string? InputChainKey(string chId) => inputChains.FirstOrDefault(x => x.Owner == chId)?.Key;
        string? MixChainKey(string mixId) => mixChains.FirstOrDefault(x => x.Owner == mixId)?.Key;

        foreach (var s in sources)
        {
            if (!pos.ContainsKey($"ch:{s.Channel}")) continue;
            IBrush brush = s.Playing ? ChannelBrush(s.Channel) : EdgeMuted;
            double w = s.Playing ? 2 : 1.2;
            // The jack's own chain is in its path; app streams join the
            // channel directly.
            string? via = s.Key.StartsWith("hw:", StringComparison.Ordinal) ? InputChainKey(s.Channel) : null;
            if (via is not null)
            {
                Edge(c, pos[s.Key], pos[via], brush, w, dashed: !s.Playing);
                Edge(c, pos[via], pos[$"ch:{s.Channel}"], brush, w, dashed: !s.Playing);
            }
            else Edge(c, pos[s.Key], pos[$"ch:{s.Channel}"], brush, w, dashed: !s.Playing);
        }

        foreach (ChannelViewModel ch in channels)
            foreach (SendViewModel send in ch.Sends)
            {
                if (!pos.ContainsKey($"mix:{send.MixId}")) continue;
                bool flows = send.Level > 0.001 && !send.Muted;
                if (!flows && send.Level <= 0.001) continue;      // no send at all: no line
                Edge(c, pos[$"ch:{ch.Id}"], pos[$"mix:{send.MixId}"],
                    flows ? ChannelBrush(ch.Id) : EdgeMuted,
                    flows ? Math.Max(1.2, send.Level * 3.0) : 1.2, dashed: !flows);
            }

        // A mix with a chain feeds it once; the chain then fans out to the
        // mix's outputs.
        foreach (ChainNode chain in mixChains)
        {
            bool live = outputs.Any(o => o.MixId == chain.Owner && o.Active);
            Edge(c, pos[$"mix:{chain.Owner}"], pos[chain.Key], live ? MixBrush(chain.Owner) : EdgeMuted, live ? 2 : 1.2, dashed: !live);
        }
        foreach (var o in outputs)
        {
            if (!pos.ContainsKey($"mix:{o.MixId}")) continue;
            string from = MixChainKey(o.MixId) ?? $"mix:{o.MixId}";
            Edge(c, pos[from], pos[o.Key], o.Active ? MixBrush(o.MixId) : EdgeMuted,
                o.Active ? 2 : 1.2, dashed: !o.Active);
        }

        // ---- nodes ----
        foreach (var s in sources)
        {
            AppStreamViewModel? app = s.Key.StartsWith("app:", StringComparison.Ordinal)
                ? _vm.ActiveApps.FirstOrDefault(a => a.Identity == s.Key[4..])
                : null;
            if (app is not null) AppNode(c, pos[s.Key], app, s.Playing, ChannelBrush(s.Channel));
            else Node(c, pos[s.Key], s.Label, s.Playing ? null : "silent", s.Playing);
        }
        foreach (ChannelViewModel ch in channels)
        {
            Border node = Node(c, pos[$"ch:{ch.Id}"], ch.Name, "Click to edit sends", true, ChannelBrush(ch.Id));
            node.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Left) return;
                new ChannelEditorWindow(_vm, ch).Show(this);
            };
        }
        foreach (MixViewModel m in mixes)
            Node(c, pos[$"mix:{m.Id}"], m.Name, m.Muted ? "muted" : $"{m.Volume * 100:0}%", !m.Muted, MixBrush(m.Id));
        foreach (var o in outputs) Node(c, pos[o.Key], o.Label, o.Active ? null : "off", o.Active);
        foreach (ChainNode chain in inputChains) ChainBox(c, pos[chain.Key], chain, ChannelBrush(chain.Owner));
        foreach (ChainNode chain in mixChains) ChainBox(c, pos[chain.Key], chain, MixBrush(chain.Owner));

        // ---- column headers ----
        for (int i = 0; i < headers.Count; i++)
        {
            var t = new TextBlock
            {
                Text = headers[i], FontSize = 11, FontWeight = FontWeight.SemiBold,
                Foreground = TextDim,
            };
            Canvas.SetLeft(t, colX(i));
            Canvas.SetTop(t, c.Height);
            c.Children.Add(t);
        }
        c.Height += 22;
    }

    /// <summary>A chain node: one line per stage, in signal order, dimmed
    /// when bypassed or broken, with the owner's accent on the left edge.</summary>
    private static void ChainBox(Canvas c, Rect r, ChainNode chain, IBrush accent)
    {
        var list = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Spacing = 2 };
        foreach ((string label, bool active, string? note) in chain.Items)
        {
            var row = new DockPanel();
            if (note is not null)
            {
                var n = new TextBlock { Text = note, FontSize = 10, Foreground = TextDim, Margin = new Thickness(6, 0, 0, 0) };
                DockPanel.SetDock(n, Dock.Right);
                row.Children.Add(n);
            }
            row.Children.Add(new TextBlock
            {
                Text = label, FontSize = 11, Foreground = active ? TextFg : TextDim,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            list.Children.Add(row);
        }
        bool anyActive = chain.Items.Any(i => i.Active);
        var node = new Border
        {
            Width = r.Width, Height = r.Height,
            Background = anyActive ? NodeBg : NodeBgDim,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = accent,
            Child = list,
        };
        Canvas.SetLeft(node, r.X);
        Canvas.SetTop(node, r.Y);
        c.Children.Add(node);
    }

    private static Border Node(Canvas c, Rect r, string label, string? sub, bool lit, IBrush? accent = null)
    {
        var text = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12, Foreground = lit ? TextFg : TextDim,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (sub is not null)
            text.Children.Add(new TextBlock { Text = sub, FontSize = 10, Foreground = TextDim });

        var node = new Border
        {
            Width = r.Width, Height = r.Height,
            Background = lit ? NodeBg : NodeBgDim,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 0),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = accent ?? Brushes.Transparent,
            Child = text,
        };
        Canvas.SetLeft(node, r.X);
        Canvas.SetTop(node, r.Y);
        c.Children.Add(node);
        return node;
    }

    /// <summary>An app source with an in-place channel assignment picker.</summary>
    private void AppNode(Canvas c, Rect r, AppStreamViewModel app, bool lit, IBrush accent)
    {
        var picker = new ComboBox
        {
            ItemsSource = app.Channels,
            SelectedItem = app.SelectedChannel,
            FontSize = 10,
            MinHeight = 0,
            Height = 24,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ChannelOption channel && channel.Id != app.ChannelId)
                app.ChannelId = channel.Id;
        };
        picker.DropDownClosed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
        var content = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Spacing = 3 };
        content.Children.Add(new TextBlock
        {
            Text = app.Label,
            FontSize = 11,
            Foreground = lit ? TextFg : TextDim,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        content.Children.Add(picker);
        var node = new Border
        {
            Width = r.Width,
            Height = r.Height,
            Background = lit ? NodeBg : NodeBgDim,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = accent,
            Child = content,
        };
        Canvas.SetLeft(node, r.X);
        Canvas.SetTop(node, r.Y);
        c.Children.Add(node);
    }

    private static void Edge(Canvas c, Rect from, Rect to, IBrush stroke, double thickness, bool dashed)
    {
        var p0 = new Point(from.Right, from.Center.Y);
        var p3 = new Point(to.X, to.Center.Y);
        double bend = (p3.X - p0.X) * 0.5;
        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(p0, isFilled: false);
            ctx.CubicBezierTo(new Point(p0.X + bend, p0.Y), new Point(p3.X - bend, p3.Y), p3);
        }
        c.Children.Add(new Path
        {
            Data = geo, Stroke = stroke, StrokeThickness = thickness,
            StrokeDashArray = dashed ? [3, 3] : null,
            Opacity = dashed ? 0.7 : 0.9,
        });
    }
}
