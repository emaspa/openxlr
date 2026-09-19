using System.Text;

namespace OpenXLR.Tui;

/// <summary>A colour, as the skin gives it: eight bits a channel, no alpha.</summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Parse(string hex)
    {
        Rgb? parsed = TryParse(hex);
        return parsed ?? new Rgb(0, 0, 0);
    }

    /// <summary>A colour from <c>#rgb</c>, <c>#rrggbb</c> or <c>#aarrggbb</c>, or null when it is not one.</summary>
    public static Rgb? TryParse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        string text = hex.Trim();
        if (text.Length == 0 || text[0] != '#') return Named(text);
        string digits = text[1..];
        if (digits.Length == 3)
            digits = string.Concat(digits[0], digits[0], digits[1], digits[1], digits[2], digits[2]);
        // An alpha channel is dropped rather than refused: a terminal cell has
        // no transparency, and the colour underneath is the one we drew.
        if (digits.Length == 8) digits = digits[2..];
        if (digits.Length != 6) return null;
        if (!byte.TryParse(digits[..2], System.Globalization.NumberStyles.HexNumber, null, out byte r) ||
            !byte.TryParse(digits[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g) ||
            !byte.TryParse(digits[4..], System.Globalization.NumberStyles.HexNumber, null, out byte b))
            return null;
        return new Rgb(r, g, b);
    }

    private static Rgb? Named(string name) => name.ToLowerInvariant() switch
    {
        "black" => new Rgb(0, 0, 0),
        "white" => new Rgb(255, 255, 255),
        "red" => new Rgb(255, 0, 0),
        "green" => new Rgb(0, 128, 0),
        "blue" => new Rgb(0, 0, 255),
        "gray" or "grey" => new Rgb(128, 128, 128),
        "silver" => new Rgb(192, 192, 192),
        "transparent" => null,
        _ => null,
    };

    /// <summary>This colour mixed towards another one, 0 being this and 1 the other.</summary>
    public Rgb Mix(Rgb other, double amount)
    {
        double k = Math.Clamp(amount, 0, 1);
        return new Rgb(
            (byte)Math.Round(R + (other.R - R) * k),
            (byte)Math.Round(G + (other.G - G) * k),
            (byte)Math.Round(B + (other.B - B) * k));
    }

    /// <summary>Relative luminance, used to decide whether a ground is light or dark.</summary>
    public double Luminance()
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
    }
}

/// <summary>One character cell.</summary>
internal record struct Cell(char Ch, Rgb Fore, Rgb Back, bool Bold);

/// <summary>
/// The character grid and what it takes to get it onto the terminal: drawing
/// into a buffer, then writing only the cells that changed since the last
/// frame. Nothing here touches a real terminal, so a test can draw a frame and
/// read the grid back.
/// </summary>
internal sealed class Screen
{
    private Cell[,] _cells;
    private Cell[,] _shown;
    private bool _everything = true;

    public Screen(int width, int height)
    {
        Width = Math.Max(width, 1);
        Height = Math.Max(height, 1);
        _cells = new Cell[Height, Width];
        _shown = new Cell[Height, Width];
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>True colour when the terminal says so, 256 colours otherwise.</summary>
    public bool TrueColor { get; init; } = DetectTrueColor();

    private static bool DetectTrueColor()
    {
        string? colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
        if (colorTerm is not null &&
            (colorTerm.Contains("truecolor", StringComparison.OrdinalIgnoreCase) ||
             colorTerm.Contains("24bit", StringComparison.OrdinalIgnoreCase)))
            return true;
        string? term = Environment.GetEnvironmentVariable("TERM");
        return term is not null && term.Contains("direct", StringComparison.OrdinalIgnoreCase);
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        _cells = new Cell[Height, Width];
        _shown = new Cell[Height, Width];
        _everything = true;
    }

    public void Clear(Rgb back)
    {
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                _cells[y, x] = new Cell(' ', back, back, false);
    }

    public Cell At(int x, int y) => _cells[y, x];

    public void Set(int x, int y, char ch, Rgb fore, Rgb back, bool bold = false)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        _cells[y, x] = new Cell(ch, fore, back, bold);
    }

    public void Fill(int x, int y, int width, int height, Rgb back)
    {
        for (int row = y; row < y + height; row++)
            for (int column = x; column < x + width; column++)
                Set(column, row, ' ', back, back);
    }

