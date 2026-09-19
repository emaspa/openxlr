using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// The drawing: the cell grid, what it writes to the terminal, and the keys
/// that come back. The screen is a buffer, so a test draws a frame and reads
/// it back without a terminal anywhere.
/// </summary>
public sealed class TuiScreenTests
{
    private static Screen Grid(int width = 40, int height = 10) => new(width, height) { TrueColor = true };

    private static string Line(Screen screen, int row)
    {
        char[] text = new char[screen.Width];
        for (int column = 0; column < screen.Width; column++) text[column] = screen.At(column, row).Ch;
        return new string(text).TrimEnd();
    }

    [Fact]
    public void TextLandsWhereItIsPutAndStopsAtItsWidth()
    {
        Screen screen = Grid();
        screen.Clear(Rgb.Parse("#000000"));
        screen.Text(2, 1, "Monitor A", Rgb.Parse("#ffffff"), Rgb.Parse("#000000"));
        screen.Text(2, 2, "a name that is far too long", Rgb.Parse("#ffffff"), Rgb.Parse("#000000"), maxWidth: 6);

        Assert.Equal("  Monitor A", Line(screen, 1));
        Assert.Equal("  a name", Line(screen, 2));
    }

    [Fact]
    public void TextNeverRunsOffTheEdge()
    {
        Screen screen = Grid(10, 3);
        screen.Clear(Rgb.Parse("#000000"));
        screen.Text(8, 0, "abcdefgh", Rgb.Parse("#ffffff"), Rgb.Parse("#000000"));
        screen.Text(-4, 1, "xy", Rgb.Parse("#ffffff"), Rgb.Parse("#000000"));

        Assert.Equal("        ab", Line(screen, 0));
        Assert.Equal(10, screen.Width);
    }

    [Fact]
    public void APanelDrawsItsOwnFrameAndItsTitle()
    {
        Screen screen = Grid();
        screen.Clear(Rgb.Parse("#000000"));
        screen.Panel(0, 0, 20, 4, Rgb.Parse("#555555"), Rgb.Parse("#111111"), "Mixes");

        Assert.Equal('╭', screen.At(0, 0).Ch);
        Assert.Equal('╮', screen.At(19, 0).Ch);
        Assert.Equal('╰', screen.At(0, 3).Ch);
        Assert.Equal('╯', screen.At(19, 3).Ch);
        Assert.Contains("Mixes", Line(screen, 0), StringComparison.Ordinal);
        Assert.Equal(Rgb.Parse("#111111"), screen.At(5, 2).Back);
    }

    [Fact]
    public void TheFirstFrameIsWrittenInFullAndTheNextOneOnlyWhereItChanged()
    {
        Screen screen = Grid(20, 3);
        screen.Clear(Rgb.Parse("#000000"));
        screen.Text(0, 0, "one", Rgb.Parse("#ffffff"), Rgb.Parse("#000000"));
        string first = screen.Render();
        Assert.Contains("\u001b[38;2;255;255;255m", first, StringComparison.Ordinal);
        Assert.True(first.Length > 60, $"the first frame was only {first.Length} characters");

        Assert.Equal(string.Empty, screen.Render());

        screen.Text(0, 0, "two", Rgb.Parse("#ffffff"), Rgb.Parse("#000000"));
        string third = screen.Render();
        Assert.Contains("two", third, StringComparison.Ordinal);
        Assert.DoesNotContain("one", third, StringComparison.Ordinal);
        Assert.True(third.Length < first.Length / 2, "a small change wrote a whole frame");
    }

    [Fact]
    public void ARepaintAsksForTheWholeFrameAgain()
    {
        Screen screen = Grid(10, 2);
        screen.Clear(Rgb.Parse("#000000"));
        screen.Render();
        Assert.Equal(string.Empty, screen.Render());

        screen.Invalidate();
        Assert.NotEqual(string.Empty, screen.Render());
    }

    [Fact]
    public void ATerminalWithoutTrueColourGetsTheNearestIndex()
    {
        Screen screen = new(10, 2) { TrueColor = false };
        screen.Clear(Rgb.Parse("#000000"));
        screen.Text(0, 0, "x", Rgb.Parse("#e6e9f0"), Rgb.Parse("#16181d"));
        string frame = screen.Render();

        Assert.Contains("\u001b[38;5;", frame, StringComparison.Ordinal);
        Assert.DoesNotContain(";2;", frame, StringComparison.Ordinal);
        // A near grey takes the grey ramp, which is finer than the colour cube.
        Assert.InRange(Screen.Xterm256(Rgb.Parse("#808080")), 232, 255);
        Assert.Equal(16, Screen.Xterm256(Rgb.Parse("#000000")));
    }

