namespace OpenXLR.Core.Devices;

/// <summary>The kernel's ALSA side of a USB unit: finding its card and running amixer on it.</summary>
internal static class AlsaCards
{
    internal static ProcessResult RunAmixer(IReadOnlyList<string> args)
        => ProcessRunner.Run("amixer", args, TimeSpan.FromSeconds(2), stdoutCap: 1024 * 1024, stderrCap: 64 * 1024);

    /// <summary>
    /// The ALSA card of one unit. Units of a model share a USB id, so with two
    /// attached the first card with that id can be the other unit, and a
    /// control would land on it while the USB controls reach this one. A
    /// card's usbbus file names the bus and device number it sits on, the
    /// pair the USB handle is opened at. A lone card of the model is used even
    /// when that file does not match, as before more than one unit was driven.
    /// </summary>
    internal static int Find(string root, UsbLocation? location, string usbId, string model)
    {
        string? want = location is null ? null : $"{location.Bus:D3}/{location.Address:D3}";
        var cards = new List<(int Card, string? Bus)>();
        foreach (string dir in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(dir);
            if (!name.StartsWith("card", StringComparison.Ordinal)
                || !int.TryParse(name.AsSpan(4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n))
                continue;
            try
            {
                string usbid = Path.Combine(dir, "usbid");
                if (!File.Exists(usbid) || File.ReadAllText(usbid).Trim() != usbId) continue;
                string usbbus = Path.Combine(dir, "usbbus");
                cards.Add((n, File.Exists(usbbus) ? File.ReadAllText(usbbus).Trim() : null));
            }
            catch (IOException) { /* card went away mid-scan */ }
            catch (UnauthorizedAccessException) { }
        }
        if (want is not null && cards.Where(d => d.Bus == want).Select(d => d.Card).ToList() is [int exact]) return exact;
        if (cards.Count == 1) return cards[0].Card;
        throw new InvalidOperationException(cards.Count == 0
            ? $"{model} present on USB but its ALSA card was not found"
            : $"Several {model}s are attached and this unit's ALSA card could not be told apart from the others");
    }
}
