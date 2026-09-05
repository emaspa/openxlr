using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

public sealed record UpdateResult(bool Available, string Tag, string Title, string Details, string? Url);

/// <summary>
/// Read-only release check against the upstream repository. It never downloads
/// or installs a package, follows no redirects, and renders release notes as
/// plain text.
/// </summary>
public sealed class UpdateChecker
{
    private const string ReleasesEndpoint = "https://api.github.com/repos/emaspa/openxlr/releases/latest";
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(8) };

    public UpdateChecker() : this(SharedHttp) { }
    internal UpdateChecker(HttpClient http, TimeSpan? timeout = null)
    {
        _http = http;
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    public async Task<UpdateResult> CheckAsync(string installedVersion, CancellationToken cancellation = default)
    {
        // ResponseHeadersRead ends HttpClient's timeout at the headers. Keep
        // one deadline over the body too, including a server that stops sending.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(_timeout);
        cancellation = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesEndpoint);
        request.Headers.UserAgent.ParseAdd("OpenXLR-UpdateCheck/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        const int limit = 512 * 1024;
        if (response.Content.Headers.ContentLength is > limit)
            throw new InvalidDataException("GitHub response exceeds the update-check size limit.");
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        int length;
        while ((length = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (content.Length + length > limit)
                throw new InvalidDataException("GitHub response exceeds the update-check size limit.");
            content.Write(buffer, 0, length);
        }

        using JsonDocument document = JsonDocument.Parse(content.ToArray());
        JsonElement root = document.RootElement;
        string tag = String(root, "tag_name");
        bool released = !Flag(root, "draft") && !Flag(root, "prerelease");
        if (!released || !Newer(tag, installedVersion))
            return new(false, tag, "OpenXLR is up to date",
                $"Installed version: {installedVersion}. Latest stable release: {tag}.", null);

        string details = String(root, "body");
        if (details.Length > 12000) details = details[..12000] + "\n… Open GitHub for the complete notes.";
        return new(true, tag, $"New OpenXLR release {tag}", details,
            $"https://github.com/emaspa/openxlr/releases/tag/{Uri.EscapeDataString(tag)}");
    }

    internal static bool Newer(string tag, string installed)
    {
        static Version? Parse(string value) =>
            Version.TryParse(value.TrimStart('v', 'V').Split('+', '-')[0], out Version? parsed)
                ? new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision))
                : null;
        Version? remote = Parse(tag), local = Parse(installed);
        return !tag.Contains('-', StringComparison.Ordinal) && remote is not null && local is not null && remote > local;
    }

    private static string String(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out JsonElement item)
            && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    private static bool Flag(JsonElement value, string key)
        => value.TryGetProperty(key, out JsonElement item) && item.ValueKind == JsonValueKind.True;
}

/// <summary>Coalesced UI state for manual and opt-in daily checks.</summary>
public sealed class UpdatesViewModel : ViewModelBase
{
    private readonly Func<CancellationToken, Task<UpdateResult>> _check;
    private readonly Func<DateTimeOffset> _clock;

    public UpdatesViewModel() : this(
        token => new UpdateChecker().CheckAsync(AppVersion.Current, token),
        () => DateTimeOffset.UtcNow) { }

    internal UpdatesViewModel(Func<CancellationToken, Task<UpdateResult>> check,
        Func<DateTimeOffset>? clock = null)
    {
        _check = check;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private bool _busy;
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    private bool _available;
    public bool Available { get => _available; private set => Set(ref _available, value); }
    private bool _bannerVisible;
    public bool BannerVisible { get => _bannerVisible; private set => Set(ref _bannerVisible, value); }
    private string _title = "Updates have not been checked";
    public string Title { get => _title; private set => Set(ref _title, value); }
    private string _details = "No network request is made unless you check manually or opt in below.";
    public string Details { get => _details; private set => Set(ref _details, value); }
    private string? _url;
    public string? Url { get => _url; private set => Set(ref _url, value); }
    private string? _tag;

    public static bool AutomaticCheckDue(UiSettings settings, DateTimeOffset now)
        => settings.CheckForUpdates &&
           (settings.LastUpdateCheckUtc is null || now - settings.LastUpdateCheckUtc >= TimeSpan.FromHours(24));

    public async Task CheckAsync(bool manual, CancellationToken cancellation = default)
    {
        if (Busy) return;
        UiSettings settings = UiSettings.Load();
        if (!manual && !AutomaticCheckDue(settings, _clock())) return;
        Busy = true;
        try
        {
            UpdateResult result = await _check(cancellation);
            Available = result.Available;
            _tag = result.Tag;
            Title = result.Title;
            Details = result.Details;
            Url = result.Url;
            BannerVisible = result.Available && settings.DismissedUpdate != result.Tag;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            Available = false;
            BannerVisible = false;
            Url = null;
            Title = "Update check unavailable";
            Details = "Audio is unaffected. Retry later or open the release page manually.";
        }
        finally
        {
            // A failed endpoint must not be retried on every UI launch either.
            (UiSettings.Load() with { LastUpdateCheckUtc = _clock() }).Save();
            Busy = false;
        }
    }

    public void DismissBanner()
    {
        if (_tag is not null) (UiSettings.Load() with { DismissedUpdate = _tag }).Save();
        BannerVisible = false;
    }
}
