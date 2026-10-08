using System.Text.Json.Nodes;
using Avalonia.Controls;
using OpenXLR.UI;

namespace OpenXLR.Tests;

internal static class PluginPresetWindowTests
{
    // Runs in the existing desktop fixture after application setup.
    internal static void Check(Window parent)
    {
        var owner = new InsertsViewModel(new DaemonClient("ws://127.0.0.1:1/ws"), "preset-fixture", 2);
        owner.Apply(new JsonArray(Entry("one"), Entry("two")));
        var target = owner.Items[0];
        var window = new PluginPresetWindow(target);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(parent);
            owner.Apply(new JsonArray(Entry("two"), Entry("one")));
            Assert.Same(target, owner.Items[1]);
            Assert.False(closed);
            owner.Apply(new JsonArray(Entry("one")));
            Assert.False(closed); // deleting a different insert retains this window
            owner.Apply(new JsonArray(Entry("one", "urn:replacement")));
            Assert.NotSame(target, owner.Items[0]);
            Assert.True(closed); // replacing the plugin in the same slot retires its window
        }
        finally { window.Close(); }
    }

    private static JsonObject Entry(string id, string plugin = "urn:fixture") => new()
    {
        ["insert"] = new JsonObject
        {
            ["id"] = id, ["kind"] = "lv2", ["plugin"] = plugin, ["label"] = "Fixture",
            ["bypass"] = false, ["nativeHost"] = false, ["params"] = new JsonObject(),
        },
    };
}
