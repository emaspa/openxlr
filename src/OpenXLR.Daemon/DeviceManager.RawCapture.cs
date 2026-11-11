using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenXLR.Core;
using OpenXLR.Core.Devices;

namespace OpenXLR.Daemon;

/// <summary>
/// Raw capture: the unit sends the computer an input before its own
/// processing (<see cref="CaptureTap"/>), so the computer's chain works on
/// the microphone as the preamp gives it. The setting is the daemon's, per
/// unit and per input (<c>rawCapture</c>, <c>rawCapture2</c>,
/// <c>lineInRawCapture</c>), saved in <c>rawcapture.json</c> with the tap the
/// unit held when it went on, and written again at every connect, so a unit
/// another program set back is put right. Turning it off puts back the tap
/// found, or 2, the value the units read as Wave Link leaves them. The
/// daemon leaves the unit as it is when it stops, as it does every other
/// unit setting. An input the setting does not name is left as the unit
/// holds it.
/// </summary>
public sealed partial class DeviceManager
{
    // By device id, the inputs whose capture OpenXLR takes raw, each with the
    // tap the unit held before, which turning raw capture off puts back.
    private readonly Dictionary<string, Dictionary<string, int>> _rawCapture = LoadRawCapture();

    private static string RawCapturePath => OpenXlrPaths.ConfigFile("rawcapture.json");

    // Every device manager writes the one file; each changes only its own unit's entry.
    private static readonly object RawCaptureWriteGate = new();

    internal static string RawCaptureKey(RawCaptureInput input) => input switch
    {
        RawCaptureInput.Xlr1 => "xlr1",
        RawCaptureInput.Xlr2 => "xlr2",
        RawCaptureInput.LineIn => "lineIn",
        _ => throw new ArgumentOutOfRangeException(nameof(input)),
    };

    private static RawCaptureInput? RawCaptureInputOf(string key) => key switch
    {
        "xlr1" => RawCaptureInput.Xlr1,
        "xlr2" => RawCaptureInput.Xlr2,
        "lineIn" => RawCaptureInput.LineIn,
        _ => null,
    };

    /// <summary>The input a <c>set</c> control names, or null for any other control.</summary>
    internal static RawCaptureInput? RawCaptureControl(string control) => control switch
    {
        ControlNames.RawCapture => RawCaptureInput.Xlr1,
        ControlNames.RawCapture2 => RawCaptureInput.Xlr2,
        ControlNames.LineInRawCapture => RawCaptureInput.LineIn,
        _ => null,
    };

    /// <summary>Whether a unit with these capabilities takes raw capture on an input.</summary>
    internal static bool RawCaptureSupported(DeviceCapabilities c, RawCaptureInput input) => c.RawCapture && input switch
    {
        RawCaptureInput.Xlr1 => true,
        RawCaptureInput.Xlr2 => c.XlrInputs > 1,
        RawCaptureInput.LineIn => c.LineInput,
        _ => false,
    };

    private static Dictionary<string, Dictionary<string, int>> LoadRawCapture()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(RawCapturePath)) ?? [];
            // Only known inputs, each with a tap that can be put back.
            return saved.ToDictionary(unit => unit.Key,
                unit => unit.Value.Where(i => RawCaptureInputOf(i.Key) is not null)
                    .ToDictionary(i => i.Key, i => CaptureTap.Restored(i.Value), StringComparer.Ordinal), StringComparer.Ordinal);
        }
        catch (Exception) { return new(StringComparer.Ordinal); }
    }

    private void SaveRawCapture(string id)
    {
        try
        {
            lock (RawCaptureWriteGate)
            {
                var saved = LoadRawCapture();
                if (_rawCapture.TryGetValue(id, out Dictionary<string, int>? inputs) && inputs.Count > 0) saved[id] = new(inputs, StringComparer.Ordinal);
                else saved.Remove(id);
                OpenXlrPaths.WriteAtomic(RawCapturePath, JsonSerializer.Serialize(saved));
            }
        }
        catch (Exception ex) { _log.LogWarning("raw capture: the setting was not saved: {msg}", ex.Message); }
    }

    /// <summary>
    /// The <c>set</c> of a raw capture control: the unit written first, so a
    /// failed write leaves the setting as it was, then the setting saved.
    /// </summary>
    private void SetRawCaptureLocked(IAudioDevice dev, RawCaptureInput input, bool on)
    {
        string id = DevId(dev), key = RawCaptureKey(input);
        _rawCapture.TryGetValue(id, out Dictionary<string, int>? inputs);
        int? found = inputs is not null && inputs.TryGetValue(key, out int saved) ? saved : null;
        if (on)
        {
            int held = dev.SetCaptureTap(input, CaptureTap.Raw);
            // A unit already raw keeps the tap found the first time.
            int restore = held != CaptureTap.Raw ? held : CaptureTap.Restored(found);
            if (inputs is null) _rawCapture[id] = inputs = new(StringComparer.Ordinal);
            inputs[key] = restore;
            _log.LogInformation("{dev}: raw capture on for {input} (the unit held tap {held}; {restore} goes back when it is off)",
                dev.Info.DisplayName, key, held, restore);
        }
        else
        {
            int restore = CaptureTap.Restored(found);
            int held = dev.SetCaptureTap(input, restore);
            if (inputs is not null && inputs.Remove(key) && inputs.Count == 0) _rawCapture.Remove(id);
            _log.LogInformation("{dev}: raw capture off for {input} (tap {held} to {restore})", dev.Info.DisplayName, key, held, restore);
        }
        SaveRawCapture(id);
    }

    /// <summary>
    /// A unit just opened gets raw capture on each input the setting names,
    /// whoever set the tap back meanwhile. A tap found other than raw becomes
    /// the one turning it off puts back. A unit without raw capture keeps the
    /// setting for a later firmware; a failed write is tried again at the
    /// next connect.
    /// </summary>
    private void ConnectRawCaptureLocked(IAudioDevice dev)
    {
        string id = DevId(dev);
        if (!_rawCapture.TryGetValue(id, out Dictionary<string, int>? inputs)) return;
        bool changed = false;
        foreach (string key in inputs.Keys.ToList())
        {
            if (RawCaptureInputOf(key) is not { } input || !RawCaptureSupported(dev.Capabilities, input)) continue;
            try
            {
                int held = dev.SetCaptureTap(input, CaptureTap.Raw);
                if (held == CaptureTap.Raw) continue;
                inputs[key] = held;
                changed = true;
                _log.LogInformation("{dev}: raw capture put back on {input}; the unit held tap {held}", dev.Info.DisplayName, key, held);
            }
            catch (UsbHungException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning("{dev}: raw capture on {input} was not written: {msg}", dev.Info.DisplayName, key, ex.Message);
            }
        }
        if (changed) SaveRawCapture(id);
    }

    /// <summary>The microphone inputs whose capture the unit sends raw as the last poll read it, bit 0 XLR 1 and bit 1 XLR 2.</summary>
    private int RawCaptureMicsLocked()
        => (_last?.RawCapture == true ? 1 : 0) | (_last?.RawCapture2 == true ? 2 : 0);

    /// <summary>Whether the driven unit sends XLR 1 to the computer before its processing, so its low cut is not in front of it.</summary>
    public bool Xlr1RawCapture
    {
        get { lock (_gate) return _device is { Connected: true } && _last?.RawCapture == true; }
    }
}
