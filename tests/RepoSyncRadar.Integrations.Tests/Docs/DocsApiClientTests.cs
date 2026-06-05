using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services.Docs;
using RichardSzalay.MockHttp;
using Xunit;

namespace RepoSyncRadar.Integrations.Tests.Docs;

/// <summary>
/// Verifies the behavior promised by <see cref="DocsApiClient"/> against fake docs.github.com
/// responses, with a controllable <see cref="TimeProvider"/> so the cache TTL logic is testable.
/// </summary>
public class DocsApiClientTests
{
    private const string _baseAddress = "https://docs.github.com/";

    private static readonly string[] _expectedPaths = ["/en/get-started", "/en/repositories"];

    [Fact]
    public async Task GetPageListAsync_Returns_Parsed_Paths()
    {
        var (client, handler, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;
        handler.Expect(HttpMethod.Get, _baseAddress + "api/pagelist/en/fpt")
            .Respond("application/json", "[\"/en/get-started\",\"/en/repositories\"]");

        var result = await client.GetPageListAsync("en", "fpt", ct);

        Assert.Equal(_expectedPaths, result);
        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task GetPageListAsync_Uses_Cache_On_Second_Call()
    {
        var (client, handler, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var hitCount = 0;
        handler.When(HttpMethod.Get, _baseAddress + "api/pagelist/en/fpt")
            .Respond(_ =>
            {
                Interlocked.Increment(ref hitCount);
                return JsonResponse("[\"/en/get-started\"]");
            });

        _ = await client.GetPageListAsync("en", "fpt", ct);
        var second = await client.GetPageListAsync("en", "fpt", ct);

        Assert.Equal(1, hitCount);
        Assert.Single(second);
    }

    [Fact]
    public async Task GetPageListAsync_Refreshes_After_Ttl()
    {
        var (client, handler, clock) = CreateClient(pageListCacheSeconds: 60);
        var ct = TestContext.Current.CancellationToken;
        var hitCount = 0;
        handler.When(HttpMethod.Get, _baseAddress + "api/pagelist/en/fpt")
            .Respond(_ =>
            {
                Interlocked.Increment(ref hitCount);
                return JsonResponse("[\"/en/get-started\"]");
            });

        _ = await client.GetPageListAsync("en", "fpt", ct);
        clock.Advance(TimeSpan.FromSeconds(61));
        _ = await client.GetPageListAsync("en", "fpt", ct);

        Assert.Equal(2, hitCount);
    }

    [Fact]
    public async Task GetArticleBodyAsync_Returns_Body_Html()
    {
        var (client, handler, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;
        handler.Expect(HttpMethod.Get, _baseAddress + "api/article/body")
            .WithQueryString("pathname", "/en/get-started")
            .Respond("text/html", "<p>hello</p>");

        var body = await client.GetArticleBodyAsync("/en/get-started", ct);

        Assert.Equal("<p>hello</p>", body);
        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task ResolveCanonicalAsync_Returns_RedirectedFrom_Target()
    {
        var (client, handler, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;
        handler.Expect(HttpMethod.Get, _baseAddress + "api/article/meta")
            .WithQueryString("pathname", "/en/old-path")
            .Respond(
                "application/json",
                "{\"canonical\":\"/en/new-path\",\"redirectedFrom\":\"/en/redirect-target\"}");

        var result = await client.ResolveCanonicalAsync("/en/old-path", ct);

        Assert.Equal("/en/redirect-target", result);
        handler.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task ResolveCanonicalAsync_NotFound_Returns_Null()
    {
        var (client, handler, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;
        handler.When(HttpMethod.Get, _baseAddress + "api/article/meta")
            .Respond(HttpStatusCode.NotFound);

        var result = await client.ResolveCanonicalAsync("/en/missing", ct);

        Assert.Null(result);
    }

    [Fact]
    public async Task Non2xx_Throws_DocsApiException()
    {
        var (client, handler, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;
        handler.When(HttpMethod.Get, _baseAddress + "api/pagelist/en/fpt")
            .Respond(HttpStatusCode.InternalServerError, "text/plain", "boom");

        var ex = await Assert.ThrowsAsync<DocsApiException>(
            () => client.GetPageListAsync("en", "fpt", ct));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Equal("boom", ex.ResponseBody);
        Assert.Contains("api/pagelist/en/fpt", ex.RequestPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserAgent_Header_Sent()
    {
        var (client, handler, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;
        HttpRequestMessage? captured = null;
        handler.When("*")
            .With(req =>
            {
                captured = req;
                return true;
            })
            .Respond(_ => JsonResponse("[]"));

        _ = await client.GetPageListAsync("en", "fpt", ct);

        Assert.NotNull(captured);
        var ua = Assert.Single(captured!.Headers.UserAgent);
        Assert.NotNull(ua.Product);
        Assert.Equal("reposyncradar", ua.Product!.Name);
        Assert.False(string.IsNullOrWhiteSpace(ua.Product.Version));
    }

    private static (DocsApiClient client, MockHttpMessageHandler handler, FakeClock clock) CreateClient(
        int pageListCacheSeconds = 86_400)
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = handler.ToHttpClient();
        var options = Options.Create(new DocsApiOptions
        {
            BaseAddress = new Uri(_baseAddress),
            ClientName = "reposyncradar",
            PageListCacheSeconds = pageListCacheSeconds,
        });
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var client = new DocsApiClient(httpClient, options, clock);
        return (client, handler, clock);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeClock(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
