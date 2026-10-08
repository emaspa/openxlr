using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenXLR.Tui;

/// <summary>
/// The desktop's light or dark preference, for Material in system mode. One
/// <c>gdbus monitor</c> on the settings portal runs while the terminal follows
/// the desktop: the current value is read when the portal appears and changes
/// arrive as signals, so nothing polls. With no session bus, no portal or no
/// gdbus the scheme stays 0 (no preference) and Material stays dark. The frame
/// loop reads <see cref="Scheme"/>, a cached value, and never waits on D-Bus.
/// </summary>
internal sealed partial class DesktopAppearance(string executable = "gdbus") : IAsyncDisposable
{
    private const string Portal = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _watch;
    private Task? _query;
    private bool _readRequested;
    private long _revision;
    private int _scheme;

    /// <summary>The portal's color-scheme: 0 no preference, 1 dark, 2 light.</summary>
    internal int Scheme => Volatile.Read(ref _scheme);

    public void Start()
    {
        if (_watch is not null || _stop.IsCancellationRequested) return;
        // Do not let a terminal without a desktop trigger D-Bus autolaunch.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        { _watch = Task.CompletedTask; return; }
        _watch = WatchAsync();
    }

    private async Task WatchAsync()
    {
        try
        {
            await ProcessRunner.RunStreamingAsync(executable,
                ["monitor", "--session", "--dest", Portal, "--object-path", ObjectPath], ReadSignals, _stop.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or OperationCanceledException)
        { /* a desktop preference is optional */ }
        finally
        {
            _stop.Cancel();
            Task? query;
            lock (_gate) { _revision++; _readRequested = false; query = _query; Volatile.Write(ref _scheme, 0); }
            if (query is not null) await query.ConfigureAwait(false);
        }
    }

    private async Task ReadSignals(Stream stream, CancellationToken cancel)
    {
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        char[] buffer = new char[1024];
        StringBuilder line = new();
        bool discard = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancel).ConfigureAwait(false)) != 0)
        {
            for (int i = 0; i < count; i++)
            {
                char ch = buffer[i];
                if (ch == '\n')
                {
                    if (!discard) Receive(line.ToString());
                    line.Clear();
                    discard = false;
                }
                else if (!discard)
                {
                    if (line.Length == 4096) { line.Clear(); discard = true; }
                    else line.Append(ch);
                }
            }
        }
    }

    private void Receive(string line)
    {
        int? value = ParseSignal(line);
        bool owner = OwnerPattern().IsMatch(line);
        bool absent = line == $"The name {Portal} does not have an owner";
        if (value is null && !owner && !absent) return;
        lock (_gate)
        {
            _revision++;
            Volatile.Write(ref _scheme, value ?? 0);
            _readRequested = owner;
            // Only one read runs at once. Owner churn coalesces to its latest
            // state, and a newer signal invalidates a late initial reply.
            if (owner && _query is null) _query = Task.Run(ReadRequestedAsync);
        }
    }

    private async Task ReadRequestedAsync()
    {
        while (true)
        {
            long revision;
            lock (_gate)
            {
                if (!_readRequested || _stop.IsCancellationRequested) { _query = null; return; }
                _readRequested = false;
                revision = _revision;
            }
            int? value = null;
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(executable,
                    ["call", "--session", "--dest", Portal, "--object-path", ObjectPath,
                     "--method", "org.freedesktop.portal.Settings.Read", "org.freedesktop.appearance", "color-scheme"],
                    TimeSpan.FromSeconds(3), stdoutCap: 4096, stderrCap: 4096, cancel: _stop.Token).ConfigureAwait(false);
                if (result.Ok) value = ParseReply(result.StdoutText.Trim());
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or OperationCanceledException)
            { /* no supported setting leaves the fallback */ }
            lock (_gate)
                if (_revision == revision && value is { } scheme) Volatile.Write(ref _scheme, scheme);
        }
    }

    internal static int? ParseSignal(string line) => Value(SignalPattern().Match(line));

    internal static int? ParseReply(string line) => Value(ReplyPattern().Match(line));

    private static int? Value(Match match)
    {
        if (!match.Success || !uint.TryParse(match.Groups["value"].Value, out uint value)) return null;
        return value is 1 or 2 ? (int)value : 0;
    }

    [GeneratedRegex(@"^/org/freedesktop/portal/desktop: org\.freedesktop\.portal\.Settings\.SettingChanged \('org\.freedesktop\.appearance', 'color-scheme', <uint32 (?<value>[0-9]{1,10})>\)$", RegexOptions.CultureInvariant)]
    private static partial Regex SignalPattern();

    // Read predates ReadOne and some portals return a double variant. Both
    // encodings carry the same uint32; other types and trailing data are refused.
    [GeneratedRegex(@"^\((?:<uint32 (?<value>[0-9]{1,10})>|<<uint32 (?<value>[0-9]{1,10})>>),\)$", RegexOptions.CultureInvariant)]
    private static partial Regex ReplyPattern();

    [GeneratedRegex(@"^The name org\.freedesktop\.portal\.Desktop is owned by :[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerPattern();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_watch is not null) await _watch.ConfigureAwait(false);
        _stop.Dispose();
    }
}
