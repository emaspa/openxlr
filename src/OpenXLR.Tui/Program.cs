using System.Runtime.InteropServices;

namespace OpenXLR.Tui;

/// <summary>
/// The terminal mixer. It talks to the same daemon the window does, over the
/// documented WebSocket, and draws with the colours of the skin chosen in
/// Options, so a desk without a desktop session gets the same mixer.
/// </summary>
internal static class Program
{
    private const string Usage = """
        openxlr-tui: the OpenXLR mixer in a terminal

          --skin <id>    use one appearance for this run without saving it
          --list-skins   print the appearances this machine has and exit
          --version      print the version and exit
          --help         this text

        The daemon must be running: systemctl --user start openxlr-daemon
        """;

    public static async Task<int> Main(string[] args)
    {
        string? skin = Environment.GetEnvironmentVariable("OPENXLR_SKIN");

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--help" or "-h":
                    Console.WriteLine(Usage);
                    return 0;
                case "--version" or "-v":
                    Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
                    return 0;
                case "--list-skins":
                    foreach (SkinEntry entry in SkinCatalog.Scan())
                        Console.WriteLine($"{entry.Id,-20} {entry.Name}");
                    return 0;
                case "--skin" when index + 1 < args.Length:
                    skin = args[++index];
                    break;
                default:
                    Console.Error.WriteLine($"openxlr-tui: unknown argument {args[index]}");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        if (!Terminal.IsTerminal)
        {
            Console.Error.WriteLine("openxlr-tui: this needs a terminal, not a pipe");
            return 2;
        }

        await using DaemonLink link = new();
        App app = CreateApp(link, skin);
        DesktopAppearance? appearance = null;

        using Terminal terminal = new();
        (int width, int height) = terminal.Size;
        Screen screen = new(width, height);

        // A resize repaints the whole frame, and a stop signal leaves through
        // the same teardown as the quit key, so the terminal is always put
        // back. SIGWINCH is a Unix signal, so it is asked for only there.
        PosixSignalRegistration? resized = null;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            resized = PosixSignalRegistration.Create(
                PosixSignal.SIGWINCH, context => { context.Cancel = true; screen.Invalidate(); });
        using PosixSignalRegistration interrupted = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM, context => { context.Cancel = true; app.Stop(); });

        link.Start();
        terminal.Start();

        try
        {
            DateTime nextFrame = DateTime.UtcNow;
            while (app.Running)
            {
                KeyPress key = terminal.ReadKey();
                if (key.Key != Key.None)
                {
                    app.Handle(key);
                }
                else
                {
                    // Nothing was waiting, so the loop sleeps instead of
                    // spinning. A frame is 66 ms, and this is short enough
                    // that a key still lands inside the one it was typed in.
                    Thread.Sleep(10);
                }

                (int nowWide, int nowHigh) = terminal.Size;
                if (nowWide != screen.Width || nowHigh != screen.Height)
                {
                    screen.Resize(nowWide, nowHigh);
                }

                // Tick the hold markers and message expiry even between packets.
                // The diff renderer emits nothing for an unchanged frame.
                DateTime now = DateTime.UtcNow;
                if (now < nextFrame) continue;
                nextFrame = now.AddMilliseconds(66);

                // The portal is watched only while Material follows the
                // desktop; a frame reads the value it last reported.
                appearance = await FollowAppearance(app, appearance);
                app.Draw(screen);
                terminal.Write(screen.Render());
            }
        }
        finally
        {
            resized?.Dispose();
            terminal.Stop();
            if (appearance is not null) await appearance.DisposeAsync();
        }

        return 0;
    }

    /// <summary>The app with the saved skin and mode, or the skin given at launch, which holds Material dark.</summary>
    internal static App CreateApp(DaemonLink link, string? skin)
    {
        // An empty variable or argument is no override, as in the window.
        if (string.IsNullOrEmpty(skin)) skin = null;
        Theme theme = SkinCatalog.Load(skin ?? UiSettingsFile.ReadSkin());
        return new App(link, theme, UiSettingsFile.ReadAppearanceMode(), skinOverride: skin is not null);
    }

    /// <summary>Start the portal watch when the app begins following the desktop, stop it when it no longer does.</summary>
    internal static async ValueTask<DesktopAppearance?> FollowAppearance(App app, DesktopAppearance? appearance)
    {
        if (!app.FollowsSystem)
        {
            if (appearance is not null) await appearance.DisposeAsync();
            return null;
        }
        if (appearance is null)
        {
            appearance = new DesktopAppearance();
            appearance.Start();
        }
        app.UseSystemScheme(appearance.Scheme);
        return appearance;
    }
}
