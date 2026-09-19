namespace OpenXLR.Tui;

/// <summary>A box on the screen, in cells.</summary>
internal readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public Rect Inset(int by) => new(X + by, Y + by, Math.Max(0, Width - 2 * by), Math.Max(0, Height - 2 * by));
}

/// <summary>
/// The drawings the terminal mixer shares: meters, faders and keys. They take
/// their colours from the skin, so an appearance chosen in the window paints
/// the terminal the same way.
/// </summary>
internal static class Widgets
{
    private const string VerticalEighths = " ▁▂▃▄▅▆▇█";

    /// <summary>
    /// A level meter. Each cell is coloured by where it sits on the scale
    /// rather than by how loud the signal is, which is the rule the window's
    /// meter follows, so the quiet end stays calm however hot the peak gets.
    ///
    /// A bar takes half its row: the lower half, or the upper half when it
    /// is the right side of a pair drawn below the row its name is on, so the
    /// two bars of a stereo pair hug that row from either side. The half
    /// block is the glyph terminals draw as one clean rectangle; the finer
    /// fractions come from the font in some of them and show a seam along the
    /// bottom, which reads as a second, thinner bar.
    /// </summary>
    public static void Meter(Screen screen, int x, int y, int width, double level, Theme theme, Rgb back,
        bool upper = false)
    {
        if (width <= 0) return;
        double clamped = Math.Clamp(level, 0, 1);
        double filled = clamped * width;
        (char full, char half, char track) = upper ? ('▀', '▘', '▔') : ('▄', '▖', '▁');
        for (int cell = 0; cell < width; cell++)
        {
            double position = (cell + 1.0) / width;
            Rgb colour = theme.MeterColour(position);
            double within = filled - cell;
            // Two steps a cell, which a bar this short does not miss.
            char ch = within >= 1 ? full : within >= 0.5 ? half : track;
            screen.Set(x + cell, y, ch, within >= 0.5 ? colour : theme.MeterTrack, back);
        }
    }

    /// <summary>A bottom-up RMS meter: one solid bar in eighths, with the hold mark above it.</summary>
    public static void VerticalMeter(Screen screen, int x, int y, int height, double level,
        double hold, Theme theme, Rgb back, int width = 1)
    {
        if (height <= 0) return;
        double filled = Math.Clamp(level, 0, 1) * height;
        int heldRow = hold > 0 ? Math.Clamp((int)Math.Ceiling(hold * height) - 1, 0, height - 1) : -1;
        for (int row = 0; row < height; row++)
        {
            double within = Math.Clamp(filled - row, 0, 1);
            char ch = within == 0 ? '│' : VerticalEighths[Math.Clamp((int)Math.Ceiling(within * 8), 1, 8)];
            bool marker = heldRow == row && hold > level;
            if (marker) ch = '━';
            Rgb fore = within > 0 || marker ? theme.MeterColour((row + 1.0) / height) : theme.Groove;
            for (int column = 0; column < width; column++)
                screen.Set(x + column, y + height - row - 1, ch, fore, back);
        }
    }

    /// <summary>A three-cell cap on a vertical track with percentage ticks beside it.</summary>
    public static void VerticalFader(Screen screen, int x, int y, int height, double value,
        double max, Theme theme, Rgb back, bool focused)
    {
        if (height <= 0) return;
        double fraction = max <= 0 ? 0 : Math.Clamp(value / max, 0, 1);
        int cap = (int)Math.Round((1 - fraction) * (height - 1));
        for (int row = 0; row < height; row++)
        {
            screen.Set(x + 1, y + row, row >= cap ? '┃' : '│',
                row >= cap ? (focused ? theme.FaderFill : theme.QuietFill) : theme.Groove, back);
            if (row == 0 || row == height - 1 || row == (height - 1) / 2)
                screen.Set(x - 1, y + row, '─', theme.TextMuted, back);
        }
        Rgb face = focused ? theme.FocusedCap : theme.FaderThumb;
        screen.Text(x, y + cap, theme.ConsoleFaders ? "╞═╡" : "━━━", theme.On(face), face, bold: focused);
    }

    public static void Center(Screen screen, int x, int y, int width, string text, Rgb fore, Rgb back, bool bold = false)
    {
        int used = Math.Min(width, text.Length);
        screen.Text(x + Math.Max(0, (width - used) / 2), y, text, fore, back, bold, Math.Max(0, width));
    }

    /// <summary>A history column uses the maximum in its time span, so a short burst survives compression.</summary>
    public static void History(Screen screen, Rect area, IReadOnlyList<double> history, Theme theme, Rgb back)
    {
        if (area.Width <= 0 || area.Height <= 0 || history.Count == 0) return;
        for (int x = 0; x < area.Width; x++)
        {
            int start = x * history.Count / area.Width;
            int end = Math.Max(start + 1, (x + 1) * history.Count / area.Width);
            double level = 0;
            for (int i = start; i < end && i < history.Count; i++) level = Math.Max(level, history[i]);
            for (int row = 0; row < area.Height; row++)
            {
                double within = Math.Clamp(level * area.Height - row, 0, 1);
                char ch = within > 0 ? VerticalEighths[Math.Clamp((int)Math.Ceiling(within * 8), 1, 8)]
                    : row == 0 ? '─' : ' ';
                screen.Set(area.X + x, area.Bottom - 1 - row, ch,
                    within > 0 ? theme.MeterColour((row + 1.0) / area.Height) : theme.Groove, back);
            }
        }
    }