    /// <summary>Writes text, cut to <paramref name="maxWidth"/>, and answers the columns used.</summary>
    public int Text(int x, int y, string text, Rgb fore, Rgb back, bool bold = false, int maxWidth = int.MaxValue)
    {
        int written = 0;
        foreach (char ch in text)
        {
            if (written >= maxWidth || x + written >= Width) break;
            Set(x + written, y, ch, fore, back, bold);
            written++;
        }
        return written;
    }

    /// <summary>Text held to a width, padded with spaces so it paints its whole box.</summary>
    public void TextPad(int x, int y, string text, int width, Rgb fore, Rgb back, bool bold = false)
    {
        int used = Text(x, y, text, fore, back, bold, width);
        for (int column = used; column < width; column++) Set(x + column, y, ' ', fore, back);
    }

    /// <summary>A rounded panel with an optional title in its top rule.</summary>
    public void Panel(int x, int y, int width, int height, Rgb border, Rgb back, string? title = null, Rgb? titleFore = null)
    {
        if (width < 2 || height < 2) return;
        Fill(x, y, width, height, back);
        Set(x, y, '╭', border, back);
        Set(x + width - 1, y, '╮', border, back);
        Set(x, y + height - 1, '╰', border, back);
        Set(x + width - 1, y + height - 1, '╯', border, back);
        for (int column = x + 1; column < x + width - 1; column++)
        {
            Set(column, y, '─', border, back);
            Set(column, y + height - 1, '─', border, back);
        }
        for (int row = y + 1; row < y + height - 1; row++)
        {
            Set(x, row, '│', border, back);
            Set(x + width - 1, row, '│', border, back);
        }
        if (!string.IsNullOrEmpty(title) && width > 6)
            Text(x + 2, y, $" {title} ", titleFore ?? border, back, bold: true, width - 4);
    }

    /// <summary>The frame as an escape sequence, holding only what changed since the last one.</summary>
    public string Render()
    {
        StringBuilder output = new();
        Rgb? fore = null, back = null;
        bool bold = false;
        int cursorRow = -1, cursorColumn = -1;

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                Cell cell = _cells[y, x];
                if (!_everything && _shown[y, x] == cell) continue;
                _shown[y, x] = cell;

                if (cursorRow != y || cursorColumn != x)
                {
                    output.Append("\u001b[").Append(y + 1).Append(';').Append(x + 1).Append('H');
                    cursorRow = y;
                    cursorColumn = x;
                }
                if (bold != cell.Bold)
                {
                    // Turning bold off resets the colours with it, so both are
                    // written again below.
                    output.Append(cell.Bold ? "\u001b[1m" : "\u001b[22m");
                    bold = cell.Bold;
                }
                if (fore != cell.Fore) { output.Append(Colour(cell.Fore, foreground: true)); fore = cell.Fore; }
                if (back != cell.Back) { output.Append(Colour(cell.Back, foreground: false)); back = cell.Back; }
                output.Append(cell.Ch);
                cursorColumn++;
            }
        }

        _everything = false;
        return output.ToString();
    }

    /// <summary>The next frame is written in full, after a resize or a repaint.</summary>
    public void Invalidate() => _everything = true;

    private string Colour(Rgb colour, bool foreground)
    {
        int lead = foreground ? 38 : 48;
        if (TrueColor) return $"\u001b[{lead};2;{colour.R};{colour.G};{colour.B}m";
        return $"\u001b[{lead};5;{Xterm256(colour)}m";
    }

    /// <summary>The nearest xterm-256 index, for a terminal without true colour.</summary>
    internal static int Xterm256(Rgb colour)
    {
        static int Level(byte value) => value < 48 ? 0 : value < 114 ? 1 : (value - 35) / 40;
        int r = Level(colour.R), g = Level(colour.G), b = Level(colour.B);
        int cube = 16 + 36 * r + 6 * g + b;

        // The grey ramp is finer than the cube for a colour that is nearly
        // grey, which most of a mixer's surfaces are.
        int average = (colour.R + colour.G + colour.B) / 3;
        int spread = Math.Max(colour.R, Math.Max(colour.G, colour.B)) - Math.Min(colour.R, Math.Min(colour.G, colour.B));
        if (spread < 16)
        {
            if (average < 8) return 16;
            if (average > 238) return 231;
            return 232 + (average - 8) / 10;
        }
        return cube;
    }
}
