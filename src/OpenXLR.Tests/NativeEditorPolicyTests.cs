using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The list of plugins whose own editor stays closed, and the user's choices
/// over it. Every test works on its own file in a temporary directory, so
/// nothing here reads or writes the real configuration.
/// </summary>
public sealed class NativeEditorPolicyTests
{
    private const string DeEsser = "ABCDEF019182FAEB4D616E75466C7665";

    private static readonly IReadOnlyList<NativeEditorRule> Sample =
    [
        new("vst3", DeEsser, "Elgato De-Esser", "Its editor freezes under Wine."),
        new("clap", "com.example.wobble", "Wobble", "Its editor crashes the host."),
    ];

    [Fact]
    public void TheReleaseListBlocksTheDeEsserEditorAndNothingElse()
    {
        Run(directory =>
        {
            var policy = new NativeEditorPolicy(Path.Combine(directory, "native-editors.json"));
            Assert.Null(policy.Error);
            Assert.True(policy.IsBlocked("vst3", DeEsser));
            string? reason = policy.BlockReason("vst3", DeEsser);
            Assert.NotNull(reason);
            Assert.Contains("Wine", reason);

            // The only shipped entry, blocked by default and untouched.
            NativeEditorRuleState rule = Assert.Single(policy.Rules);
            Assert.Equal("vst3", rule.Kind);
            Assert.Equal(DeEsser, rule.Plugin);
            Assert.Equal("Elgato De-Esser", rule.Name);
            Assert.True(rule.DefaultBlocked);
            Assert.False(rule.Override.HasValue);
            Assert.True(rule.Blocked);

            // Every other plugin keeps its own editor.
            Assert.False(policy.IsBlocked("vst3", "0123456789ABCDEF0123456789ABCDEF"));
            Assert.False(policy.IsBlocked("clap", "com.example.other"));
            Assert.Null(policy.BlockReason("clap", "com.example.other"));
        });
    }

    [Fact]
    public void ARuleIsKeptByKindAndPluginIdNotByNameOrSpelling()
    {
        Run(directory =>
        {
            var policy = new NativeEditorPolicy(Path.Combine(directory, "native-editors.json"));

            // The same class id however the host spelt it.
            Assert.True(policy.IsBlocked("vst3", DeEsser.ToLowerInvariant()));
            Assert.True(policy.IsBlocked("VST3", " abcdef01-9182-faeb-4d61-6e75466c7665 "));
            Assert.True(policy.IsBlocked("vst3", "{ABCDEF019182FAEB4D616E75466C7665}"));

            // The id belongs to one kind only, and the display name decides nothing.
            Assert.False(policy.IsBlocked("clap", DeEsser));
            Assert.False(policy.IsBlocked("lv2", DeEsser));
            Assert.False(policy.IsBlocked("vst3", "Elgato De-Esser"));
        });
    }

    [Fact]
    public void AllowingAndResettingOneRuleSurvivesAndLeavesTheListAlone()
    {
        Run(directory =>
        {
            string path = Path.Combine(directory, "native-editors.json");
            var policy = new NativeEditorPolicy(path, Sample);

            Assert.Null(policy.Set("vst3", DeEsser.ToLowerInvariant(), null, false));
            Assert.False(policy.IsBlocked("vst3", DeEsser));
            Assert.Null(policy.BlockReason("vst3", DeEsser));
            Assert.True(policy.IsBlocked("clap", "com.example.wobble"));

            // The choice is on disk, and only the choice.
            var reopened = new NativeEditorPolicy(path, Sample);
            Assert.Null(reopened.Error);
            Assert.False(reopened.IsBlocked("vst3", DeEsser));
            NativeEditorRuleState allowed = reopened.Rules.Single(r => r.Plugin == DeEsser);
            Assert.True(allowed.DefaultBlocked);
            Assert.False(allowed.Override);
            Assert.False(allowed.Blocked);
            Assert.Equal(2, reopened.Rules.Count);
            Assert.Single(Stored(path));

            // Dropping the choice follows the release list again and empties the file.
            Assert.Null(reopened.Set("vst3", DeEsser, null, null));
            Assert.True(reopened.IsBlocked("vst3", DeEsser));
            Assert.Empty(Stored(path));
            Assert.False(new NativeEditorPolicy(path, Sample).Rules.Single(r => r.Plugin == DeEsser).Override.HasValue);
        });
    }

