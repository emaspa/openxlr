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
    private const string Eighths = " ▏▎▍▌▋▊▉█";

    /// <summary>
    /// A level meter. Each cell is coloured by where it sits on the scale
    /// rather than by how loud the signal is, which is the rule the window's
    /// meter follows, so the quiet end stays calm however hot the peak gets.
    /// </summary>
    public static void Meter(Screen screen, int x, int y, int width, double level, Theme theme, Rgb back)
    {
        if (width <= 0) return;
        double clamped = Math.Clamp(level, 0, 1);
        double filled = clamped * width;
        for (int cell = 0; cell < width; cell++)
        {
            double position = (cell + 1.0) / width;
            Rgb colour = theme.MeterColour(position);
            double within = filled - cell;
            char ch = within >= 1 ? '█' : within <= 0 ? '─' : Eighths[(int)Math.Clamp(within * 8, 1, 8)];
            screen.Set(x + cell, y, ch, within > 0 ? colour : theme.MeterTrack, back);
        }
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
        string text = $" {label} ";
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
        screen.Text(x, y, $" {label} ", fore, back, bold: true);
    }

    /// <summary>A lamp: on, off, or alert.</summary>
    public static void Lamp(Screen screen, int x, int y, bool on, Theme theme, Rgb back, bool alert = false)
        => screen.Set(x, y, '●', alert ? theme.LedAlert : on ? theme.LedOn : theme.LedOff, back);

    /// <summary>A percentage the way the window writes it.</summary>
    public static string Percent(double value) => $"{Math.Round(value * 100)}%";

    /// <summary>A percentage already on the 0 to 100 scale.</summary>
    public static string Percent0(double value) => $"{Math.Round(value)}%";

    /// <summary>A decibel reading with its sign, as the hardware pages show it.</summary>
    public static string Decibels(double value) => $"{value:0.#} dB";
}
