using System.Text.Json.Nodes;

namespace OpenXLR.UI;

/// <summary>
/// Raw capture: the unit sends the computer an input before its own
/// processing, on XLR 1, XLR 2 and Line In of a unit whose capabilities
/// report <c>rawCapture</c>. The switches sit with each input's unit
/// switches and write <c>rawCapture</c>, <c>rawCapture2</c> and
/// <c>lineInRawCapture</c>; the state reads back what the unit holds.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _capRawCapture;
    /// <summary>The unit takes raw capture on XLR 1.</summary>
    public bool CapRawCapture { get => _capRawCapture; private set => Set(ref _capRawCapture, value); }

    private bool _capRawCapture2;
    /// <summary>The unit takes raw capture on XLR 2.</summary>
    public bool CapRawCapture2 { get => _capRawCapture2; private set => Set(ref _capRawCapture2, value); }

    private bool _capLineInRawCapture;
    /// <summary>The unit takes raw capture on Line In.</summary>
    public bool CapLineInRawCapture { get => _capLineInRawCapture; private set => Set(ref _capLineInRawCapture, value); }

    private bool _rawCapture;
    public bool RawCapture
    {
        get => _rawCapture;
        set { if (Set(ref _rawCapture, value) && !_applying) _ = _client.SetControlAsync("rawCapture", value); }
    }

    private bool _rawCapture2;
    public bool RawCapture2
    {
        get => _rawCapture2;
        set { if (Set(ref _rawCapture2, value) && !_applying) _ = _client.SetControlAsync("rawCapture2", value); }
    }

    private bool _lineInRawCapture;
    public bool LineInRawCapture
    {
        get => _lineInRawCapture;
        set { if (Set(ref _lineInRawCapture, value) && !_applying) _ = _client.SetControlAsync("lineInRawCapture", value); }
    }

    /// <summary>The switches' capabilities and states from a state message; off while no unit with raw capture is driven.</summary>
    private void ApplyRawCapture(JsonNode? capabilities, JsonNode? state)
    {
        bool raw = capabilities?["rawCapture"]?.GetValue<bool>() ?? false;
        CapRawCapture = raw;
        CapRawCapture2 = raw && (capabilities?["xlrInputs"]?.GetValue<int>() ?? 1) > 1;
        CapLineInRawCapture = raw && (capabilities?["lineInput"]?.GetValue<bool>() ?? false);
        RawCapture = state?["rawCapture"]?.GetValue<bool>() ?? false;
        RawCapture2 = state?["rawCapture2"]?.GetValue<bool>() ?? false;
        LineInRawCapture = state?["lineInRawCapture"]?.GetValue<bool>() ?? false;
    }
}