    [Fact]
    public void APluginTheReleaseDoesNotKnowCanBeBlockedByHand()
    {
        Run(directory =>
        {
            string path = Path.Combine(directory, "native-editors.json");
            var policy = new NativeEditorPolicy(path, Sample);
            Assert.Null(policy.Set("lv2", "urn:example:comp", "Example Compressor", true));
            Assert.True(policy.IsBlocked("lv2", "urn:example:comp"));
            Assert.Equal(NativeEditorPolicy.ChosenReason, policy.BlockReason("lv2", "urn:example:comp"));

            NativeEditorRuleState added = new NativeEditorPolicy(path, Sample).Rules.Single(r => r.Kind == "lv2");
            Assert.Equal("urn:example:comp", added.Plugin);
            Assert.Equal("Example Compressor", added.Name);
            Assert.False(added.DefaultBlocked);
            Assert.True(added.Override);
            Assert.True(added.Blocked);
        });
    }

    [Fact]
    public void ChoicesOutliveEntriesTheReleaseListAddsOrDrops()
    {
        Run(directory =>
        {
            string path = Path.Combine(directory, "native-editors.json");
            var before = new NativeEditorPolicy(path, Sample);
            Assert.Null(before.Set("vst3", DeEsser, null, false));                    // allow a shipped entry
            Assert.Null(before.Set("clap", "com.example.wobble", "Wobble", true));    // agree with another
            Assert.Null(before.Set("lv2", "urn:example:comp", "Example", true));      // add one of their own

            // A later release drops the CLAP case and adds a new VST3 one.
            const string added = "00112233445566778899AABBCCDDEEFF";
            IReadOnlyList<NativeEditorRule> next =
            [
                new("vst3", DeEsser, "Elgato De-Esser", "Its editor freezes under Wine."),
                new("vst3", added, "Newly Known", "Its editor does not repaint."),
            ];
            var after = new NativeEditorPolicy(path, next);
            Assert.Null(after.Error);

            Assert.False(after.IsBlocked("vst3", DeEsser));              // the user's choice survived
            Assert.True(after.IsBlocked("vst3", added));                 // the new case applies at once
            Assert.True(after.IsBlocked("lv2", "urn:example:comp"));     // the hand-made entry survived
            Assert.True(after.IsBlocked("clap", "com.example.wobble"));  // the dropped case is now the user's own

            NativeEditorRuleState dropped = after.Rules.Single(r => r.Kind == "clap");
            Assert.False(dropped.DefaultBlocked);
            Assert.True(dropped.Override);
            Assert.Equal("Wobble", dropped.Name);   // the name captured when the choice was made

            // And a release that knows nothing leaves every choice readable.
            var bare = new NativeEditorPolicy(path, []);
            Assert.Equal(3, bare.Rules.Count);
            Assert.False(bare.IsBlocked("vst3", DeEsser));
            Assert.All(bare.Rules, r => Assert.False(r.DefaultBlocked));
        });
    }

    [Fact]
    public void AFailedSaveChangesNeitherTheFileNorTheRunningPolicy()
    {
        Run(directory =>
        {
            // A directory in the file's place: the atomic rename cannot land.
            string path = Path.Combine(directory, "native-editors.json");
            var policy = new NativeEditorPolicy(path, Sample);
            Assert.Null(policy.Error);
            Directory.CreateDirectory(path);

            string? failure = policy.Set("vst3", DeEsser, null, false);
            Assert.NotNull(failure);
            Assert.Contains(path, failure);
            Assert.True(policy.IsBlocked("vst3", DeEsser));
            Assert.False(policy.Rules.Single(r => r.Plugin == DeEsser).Override.HasValue);
            Directory.Delete(path);
            Assert.Null(policy.Set("vst3", DeEsser, null, false));
            Assert.False(policy.IsBlocked("vst3", DeEsser));
        });
    }

