namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    private const int AppVolumeWritesPerSweep = 8;
    private Dictionary<string, double> _appVolumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (int Serial, string Identity, double Volume, long RetryAt)> _appVolumeApplied = [];

    public const int MaxAppVolumeIdentityLength = 256;

    public static bool ValidAppVolumeIdentity(string? identity)
        => identity is { Length: > 0 and <= MaxAppVolumeIdentityLength }
            && !string.IsNullOrWhiteSpace(identity) && !identity.Any(char.IsControl)
            && !PipeWireAdapter.IsPlumbingIdentity(StreamMatcher.MigrateIdentity(identity));

    internal static bool ValidPlaybackVolume(double volume) => volume is >= 0 and <= 1;

    public void SetAppVolume(string identity, double? volume, Func<MixerSettings, string?> save)
    {
        if (!ValidAppVolumeIdentity(identity)) throw new ArgumentException("invalid application identity");
        if (volume is double level && !ValidPlaybackVolume(level))
            throw new ArgumentOutOfRangeException(nameof(volume), "playback volume must be between 0 and 1");
        identity = StreamMatcher.MigrateIdentity(identity);
        lock (_gate)
        {
            if (!_apps.TryGetValue(identity, out StreamAssignment? app))
                throw new InvalidOperationException("the application is no longer listed");
            if (volume is not null && app.BrowserHeld)
                throw new InvalidOperationException("use Browser tabs for a browser with individual tab routing enabled");
            if (volume is not null && !_appVolumes.ContainsKey(identity) && _appVolumes.Count >= MaxAppOverrides)
                throw new InvalidOperationException($"at most {MaxAppOverrides} application volumes can be remembered");
            double? nextVolume = volume is double value ? SnapPlaybackVolume(value) : null;
            double? previous = _appVolumes.TryGetValue(identity, out double current) ? current : null;
            if (nextVolume == previous)
            {
                if (nextVolume is not null) ClearAppliedAppVolumeLocked(identity);
                return;
            }
            var next = new Dictionary<string, double>(_appVolumes, StringComparer.OrdinalIgnoreCase);
            if (nextVolume is double kept) next[identity] = kept;
            else next.Remove(identity);
            if (save(ExportSettings() with { AppVolumes = next }) is string error) throw new IOException(error);
            _appVolumes = next;
            if (volume is null) ClearAppliedAppVolumeLocked(identity);
        }
    }

    private static double SnapPlaybackVolume(double volume) => Math.Round(volume * 100) / 100;

    private void ClearAppliedAppVolumeLocked(string identity)
    {
        foreach (int id in _appVolumeApplied.Where(p => p.Value.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase))
                     .Select(p => p.Key).ToArray())
            _appVolumeApplied.Remove(id);
    }

    private void SyncAppVolumeLocked(StreamAssignment stream, ref int budget)
    {
        if (stream.BrowserHeld || !_appVolumes.TryGetValue(stream.Identity, out double volume))
        {
            _appVolumeApplied.Remove(stream.Id);
            return;
        }
        if (budget <= 0 || stream.Serial <= 0) return;
        if (_appVolumeApplied.TryGetValue(stream.Id, out var applied) && applied.Serial == stream.Serial
            && applied.Volume == volume && applied.Identity.Equals(stream.Identity, StringComparison.OrdinalIgnoreCase)
            && (applied.RetryAt == 0 || Clock() < applied.RetryAt)) return;
        budget--;
        long retryAt = 0;
        try { _pw.SetSinkInputVolume(stream.Serial, volume); }
        catch (InvalidOperationException)
        {
            // A stalled pipewire-pulse should cost at most one failed write
            // per sweep. Leave the other streams for the next one.
            retryAt = Clock() + 3000;
            budget = 0;
        }
        _appVolumeApplied[stream.Id] = (stream.Serial, stream.Identity, volume, retryAt);
    }
}
