using System.Net;
using System.Text.Json;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>Offline contracts: these tests never contact GitHub or install anything.</summary>
public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.10.0", "1.9.9", true)]
    [InlineData("1.2.0", "1.2", false)]
    [InlineData("1.2.0", "1.2.0+abc", false)]
    [InlineData("1.2.0-rc1", "1.1.0", false)]
    [InlineData("v0.1.19", "0.1.20", false)]
    [InlineData("latest", "0.1.20", false)]
    public void VersionsCompareNumerically(string remote, string local, bool expected)
        => Assert.Equal(expected, UpdateChecker.Newer(remote, local));

    [Fact]
    public async Task ReleaseUsesConstructedUpstreamUrlAndBoundedPlainNotes()
    {
        using var handler = new ResponseHandler(JsonSerializer.Serialize(new
        {
            tag_name = "v0.2.0",
            body = new string('a', 15000),
            html_url = "https://evil.example/run",
        }));
        using var http = new HttpClient(handler);

        UpdateResult result = await new UpdateChecker(http).CheckAsync("0.1.20");

        Assert.True(result.Available);
        Assert.Equal("https://github.com/emaspa/openxlr/releases/tag/v0.2.0", result.Url);
        Assert.InRange(result.Details.Length, 12000, 12100);
        Assert.Equal("api.github.com", handler.RequestUri!.Host);
        Assert.Null(handler.Authorization);
    }

    [Theory]
    [InlineData("{\"tag_name\":\"v99.0.0\",\"prerelease\":true}")]
    [InlineData("{\"tag_name\":\"v99.0.0\",\"draft\":true}")]
    [InlineData("{\"tag_name\":null}")]
    public async Task DraftPrereleaseAndMissingTagsAreNotAdvertised(string response)
    {
        using var handler = new ResponseHandler(response);
        using var http = new HttpClient(handler);

        UpdateResult result = await new UpdateChecker(http).CheckAsync("0.1.20");

        Assert.False(result.Available);
        Assert.Null(result.Url);
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        using var handler = new ResponseHandler(new string('x', 512 * 1024 + 1));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new UpdateChecker(http).CheckAsync("0.1.20"));
    }

    [Fact]
    public async Task DeadlineCancelsAStalledBodyAfterHeadersArrive()
    {
        using var stream = new StalledStream();
        using var handler = new StreamHandler(stream);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var checker = new UpdateChecker(http, TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => checker.CheckAsync("0.1.20").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stream.ReadStarted);
    }

    private sealed class StreamHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            });
    }

    private sealed class StalledStream : MemoryStream
    {
        public bool ReadStarted { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public void AutomaticChecksAreOptInAndDaily()
    {
        var now = new DateTimeOffset(2026, 9, 5, 8, 0, 0, TimeSpan.Zero);
        Assert.False(UpdatesViewModel.AutomaticCheckDue(new UiSettings(), now));
        Assert.True(UpdatesViewModel.AutomaticCheckDue(
            new UiSettings { CheckForUpdates = true }, now));
        Assert.False(UpdatesViewModel.AutomaticCheckDue(new UiSettings
        {
            CheckForUpdates = true,
            LastUpdateCheckUtc = now.AddHours(-23),
        }, now));
        Assert.True(UpdatesViewModel.AutomaticCheckDue(new UiSettings
        {
            CheckForUpdates = true,
            LastUpdateCheckUtc = now.AddHours(-24),
        }, now));
    }

    private sealed class ResponseHandler(string response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Assert.NotEmpty(request.Headers.UserAgent);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            });
        }
    }
}
