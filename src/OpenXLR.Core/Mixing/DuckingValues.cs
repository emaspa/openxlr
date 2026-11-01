using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// A ducking sensitivity or amount: a preset word, or a whole number of dB
/// (the key threshold in dBFS for a sensitivity, the depth for an amount).
/// JSON holds it as the string or the number it was set as, so the state
/// reports it in the form it was given. A value read that is neither, such
/// as a fraction or a boolean, has no word and no number, and every check
/// refuses it as it refuses a word it does not know.
/// </summary>
[JsonConverter(typeof(DuckingLevelJsonConverter))]
public sealed record DuckingLevel
{
    private DuckingLevel(string? word, int? db)
    {
        Word = word;
        Db = db;
    }

    /// <summary>The preset word, or null for a number.</summary>
    public string? Word { get; }

    /// <summary>The number of dB, or null for a word.</summary>
    public int? Db { get; }

    public static DuckingLevel Preset(string word) => new(word ?? throw new ArgumentNullException(nameof(word)), null);

    public static DuckingLevel Number(int db) => new(null, db);

    /// <summary>What the reader keeps for a value that is neither a word nor a whole number.</summary>
    internal static DuckingLevel Unreadable { get; } = new(null, null);

    public static implicit operator DuckingLevel(string word) => Preset(word);

    public static implicit operator DuckingLevel(int db) => Number(db);

    /// <summary>The word, or the number: as the journal and an error show it.</summary>
    public override string ToString() => Word ?? Db?.ToString(CultureInfo.InvariantCulture) ?? "unreadable";
}

/// <summary>
/// A ducking speed: a preset word, or the attack and release times in whole
/// milliseconds, written in JSON as <c>{"attack": ms, "release": ms}</c>. As
/// with <see cref="DuckingLevel"/>, a value read that is neither keeps no
/// word and no times, and every check refuses it.
/// </summary>
[JsonConverter(typeof(DuckingSpeedJsonConverter))]
public sealed record DuckingSpeed
{
    private DuckingSpeed(string? word, int? attack, int? release)
    {
        Word = word;
        Attack = attack;
        Release = release;
    }

    /// <summary>The preset word, or null for times.</summary>
    public string? Word { get; }

    /// <summary>How long the channels take to go down, in ms; null for a word.</summary>
    public int? Attack { get; }

    /// <summary>How long they take to come back, in ms; null for a word.</summary>
    public int? Release { get; }

    public static DuckingSpeed Preset(string word) => new(word ?? throw new ArgumentNullException(nameof(word)), null, null);

    public static DuckingSpeed Times(int attack, int release) => new(null, attack, release);

    internal static DuckingSpeed Unreadable { get; } = new(null, null, null);

    public static implicit operator DuckingSpeed(string word) => Preset(word);

    public override string ToString() => Word
        ?? (Attack is { } attack && Release is { } release
            ? string.Create(CultureInfo.InvariantCulture, $"attack {attack} ms, release {release} ms") : "unreadable");
}

/// <summary>Reads a string as a word and a whole number as dB; anything else as unreadable.</summary>
public sealed class DuckingLevelJsonConverter : JsonConverter<DuckingLevel>
{
    public override DuckingLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString() is { } word ? DuckingLevel.Preset(word) : DuckingLevel.Unreadable;
            case JsonTokenType.Number:
                return DuckingJson.Whole(reader.GetDouble()) is { } db ? DuckingLevel.Number(db) : DuckingLevel.Unreadable;
            default:
                reader.Skip();
                return DuckingLevel.Unreadable;
        }
    }

    public override void Write(Utf8JsonWriter writer, DuckingLevel value, JsonSerializerOptions options)
    {
        if (value.Word is { } word) writer.WriteStringValue(word);
        else if (value.Db is { } db) writer.WriteNumberValue(db);
        else writer.WriteNullValue();
    }
}

/// <summary>
/// Reads a string as a word and an object holding exactly whole-number
/// <c>attack</c> and <c>release</c> as times; anything else as unreadable.
/// </summary>
public sealed class DuckingSpeedJsonConverter : JsonConverter<DuckingSpeed>
{
    public override DuckingSpeed Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString() is { } word ? DuckingSpeed.Preset(word) : DuckingSpeed.Unreadable;
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return DuckingSpeed.Unreadable;
        }
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return DuckingJson.Speed(document.RootElement) ?? DuckingSpeed.Unreadable;
    }

    public override void Write(Utf8JsonWriter writer, DuckingSpeed value, JsonSerializerOptions options)
    {
        if (value.Word is { } word) writer.WriteStringValue(word);
        else if (value is { Attack: { } attack, Release: { } release })
        {
            writer.WriteStartObject();
            writer.WriteNumber("attack", attack);
            writer.WriteNumber("release", release);
            writer.WriteEndObject();
        }
        else writer.WriteNullValue();
    }
}

/// <summary>The one reading of a speed object and of a whole number, shared by the files and the command.</summary>
public static class DuckingJson
{
    /// <summary>
    /// The times an object holds, or null unless it has exactly
    /// <c>attack</c> and <c>release</c>, each a whole number. Their range is
    /// checked elsewhere.
    /// </summary>
    public static DuckingSpeed? Speed(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        int? attack = null, release = null;
        foreach (JsonProperty field in value.EnumerateObject())
        {
            if (field.Value.ValueKind != JsonValueKind.Number || Whole(field.Value.GetDouble()) is not { } ms) return null;
            switch (field.Name)
            {
                case "attack" when attack is null: attack = ms; break;
                case "release" when release is null: release = ms; break;
                default: return null;
            }
        }
        return attack is { } a && release is { } r ? DuckingSpeed.Times(a, r) : null;
    }

    /// <summary>A JSON number as a whole number, 12 and 12.0 alike, or null for a fraction or one past an int.</summary>
    public static int? Whole(double value)
        => double.IsFinite(value) && value == Math.Floor(value) && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
}
