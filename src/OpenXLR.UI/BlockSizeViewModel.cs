using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

public sealed partial class MainViewModel
{
    // The block size the graph is forced to, a machine setting the daemon
    // saves. Index 0 is automatic, then the sizes the daemon offers.
    private int[] _blockSizes = [];
    private int? _blockSize;
    public bool BlockSizeReported => _blockSizes.Length > 0;
    public IReadOnlyList<string> BlockSizeChoices { get; private set; } = [];

    public int BlockSizeIndex
    {
        get => _blockSize is int frames ? Array.IndexOf(_blockSizes, frames) + 1 : 0;
        set
        {
            // A ComboBox writes -1 while its items are replaced.
            if (value < 0 || value > _blockSizes.Length) return;
            int? chosen = value == 0 ? null : _blockSizes[value - 1];
            if (chosen == _blockSize) return;
            _blockSize = chosen;
            Raise();
            if (!_applying) _ = SetBlockSizeAsync(chosen);
        }
    }

    private async Task SetBlockSizeAsync(int? frames)
    {
        string? error = await _client.SetBlockSizeAsync(frames);
        if (error is not null) ReportError(error);
    }

    /// <summary>A block of <paramref name="frames"/> at 48 kHz, with its length in milliseconds.</summary>
    internal static string BlockSizeLabel(int frames)
        => Localizer.Format("BlockSizeFrames", frames, (frames / 48.0).ToString("0.#", CultureInfo.CurrentCulture));

    private void ApplyBlockSize(JsonNode? mixer)
    {
        int[] sizes = mixer?["blockSizes"] is JsonArray list
            ? [.. list.OfType<JsonValue>().Select(v => v.TryGetValue(out int f) ? f : 0).Where(f => f > 0)]
            : [];
        if (!sizes.SequenceEqual(_blockSizes))
        {
            _blockSizes = sizes;
            BlockSizeChoices = sizes.Length == 0 ? [] : [Localizer.Text("BlockSizeAuto"), .. sizes.Select(BlockSizeLabel)];
            Raise(nameof(BlockSizeChoices)); Raise(nameof(BlockSizeReported));
        }
        _blockSize = mixer?["blockSize"] is JsonValue value && value.TryGetValue(out int forced) ? forced : null;
        Raise(nameof(BlockSizeIndex));
    }
}
