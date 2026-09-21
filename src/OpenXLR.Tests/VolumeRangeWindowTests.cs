using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenXLR.UI;

namespace OpenXLR.Tests;

internal static class VolumeRangeWindowTests
{
    internal static void Check()
    {
        var main = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            SliderSync.ReleaseTouchGuards();
            var vm = (MainViewModel)main.DataContext!;
            var requests = new List<bool>();
            vm.DesktopVolumeBoostRequested += requests.Add;
            Apply(vm, """
                {"devices":[],"mixer":{"outputVolume":0.67,"mixes":[
                  {"id":"monitor","name":"Monitor A","kind":"monitor","volume":0.67},
                  {"id":"monitor2","name":"Monitor B","kind":"monitor","volume":0.5},
                  {"id":"stream","name":"Stream","kind":"virtualMic","volume":0.4}]}}
                """);
            main.Show();
            Dispatcher.UIThread.RunJobs();
            var output = main.FindControl<Slider>("OutputVolumeSlider")!;
            vm.ApplyDesktopVolumeBoost(false);
            Assert.Equal(1, output.Maximum);
            Assert.Equal(.67, output.Value);
            vm.ApplyDesktopVolumeBoost(true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1.5, output.Maximum);
            Assert.Equal(.67, output.Value);
            Assert.Equal("67%", vm.OutputVolumeText);
            Assert.Equal(.67, vm.Mixes[0].Volume);
            Assert.Equal(1.5, vm.Mixes[0].VolumeRange.Maximum);
            Assert.Equal(1, vm.Mixes[2].VolumeRange.Maximum);
            Assert.Empty(requests); // Readback must not echo the desktop setting.

            vm.Mixes[0].VolumeRange.Boost = false;
            Assert.Equal(new[] { false }, requests);
            Assert.Equal(1, output.Maximum);
            Assert.Equal(1, vm.Mixes[1].VolumeRange.Maximum);
            Assert.Equal(.67, output.Value);
            vm.Mixes[1].Volume = 1.2; // Also opens Plasma's range for a boosted readback.
            Assert.Equal(new[] { false, true }, requests);
            Assert.Equal(1.5, output.Maximum);
            Assert.Equal(.67, vm.OutputVolume);
            output.Value = 1.5;
            Assert.Equal("150%", vm.OutputVolumeText);
            vm.ApplyDesktopVolumeBoost(false);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, output.Maximum);
            Assert.Equal(1, output.Value);
            Assert.Equal(1, vm.Mixes[1].Volume);
            Assert.Equal(.67, vm.Mixes[0].Volume);
            Assert.Equal(.4, vm.Mixes[2].Volume);

            vm.ApplyDesktopVolumeBoost(true);
            Apply(vm, """
                {"devices":[],"mixer":{"outputVolume":1,"mixes":[
                  {"id":"new","name":"New monitor","kind":"monitor","volume":0.25}]}}
                """);
            Assert.Equal(1.5, vm.Mixes[0].VolumeRange.Maximum);
            Assert.Equal(.25, vm.Mixes[0].Volume);
            vm.VolumeRangeError = "Desktop preference is locked.";
            Assert.True(vm.HasVolumeRangeError);
            vm.VolumeRangeError = null;
            Assert.False(vm.HasVolumeRangeError);
        }
        finally { main.Quit(); }
    }

    private static void Apply(MainViewModel model, string state)
        => typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [JsonNode.Parse(state)!]);
}