    [Fact]
    public void UntouchedDefaultsDisappearWhenAReleaseRemovesThem()
    {
        Run(directory =>
        {
            string path = Path.Combine(directory, "native-editors.json");
            var before = new NativeEditorPolicy(path, Sample);
            Assert.True(before.IsBlocked("vst3", DeEsser));
            Assert.False(File.Exists(path));
            var after = new NativeEditorPolicy(path, []);
            Assert.False(after.IsBlocked("vst3", DeEsser));
            Assert.Empty(after.Rules);
        });
    }

    [Theory]
    [InlineData("{\"version\":1,\"overrides\":[null]}")]
    [InlineData("{\"version\":1,\"overrides\":[{\"kind\":\"clap\",\"plugin\":\"example\"}]}")]
    [InlineData("{\"version\":1}")]
    public void IncompleteChoicesCannotSilentlyAllowAnEditor(string contents)
    {
        Run(directory =>
        {
            string path = Path.Combine(directory, "native-editors.json");
            File.WriteAllText(path, contents);
            var policy = new NativeEditorPolicy(path);
            Assert.NotNull(policy.Error);
            Assert.True(policy.IsBlocked("vst3", DeEsser));
            Assert.NotNull(policy.Set("vst3", DeEsser, null, false));
            Assert.Equal(contents, File.ReadAllText(path));
        });
    }

    [Fact]
    public void ADamagedFileIsReportedAndNeverQuietlyReplaced()
    {
        Run(directory =>
        {
            string path = Path.Combine(directory, "native-editors.json");
            const string damaged = "{ \"version\": 1, \"overrides\": [ this is not json";
            File.WriteAllText(path, damaged);

            var policy = new NativeEditorPolicy(path, Sample);
            string? error = policy.Error;
            Assert.NotNull(error);
            Assert.Contains(path, error);

            // The release list still protects the user while the file is broken.
            Assert.True(policy.IsBlocked("vst3", DeEsser));
            Assert.Equal(2, policy.Rules.Count);
            Assert.All(policy.Rules, r => Assert.False(r.Override.HasValue));

            // A change is refused with the same reason, and the file is left alone.
            Assert.Equal(error, policy.Set("vst3", DeEsser, null, false));
            Assert.True(policy.IsBlocked("vst3", DeEsser));
            Assert.Equal(damaged, File.ReadAllText(path));
        });
    }

    [Fact]
    public void AFileFromAnotherFormatOrWithABadEntryIsRefusedTheSameWay()
    {
        Run(directory =>
        {
            string path = Path.Combine(directory, "native-editors.json");
            string future = "{\"version\":99,\"overrides\":[{\"kind\":\"vst3\",\"plugin\":\"" + DeEsser + "\",\"blocked\":false}]}";
            File.WriteAllText(path, future);
            var policy = new NativeEditorPolicy(path, Sample);
            string? error = policy.Error;
            Assert.NotNull(error);
            Assert.Contains("99", error);
            Assert.True(policy.IsBlocked("vst3", DeEsser));   // the unreadable choice is not guessed at
            Assert.NotNull(policy.Set("vst3", DeEsser, null, false));
            Assert.Equal(future, File.ReadAllText(path));

            string nonsense = "{\"version\":1,\"overrides\":[{\"kind\":\"vst2\",\"plugin\":\"x\",\"blocked\":true}]}";
            File.WriteAllText(path, nonsense);
            var second = new NativeEditorPolicy(path, Sample);
            Assert.NotNull(second.Error);
            Assert.NotNull(second.Set("clap", "com.example.wobble", null, false));
            Assert.Equal(nonsense, File.ReadAllText(path));
        });
    }

    [Fact]
    public void BadInputIsRejectedWithoutTouchingTheFile()
    {
        Run(directory =>
        {
            string path = Path.Combine(directory, "native-editors.json");
            var policy = new NativeEditorPolicy(path, Sample);

            Assert.NotNull(policy.Set("vst2", DeEsser, null, true));                       // unknown kind
            Assert.NotNull(policy.Set("", DeEsser, null, true));
            Assert.NotNull(policy.Set("vst3", "not-a-class-id", null, true));              // bad class id
            Assert.NotNull(policy.Set("vst3", DeEsser + "00", null, true));
            Assert.NotNull(policy.Set("clap", "", null, true));                            // empty id
            Assert.NotNull(policy.Set("clap", new string('x', 600), null, true));          // unbounded id
            Assert.NotNull(policy.Set("clap", "com.example\u0007bell", null, true));       // control character
            Assert.NotNull(policy.Set("clap", "com example", null, true));                 // spaces in an id
            Assert.NotNull(policy.Set("clap", "com.example.wobble", new string('n', 400), true));
            Assert.NotNull(policy.Set("clap", "com.example.wobble", "Wob\u0007ble", true));

            Assert.False(File.Exists(path));
            Assert.Equal(2, policy.Rules.Count);
            Assert.All(policy.Rules, r => Assert.False(r.Override.HasValue));
        });
    }

