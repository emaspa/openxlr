using System;
using System.Collections.Generic;
using System.Text.Json;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core.Mixing;
#endif

/// <summary>
/// pw-dump may append change arrays while the graph is changing. Fold those
/// batches by registry id, replacing full objects and removing tombstones.
/// </summary>
internal static class PipeWireSnapshot
{
    internal static JsonDocument Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json,
            new JsonReaderOptions { AllowMultipleValues = true });
        JsonDocument first = JsonDocument.ParseValue(ref reader);
        bool returnedFirst = false;
        try
        {
            if (first.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Expected a PipeWire object array.");
            // Most snapshots contain one array. Keep that document without
            // cloning and serializing the entire graph on every sweep.
            if (!reader.Read())
            {
                returnedFirst = true;
                return first;
            }

            var objects = new Dictionary<uint, JsonElement>();
            Apply(first.RootElement, objects);
            do
            {
                using JsonDocument batch = JsonDocument.ParseValue(ref reader);
                Apply(batch.RootElement, objects);
            } while (reader.Read());
            return JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(objects.Values));
        }
        finally
        {
            if (!returnedFirst) first.Dispose();
        }
    }

    private static void Apply(JsonElement batch, Dictionary<uint, JsonElement> objects)
    {
        if (batch.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected a PipeWire object array.");
        foreach (JsonElement item in batch.EnumerateArray())
        {
            uint id = RegistryId(item);
            if (IsRemoval(item))
                objects.Remove(id);
            else
                objects[id] = item.Clone();
        }
    }

    internal static uint RegistryId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out JsonElement key)
            || key.ValueKind != JsonValueKind.Number || !key.TryGetUInt32(out uint id))
            throw new JsonException("PipeWire update has no valid registry id.");
        return id;
    }

    internal static bool IsRemoval(JsonElement item)
        => item.TryGetProperty("info", out JsonElement info) && info.ValueKind == JsonValueKind.Null
            || item.TryGetProperty("props", out JsonElement props) && props.ValueKind == JsonValueKind.Null;
}
