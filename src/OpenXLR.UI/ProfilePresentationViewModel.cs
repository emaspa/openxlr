using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenXLR.UI;

public sealed partial class MainViewModel
{
    public event Action? PresentationRecalled;
    private string? _presentationAttempt;
    private string? _presentationError;

    private void ApplyProfilePresentation(JsonNode? value)
    {
        if (value is null) { _presentationError = null; return; }
        string? revision = value["revision"]?.GetValue<string>();
        if (revision is not { Length: 32 }) return;
        if (revision != _presentationAttempt)
        {
            _presentationAttempt = revision;
            _presentationError = null;
            try
            {
                UiSettings current = UiSettings.Load();
                // Persist the receipt together with the choices. A reconnect or
                // window restart must not overwrite edits made after this recall.
                if (current.AppliedPresentation == revision) return;
                var settings = value["settings"]?.Deserialize<WindowPresentation>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new JsonException("Missing profile presentation.");
                settings.Validate();
                current.WithPresentation(settings, revision).SaveChecked();
                _compactMixer = settings.CompactMixer;
                _compactChannelId = settings.CompactChannel;
                Raise(nameof(CompactMixer));
                RefreshChannelPresentation();
                PresentationRecalled?.Invoke();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _presentationError = $"Profile presentation could not be restored: {ex.Message}";
            }
        }
        if (_presentationError is not null) Status = _presentationError;
    }

    internal void ReportPresentationError(string message) => Status = _presentationError = message;

    internal bool SavePresentationChoice(UiSettings settings)
    {
        try { settings.SaveChecked(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Window settings could not be saved: {ex.Message}";
            return false;
        }
    }
}
