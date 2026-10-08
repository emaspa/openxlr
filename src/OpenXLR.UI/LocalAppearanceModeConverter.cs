using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.UI;

/// <summary>
/// Reads the saved Material mode. A value of the wrong type or an unknown
/// name reads as <c>system</c> instead of failing the whole of ui.json, so
/// one damaged key does not cost the user every other window preference.
/// </summary>
public sealed class LocalAppearanceModeConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return AppearanceModes.Normalize(reader.GetString());
        reader.Skip();
        return AppearanceModes.System;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(AppearanceModes.Normalize(value));
}