    [Fact]
    public void ChoicesMadeAtOnceFromSeveralThreadsAllSurvive()
    {
        Run(directory =>
        {
            string path = Path.Combine(directory, "native-editors.json");
            var policy = new NativeEditorPolicy(path, Sample);
            string[] plugins = [.. Enumerable.Range(0, 24).Select(i => $"com.example.plugin{i}")];

            var stop = new ManualResetEventSlim(false);
            var reader = new Thread(() =>
            {
                while (!stop.IsSet)
                {
                    policy.IsBlocked("vst3", DeEsser);
                    _ = policy.Rules.Count;
                }
            });
            reader.Start();

            Parallel.ForEach(plugins, plugin => Assert.Null(policy.Set("clap", plugin, "Plugin", true)));
            stop.Set();
            Assert.True(reader.Join(TimeSpan.FromSeconds(5)));

            foreach (string plugin in plugins) Assert.True(policy.IsBlocked("clap", plugin));
            var reopened = new NativeEditorPolicy(path, Sample);
            Assert.Null(reopened.Error);
            foreach (string plugin in plugins) Assert.True(reopened.IsBlocked("clap", plugin));
            Assert.Equal(plugins.Length, Stored(path).Count);
        });
    }

    [Fact]
    public void ABlockedEditorAlwaysSaysWhyAndAnAllowedOneSaysNothing()
    {
        Run(directory =>
        {
            const string blank = "00112233445566778899AABBCCDDEEFF";
            IReadOnlyList<NativeEditorRule> defaults =
            [
                new("vst3", DeEsser, "Elgato De-Esser", "Its editor freezes under Wine."),
                new("vst3", blank, "Unstated", "   "),   // a rule that forgot to say why
            ];
            string path = Path.Combine(directory, "native-editors.json");
            var policy = new NativeEditorPolicy(path, defaults);

            Assert.Equal(NativeEditorPolicy.UnstatedReason, policy.BlockReason("vst3", blank));
            Assert.Null(policy.Set("clap", "com.example.wobble", "Wobble", true));
            Assert.Equal(NativeEditorPolicy.ChosenReason, policy.BlockReason("clap", "com.example.wobble"));
            Assert.Null(policy.Set("vst3", DeEsser, null, false));
            Assert.Null(policy.BlockReason("vst3", DeEsser));            // allowed, so nothing to say
            Assert.Null(policy.BlockReason("lv2", "urn:example:none"));  // never named, so nothing to say
            Assert.Null(policy.BlockReason("vst2", DeEsser));            // not even a kind we know

            // A reason is given exactly when the editor stays closed.
            foreach (NativeEditorRuleState rule in policy.Rules)
            {
                string? reason = policy.BlockReason(rule.Kind, rule.Plugin);
                Assert.Equal(rule.Blocked, policy.IsBlocked(rule.Kind, rule.Plugin));
                if (rule.Blocked) Assert.False(string.IsNullOrWhiteSpace(reason));
                else Assert.Null(reason);
            }
        });
    }

    [Fact]
    public void TheFileLivesUnderTheConfigurationDirectory()
        => Assert.Equal(OpenXlrPaths.ConfigFile("native-editors.json"), NativeEditorPolicy.DefaultPath);

    /// <summary>The choices the file holds, which are never a copy of the release list.</summary>
    private static List<JsonElement> Stored(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        return [.. document.RootElement.GetProperty("overrides").EnumerateArray().Select(e => e.Clone())];
    }

    private static void Run(Action<string> test)
    {
        string directory = Directory.CreateTempSubdirectory("native-editor-policy-").FullName;
        try { test(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
