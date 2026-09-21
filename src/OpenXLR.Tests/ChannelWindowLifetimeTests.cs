using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using OpenXLR.UI;

namespace OpenXLR.Tests;

internal static class ChannelWindowLifetimeTests
{
    internal static void CheckRemoval(Window owner, DaemonClient client)
    {
        var model = new MainViewModel(client);
        foreach (bool mix in new[] { false, true })
        {
            Apply(model, true);
            var chain = mix ? model.Mixes.Single(m => m.Id == "extra").Inserts
                : model.Channels.Single(c => c.Id == "extra").Inserts;
            string key = mix ? "mix:extra" : "extra";
            var kept = model.Mixes.Single(m => m.Id == "keep").Inserts;
            InsertWindows.OpenChain(owner, kept, "mix:keep");
            var keptWindow = Assert.Single(owner.OwnedWindows.OfType<MixInsertsWindow>(), w => ReferenceEquals(w.DataContext, kept));
            InsertWindows.OpenChain(owner, chain, key);
            InsertWindows.OpenControls(owner, Assert.Single(chain.Items));
            var chainWindow = Assert.Single(owner.OwnedWindows.OfType<MixInsertsWindow>(), w => ReferenceEquals(w.DataContext, chain));
            var controls = Assert.Single(owner.OwnedWindows.OfType<InsertControlsWindow>(), w => ReferenceEquals(w.DataContext, chain.Items[0]));
            try
            {
                Apply(model, false);
                Assert.False(chainWindow.IsVisible);
                Assert.False(controls.IsVisible);
                Assert.Empty(chain.Items);
                Assert.True(keptWindow.IsVisible);
                Assert.Same(kept, model.Mixes.Single(m => m.Id == "keep").Inserts);
                Apply(model, true);
                var fresh = mix ? model.Mixes.Single(m => m.Id == "extra").Inserts
                    : model.Channels.Single(c => c.Id == "extra").Inserts;
                Assert.NotSame(chain, fresh);
                InsertWindows.OpenChain(owner, fresh, key);
                Assert.Contains(owner.OwnedWindows, w => ReferenceEquals(w.DataContext, fresh));
            }
            finally
            {
                foreach (var window in owner.OwnedWindows.Where(w => w is MixInsertsWindow or InsertControlsWindow).ToArray()) window.Close();
            }
        }
    }

    private static void Apply(MainViewModel model, bool extra)
    {
        JsonArray items = new(new JsonObject { ["id"] = "keep", ["name"] = "Keep" });
        if (extra) items.Add(new JsonObject { ["id"] = "extra", ["name"] = "Extra" });
        var state = new JsonObject
        {
            ["channels"] = items.DeepClone(), ["mixes"] = items.DeepClone(),
            ["inserts"] = JsonNode.Parse("""{"extra":[{"insert":{"id":"channel-effect","plugin":"urn:test"}}],"mix:extra":[{"insert":{"id":"mix-effect","plugin":"urn:test"}}]}"""),
        };
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [state]);
    }
}