    /// <summary>Three-row numerals keep a held RMS reading legible across the desk.</summary>
    public static void BigNumber(Screen screen, int x, int y, string text, Theme theme, Rgb back)
    {
        string[] digits =
        [
            "┏━┓┃ ┃┗━┛", " ╻  ┃  ╹ ", "╺━┓┏━┛┗━╸", "╺━┓ ━┫╺━┛", "╻ ╻┗━┫  ╹",
            "┏━╸┗━┓╺━┛", "┏━╸┣━┓┗━┛", "╺━┓  ┃  ╹", "┏━┓┣━┫┗━┛", "┏━┓┗━┫╺━┛",
        ];
        foreach (char ch in text)
        {
            string glyph = ch is >= '0' and <= '9' ? digits[ch - '0'] : "   ━━━   ";
            for (int row = 0; row < 3; row++)
                screen.Text(x, y + row, glyph.Substring(row * 3, 3), theme.TextPrimary, back, bold: true);
            x += 4;
        }
    }

    public static string Rms(double level) => level <= 0 ? "<-60" : $"{level * 60 - 60:0}";

    /// <summary>A thirteen by five cell gain arc, using the terminal's braille dot grid.</summary>
    public static void Dial(Screen screen, int x, int y, double fraction, string label, Theme theme, Rgb back, bool focused)
    {
        int[,] dots = new int[5, 13];
        bool[,] lit = new bool[5, 13];
        int[,] bits = { { 1, 8 }, { 2, 16 }, { 4, 32 }, { 64, 128 } };
        for (int i = 0; i <= 40; i++)
        {
            double angle = (135 + i * 270.0 / 40) * Math.PI / 180;
            int px = (int)Math.Round(12.5 + 12 * Math.Cos(angle));
            int py = (int)Math.Round(9.5 + 9 * Math.Sin(angle));
            dots[py / 4, px / 2] |= bits[py % 4, px % 2];
            lit[py / 4, px / 2] |= i / 40.0 <= fraction;
        }
        for (int row = 0; row < 5; row++)
            for (int column = 0; column < 13; column++)
                if (dots[row, column] != 0)
                    screen.Set(x + column, y + row, (char)(0x2800 + dots[row, column]),
                        lit[row, column] ? focused ? theme.Accent : theme.TextDetail : theme.Groove, back);
        Center(screen, x + 2, y + 2, 9, label, theme.TextPrimary, back, bold: true);
    }

    /// <summary>A fader: an inset groove, the filled part, and a cap at the value.</summary>
    public static void Fader(Screen screen, int x, int y, int width, double value, double max, Theme theme, Rgb back, bool focused)
    {
        if (width <= 0) return;
        double fraction = max <= 0 ? 0 : Math.Clamp(value / max, 0, 1);
        int cap = (int)Math.Round(fraction * (width - 1));
        for (int cell = 0; cell < width; cell++)
        {
            if (cell == cap)
                screen.Set(x + cell, y, '┃', focused ? theme.Accent : theme.FaderThumb, back, focused);
            else
                screen.Set(x + cell, y, cell < cap ? '━' : '─', cell < cap ? theme.FaderFill : theme.FaderTrack, back);
        }
    }

    /// <summary>A key: a label on a face, lit when it is on and lettered by its state.</summary>
    public static void Key(Screen screen, int x, int y, string label, bool on, Theme theme, bool focused, int width = 0)
    {
        string text = theme.CapKeys ? $"[{label}]" : $" {label} ";
        if (width > 0 && text.Length < width) text = text.PadRight(width);
        Rgb back = on ? theme.ControlBackChecked : theme.ControlBack;
        Rgb fore = on ? theme.ControlForeChecked : theme.ControlFore;
        if (focused)
        {
            back = back.Mix(theme.Accent, 0.35);
            fore = theme.TextPrimary;
        }
        screen.Text(x, y, text, fore, back, bold: on || focused);
    }

    /// <summary>The mute key, which is green while audio passes and red when it does not.</summary>
    public static void MuteKey(Screen screen, int x, int y, string label, bool muted, Theme theme, bool focused)
    {
        Rgb back = muted ? theme.MuteBackChecked : theme.MuteBack;
        Rgb fore = muted ? theme.MuteForeChecked : theme.MuteFore;
        if (focused) back = back.Mix(theme.Accent, 0.3);
        screen.Text(x, y, theme.CapMutes ? $"[{label}]" : $" {label} ", fore, back, bold: true);
    }

    /// <summary>
    /// A lamp: on, off, or alert. Both faces sit on the middle of the line,
    /// level with the lettering beside them, which a half block would not.
    /// </summary>
    public static void Lamp(Screen screen, int x, int y, bool on, Theme theme, Rgb back, bool alert = false)
        => screen.Set(x, y, theme.LampLeds ? '■' : '●', alert ? theme.LedAlert : on ? theme.LedOn : theme.LedOff, back);

    /// <summary>A percentage the way the window writes it.</summary>
    public static string Percent(double value) => $"{Math.Round(value * 100)}%";

    /// <summary>A percentage already on the 0 to 100 scale.</summary>
    public static string Percent0(double value) => $"{Math.Round(value)}%";

    /// <summary>A decibel reading with its sign, as the hardware pages show it.</summary>
    public static string Decibels(double value) => $"{value:0.#} dB";
}
