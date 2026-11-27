using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The choice of host for a Windows plugin in a real controls window, run
/// inside the Xvfb layout test's UI dispatcher: the chain asks the daemon
/// once whether its build has the host, the switch sits with the other
/// actions, and an insert running on that host keeps its Plugin UI button
/// and gets the Native editors button only under a compatibility rule.
/// </summary>
internal static class WindowsHostChoiceWindowTests
{
    private const string ClassId = "0123456789ABCDEF0123456789ABCDEF";
    private const string Wrapper = "/home/u/.local/share/openxlr/yabridge/vst3/Deep Verb.vst3";

    internal static void Check()
    {
        Task check = Verify();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!check.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(check.IsCompleted, "Windows plugin host check hung.");
        check.GetAwaiter().GetResult();
    }

    private static JsonArray Chain(string plugin, string? windowsHost = null, bool running = false, string? error = null, string? blockReason = null)
    {
        var insert = new JsonObject { ["id"] = "fx", ["kind"] = "vst3", ["plugin"] = plugin, ["label"] = "Deep Verb", ["params"] = new JsonObject() };
        if (windowsHost is not null) insert["windowsHost"] = windowsHost;
        var entry = new JsonObject { ["insert"] = insert, ["nativeHostRunning"] = true, ["windowsHostRunning"] = running };
        if (error is not null) entry["windowsHostError"] = error;
        if (blockReason is not null) { entry["nativeUiBlocked"] = true; entry["nativeUiBlockReason"] = blockReason; }
        return [entry];
    }

    private static async Task Verify()
    {
        int setups = 0;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                string? id = command["requestId"]?.GetValue<string>();
                if (cmd == "listPlugins")
                    await SocketTestServer.Send(socket, new
                    {
                        type = "plugins",
                        plugins = new object[]
                        {
                            new { kind = "vst3", plugin = ClassId, name = "Deep Verb", path = Wrapper, audioIns = 2, audioOuts = 2,
                                  nativeEditorSupported = true, nativeEditorAvailable = true, @params = new object[0] },
                            new { kind = "vst3", plugin = "FEDCBA9876543210FEDCBA9876543210", name = "Native Verb", path = "/usr/lib/vst3/Native Verb.vst3",
                                  audioIns = 2, audioOuts = 2, @params = new object[0] },
                        },
                    }, stop);
                else if (cmd == "getPluginSetup")
                {
                    Interlocked.Increment(ref setups);
                    await SocketTestServer.Send(socket, new { type = "pluginSetup", windowsHostInstalled = true, windowsHostDefault = "yabridge" }, stop);
                }
                if (id is not null) await SocketTestServer.Send(socket, new { type = "commandResult", requestId = id }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // A chain of native plugins never asks.
        var native = new InsertsViewModel(client, "mix:chat", 2);
        native.ResetForNewConnection();
        native.Apply(Chain("FEDCBA9876543210FEDCBA9876543210"));
        await native.LoadPluginsAsync();
        Assert.False(Assert.Single(native.Items).Bridged);

        var view = new InsertsViewModel(client, "mix:monitor", 2);
        view.Apply(Chain(ClassId));
        await view.LoadPluginsAsync();
        InsertViewModel insert = Assert.Single(view.Items);
        Assert.True(insert.Bridged);
        for (var deadline = DateTime.UtcNow.AddSeconds(5); !view.WindowsHostInstalled && DateTime.UtcNow < deadline;)
            await Task.Delay(10);
        Assert.True(view.WindowsHostInstalled);
        view.Apply(Chain(ClassId));
        view.Apply(Chain(ClassId));
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref setups));
        Assert.False(native.WindowsHostInstalled, "Only a chain with a Windows plugin takes the answer.");

        var controls = new InsertControlsWindow { DataContext = insert };
        controls.Show();
        try
        {
            var choice = controls.FindControl<ComboBox>("PluginWindowsHost")!;
            var state = controls.FindControl<TextBlock>("PluginWindowsHostState")!;
            var note = controls.FindControl<TextBlock>("PluginWindowsHostNote")!;
            var rules = controls.FindControl<Button>("ManageNativeEditors")!;
            foreach (double width in new[] { 420d, 620 })
            {
                Layout(controls, width, 560);
                Assert.True(choice.IsVisible);
                Assert.Equal(3, choice.ItemCount);
                Assert.Same(insert.WindowsHostOptions[0], choice.SelectedItem);
                Assert.Equal(Localizer.Text("WindowsHostFollowDefaultYabridge"), insert.WindowsHostOptions[0].Label);
                Assert.Equal(Localizer.Text("RunOnOpenXlrSWindowsPluginHostTip"), ToolTip.GetTip(choice) as string);
                Assert.True(controls.FindControl<ToggleButton>("PluginOwnBlock")!.IsVisible);
                Assert.Equal(Localizer.Text("RunsThroughYabridge"), state.Text);
                Assert.True(state.IsVisible);
                Assert.False(note.IsVisible);
                AssertInside(choice, controls);
                AssertInside(state, controls);
                Capture(controls, "plugin-windows-host-" + width);
            }

            // On the Windows plugin host the plugin's editor opens as on
            // yabridge, and no rule blocks it.
            view.Apply(Chain(ClassId, "openxlr", running: true));
            Layout(controls, 420, 560);
            Assert.Same(insert.WindowsHostOptions[2], choice.SelectedItem);
            Assert.Equal(Localizer.Text("RunsOnTheWindowsPluginHost"), state.Text);
            Assert.True(insert.NativeEditorAvailable);
            Assert.False(rules.IsVisible);
            Capture(controls, "plugin-windows-host-running-420");

            // A compatibility rule blocks it there too and offers its list.
            view.Apply(Chain(ClassId, "openxlr", running: true, blockReason: "Known editor issue."));
            Layout(controls, 420, 560);
            Assert.False(insert.NativeEditorAvailable);
            Assert.True(rules.IsVisible);

            // Saved there and running through yabridge, with the reason.
            const string missing = "the Windows plugin behind 'Deep Verb' is missing: "
                + "/home/u/.wine/drive_c/Program Files/Common Files/VST3/Deep Verb.vst3/Contents/x86_64-win/Deep Verb.vst3";
            view.Apply(Chain(ClassId, "openxlr", error: missing));
            Layout(controls, 420, 560);
            Assert.Same(insert.WindowsHostOptions[2], choice.SelectedItem);
            Assert.True(note.IsVisible);
            Assert.Equal(Localizer.Format("WindowsHostKept", missing), note.Text);
            AssertInside(note, controls);
            Capture(controls, "plugin-windows-host-kept-420");
        }
        finally { controls.Close(); }
    }
}
