using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;

namespace OpenXLR.Tests;

internal static class PrimaryInputWindowTests
{
    internal static void Check()
    {
        Task pending = CheckPicker();
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "Primary microphone picker check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CheckPicker()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        bool reject = true;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Send(socket, State(false), stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode cmd = await SocketTestServer.Receive(socket, stop);
                if (cmd["cmd"]!.GetValue<string>() != "setPrimaryInput") continue;
                commands.Enqueue(cmd);
                await SocketTestServer.Send(socket, State(!reject && cmd["source"] is not null), stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = cmd["requestId"]!.GetValue<string>(),
                    error = reject ? "Settings could not be saved." : null }, stop);
                reject = false;
            }
        });
        await using var client = new DaemonClient(server.Url);
        var main = new MainViewModel(client);
        client.Start();
        await Until(() => main.HasMixer);
        var options = new OptionsWindow { DataContext = new OptionsViewModel(client, main) };
        options.Show();
        try
        {
            Open();
            Window dialog = Assert.Single(options.OwnedWindows);
            var source = Find<ComboBox>(dialog, "PrimaryInputSource");
            var port = Find<NumericUpDown>(dialog, "PrimaryInputChannel");
            var save = Find<Button>(dialog, "ApplyPrimaryInput");
            Assert.Equal(2, source.ItemCount); // Automatic and the external source, without our own virtual microphone.
            Assert.False(port.IsEnabled);
            source.SelectedIndex = 1;
            port.Value = null;
            Assert.False(save.IsEnabled);
            port.Value = 1.5m;
            Assert.False(save.IsEnabled);
            port.Value = 2;
            Assert.True(port.IsEnabled);
            Assert.True(save.IsEnabled);
            foreach (double width in new[] { 480d, 360d })
            {
                WindowLayoutTests.Layout(dialog, width, 380);
                Assert.Equal(width, dialog.ClientSize.Width);
                Assert.True(source.Bounds.Width > 100);
                Assert.True(source.Bounds.Width < dialog.ClientSize.Width);
                if (Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_ARTIFACTS") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)dialog.ClientSize.Width, (int)dialog.ClientSize.Height));
                    bitmap.Render(dialog);
                    bitmap.Save(Path.Combine(directory, $"primary-microphone-{width}.png"), PngBitmapEncoderOptions.Default);
                }
            }
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Find<TextBlock>(dialog, "PrimaryInputError").Text?.Contains("could not be saved") == true);
            Assert.True(dialog.IsVisible);
            Assert.Equal(1, commands.Single()["captureMonoChannel"]!.GetValue<int>());
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !dialog.IsVisible);
            await Until(() => main.GenericMicrophone);
            Assert.True(main.InputConnected);
            Assert.True(main.ShowSoftLowCut);
            CheckInputTemplates(main);
            Open();
            dialog = Assert.Single(options.OwnedWindows);
            Find<NumericUpDown>(dialog, "PrimaryInputChannel").Value = null;
            Find<ComboBox>(dialog, "PrimaryInputSource").SelectedIndex = 0;
            Find<Button>(dialog, "ApplyPrimaryInput").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !dialog.IsVisible);
            JsonNode cleared = commands.Last();
            Assert.Null(cleared["source"]);
            Assert.Null(cleared["captureMonoChannel"]);
            await Until(() => !main.GenericMicrophone);
        }
        finally { foreach (Window dialog in options.OwnedWindows.ToArray()) dialog.Close(); options.Close(); }

        void Open() => typeof(OptionsWindow).GetMethod("OnPrimaryMicrophone", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(options, [null, new RoutedEventArgs()]);
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void CheckInputTemplates(MainViewModel main)
    {
        using var rack = new InputRackViewModel(main);
        using var matrix = new MixMatrixViewModel(main);
        var rackView = new InputRackView { DataContext = rack };
        var matrixView = new MixMatrixView { DataContext = matrix };
        var surface = new Window { Width = 640, Height = 700,
            Content = new StackPanel { Children = { rackView, matrixView } } };
        surface.Show();
        try
        {
            WindowLayoutTests.Layout(surface, 640, 700);
            var gains = surface.GetVisualDescendants().OfType<Slider>()
                .Where(s => AutomationProperties.GetName(s) == Localizer.Text("XLR1Gain")).ToArray();
            Assert.Equal(2, gains.Length);
            Assert.All(gains, gain => Assert.False(gain.IsEffectivelyVisible));
            Assert.Contains(rackView.GetVisualDescendants().OfType<LevelMeter>(), meter => meter.IsEffectivelyVisible);
            Assert.Contains(matrixView.GetVisualDescendants().OfType<LevelMeter>(), meter => meter.IsEffectivelyVisible);
        }
        finally { surface.Close(); }
    }

    private static async Task Until(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(ready());
    }

    private static object State(bool generic) => new
    {
        type = "state", connected = false,
        devices = new[]
        {
            new { name = "primary.mic", description = "External USB microphone with a long descriptive device name", kind = 1, isOwn = false },
            new { name = "OpenXLR_chat", description = "OpenXLR Chat", kind = 1, isOwn = true },
        },
        mixer = new { primaryInput = generic ? new { source = "primary.mic", channel = 1 } : null,
            primaryInputConnected = generic,
            mixes = new[] { new { id = "monitor", name = "Monitor", kind = "monitor", volume = 1d, muted = false } },
            channels = new[] { new { id = "xlr1", name = "Microphone", hardware = true, present = true,
                levels = new { monitor = 1d }, mutedIn = Array.Empty<string>() } } },
    };
}
