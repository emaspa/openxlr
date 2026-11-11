using OpenXLR.UI.Localization;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>The command the LED ring sends; the daemon client in the application, a recorder in the tests.</summary>
public interface ILedCommands
{
    /// <summary>Set one LED control, or with <c>reset</c> the factory LEDs; null once the unit has it, else the reason.</summary>
    Task<string?> SetLedAsync(string control, object? value);
}

/// <summary>
/// The LED ring of a unit whose capabilities report <c>ledRing</c> (the Wave
/// XLR MK.2 from firmware 1.0.15): the ring's and the level meter's
/// brightness and the ring's colour in each of the dial's modes. Each change
/// is a <c>setLed</c>; the state's <c>led</c> is what the unit reads back.
/// </summary>
public sealed class LedRingViewModel(ILedCommands client) : ViewModelBase
{
    /// <summary>The colours offered as swatches: white, as the unit ships, and the three colours seen on the desk.</summary>
    internal static IReadOnlyList<(string Hex, string Name)> Swatches { get; } =
    [
        ("#ffffff", Localizer.Text("ColourWhite")),
        ("#ff0000", Localizer.Text("ColourRed")),
        ("#00ff00", Localizer.Text("ColourGreen")),
        ("#0000ff", Localizer.Text("ColourBlue")),
    ];

    private bool _applying;
    private bool _available;
    private int _ringBrightness = 100;
    private int _meterBrightness = 100;
    private string _gainColour = "#ffffff";
    private string _headphoneColour = "#ffffff";
    private string _mixColour = "#ffffff";
    private string? _error;

    /// <summary>The driven unit has LEDs OpenXLR sets.</summary>
    public bool Available { get => _available; private set => Set(ref _available, value); }

    public int RingBrightness
    {
        get => _ringBrightness;
        set
        {
            if (!Set(ref _ringBrightness, value)) return;
            Raise(nameof(RingBrightnessText));
            if (_applying) return;
            int v = value;
            SliderSync.Touch("ledRing");
            SliderSync.Send("ledRing", () => Send("ringBrightness", v));
        }
    }

    public int MeterBrightness
    {
        get => _meterBrightness;
        set
        {
            if (!Set(ref _meterBrightness, value)) return;
            Raise(nameof(MeterBrightnessText));
            if (_applying) return;
            int v = value;
            SliderSync.Touch("ledMeter");
            SliderSync.Send("ledMeter", () => Send("meterBrightness", v));
        }
    }

    public string RingBrightnessText => $"{_ringBrightness}%";
    public string MeterBrightnessText => $"{_meterBrightness}%";

    // A colour field sends once it holds a whole #rrggbb; anything shorter is still being typed.
    public string GainColour { get => _gainColour; set => SetColour(ref _gainColour, value, "gainColour"); }
    public string HeadphoneColour { get => _headphoneColour; set => SetColour(ref _headphoneColour, value, "headphoneColour"); }
    public string MixColour { get => _mixColour; set => SetColour(ref _mixColour, value, "mixColour"); }

    /// <summary>Why the last change did not reach the unit, or null.</summary>
    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;

    private void SetColour(ref string field, string? value, string control)
    {
        string text = (value ?? "").Trim();
        if (!Set(ref field, text) || _applying || !IsColour(text)) return;
        Send(control, text.ToLowerInvariant());
    }

    /// <summary>True for <c>#rrggbb</c>, the colours the daemon takes.</summary>
    internal static bool IsColour(string text)
        => text.Length == 7 && text[0] == '#' && text.Skip(1).All(char.IsAsciiHexDigit);

    private async void Send(string control, object value)
        => Error = await client.SetLedAsync(control, value);

    /// <summary>The unit's factory LEDs: full brightness and white in every mode.</summary>
    public async Task ResetAsync()
    {
        SliderSync.Forget("ledRing");
        SliderSync.Forget("ledMeter");
        Error = await client.SetLedAsync("reset", null);
    }

    /// <summary>Take the daemon's word on whether the unit has LEDs and what it holds.</summary>
    internal void Apply(bool available, JsonNode? led)
    {
        _applying = true;
        try
        {
            Available = available && led is JsonObject;
            if (!Available) return;
            if (!SliderSync.RecentlyTouched("ledRing")) RingBrightness = led!["ringBrightness"]?.GetValue<int>() ?? 100;
            if (!SliderSync.RecentlyTouched("ledMeter")) MeterBrightness = led!["meterBrightness"]?.GetValue<int>() ?? 100;
            // A field being typed in keeps its text until it is a colour.
            if (IsColour(_gainColour) || _gainColour.Length == 0) GainColour = led!["gainColour"]?.GetValue<string>() ?? "#ffffff";
            if (IsColour(_headphoneColour) || _headphoneColour.Length == 0) HeadphoneColour = led!["headphoneColour"]?.GetValue<string>() ?? "#ffffff";
            if (IsColour(_mixColour) || _mixColour.Length == 0) MixColour = led!["mixColour"]?.GetValue<string>() ?? "#ffffff";
        }
        finally { _applying = false; }
    }

    /// <summary>The daemon went away: nothing to set until it says again.</summary>
    internal void Reset()
    {
        Available = false;
        Error = null;
    }
}
