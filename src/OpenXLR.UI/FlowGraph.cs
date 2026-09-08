using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenXLR.UI;

internal enum FlowStage { Input, Channel, Mix, Output }
internal enum FlowIcon { Microphone, Application, Channel, Headphones, Mix, Speaker }

internal sealed record FlowNode(string Key, string Label, string Detail, FlowStage Stage,
    FlowIcon Icon, double Y, bool Active = true, string Processing = "")
{
    public double Height => Processing.Length == 0 ? 58 : 82;
}

internal sealed record FlowRoute(string From, string To, FlowStage Stage, bool Active);

/// <summary>A read-only snapshot of the routing and its four-column layout.</summary>
internal sealed record FlowGraph(IReadOnlyList<FlowNode> Nodes, IReadOnlyList<FlowRoute> Routes)
{
    private const double Top = 62, Gap = 10;

    public static FlowGraph From(MainViewModel vm)
    {
        var nodes = new List<FlowNode>();
        var routes = new List<FlowRoute>();
        double y = Top;
        static string Inserts(InsertsViewModel inserts) => string.Join("\n", inserts.Items.Select(i =>
            i.Label + (i.HasError ? " (problem)" : i.Bypass ? " (bypassed)" : "")));

        foreach (ChannelViewModel channel in vm.Channels)
        {
            var sources = new List<(string Key, string Label, string Detail, FlowIcon Icon, bool Active)>();
            if (channel.Id is "xlr1" or "xlr2" or "aux")
                sources.Add(($"hw:{channel.Id}", channel.Id switch
                {
                    "xlr1" => "XLR 1 jack", "xlr2" => "XLR 2 jack", _ => "Line In / USB Aux",
                }, "Hardware", FlowIcon.Microphone, true));
            sources.AddRange(vm.ActiveApps.Where(a => a.ChannelId == channel.Id)
                .Select(a => ($"app:{a.Identity}", a.Label, a.Active ? "Software" : a.StatusText,
                    FlowIcon.Application, a.Active)));

            string processing = channel.Id switch
            {
                "xlr1" => Inserts(vm.Inserts), "xlr2" => Inserts(vm.Inserts2), _ => "",
            };
            if (channel.Id == "xlr1")
            {
                var stages = new List<string>();
                if (vm.ShowSoftLowCut && vm.SoftLowCutOn) stages.Add($"Low cut {vm.SoftLowCutHz} Hz");
                if (vm.ShowSoftClipGuard && vm.SoftClipGuard)
                    stages.Add("ClipGuard" + (vm.SoftClipGuardAvailable ? "" : " (unavailable)"));
                if (processing.Length > 0) stages.Add(processing);
                processing = string.Join("\n", stages);
            }
            var node = new FlowNode($"ch:{channel.Id}", channel.Name,
                channel.IsHardware ? "Hardware channel" : "Application channel",
                FlowStage.Channel, FlowIcon.Channel, y, Processing: processing);
            double laneHeight = Math.Max(node.Height, sources.Count * (58 + Gap) - Gap);
            node = node with { Y = y + (laneHeight - node.Height) / 2 };
            nodes.Add(node);
            for (int i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                nodes.Add(new FlowNode(source.Key, source.Label, source.Detail, FlowStage.Input,
                    source.Icon, y + i * (58 + Gap), source.Active));
                routes.Add(new FlowRoute(source.Key, node.Key, FlowStage.Input, source.Active));
            }
            foreach (SendViewModel send in channel.Sends)
                if (send.Level > 0.001 && vm.Mixes.Any(m => m.Id == send.MixId))
                    routes.Add(new FlowRoute(node.Key, $"mix:{send.MixId}", FlowStage.Channel, !send.Muted));
            y += laneHeight + Gap;
        }
        // Desktop-managed or stale assignments remain visible without inventing a route.
        foreach (AppStreamViewModel app in vm.ActiveApps.Where(a => vm.Channels.All(c => c.Id != a.ChannelId)))
        {
            nodes.Add(new FlowNode($"app:{app.Identity}", app.Label,
                app.ChannelId == AppStreamViewModel.Ignore ? "Desktop routing" : "Channel unavailable",
                FlowStage.Input, FlowIcon.Application, y, app.Active));
            y += 58 + Gap;
        }

        y = Top;
        foreach (MixViewModel mix in vm.Mixes)
        {
            bool active = !mix.Muted && mix.Volume > 0.001;
            var node = new FlowNode($"mix:{mix.Id}", mix.Name,
                mix.Muted ? "Muted" : $"Vol {mix.VolumeText}", FlowStage.Mix,
                mix.Kind == "monitor" ? FlowIcon.Headphones : FlowIcon.Mix, y,
                active, Inserts(mix.Inserts));
            nodes.Add(node);
            y += node.Height + Gap;
        }

        y = Top;
        void Output(string key, string label, string detail, IEnumerable<string> feeds, bool enabled, FlowIcon icon)
        {
            var feedNodes = nodes.Where(n => n.Stage == FlowStage.Mix && feeds.Contains(n.Key[4..])).ToList();
            nodes.Add(new FlowNode(key, label, detail, FlowStage.Output, icon, y,
                enabled && feedNodes.Any(n => n.Active)));
            foreach (FlowNode feed in feedNodes)
                routes.Add(new FlowRoute(feed.Key, key, FlowStage.Mix, enabled && feed.Active));
            y += 58 + Gap;
        }
        foreach (MonitorOutputItem output in vm.MonitorOutputs.Where(o => o.IsSelected))
            Output($"out:{output.Name}", output.Label, output.Feed?.Name ?? "Monitor output",
                (output.Feed?.Id ?? vm.Mixes.FirstOrDefault(m => m.Kind == "monitor")?.Id ?? "monitor")
                    .Split('+', StringSplitOptions.RemoveEmptyEntries), true, FlowIcon.Speaker);
        foreach (MixViewModel mix in vm.Mixes)
        {
            if (mix.Kind == "virtualMic")
                Output($"vm:{mix.Id}", $"OpenXLR {mix.Name}", "Virtual microphone", [mix.Id], true, FlowIcon.Microphone);
            else if (mix.IsAuxPort)
                Output("aux:port", "USB Aux port", mix.AuxPortEnabled ? "Second computer" : "Off",
                    [mix.Id], mix.AuxPortEnabled, FlowIcon.Speaker);
        }
        return new FlowGraph(nodes, routes);
    }

    /// <summary>Trace each direction separately so a shared mix does not select sibling inputs.</summary>
    public HashSet<FlowRoute> Trace(string key)
    {
        var result = new HashSet<FlowRoute>();
        void Walk(bool upstream)
        {
            var pending = new Stack<string>();
            var seen = new HashSet<string> { key };
            pending.Push(key);
            while (pending.TryPop(out string? current))
                foreach (FlowRoute route in Routes.Where(r => (upstream ? r.To : r.From) == current))
                {
                    result.Add(route);
                    string next = upstream ? route.From : route.To;
                    if (seen.Add(next)) pending.Push(next);
                }
        }
        Walk(true);
        Walk(false);
        return result;
    }
}
