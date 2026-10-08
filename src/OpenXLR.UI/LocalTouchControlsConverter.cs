using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.UI;

/// <summary>
/// Reads the saved control sizing. A value that is not true or false reads as
/// Standard instead of failing the whole of ui.json, so one damaged key does
/// not cost the user every other window preference.
/// </summary>
public sealed class LocalTouchControlsConverter : JsonConverter<bool>
{
    public override bool HandleNull => true;

    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False) return reader.GetBoolean();
        reader.Skip();
        return false;
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}
