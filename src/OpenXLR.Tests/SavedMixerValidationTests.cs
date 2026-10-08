using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class SavedMixerValidationTests
{
    // The same bad entry: the settings file keeps everything else, a profile is refused whole.
    [Theory]
    [InlineData("{\"mixVolumes\":null}", "mixVolumes", "null entry")]
    [InlineData("{\"mixMuted\":null}", "mixMuted", "null entry")]
    [InlineData("{\"levels\":null}", "levels", "null entry")]
    [InlineData("{\"channelMuted\":[null]}", "channelMuted", "null entry")]
    [InlineData("{\"monitorOutputs\":[null]}", "monitorOutputs", "null entry")]
    [InlineData("{\"monitorFeeds\":{\"output\":null}}", "monitorFeeds", "null entry")]
    [InlineData("{\"inserts\":{\"xlr1\":null}}", "inserts.xlr1", "null entry")]
    [InlineData("{\"inserts\":{\"xlr1\":[null]}}", "inserts.xlr1", "null entry")]
    [InlineData("{\"inserts\":{\"xlr1\":[{\"id\":null,\"kind\":\"lv2\",\"plugin\":\"test\"}]}}", "inserts.xlr1", "null entry")]
    [InlineData("{\"inserts\":{\"xlr1\":[{\"id\":\"one\",\"kind\":\"lv2\",\"plugin\":\"test\",\"params\":null}]}}", "inserts.xlr1.params", "null entry")]
    [InlineData("{\"mixVolumes\":{\"monitor\":1e999}}", "mixVolumes", "non-finite number")]
    [InlineData("{\"inserts\":{\"xlr1\":[{\"id\":\"one\",\"kind\":\"lv2\",\"plugin\":\"test\",\"params\":{\"gain\":1e999}}]}}", "inserts.xlr1.params", "non-finite number")]
    public void SettingsDropTheBadEntryWhileProfilesAreRefusedWhole(string json, string field, string problem)
    {
        InConfig(root =>
        {
            string settings = Path.Combine(root, "mixer.json");
            string kept = json.Insert(1, "\"userChannels\":[{\"id\":\"music\",\"name\":\"Music\"}],\"appOverrides\":{\"firefox\":\"music\"},");
            File.WriteAllText(settings, kept);
            MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(settings, out string? warning));
            Assert.Contains(settings, warning);
            Assert.Contains($"{field}: {problem}", warning);
            Assert.Equal("music", Assert.Single(loaded.UserChannels!).Id);
            Assert.Equal("music", loaded.AppOverrides["firefox"]);
            Assert.Empty(loaded.MixVolumes);
            Assert.Empty(loaded.MixMuted);
            Assert.Empty(loaded.Levels);
            Assert.Empty(loaded.ChannelMuted);
            Assert.Empty(loaded.MonitorOutputs);
            Assert.Empty(loaded.MonitorFeeds);
            Assert.All(loaded.Inserts.Values, chain => Assert.All(chain, insert => Assert.Empty(insert.Params)));
            Assert.False(File.Exists(settings + ".corrupt"));

            string profilePath = Path.Combine(root, "openxlr", "profiles", "0fd9-007d", "Test.json");
            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
            string profile = "{\"device\":{\"gainDb\":75},\"mixer\":" + json + "}";
            File.WriteAllText(profilePath, profile);
            var ex = Assert.Throws<JsonException>(() => ProfileStore.Load("0fd9:007d", "Test"));
            Assert.Equal($"Invalid saved mixer field '{field}': {problem}.", ex.Message);
            Assert.Equal(kept, File.ReadAllText(settings));
            Assert.Equal(profile, File.ReadAllText(profilePath));
        });
    }

    [Fact]
    public void SettingsDropUserMixesOfAnUnknownKind()
    {
        InConfig(root =>
        {
            string path = Path.Combine(root, "mixer.json");
            File.WriteAllText(path, """
                {"userMixes":[{"id":"phones","name":"Phones","kind":"monitor"},
                  {"id":"old","name":"Old"},
                  {"id":"port","name":"Port","kind":"auxPort"},
                  {"id":"none","name":"None","kind":null}]}
                """);
            MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
            Assert.Contains("userMixes: unknown kind", warning);
            Assert.Equal(["phones", "old"], loaded.UserMixes!.Select(m => m.Id));
            Assert.Equal(["monitor", "virtualMic"], loaded.UserMixes!.Select(m => m.Kind));
        });
    }

    [Theory]
    [InlineData("{\"inserts\":null}", "inserts")]
    [InlineData("{\"monitorOutputs\":null}", "monitorOutputs")]
    [InlineData("{\"monitorFeeds\":null}", "monitorFeeds")]
    [InlineData("{\"appOverrides\":null}", "appOverrides")]
    [InlineData("{\"appOverrides\":{\"app\":null}}", "appOverrides")]
    [InlineData("{\"knownApps\":null}", "knownApps")]
    [InlineData("{\"knownApps\":[null]}", "knownApps")]
    [InlineData("{\"knownApps\":[{\"identity\":null,\"label\":\"App\",\"channelId\":\"system\"}]}", "knownApps")]
    public void SettingsReplaceNullCollectionsAndDropIncompleteApps(string json, string field)
    {
        InConfig(root =>
        {
            string path = Path.Combine(root, "mixer.json");
            File.WriteAllText(path, json);
            MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
            Assert.Contains($"{field}: null entry", warning);
            Assert.Empty(loaded.Inserts);
            Assert.Empty(loaded.MonitorOutputs);
            Assert.Empty(loaded.MonitorFeeds);
            Assert.Empty(loaded.AppOverrides);
            Assert.Empty(loaded.KnownApps);
            Assert.Equal(json, File.ReadAllText(path));
        });
    }

    [Fact]
    public void SettingsKeepTheValidNeighboursOfADroppedEntry()
    {
        InConfig(root =>
        {
            string path = Path.Combine(root, "mixer.json");
            File.WriteAllText(path, """
                {
                  "mixVolumes": {"monitor": 0.5, "stream": 1e999},
                  "levels": {"music|stream": 0.25},
                  "channelMuted": ["music|chat", null],
                  "knownApps": [null, {"identity": "firefox", "label": "Firefox", "channelId": "music"}],
                  "inserts": {"xlr1": [null, {"id": "one", "kind": "lv2", "plugin": "urn:eq", "params": {"gain": 1e999, "q": 0.7}}], "xlr2": null}
                }
                """);
            MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
            Assert.Equal(0.5, Assert.Single(loaded.MixVolumes).Value);
            Assert.Equal(0.25, loaded.Levels["music|stream"]);
            Assert.Equal("music|chat", Assert.Single(loaded.ChannelMuted));
            Assert.Equal("firefox", Assert.Single(loaded.KnownApps).Identity);
            InsertDefinition insert = Assert.Single(Assert.Single(loaded.Inserts).Value);
            Assert.Equal("urn:eq", insert.Plugin);
            Assert.Equal(0.7, Assert.Single(insert.Params).Value);
            foreach (string note in new[] { "mixVolumes: non-finite number", "channelMuted: null entry", "knownApps: null entry",
                         "inserts.xlr1: null entry", "inserts.xlr1.params: non-finite number", "inserts.xlr2: null entry" })
                Assert.Contains(note, warning);
            Assert.Null(loaded.Save(path));
            Assert.Null(MixerSettings.Load(path, out warning)!.Save(path));
            Assert.Null(warning);
        });
    }

    // A control value past the cap is refused by the live mixer, so a file
    // holding more re-enters trimmed: the settings keep the first entries
    // with a note, a profile is refused whole.
    [Fact]
    public void AnInsertWithTooManyControlValuesIsTrimmedInSettingsAndRefusedInAProfile()
    {
        InConfig(root =>
        {
            string values = string.Join(',', Enumerable.Range(0, InsertDefinition.MaxParams + 1).Select(i => $"\"p{i}\":{i}"));
            string json = "{\"inserts\":{\"xlr1\":[{\"id\":\"one\",\"kind\":\"lv2\",\"plugin\":\"urn:eq\",\"params\":{" + values + "}}]}}";
            string settings = Path.Combine(root, "mixer.json");
            File.WriteAllText(settings, json);
            MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(settings, out string? warning));
            Assert.Contains("inserts.xlr1.params: too many entries", warning);
            InsertDefinition insert = Assert.Single(Assert.Single(loaded.Inserts).Value);
            Assert.Equal(InsertDefinition.MaxParams, insert.Params.Count);
            Assert.Equal(0, insert.Params["p0"]);

            string profilePath = Path.Combine(root, "openxlr", "profiles", "0fd9-007d", "Test.json");
            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
            File.WriteAllText(profilePath, "{\"device\":{\"gainDb\":75},\"mixer\":" + json + "}");
            var ex = Assert.Throws<JsonException>(() => ProfileStore.Load("0fd9:007d", "Test"));
            Assert.Equal("Invalid saved mixer field 'inserts.xlr1.params': too many entries.", ex.Message);
        });
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void ACorruptBackupCannotOverwriteASymbolicLinkTarget()
    {
        InConfig(root =>
        {
            string path = Path.Combine(root, "mixer.json");
            string unrelated = Path.Combine(root, "unrelated.txt");
            File.WriteAllText(path, "{broken");
            File.WriteAllText(unrelated, "keep this file");
            File.CreateSymbolicLink(path + ".corrupt", unrelated);
            Assert.Null(MixerSettings.Load(path, out string? warning));
            Assert.Contains("copy kept as", warning);
            Assert.Equal("keep this file", File.ReadAllText(unrelated));
            Assert.Equal("{broken", File.ReadAllText(path + ".corrupt"));
            Assert.Null(new FileInfo(path + ".corrupt").LinkTarget);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path + ".corrupt"));
        });
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void ACorruptBackupIsPrivateEvenWhenTheOriginalWasShared()
    {
        InConfig(root =>
        {
            string path = Path.Combine(root, "mixer.json");
            File.WriteAllText(path, "{broken");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            Assert.Null(MixerSettings.Load(path, out _));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path + ".corrupt"));
        });
    }

    [Fact]
    public void UnparseableSettingsAreSetAsideBeforeTheNextSave()
    {
        InConfig(root =>
        {
            string path = Path.Combine(root, "mixer.json");
            File.WriteAllText(path, "{\"mixVolumes\":{\"monitor\":0.5}");
            Assert.Null(MixerSettings.Load(path, out string? warning));
            Assert.StartsWith(path + ": ", warning);
            Assert.Contains("copy kept as " + path + ".corrupt", warning);
            Assert.Equal("{\"mixVolumes\":{\"monitor\":0.5}", File.ReadAllText(path));
            Assert.Equal("{\"mixVolumes\":{\"monitor\":0.5}", File.ReadAllText(path + ".corrupt"));
            Assert.Null(new MixerSettings().Save(path));
            Assert.Equal("{\"mixVolumes\":{\"monitor\":0.5}", File.ReadAllText(path + ".corrupt"));
            Assert.NotNull(MixerSettings.Load(path, out warning));
            Assert.Null(warning);
        });
    }

    [Fact]
    public void LegacySettingsWithoutInsertsOrFeedsStillLoad()
    {
        InConfig(root =>
        {
            // The shape written before insert chains, Monitor B feeds, the
            // app registry and the editable layout existed; micInput is a
            // field that no longer exists.
            string path = Path.Combine(root, "mixer.json");
            File.WriteAllText(path, """
                {
                  "mixVolumes": {"monitor": 0.8, "stream": 1, "chat": 0.6},
                  "mixMuted": ["chat"],
                  "levels": {"music|stream": 0.5, "xlr1|chat": 1},
                  "channelMuted": ["game|chat"],
                  "monitorOutput": "alsa_output.pci-0000_00_1f.3.analog-stereo",
                  "micInput": "alsa_input.usb-Elgato_Wave_XLR-00.mono-fallback",
                  "appOverrides": {"firefox": "music"},
                  "enforcedDefaultSink": "@monitor"
                }
                """);
            MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
            Assert.Null(warning);
            Assert.Equal(0.6, loaded.MixVolumes["chat"]);
            Assert.Equal("chat", Assert.Single(loaded.MixMuted));
            Assert.Equal(0.5, loaded.Levels["music|stream"]);
            Assert.Equal("game|chat", Assert.Single(loaded.ChannelMuted));
            Assert.Equal("alsa_output.pci-0000_00_1f.3.analog-stereo", loaded.MonitorOutput);
            Assert.Empty(loaded.MonitorOutputs);
            Assert.Empty(loaded.MonitorFeeds);
            Assert.Empty(loaded.Inserts);
            Assert.Empty(loaded.KnownApps);
            Assert.Null(loaded.UserChannels);
            Assert.Null(loaded.UserMixes);
            Assert.Null(loaded.AuxPortEnabled);
            Assert.Equal("music", loaded.AppOverrides["firefox"]);
            Assert.Equal("@monitor", loaded.EnforcedDefaultSink);
            Assert.Equal(MixerConfig.Default().Mixes.Select(m => m.Id), MixerConfig.FromSettings(loaded).Mixes.Select(m => m.Id));
        });
    }

    [Theory]
    [InlineData("{\"device\":{\"hpVolumeDb\":1e999}}")]
    [InlineData("{\"device\":{\"hp2VolumeDb\":-1e999}}")]
    [InlineData("{\"device\":{\"auxLevelDb\":1e999}}")]
    [InlineData("{\"mixer\":{\"outputVolume\":1e999}}")]
    public void SavedOutputLevelsCannotOverflow(string json)
    {
        InConfig(root =>
        {
            string path = Path.Combine(root, "openxlr", "profiles", "0fd9-007d", "Bad.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            Assert.Throws<JsonException>(() => ProfileStore.Load("0fd9:007d", "Bad"));
            Assert.Equal(json, File.ReadAllText(path));
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("device", out var device))
            {
                string directory = Path.Combine(root, "openxlr", "devices", "0fd9-007d");
                Directory.CreateDirectory(directory);
                foreach (string file in new[] { "last-state.json", "defaults.json" })
                    File.WriteAllText(Path.Combine(directory, file), device.GetRawText());
                Assert.Null(DeviceStateStore.LoadLast("0fd9:007d"));
                Assert.Null(DeviceStateStore.LoadDefaults("0fd9:007d"));
            }
        });
    }

    [Fact]
    public void OldProfilesKeepNullableRoutingAndMissingPluginsRemainLoadable()
    {
        InConfig(root =>
        {
            var missing = new InsertDefinition { Id = "one", Kind = "lv2", Plugin = "urn:not-installed" };
            ProfileStore.Save("0fd9:007d", "Old", new Profile { Mixer = new MixerScene { Inserts = new() { ["xlr1"] = [missing] } } });
            var scene = ProfileStore.Load("0fd9:007d", "Old")!.Mixer!;
            Assert.Null(scene.MonitorOutputs);
            Assert.Null(scene.MonitorFeeds);
            Assert.Equal(missing.Plugin, scene.Inserts!["xlr1"][0].Plugin);
            string path = Path.Combine(root, "mixer.json");
            File.WriteAllText(path, "{}");
            Assert.NotNull(MixerSettings.Load(path, out string? warning));
            Assert.Null(warning);
        });
    }

    private static void InConfig(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-validation-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", root);
        try { action(root); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(root, recursive: true);
        }
    }
}
