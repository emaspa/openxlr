using OpenXLR.UI.Localization;
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

    /// <summary>The one card that stands for every mix the Wave XLR Pro builds for its jacks.</summary>
    internal const string UnitMixKey = "mix:@unit";

    public static FlowGraph From(MainViewModel vm)
    {
        var nodes = new List<FlowNode>();
        var routes = new List<FlowRoute>();
        double y = Top;
        // While hardware mixing runs, the mixes the unit builds for its jacks
        // are one card named for the unit, between the channels and the jacks.
        // A mix also heard on another output keeps its own card for that output.
        HashSet<string> unitMixes = vm.HardwareMixing.Active
            ? [.. vm.Mixes.Where(m => m.HardwareSection is not null).Select(m => m.Id)]
            : [];
        static bool IsJack(MonitorOutputItem output) => output.Name.Contains('#', StringComparison.Ordinal);
        HashSet<string> heardElsewhere = [.. vm.MonitorOutputs.Where(o => o.IsSelected && !IsJack(o))
            .SelectMany(o => (o.Feed?.Id ?? vm.DefaultMonitorMix).Split('+', StringSplitOptions.RemoveEmptyEntries))];
        static string Inserts(InsertsViewModel inserts) => string.Join("\n", inserts.Items.Select(i =>
            i.HasError ? Localizer.Format("FlowInsertProblem", i.Label) : i.Bypass ? Localizer.Format("FlowInsertBypassed", i.Label) : i.Label));

        foreach (ChannelViewModel channel in vm.Channels)
        {
            var sources = new List<(string Key, string Label, string Detail, FlowIcon Icon, bool Active)>();
            // A strip bound to another source draws that source where its jack would be.
            if (channel.IsSourceBound && channel.Binding is { Source: { } bound } binding)
                sources.Add(($"capture:{channel.Id}", channel.BindingLabel.Length > 0 ? channel.BindingLabel : bound,
                    channel.BindingWarning ?? (channel.BindingConnected
                        ? Localizer.Format("CaptureConnectedPort", binding.Port + 1) : Localizer.Format("CaptureOfflinePort", binding.Port + 1)),
                    FlowIcon.Microphone, channel.BindingConnected));
            else if (channel.Id is "xlr1" or "xlr2" or "aux" or "line")
                sources.Add(($"hw:{channel.Id}", channel.Id switch
                {
                    // A USB microphone's input is its capsule, not a jack.
                    "xlr1" or "xlr2" when !vm.UnitXlrJacks => Localizer.Text("UnitCapsule"),
                    "xlr1" => Localizer.Text("Xlr1Jack"), "xlr2" => Localizer.Text("Xlr2Jack"),
                    "line" => Localizer.Text("LineInJack"), _ => Localizer.Text("USBAuxIn"),
                }, Localizer.Text("FlowHardware"), FlowIcon.Microphone, true));
            if (channel.CaptureSource is { } capture)
                sources.Add(($"capture:{channel.Id}", capture, channel.CaptureLabel,
                    FlowIcon.Microphone, channel.CaptureConnected));
            sources.AddRange(vm.ActiveApps.Where(a => a.ChannelId == channel.Id)
                .Select(a => ($"app:{a.Identity}", a.Label, a.Active ? Localizer.Text("FlowSoftware") : a.StatusText,
                    FlowIcon.Application, a.Active)));

            // The input's processing in the order the signal meets it: the
            // EQ at the head, on the unit before USB or on this computer,
            // XLR 1's software low cut and ClipGuard, then the plugin chain.
            var stages = new List<string>();
            if (channel.Eq is { Enabled: true } eq)
            {
                int bands = eq.Bands.Count(b => b.Enabled);
                stages.Add(eq.IsFaulty ? Localizer.Format("OutputUnavailable", Localizer.Text("Eq"))
                    : eq.OnDevice ? Localizer.Format("FlowEqDevice", bands, eq.DeviceName ?? Localizer.Text("EqTheInterface"))
                    : Localizer.Format("FlowEq", bands));
            }
            if (channel.Id == "xlr1")
            {
                if (vm.ShowSoftLowCut && vm.SoftLowCutOn) stages.Add(Localizer.Format("FlowLowCut", vm.SoftLowCutHz));
                if (vm.ShowSoftClipGuard && vm.SoftClipGuard)
                    stages.Add(vm.SoftClipGuardAvailable ? "ClipGuard" : Localizer.Format("OutputUnavailable", "ClipGuard"));
            }
            string chain = channel.Id switch
            {
                "xlr1" => Inserts(vm.Inserts), "xlr2" => Inserts(vm.Inserts2),
                _ => channel.Inserts is { } inserts ? Inserts(inserts) : "",
            };
            if (chain.Length > 0) stages.Add(chain);
            string processing = string.Join("\n", stages);
            var node = new FlowNode($"ch:{channel.Id}", channel.Name,
                channel.IsHardware ? Localizer.Text("HardwareChannel") : channel.IsApplication ? Localizer.Text("ApplicationChannel") : Localizer.Text("CaptureChannel"),
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
            bool toUnit = false, toUnitHeard = false;
            foreach (SendViewModel send in channel.Sends)
                if (send.Level > 0.001 && vm.Mixes.Any(m => m.Id == send.MixId))
                {
                    if (unitMixes.Contains(send.MixId))
                    {
                        toUnit = true;
                        toUnitHeard |= !send.Muted;
                        if (!heardElsewhere.Contains(send.MixId)) continue;
                    }
                    routes.Add(new FlowRoute(node.Key, $"mix:{send.MixId}", FlowStage.Channel, !send.Muted));
                }
            if (toUnit) routes.Add(new FlowRoute(node.Key, UnitMixKey, FlowStage.Channel, toUnitHeard));
            y += laneHeight + Gap;
        }
        // Desktop-managed or stale assignments remain visible without inventing a route.
        foreach (AppStreamViewModel app in vm.ActiveApps.Where(a => vm.Channels.All(c => c.Id != a.ChannelId)))
        {
            nodes.Add(new FlowNode($"app:{app.Identity}", app.Label,
                app.ChannelId == AppStreamViewModel.Ignore ? Localizer.Text("DesktopRouting") : Localizer.Text("ChannelUnavailable"),
                FlowStage.Input, FlowIcon.Application, y, app.Active));
            y += 58 + Gap;
        }

        y = Top;
        if (unitMixes.Count > 0)
        {
            MixViewModel[] built = [.. vm.Mixes.Where(m => unitMixes.Contains(m.Id))];
            var unit = new FlowNode(UnitMixKey, vm.DeviceName.Length > 0 ? vm.DeviceName : Localizer.Text("HardwareMixingUnitCard"),
                Localizer.Format("FlowUnitMixes", string.Join(", ", built.Select(m => m.Name))), FlowStage.Mix, FlowIcon.Mix, y,
                built.Any(m => !m.Muted && m.Volume > 0.001));
            nodes.Add(unit);
            y += unit.Height + Gap;
        }
        foreach (MixViewModel mix in vm.Mixes)
        {
            if (unitMixes.Contains(mix.Id) && !heardElsewhere.Contains(mix.Id)) continue;
            bool active = !mix.Muted && mix.Volume > 0.001;
            var node = new FlowNode($"mix:{mix.Id}", mix.Name,
                mix.Muted ? Localizer.Text("FlowMuted") : Localizer.Format("FlowVolume", mix.VolumeText), FlowStage.Mix,
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
        {
            string[] feeds = (output.Feed?.Id ?? vm.DefaultMonitorMix).Split('+', StringSplitOptions.RemoveEmptyEntries);
            // A jack the unit feeds hears its card; every other output the mixes this computer builds.
            if (IsJack(output) && feeds.Any(unitMixes.Contains)) feeds = [UnitMixKey[4..]];
            Output($"out:{output.Name}", output.Label, output.Feed?.Name ?? Localizer.Text("MonitorOutput"),
                feeds, output.IsPresent, FlowIcon.Speaker);
        }
        foreach (MixViewModel mix in vm.Mixes)
        {
            if (mix.Kind == "virtualMic")
                Output($"vm:{mix.Id}", $"OpenXLR {mix.Name}", Localizer.Text("VirtualMicrophone"), [mix.Id], true, FlowIcon.Microphone);
            else if (mix.IsAuxPort)
                Output("aux:port", Localizer.Text("UsbAuxPortKind"), mix.AuxPortEnabled ? Localizer.Text("SecondComputer") : Localizer.Text("FlowOff"),
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
