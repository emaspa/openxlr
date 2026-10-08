using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>The full, compact and mini views of the submixer, and where their choices are kept.</summary>
[Collection("xdg-config")]
public sealed class MixerViewTests
{
    [Fact]
    public async Task CompactShowsTheChosenChannelEvenHiddenAndFallsBackWithoutForgettingIt()
    {
        await InConfig(async () =>
        {
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            var model = new MainViewModel(client);
            Apply(model, ["game", "music", "chat"], hidden: "game");
            ChannelViewModel game = Channel(model, "game"), music = Channel(model, "music");
            Assert.False(game.DisplayVisible);
            Assert.True(music.DisplayVisible);

            Assert.Null(model.ChooseMixerView(MainViewModel.CompactView));
            Assert.True(model.ShowChannelSelector);
            Assert.False(model.ShowMixSelector);
            Assert.True(model.ShowDetailedSections);
            Assert.Same(music, model.SelectedCompactChannel);   // the first channel that is not hidden
            model.SelectedCompactChannel = game;
            Assert.Equal(["game"], model.Channels.Where(c => c.DisplayVisible).Select(c => c.Id));
            Assert.All(game.Sends, send => Assert.True(send.DisplayVisible));
            Assert.All(model.Mixes, mix => Assert.True(mix.DisplayVisible));
            Assert.Equal("compact", UiSettings.Load().MixerView);
            Assert.Equal("game", UiSettings.Load().CompactChannel);

            Apply(model, ["music", "chat"], hidden: null);
            Assert.Same(Channel(model, "music"), model.SelectedCompactChannel);
            Assert.Equal("game", UiSettings.Load().CompactChannel);
            Apply(model, ["game", "music", "chat"], hidden: "game");
            Assert.Equal("game", model.SelectedCompactChannel!.Id);
            Assert.Equal(0.42, Channel(model, "game").Sends[0].Level);

            Assert.Null(model.ChooseMixerView(MainViewModel.FullView));
            Assert.False(Channel(model, "game").DisplayVisible);
            Assert.Null(UiSettings.Load().MixerView);
        });
    }

    [Fact]
    public async Task MiniShowsOneSendAndOneMasterAndSetsSectionsAside()
    {
        await InConfig(async () =>
        {
            new UiSettings { MixerView = "mini", CompactChannel = "music", CompactMix = "stream" }.Save();
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            var model = new MainViewModel(client);
            Apply(model, ["game", "music"], hidden: null);
            Assert.True(model.IsMiniView);
            Assert.False(model.ShowDetailedSections);
            Assert.False(model.ShowApplications);
            Assert.Equal(["music"], model.Channels.Where(c => c.DisplayVisible).Select(c => c.Id));
            Assert.Equal(["stream"], model.Mixes.Where(m => m.DisplayVisible).Select(m => m.Id));
            Assert.Equal(["stream"], Channel(model, "music").Sends.Where(s => s.DisplayVisible).Select(s => s.MixId));

            model.SelectedCompactMix = model.Mixes.Single(m => m.Id == "monitor");
            Assert.Equal(["monitor"], model.Mixes.Where(m => m.DisplayVisible).Select(m => m.Id));
            Assert.Equal("monitor", UiSettings.Load().CompactMix);
            Assert.All(Channel(model, "music").Sends, send => Assert.Equal(0.42, send.Level));
        });
    }

    [Fact]
    public async Task AChoiceThatCannotBeSavedStaysForTheRunAndSaysWhy()
    {
        await InConfig(async () =>
        {
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            var model = new MainViewModel(client);
            Apply(model, ["game", "music"], hidden: null);
            var options = new OptionsViewModel(client, model);
            string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
            Directory.CreateDirectory(path);
            try
            {
                options.SelectedMixerView = options.MixerViewChoices.Single(c => c.Id == MainViewModel.CompactView);
                Assert.True(model.IsCompactView);
                Assert.NotNull(options.PreferenceError);
                model.SelectedCompactChannel = Channel(model, "music");
                Assert.Equal(["music"], model.Channels.Where(c => c.DisplayVisible).Select(c => c.Id));
                Assert.NotNull(model.ViewPreferenceError);
            }
            finally { Directory.Delete(path); }
            model.SelectedCompactChannel = Channel(model, "game");
            Assert.Null(model.ViewPreferenceError);
            options.SelectedMixerView = options.MixerViewChoices.Single(c => c.Id == MainViewModel.MiniView);
            Assert.Null(options.PreferenceError);
            Assert.Equal("mini", UiSettings.Load().MixerView);
        });
    }

    private static ChannelViewModel Channel(MainViewModel model, string id) => model.Channels.Single(c => c.Id == id);

    private static void Apply(MainViewModel model, string[] channels, string? hidden)
    {
        var state = new JsonObject
        {
            ["mixes"] = new JsonArray(
                new JsonObject { ["id"] = "monitor", ["name"] = "Monitor A", ["kind"] = "monitor" },
                new JsonObject { ["id"] = "stream", ["name"] = "Stream", ["kind"] = "virtualMic" }),
            ["channels"] = new JsonArray([.. channels.Select(id => (JsonNode)new JsonObject
            {
                ["id"] = id, ["name"] = id, ["levels"] = new JsonObject { ["monitor"] = 0.42, ["stream"] = 0.42 },
                ["appearance"] = new JsonObject { ["hidden"] = id == hidden },
            })]),
        };
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [state]);
    }

    private static async Task InConfig(Func<Task> action)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-view-").FullName;
        string? old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory); await action(); }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old); Directory.Delete(directory, true); }
    }
}
