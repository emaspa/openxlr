using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenXLR.UI;

/// <summary>A portable parameter snapshot. Plugin-private binary state is not part of this format.</summary>
public sealed record EffectChainData(int Version, int Channels, JsonArray Inserts)
{
    public EffectChainData Copy(bool freshIds = false)
    {
        var copy = (JsonArray)Inserts.DeepClone();
        if (freshIds) foreach (var insert in copy) insert!["id"] = Guid.NewGuid().ToString("N");
        return this with { Inserts = copy };
    }
    public void Validate()
    {
        if (Version != 1 || Channels is not (1 or 2) || Inserts is null || Inserts.Count > 16)
            throw new InvalidDataException("Unsupported effect chain version, width or size.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Inserts)
        {
            if (node is not JsonObject insert) throw new InvalidDataException("An effect definition is missing.");
            string id = Text(insert, "id", 64), kind = Text(insert, "kind", 4);
            if (!id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') || !ids.Add(id)
                || kind is not ("lv2" or "clap" or "vst3")) throw new InvalidDataException("An effect id or format is invalid.");
            _ = Text(insert, "plugin", 512);
            if (insert["label"] is not null) _ = Text(insert, "label", 256, empty: true);
            foreach (string field in new[] { "bypass", "nativeHost" })
                if (insert[field] is not null && (insert[field] is not JsonValue value || !value.TryGetValue<bool>(out _)))
                    throw new InvalidDataException("An effect switch is invalid.");
            if (insert["params"] is not JsonObject parameters || parameters.Count > 256)
                throw new InvalidDataException("An effect has invalid parameters.");
            foreach (var (symbol, value) in parameters)
                if (symbol.Length is 0 or > 256 || symbol.Any(char.IsControl)
                    || value is not JsonValue number || !number.TryGetValue<double>(out double n) || !double.IsFinite(n))
                    throw new InvalidDataException("An effect parameter is invalid.");
        }
    }
    private static string Text(JsonObject insert, string field, int limit, bool empty = false)
    {
        if (insert[field] is not JsonValue value || !value.TryGetValue<string>(out string? text)
            || text.Length > limit || (!empty && string.IsNullOrWhiteSpace(text)) || text.Any(char.IsControl))
            throw new InvalidDataException($"Invalid effect {field}.");
        return text;
    }
}

public sealed record EffectChainPreset(string Name, EffectChainData Chain)
{
    public override string ToString() => Name;
}

/// <summary>One private, bounded file. Refuse edits to damaged data instead of overwriting it.</summary>
public static class EffectChainPresets
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static string FilePath => OpenXlrPaths.ConfigFile("effect-chain-presets.json");
    private const int MaxFileBytes = 8 * 1024 * 1024;
    public static IReadOnlyList<EffectChainPreset> Read()
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return [];
            using var file = File.OpenRead(FilePath);
            if (file.Length > MaxFileBytes) throw new InvalidDataException("The effect preset file is too large.");
            byte[] bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1) throw new IOException("The effect preset file changed while reading.");
            var presets = JsonSerializer.Deserialize<List<EffectChainPreset>>(bytes, Json)
                ?? throw new InvalidDataException("The effect preset file is empty.");
            if (presets.Count > 64) throw new InvalidDataException("At most 64 effect presets are supported.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var preset in presets)
            {
                if (preset is null || !ValidName(preset.Name) || !names.Add(preset.Name) || preset.Chain is null)
                    throw new InvalidDataException("The effect preset file contains an invalid entry.");
                preset.Chain.Validate();
            }
            return presets;
        }
    }
    public static void Save(string name, EffectChainData chain)
    {
        name = name.Trim();
        if (!ValidName(name)) throw new InvalidDataException("Use a preset name of 1 to 80 characters without control characters.");
        chain.Validate();
        lock (Gate)
        {
            var presets = Read().ToList();
            if (presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("That preset name already exists. Choose a new name or delete the old preset.");
            if (presets.Count >= 64) throw new InvalidDataException("The limit of 64 presets has been reached.");
            presets.Add(new(name, chain.Copy()));
            Write(presets);
        }
    }
    public static void Delete(string name)
    {
        lock (Gate) Write(Read().Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList());
    }
    private static bool ValidName(string? name) => name is { Length: > 0 and <= 80 } && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);
    private static void Write(IReadOnlyList<EffectChainPreset> presets)
    {
        string text = JsonSerializer.Serialize(presets, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxFileBytes) throw new InvalidDataException("The effect preset file would exceed 8 MiB.");
        OpenXlrPaths.WriteAtomic(FilePath, text);
    }
}
