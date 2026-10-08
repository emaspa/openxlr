using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenXLR.Tui;
using Tmds.DBus.Protocol;
using App = OpenXLR.Tui.App;
using ProcessRunner = OpenXLR.Core.ProcessRunner;
using TuiProgram = OpenXLR.Tui.Program;

namespace OpenXLR.Tests;

/// <summary>
/// Material's mode in the terminal mixer: the palette it picks, the shared
/// ui.json key, Options, and the portal watch behind system mode.
/// </summary>
[Collection("xdg-config")]
public sealed class TuiAppearanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openxlr-tui-appearance-" + Guid.NewGuid());
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private readonly string? _oldBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");

    public TuiAppearanceTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", _oldBus);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void MaterialModeUsesTheSharedLightPaletteAndDesktopEventsDoNotAllocate()
    {
        var app = new App(new DaemonLink(), Theme.Material);
        Assert.Same(Theme.Material, app.Theme);
        app.UseSystemScheme(2);
        Assert.Same(Theme.MaterialLight, app.Theme);
        Assert.True(app.Theme.Light);
        Assert.Equal("default", app.Theme.Id);
        app.UseAppearance("dark");
        Assert.Same(Theme.Material, app.Theme);
        app.UseAppearance("light");
        Assert.Same(Theme.MaterialLight, app.Theme);
        app.UseAppearance("system");
        app.UseSystemScheme(0);
        Assert.Same(Theme.Material, app.Theme);
        // Duplicate and changing desktop events only select an existing palette.
        for (int i = 0; i < 100; i++) app.UseSystemScheme(i % 3);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) app.UseSystemScheme(i % 3);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Same(Theme.Material, app.Theme);
    }

    [Fact]
    public void ExplicitSkinsKeepTheirPaletteUntilTheUserMakesANewChoice()
    {
        Theme custom = Theme.FromJson("""{"tokens":{"Ox.Window.Background":"#fefefe"}}""", "custom", "Custom");
        var app = new App(new DaemonLink(), custom, "dark");
        app.UseSystemScheme(2);
        Assert.Same(custom, app.Theme);
        app.UseAppearance("light");
        Assert.Same(custom, app.Theme);
        app.UseTheme(Theme.Material);
        Assert.Same(Theme.MaterialLight, app.Theme);

        var overridden = new App(new DaemonLink(), Theme.Material, "light", skinOverride: true);
        overridden.UseSystemScheme(2);
        Assert.False(overridden.FollowsSystem);
        Assert.Same(Theme.Material, overridden.Theme);
        overridden.UseAppearance("system");
        Assert.Same(Theme.MaterialLight, overridden.Theme);
        Assert.True(overridden.FollowsSystem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEmptyStartupOverrideUsesTheSavedSkinAndMode(string? skin)
    {
        Assert.True(UiSettingsFile.WriteAppearanceMode("light"));
        Assert.True(UiSettingsFile.WriteSkin("default"));
        App material = TuiProgram.CreateApp(new DaemonLink(), skin);
        Assert.Same(Theme.MaterialLight, material.Theme);
        Assert.True(UiSettingsFile.WriteSkin("opendeck"));
        App saved = TuiProgram.CreateApp(new DaemonLink(), skin);
        Assert.Equal("opendeck", saved.Theme.Id);
        App forced = TuiProgram.CreateApp(new DaemonLink(), "default");
        Assert.Same(Theme.Material, forced.Theme);
        App custom = TuiProgram.CreateApp(new DaemonLink(), "nord");
        Assert.Equal("nord", custom.Theme.Id);
    }

    [Fact]
    public void AppearanceSettingPreservesSkinAndUnrelatedSettingsAndRejectsUnknownModes()
    {
        Assert.Equal("system", UiSettingsFile.ReadAppearanceMode());
        Assert.True(UiSettingsFile.WriteSkin("nord"));
        string file = Path.Combine(_root, "openxlr", "ui.json");
        JsonObject root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        root["futureSetting"] = new JsonObject { ["preserved"] = true };
        File.WriteAllText(file, root.ToJsonString());
        Assert.True(UiSettingsFile.WriteAppearanceMode("light"));
        Assert.Equal("light", UiSettingsFile.ReadAppearanceMode());
        Assert.Equal("nord", UiSettingsFile.ReadSkin());
        Assert.True(JsonNode.Parse(File.ReadAllText(file))!["futureSetting"]!["preserved"]!.GetValue<bool>());
        Assert.False(UiSettingsFile.WriteAppearanceMode("sepia"));
        Assert.Equal("light", UiSettingsFile.ReadAppearanceMode());
        Assert.True(UiSettingsFile.WriteSkin("default"));
        Assert.Equal("light", UiSettingsFile.ReadAppearanceMode());
    }

    [Theory]
    [InlineData("{\"appearanceMode\":17}")]
    [InlineData("{\"appearanceMode\":{}}")]
    [InlineData("{\"appearanceMode\":\"future\"}")]
    [InlineData("not json")]
    [InlineData("{\"appearanceMode\":\"light\",\"appearanceMode\":\"dark\"}")]
    public void AnInvalidSavedModeFallsBackToSystem(string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, "openxlr"));
        File.WriteAllText(Path.Combine(_root, "openxlr", "ui.json"), json);
        Assert.Equal("system", UiSettingsFile.ReadAppearanceMode());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"appearanceMode\":\"light\",\"appearanceMode\":\"dark\"}")]
    public void AnUnreadableSettingsDocumentIsPreservedWhenSavingFails(string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, "openxlr"));
        string file = Path.Combine(_root, "openxlr", "ui.json");
        File.WriteAllText(file, json);
        Assert.False(UiSettingsFile.WriteAppearanceMode("light"));
        Assert.False(UiSettingsFile.WriteSkin("default"));
        Assert.Equal(json, File.ReadAllText(file));
    }

    [Fact]
    public void OptionsChangesTheModeWithTheExistingChoiceControl()
    {
        var app = new App(new DaemonLink(), Theme.Material);
        app.ShowTab(7);
        app.Draw(new Screen(100, 30));
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal("light", app.AppearanceMode);
        Assert.Same(Theme.MaterialLight, app.Theme);
        Assert.Equal("light", UiSettingsFile.ReadAppearanceMode());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChoiceThatCannotBeSavedIsStillWornForTheRun(bool chooseSkin)
    {
        string file = Path.Combine(_root, "openxlr", "ui.json");
        Directory.CreateDirectory(file); // a real write failure, whoever runs the test
        var app = new App(new DaemonLink(), Theme.Material, "system", skinOverride: true);
        app.UseSystemScheme(2);
        Assert.Same(Theme.Material, app.Theme); // the launch override holds Material dark
        app.ShowTab(7);
        var screen = new Screen(120, 30);
        app.Draw(screen);
        if (chooseSkin) app.Handle(new KeyPress(Key.Down)); // Material is the first skin after the mode
        app.Handle(new KeyPress(chooseSkin ? Key.Enter : Key.Right));

        // The choice ends the override and goes on, so the light desktop shows.
        Assert.Same(Theme.MaterialLight, app.Theme);
        Assert.Equal(chooseSkin ? "system" : "light", app.AppearanceMode);
        Assert.True(Directory.Exists(file));
        app.Draw(screen);
        Assert.Contains("the saved choice could not be written", Text(screen));
    }

    private static string Text(Screen screen) =>
        string.Join("\n", Enumerable.Range(0, screen.Height)
            .Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray())));

    [Fact]
    public async Task LeavingSystemModeDisposesItsSubscriptionAndReturningStartsOnce()
    {
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", null);
        var app = new App(new DaemonLink(), Theme.Material);
        DesktopAppearance? appearance = await TuiProgram.FollowAppearance(app, null);
        Assert.NotNull(appearance);
        Assert.Same(appearance, await TuiProgram.FollowAppearance(app, appearance));
        app.UseAppearance("dark");
        Assert.Null(await TuiProgram.FollowAppearance(app, appearance));
        app.UseAppearance("system");
        appearance = await TuiProgram.FollowAppearance(app, null);
        Assert.NotNull(appearance);
        app.UseTheme(Theme.FromJson("{}", "custom", "Custom"));
        Assert.Null(await TuiProgram.FollowAppearance(app, appearance));
    }

    [Theory]
    [InlineData("(<uint32 2>,)", 2)]
    [InlineData("(<<uint32 1>>,)", 1)]
    [InlineData("(<<uint32 99>>,)", 0)]
    [InlineData("(<uint32 4294967295>,)", 0)]
    [InlineData("(<uint32 4294967296>,)", null)]
    [InlineData("(<int32 2>,)", null)]
    [InlineData("(<uint32 2>>,)", null)]
    [InlineData("(<uint32 2>,)garbage", null)]
    public void PortalReplyAcceptsOnlyTheExpectedVariantShape(string text, int? expected) =>
        Assert.Equal(expected, DesktopAppearance.ParseReply(text));

    [Theory]
    [InlineData("org.freedesktop.appearance', 'color-scheme', <uint32 2>", 2)]
    [InlineData("org.freedesktop.appearance', 'color-scheme', <uint32 0>", 0)]
    [InlineData("org.freedesktop.appearance', 'color-scheme', <uint32 15>", 0)]
    [InlineData("org.freedesktop.appearance', 'contrast', <uint32 2>", null)]
    [InlineData("org.example.appearance', 'color-scheme', <uint32 2>", null)]
    [InlineData("org.freedesktop.appearance', 'color-scheme', <'2'>", null)]
    public void OtherPortalSettingsCannotChangeThePalette(string body, int? expected) =>
        Assert.Equal(expected, DesktopAppearance.ParseSignal(
            "/org/freedesktop/portal/desktop: org.freedesktop.portal.Settings.SettingChanged ('" + body + ")"));

    [Fact]
    public async Task NoBusAndAMissingHelperLeaveTheFallbackAvailable()
    {
        string marker = Path.Combine(_root, "started");
        string executable = ExecutableScript.Write(Path.Combine(_root, "fake-gdbus"), $"touch '{marker}'\n");
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", null);
        await using (var appearance = new DesktopAppearance(executable))
        {
            appearance.Start();
            Assert.Equal(0, appearance.Scheme);
        }
        Assert.False(File.Exists(marker));
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/not-a-bus");
        await using var missing = new DesktopAppearance(Path.Combine(_root, "missing"));
        missing.Start();
        Assert.Equal(0, missing.Scheme);
    }

    [Fact]
    public async Task OversizedLinesAndLateReadsCannotReplaceANewerSignalAndShutdownReapsTheHelper()
    {
        string script = ExecutableScript.Write(Path.Combine(_root, "fake-gdbus"), $$"""
            if [ "$1" = monitor ]; then
                echo $$ > '{{_root}}/pid'
                echo 'The name org.freedesktop.portal.Desktop is owned by :1.2'
                while [ ! -e '{{_root}}/read' ]; do sleep 0.01; done
                printf '%05000d\n' 0
                echo "/org/freedesktop/portal/desktop: org.freedesktop.portal.Settings.SettingChanged ('org.freedesktop.appearance', 'color-scheme', <uint32 1>)"
                while [ ! -e '{{_root}}/done' ]; do sleep 0.01; done
                echo "/org/freedesktop/portal/desktop: org.freedesktop.portal.Settings.SettingChanged ('org.freedesktop.appearance', 'contrast', <uint32 2>)"
                exec sleep 30
            else
                touch '{{_root}}/read'
                while [ ! -e '{{_root}}/release' ]; do sleep 0.01; done
                echo '(<uint32 2>,)'
                touch '{{_root}}/done'
            fi
            """);
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/private-test");
        int pid;
        await using (var appearance = new DesktopAppearance(script))
        {
            appearance.Start();
            await Wait(() => appearance.Scheme == 1);
            pid = int.Parse(File.ReadAllText(Path.Combine(_root, "pid")).Trim());
            Task pending = (Task)typeof(DesktopAppearance).GetField("_query",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(appearance)!;
            File.WriteAllText(Path.Combine(_root, "release"), "");
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, appearance.Scheme);
        }
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [PortalAppearanceFact]
    public async Task RealPortalSignalsChangeThePaletteAndOwnerReplacementRefreshesTheInitialValue()
    {
        string address = "unix:path=" + Path.Combine(_root, "bus");
        string config = Path.Combine(_root, "bus.conf");
        File.WriteAllText(config, $"""
            <busconfig><type>session</type><listen>{address}</listen>
              <policy context="default"><allow send_destination="*"/><allow own="*"/><allow eavesdrop="true"/></policy>
            </busconfig>
            """);
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", address);
        using var stop = new CancellationTokenSource();
        Task<OpenXLR.Core.ProcessResult> bus = ProcessRunner.RunAsync("dbus-daemon", ["--nofork", "--config-file=" + config],
            TimeSpan.FromSeconds(30), cancel: stop.Token);
        try
        {
            await Wait(() => File.Exists(Path.Combine(_root, "bus")));
            using var desktop = new DBusConnection(address);
            await desktop.ConnectAsync();
            var portal = new AppearancePortal(desktop, 2);
            desktop.AddMethodHandler(portal);
            await desktop.RequestNameAsync("org.freedesktop.portal.Desktop");
            await using var appearance = new DesktopAppearance();
            appearance.Start();
            await Wait(() => appearance.Scheme == 2);
            Assert.Equal(1, portal.Reads);
            portal.Signal(1);
            await Wait(() => appearance.Scheme == 1);
            portal.Signal(2, key: "contrast");
            await Task.Delay(50);
            Assert.Equal(1, appearance.Scheme);
            Assert.Equal(1, portal.Reads);
            desktop.Dispose();
            await Wait(() => appearance.Scheme == 0);
            using var replacement = new DBusConnection(address);
            await replacement.ConnectAsync();
            var next = new AppearancePortal(replacement, 2);
            replacement.AddMethodHandler(next);
            await replacement.RequestNameAsync("org.freedesktop.portal.Desktop");
            await Wait(() => appearance.Scheme == 2);
            Assert.Equal(1, next.Reads);
        }
        finally { stop.Cancel(); await bus; }
    }

    private static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class AppearancePortal(DBusConnection connection, uint value) : IPathMethodHandler
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public string Path => "/org/freedesktop/portal/desktop";
        public bool HandlesChildPaths => false;
        public bool RunMethodHandlerSynchronously(Message message) => true;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.MemberAsString == "Read")
            {
                Interlocked.Increment(ref _reads);
                using var reply = context.CreateReplyWriter("v");
                reply.WriteSignature("v");
                reply.WriteVariantUInt32(value);
                context.Reply(reply.CreateMessage());
            }
            else context.ReplyUnknownMethodError();
            return ValueTask.CompletedTask;
        }

        public void Signal(uint scheme, string key = "color-scheme")
        {
            using var signal = connection.GetMessageWriter();
            signal.WriteSignalHeader(path: Path, @interface: "org.freedesktop.portal.Settings", member: "SettingChanged", signature: "ssv");
            signal.WriteString("org.freedesktop.appearance");
            signal.WriteString(key);
            signal.WriteVariantUInt32(scheme);
            connection.TrySendMessage(signal.CreateMessage());
        }
    }
}

internal sealed class PortalAppearanceFactAttribute : FactAttribute
{
    public PortalAppearanceFactAttribute()
    {
        if (DesktopBusSkip.Reason is { } reason) Skip = reason;
        else if (Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Any(directory => File.Exists(Path.Combine(directory, "gdbus"))) != true)
            Skip = "gdbus is not installed; the terminal's optional appearance subscription is not available.";
    }
}
