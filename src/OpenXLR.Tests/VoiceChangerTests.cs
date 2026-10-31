using System.Reflection;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The microphone voice changer: its model, validation, how it is saved and
/// recalled, the stage it asks for, the controls it writes and the state
/// clients read. The audio itself is checked by <c>make -C native
/// test-voice</c> and, on a private PipeWire server, by the DSP integration
/// tests.
/// </summary>
public sealed class VoiceChangerTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1" or "xlr2" or "aux" or "system" or "guest";
        public bool HasMix(string id) => id is "monitor";
        public bool IsMonitorFeed(string feed) => feed is "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => key is "xlr1" or "xlr2" or "guest";
        public bool IsMicrophoneChannel(string id) => id is "xlr1" or "xlr2" or "guest";
        public int OverrideCount => 0;
    }

    private static string? Check(string json, bool host = true)
        => CommandValidation.Check(JsonSerializer.Deserialize<Command>(json, Web)!, new Layout(), _ => null, host);

    private static MicVoiceState Voice(Mixer mixer, string channel = "xlr1")
        => mixer.Snapshot().Channels.Single(c => c.Id == channel).Voice!;

    [Fact]
    public void TheDefaultIsOffOnTheDeepVoiceWithNoShift()
    {
        MicVoice voice = MicVoice.Default;
        Assert.False(voice.Enabled);
        Assert.Equal("deep", voice.Voice);
        Assert.Equal(0, voice.Pitch);
        Assert.Equal(0, voice.Formant);
        // The ids the native stage numbers, in its fixed order.
        Assert.Equal(["custom", "deep", "high", "robot", "radio", "alien", "dracula", "frankenstein", "mummy", "werewolf", "witch"],
            MicVoice.Voices);
        Assert.Equal(0, MicVoice.VoiceIndex("custom"));
        Assert.Equal(1, MicVoice.VoiceIndex("deep"));
        Assert.Equal(10, MicVoice.VoiceIndex("witch"));
        Assert.Equal(-1, MicVoice.VoiceIndex("Robot"));
        Assert.Equal(-1, MicVoice.VoiceIndex(null));
    }

    [Theory]
    [InlineData("custom", 0, 0, null)]
    [InlineData("witch", -12, 12, null)]
    [InlineData("custom", 3.3, -4.6, null)]
    [InlineData("ghost", 0, 0, "voice must be one of custom, deep, high, robot, radio, alien, dracula, frankenstein, mummy, werewolf, witch")]
    [InlineData("custom", -12.5, 0, "pitch must be -12 to 12 semitones")]
    [InlineData("custom", double.NaN, 0, "pitch must be -12 to 12 semitones")]
    [InlineData("custom", 0, 12.5, "formant must be -12 to 12 semitones")]
    [InlineData("custom", 0, double.PositiveInfinity, "formant must be -12 to 12 semitones")]
    public void ASettingOutsideTheRangeIsRefusedNotClamped(string voice, double pitch, double formant, string? error)
    {
        using var culture = new CultureScope("it-IT");
        Assert.Equal(error, MicVoice.Check(new MicVoice { Enabled = true, Voice = voice, Pitch = pitch, Formant = formant }));
    }

    [Fact]
    public void ShiftsSnapToHalfSemitones()
    {
        Assert.Equal(3.5, MicVoice.SnapShift(3.3));
        Assert.Equal(-4.5, MicVoice.SnapShift(-4.6));
        Assert.Equal(1, MicVoice.SnapShift(0.75));
        MicVoice? read = MicVoice.Read(JsonDocument.Parse("""{"enabled":true,"voice":"custom","pitch":2.2,"formant":-1.3,"active":"x"}""").RootElement, out string? error);
        Assert.Null(error);
        Assert.Equal(new MicVoice { Enabled = true, Voice = "custom", Pitch = 2, Formant = -1.5 }, read);
        Assert.Null(MicVoice.Read(JsonDocument.Parse("""{"enabled":true,"voice":"deep","pitch":0}""").RootElement, out error));
        Assert.Equal("need 'enabled', 'voice', 'pitch' and 'formant'", error);
        Assert.Null(MicVoice.Read(JsonDocument.Parse("""{"enabled":true,"voice":"x","pitch":0,"formant":0}""").RootElement, out error));
        Assert.StartsWith("voice must be one of", error);
    }

    [Theory]
    [InlineData("""{"cmd":"setVoiceChanger","channel":"xlr1","enabled":true,"voice":"robot"}""", null)]
    [InlineData("""{"cmd":"setVoiceChanger","channel":"xlr2","enabled":false}""", null)]
    [InlineData("""{"cmd":"setVoiceChanger","channel":"guest","voice":"custom","pitch":-3,"formant":2.5}""", null)]
    [InlineData("""{"cmd":"setVoiceChanger","channel":"xlr1","formant":12}""", null)]
    [InlineData("""{"cmd":"setVoiceChanger","channel":"xlr1"}""", "setVoiceChanger: need at least one of 'enabled', 'voice', 'pitch' and 'formant'")]
    [InlineData("""{"cmd":"setVoiceChanger","enabled":true}""", "setVoiceChanger: need 'channel'")]
    [InlineData("""{"cmd":"setVoiceChanger","channel":"aux","enabled":true}""", "setVoiceChanger: 'aux' is not a microphone input")]
    [InlineData("""{"cmd":"setVoiceChanger","channel":"system","voice":"deep"}""", "setVoiceChanger: 'system' is not a microphone input")]
    [InlineData("""{"cmd":"setVoiceChanger","channel":"xlr1","voice":"Robot"}""", "setVoiceChanger: voice must be one of custom, deep, high, robot, radio, alien, dracula, frankenstein, mummy, werewolf, witch")]
    [InlineData("""{"cmd":"setVoiceChanger","channel":"xlr1","pitch":13}""", "setVoiceChanger: pitch must be -12 to 12 semitones")]
    [InlineData("""{"cmd":"setVoiceChanger","channel":"xlr1","formant":-12.5}""", "setVoiceChanger: formant must be -12 to 12 semitones")]
    public void TheCommandIsCheckedBeforeTheMixer(string json, string? error) => Assert.Equal(error, Check(json));

    [Fact]
    public void SwitchingItOnNeedsTheNativeHelper()
    {
        Assert.Equal(Mixer.VoiceNeedsHost, Check("""{"cmd":"setVoiceChanger","channel":"xlr1","enabled":true,"voice":"witch"}""", host: false));
        // Off, the voice and the shifts are only stored.
        Assert.Null(Check("""{"cmd":"setVoiceChanger","channel":"xlr1","enabled":false}""", host: false));
        Assert.Null(Check("""{"cmd":"setVoiceChanger","channel":"xlr1","voice":"alien"}""", host: false));
    }

    [Fact]
    public void TheMixerStoresSnappedValuesAndRefusesTheRest()
    {
        using var mixer = new Mixer();
        Assert.Equal(MicVoice.Default, mixer.VoiceFor("xlr1"));
        mixer.SetVoiceChanger("xlr1", new(Enabled: true, Voice: "custom", Pitch: 4.2, Formant: -2.7));
        Assert.Equal(new MicVoice { Enabled = true, Voice = "custom", Pitch = 4, Formant = -2.5 }, mixer.VoiceFor("xlr1"));
        // One field at a time keeps the others, as a hold key's release does.
        mixer.SetVoiceChanger("xlr1", new(Enabled: false));
        Assert.Equal(new MicVoice { Enabled = false, Voice = "custom", Pitch = 4, Formant = -2.5 }, mixer.VoiceFor("xlr1"));
        Assert.Throws<ArgumentException>(() => mixer.SetVoiceChanger("xlr1", new(Voice: "ghost")));
        Assert.Throws<ArgumentException>(() => mixer.SetVoiceChanger("xlr1", new(Pitch: 20)));
        Assert.Throws<ArgumentException>(() => mixer.SetVoiceChanger("xlr1", new()));
        Assert.Throws<ArgumentException>(() => mixer.SetVoiceChanger("aux", new(Enabled: true)));
        Assert.Equal("custom", mixer.VoiceFor("xlr1").Voice);
        Assert.Equal(MicVoice.Default, mixer.VoiceFor("xlr2"));

        Assert.Equal("custom", mixer.ExportSettings().VoiceChanger["xlr1"].Voice);
        Assert.False(mixer.ExportSettings().VoiceChanger.ContainsKey("xlr2"), "A setting never made is not written: absent means off.");
        MixerScene scene = mixer.ExportScene();
        Assert.Equal(["xlr1", "xlr2"], scene.VoiceChanger!.Keys.Order());
        Assert.Equal(MicVoice.Default, scene.VoiceChanger["xlr2"]);
    }

    [Fact]
    public void TheStateSaysWhyTheVoiceChangerCannotRun()
    {
        using var mixer = new Mixer();
        ((PipeWireAdapter)typeof(Mixer).GetField("_pw", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!).EqHost
            = "/nonexistent/openxlr-lv2-host";
        mixer.SetVoiceChanger("xlr1", new(Enabled: true, Voice: "robot"));
        MicVoiceState state = Voice(mixer);
        Assert.True(state.Enabled);
        Assert.Equal("robot", state.Voice);
        Assert.False(state.Available);
        Assert.Null(state.Active);
        Assert.Equal(Mixer.VoiceNeedsHost, state.Error);
        Assert.Null(Voice(mixer, "xlr2").Error);
        Assert.Null(mixer.Snapshot().Channels.Single(c => c.Id == "aux").Voice);
        Assert.Null(mixer.Snapshot().Channels.Single(c => c.Id == "system").Voice);

        using JsonDocument json = JsonSerializer.SerializeToDocument(mixer.Snapshot(), Web);
        JsonElement xlr1 = json.RootElement.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "xlr1");
        JsonElement voice = xlr1.GetProperty("voice");
        Assert.True(voice.GetProperty("enabled").GetBoolean());
        Assert.Equal("robot", voice.GetProperty("voice").GetString());
        Assert.Equal(0, voice.GetProperty("pitch").GetDouble());
        Assert.Equal(0, voice.GetProperty("formant").GetDouble());
        Assert.Equal(JsonValueKind.Null, voice.GetProperty("active").ValueKind);
        Assert.False(voice.GetProperty("available").GetBoolean());
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "The voice changer could not start. Switch it off and on to try again.")]
    [InlineData(2, "The voice changer could not keep up on this computer. Switch it off and on to try again, or choose another voice.")]
    public void TheStagesStatusBecomesASentence(int status, string? sentence)
        => Assert.Equal(sentence, Mixer.VoiceStatusText(status));

    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, "custom")]
    [InlineData(3, "robot")]
    [InlineData(10, "witch")]
    [InlineData(11, null)]
    [InlineData(double.NaN, null)]
    public void TheActiveMeterNamesTheVoiceHeard(double meter, string? voice)
    {
        Assert.Equal(voice, Mixer.ActiveVoice(new Dictionary<string, double> { ["voice_active"] = meter }));
        Assert.Null(Mixer.ActiveVoice(new Dictionary<string, double>()));
    }

    [Theory]
    [InlineData(1600, 48000, 33.3)]
    [InlineData(1470, 44100, 33.3)]
    [InlineData(1440, 48000, 30.0)]
    [InlineData(0, 48000, null)]
    [InlineData(double.NaN, 48000, null)]
    public void ADelayMeterReadsInMillisecondsAtTheStagesRate(double samples, int rate, double? ms)
    {
        var meters = new Dictionary<string, double> { [SoftwareEqBackend.VoiceLatencyMeter] = samples };
        Assert.Equal(ms, SoftwareEqBackend.MeterMilliseconds(meters, SoftwareEqBackend.VoiceLatencyMeter, rate));
        Assert.Null(SoftwareEqBackend.MeterMilliseconds(meters, SoftwareEqBackend.NoiseLatencyMeter, rate));
    }

    [Fact]
    public void TheVoiceChangerAloneAsksForTheMicrophoneStage()
    {
        using var mixer = new Mixer();
        Assert.False(mixer.WantsMicrophoneStage("xlr1"));
        mixer.SetVoiceChanger("xlr1", new(Voice: "alien"));
        Assert.False(mixer.WantsMicrophoneStage("xlr1"), "A voice chosen while off asks for nothing.");
        mixer.SetVoiceChanger("xlr1", new(Enabled: true));
        Assert.True(mixer.WantsMicrophoneStage("xlr1"));
        Assert.False(mixer.EqWantsSoftwareStage("xlr1"), "The EQ itself stays off.");
        Assert.False(mixer.WantsMicrophoneStage("xlr2"));
        mixer.SetVoiceChanger("xlr1", new(Enabled: false));
        Assert.False(mixer.WantsMicrophoneStage("xlr1"));
        Assert.False(mixer.WantsMicrophoneStage("aux"));
    }

    [Fact]
    public void ARecallChangesOnlyTheSettingsItNames()
    {
        MicVoice on = new() { Enabled = true, Voice = "dracula" };
        MicVoice guest = new() { Enabled = false, Voice = "custom", Pitch = 3 };
        var current = new Dictionary<string, MicVoice> { ["xlr1"] = on, ["guest"] = guest };
        // A profile from before the voice changer carries none: nothing changes.
        Assert.Equal(current, Mixer.RecallVoice(current, null));
        Assert.Equal(current, Mixer.RecallVoice(current, new Dictionary<string, MicVoice>()));
        var next = Mixer.RecallVoice(current, new Dictionary<string, MicVoice> { ["xlr2"] = on with { Voice = "custom", Pitch = 1.2 } });
        Assert.Same(on, next["xlr1"]);
        Assert.Same(guest, next["guest"]);
        Assert.Equal(1, next["xlr2"].Pitch);
        // An unreadable entry never replaces a good one.
        next = Mixer.RecallVoice(current, new Dictionary<string, MicVoice> { ["xlr1"] = on with { Voice = "x" } });
        Assert.Same(on, next["xlr1"]);
    }

    [Fact]
    public void TheSettingsFileKeepsEveryReadableSettingAndDropsTheRest()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-voice-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            File.WriteAllText(path, """
                {
                  "voiceChanger": {
                    "xlr1": {"enabled": true, "voice": "custom", "pitch": 2.2, "formant": -3},
                    "cap1": {"enabled": false, "voice": "witch", "pitch": 0, "formant": 0},
                    "xlr2": {"enabled": true, "voice": "custom", "pitch": 30, "formant": 0},
                    "guest": {"enabled": true, "voice": "pirate", "pitch": 0, "formant": 0}
                  }
                }
                """);
            MixerSettings? settings = MixerSettings.Load(path, out string? warning);
            Assert.NotNull(settings);
            Assert.Contains("voiceChanger.xlr2: pitch must be -12 to 12 semitones", warning);
            Assert.Contains("voiceChanger.guest: voice must be one of", warning);
            Assert.Equal(["cap1", "xlr1"], settings!.VoiceChanger.Keys.Order());
            Assert.Equal(2, settings.VoiceChanger["xlr1"].Pitch);
            Assert.Null(settings.Save(path));
            MixerSettings again = MixerSettings.Load(path, out string? clean)!;
            Assert.Null(clean);
            Assert.Equal(settings.VoiceChanger, again.VoiceChanger);
            Assert.Contains("\"voice\": \"witch\"", File.ReadAllText(path));
            // A file from before the voice changer has it off everywhere.
            File.WriteAllText(path, "{}");
            Assert.Empty(MixerSettings.Load(path, out _)!.VoiceChanger);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AProfileRoundTripsItsSettingAndOneWithoutLeavesItOut()
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        MicVoice voice = new() { Enabled = true, Voice = "custom", Pitch = -5, Formant = 1.5 };
        var scene = new MixerScene { VoiceChanger = new() { ["xlr1"] = voice } };
        MixerScene back = JsonSerializer.Deserialize<MixerScene>(JsonSerializer.Serialize(scene, json), json)!;
        Assert.Equal(voice, back.VoiceChanger!["xlr1"]);
        SavedMixerValidation.Validate(back);
        Assert.Null(JsonSerializer.Deserialize<MixerScene>("{}", json)!.VoiceChanger);
        MixerScene bad = back with { VoiceChanger = new() { ["xlr1"] = voice with { Formant = 13 } } };
        Assert.Contains("voiceChanger.xlr1", Assert.Throws<JsonException>(() => SavedMixerValidation.Validate(bad)).Message);
    }

    [Fact]
    public void TheNativeControlsCarryEveryValueByItsSymbol()
    {
        var voice = new MicVoice { Enabled = true, Voice = "werewolf", Pitch = -2, Formant = 3.5 };
        Dictionary<string, double> controls = SoftwareEqBackend.Controls(voice);
        // The voice before the switch, so switching on starts on the voice asked for.
        Assert.Equal(["voice_preset", "voice_pitch", "voice_formant", "voice_enabled"], controls.Keys);
        Assert.Equal(9, controls["voice_preset"]);
        Assert.Equal(-2, controls["voice_pitch"]);
        Assert.Equal(3.5, controls["voice_formant"]);
        Assert.Equal(1, controls["voice_enabled"]);
        // The default setting is what the native stage starts on, so a stage
        // whose voice changer was never switched on is never written to.
        Assert.Equal(new Dictionary<string, double> { ["voice_preset"] = 1, ["voice_pitch"] = 0, ["voice_formant"] = 0, ["voice_enabled"] = 0 },
            SoftwareEqBackend.Controls(MicVoice.Default));
        // The stage starts without them: a helper from before the voice
        // changer refuses a control it does not declare.
        Assert.DoesNotContain(SoftwareEqBackend.Controls(MicEq.Default, MicNoise.Default).Keys, k => k.StartsWith("voice_", StringComparison.Ordinal));
        Assert.Contains("voice_active", SoftwareEqBackend.Meters);
        Assert.Contains("voice_status", SoftwareEqBackend.Meters);
        Assert.Contains("voice_latency", SoftwareEqBackend.Meters);
        // The C side declares the same symbols and meters, once it is in the tree.
        string native = Path.Combine(MicEqTests.RepoRoot, "native", "voice.c");
        if (!File.Exists(native)) return;
        string source = File.ReadAllText(native);
        foreach (string symbol in controls.Keys.Append("voice_active").Append("voice_status").Append("voice_latency"))
            Assert.Contains($"\"{symbol}\"", source);
    }
}
