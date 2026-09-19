namespace OpenXLR.Tui;

/// <summary>A key press, already decoded from whatever the terminal sent.</summary>
internal enum Key
{
    None, Char, Up, Down, Left, Right, PageUp, PageDown, Home, End,
    Enter, Escape, Tab, BackTab, Backspace, Delete, F1, F2, F3, F4, F5,
}

/// <summary>One decoded key press: a <see cref="Key"/> and, for <see cref="Key.Char"/>, its character.</summary>
internal readonly record struct KeyPress(Key Key, char Char = '\0', bool Ctrl = false)
{
    public bool Is(char c) => Key == Key.Char && char.ToLowerInvariant(Char) == c;
}

/// <summary>
/// The terminal itself: the alternate screen, the cursor, the size and the
/// keys.
///
/// Keys come through the runtime's own console reader rather than through a
/// hand-rolled escape parser: it already knows the terminal's sequences, it
/// puts the terminal in the mode it needs and puts it back at exit, and a
/// reader of our own would have to be taught every terminfo entry again.
/// </summary>
internal sealed class Terminal : IDisposable
{
    private readonly Stream _output = Console.OpenStandardOutput();
    private bool _started;

    /// <summary>True when stdin and stdout are a terminal we can drive.</summary>
    public static bool IsTerminal => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <summary>The terminal's size, or a usable minimum when it cannot be read.</summary>
    public (int Width, int Height) Size
    {
        get
        {
            try { return (Math.Max(Console.WindowWidth, 20), Math.Max(Console.WindowHeight, 6)); }
            catch (IOException) { return (80, 24); }
            catch (PlatformNotSupportedException) { return (80, 24); }
        }
    }

    /// <summary>The alternate screen, no cursor, and Ctrl+C delivered as a key.</summary>
    public void Start()
    {
        try { Console.TreatControlCAsInput = true; } catch (IOException) { }
        try { Console.CursorVisible = false; } catch (IOException) { }
        Write("\u001b[?1049h\u001b[2J");
        _started = true;
    }

    /// <summary>The screen and the settings the program found.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;
        Write("\u001b[0m\u001b[?1049l");
        try { Console.CursorVisible = true; } catch (IOException) { }
        try { Console.TreatControlCAsInput = false; } catch (IOException) { }
    }

    public void Write(string text)
    {
        if (text.Length == 0) return;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        try { _output.Write(bytes, 0, bytes.Length); _output.Flush(); }
        catch (IOException) { }
    }

    /// <summary>
    /// The next key, or <see cref="Key.None"/> when none is waiting, which is
    /// what gives the drawing loop its tick.
    /// </summary>
    public KeyPress ReadKey()
    {
        try
        {
            if (!Console.KeyAvailable) return new KeyPress(Key.None);
            return Map(Console.ReadKey(intercept: true));
        }
        catch (InvalidOperationException) { return new KeyPress(Key.None); }
        catch (IOException) { return new KeyPress(Key.None); }
    }

    /// <summary>
    /// Turns what the runtime read into the keys this program knows. Kept
    /// separate and static so the mapping can be tested without a terminal.
    /// </summary>
    internal static KeyPress Map(ConsoleKeyInfo info)
    {
        bool ctrl = (info.Modifiers & ConsoleModifiers.Control) != 0;
        bool shift = (info.Modifiers & ConsoleModifiers.Shift) != 0;

        Key key = info.Key switch
        {
            ConsoleKey.UpArrow => Key.Up,
            ConsoleKey.DownArrow => Key.Down,
            ConsoleKey.LeftArrow => Key.Left,
            ConsoleKey.RightArrow => Key.Right,
            ConsoleKey.PageUp => Key.PageUp,
            ConsoleKey.PageDown => Key.PageDown,
            ConsoleKey.Home => Key.Home,
            ConsoleKey.End => Key.End,
            ConsoleKey.Enter => Key.Enter,
            ConsoleKey.Escape => Key.Escape,
            ConsoleKey.Tab => shift ? Key.BackTab : Key.Tab,
            ConsoleKey.Backspace => Key.Backspace,
            ConsoleKey.Delete => Key.Delete,
            ConsoleKey.F1 => Key.F1,
            ConsoleKey.F2 => Key.F2,
            ConsoleKey.F3 => Key.F3,
            ConsoleKey.F4 => Key.F4,
            ConsoleKey.F5 => Key.F5,
            _ => Key.None,
        };
        if (key != Key.None) return new KeyPress(key, '\0', ctrl);

        // Ctrl and a letter arrive as the letter's ordinal, which is not a
        // character anyone typed.
        if (ctrl && info.KeyChar is > (char)0 and < (char)27)
            return new KeyPress(Key.Char, (char)('a' + info.KeyChar - 1), Ctrl: true);
        if (info.KeyChar >= ' ') return new KeyPress(Key.Char, info.KeyChar, ctrl);
        return new KeyPress(Key.None);
    }

    public void Dispose() => Stop();
}