    [Fact]
    public void AMeterColoursEachCellByWhereItSitsOnTheScale()
    {
        Screen screen = Grid(20, 2);
        Theme theme = Theme.FromJson("""
            {"schema":1,"name":"T","tokens":{
              "Ox.Meter.Fill":"#00ff00","Ox.Meter.Warning":"#ffff00","Ox.Meter.Hot":"#ff0000",
              "Ox.Meter.WarningLevel":0.7,"Ox.Meter.HotLevel":0.9}}
            """, "t", "T");
        screen.Clear(Rgb.Parse("#000000"));

        Widgets.Meter(screen, 0, 0, 10, 1.0, theme, Rgb.Parse("#000000"));

        // Full scale: the quiet end is still green and only the top is red.
        Assert.Equal(Rgb.Parse("#00ff00"), screen.At(0, 0).Fore);
        Assert.Equal(Rgb.Parse("#ffff00"), screen.At(7, 0).Fore);
        Assert.Equal(Rgb.Parse("#ff0000"), screen.At(9, 0).Fore);
    }

    [Fact]
    public void AMeterThatReadsNothingDrawsItsGrooveAndNoLevel()
    {
        Screen screen = Grid(20, 2);
        screen.Clear(Rgb.Parse("#000000"));
        Widgets.Meter(screen, 0, 0, 8, 0, Theme.Material, Rgb.Parse("#000000"));

        for (int cell = 0; cell < 8; cell++)
        {
            Assert.Equal('─', screen.At(cell, 0).Ch);
            Assert.Equal(Theme.Material.MeterTrack, screen.At(cell, 0).Fore);
        }
    }

    [Fact]
    public void AFaderPutsItsCapWhereTheValueIs()
    {
        Screen screen = Grid(20, 2);
        screen.Clear(Rgb.Parse("#000000"));

        Widgets.Fader(screen, 0, 0, 11, 0.5, 1, Theme.Material, Rgb.Parse("#000000"), focused: false);
        Assert.Equal('┃', screen.At(5, 0).Ch);
        Assert.Equal('━', screen.At(0, 0).Ch);
        Assert.Equal('─', screen.At(10, 0).Ch);

        Widgets.Fader(screen, 0, 1, 11, 1, 1, Theme.Material, Rgb.Parse("#000000"), focused: false);
        Assert.Equal('┃', screen.At(10, 1).Ch);
    }

    [Fact]
    public void AMuteKeyIsLetteredByWhetherAudioPasses()
    {
        Screen screen = Grid(20, 2);
        screen.Clear(Rgb.Parse("#000000"));

        Widgets.MuteKey(screen, 0, 0, "ON", muted: false, Theme.Material, focused: false);
        Widgets.MuteKey(screen, 0, 1, "M", muted: true, Theme.Material, focused: false);

        Assert.Equal(Theme.Material.MuteBack, screen.At(1, 0).Back);
        Assert.Equal(Theme.Material.MuteBackChecked, screen.At(1, 1).Back);
    }

    // --- keys ---

    [Fact]
    public void TheKeysTheViewsWaitForArriveAsThemselves()
    {
        // The key type is the application's own, so the table lives here
        // rather than in InlineData, which a public test method cannot take.
        (ConsoleKey Pressed, Key Expected)[] table =
        [
            (ConsoleKey.UpArrow, Key.Up),
            (ConsoleKey.DownArrow, Key.Down),
            (ConsoleKey.LeftArrow, Key.Left),
            (ConsoleKey.RightArrow, Key.Right),
            (ConsoleKey.PageUp, Key.PageUp),
            (ConsoleKey.PageDown, Key.PageDown),
            (ConsoleKey.Home, Key.Home),
            (ConsoleKey.End, Key.End),
            (ConsoleKey.Enter, Key.Enter),
            (ConsoleKey.Escape, Key.Escape),
            (ConsoleKey.Backspace, Key.Backspace),
            (ConsoleKey.F1, Key.F1),
        ];

        foreach ((ConsoleKey pressed, Key expected) in table)
            Assert.Equal(expected, Terminal.Map(new ConsoleKeyInfo('\0', pressed, false, false, false)).Key);
    }

    [Fact]
    public void ShiftAndTabIsTheOtherWayRoundThroughTheTabs()
    {
        Assert.Equal(Key.Tab, Terminal.Map(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false)).Key);
        Assert.Equal(Key.BackTab, Terminal.Map(new ConsoleKeyInfo('\t', ConsoleKey.Tab, true, false, false)).Key);
    }

    [Fact]
    public void ALetterIsItselfAndControlAndALetterIsMarkedAsSuch()
    {
        KeyPress letter = Terminal.Map(new ConsoleKeyInfo('m', ConsoleKey.M, false, false, false));
        Assert.True(letter.Is('m'));
        Assert.False(letter.Ctrl);

        KeyPress control = Terminal.Map(new ConsoleKeyInfo((char)3, ConsoleKey.C, false, false, true));
        Assert.True(control.Ctrl);
        Assert.True(control.Is('c'));
    }

    [Fact]
    public void ASpaceIsAKeyBecauseItIsWhatTogglesAMute()
    {
        KeyPress key = Terminal.Map(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false));
        Assert.Equal(Key.Char, key.Key);
        Assert.Equal(' ', key.Char);
    }
}
