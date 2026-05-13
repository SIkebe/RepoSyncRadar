using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.Core.Services.Docs;

/// <summary>
/// Implementation of <see cref="IDocsApiClient"/> backed by the public docs.github.com API.
/// </summary>
/// <remarks>
/// <para>
/// Three endpoints are used:
/// <list type="bullet">
///   <item><c>api/pagelist/{language}/{version}</c> — canonical path enumeration per version.</item>
///   <item><c>api/article/meta?pathname=...</c> — canonical URL resolution + redirect target.</item>
///   <item><c>api/article/body?pathname=...</c> — rendered HTML for an article.</item>
/// </list>
/// </para>
/// <para>
/// The per-version page list response is large enough to be worth caching in-process. We avoid
/// <see cref="System.Runtime.Caching.MemoryCache"/> so callers can swap the clock for tests via
/// <see cref="TimeProvider"/>.
/// </para>
/// </remarks>
public sealed class DocsApiClient : IDocsApiClient
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly DocsApiOptions _options;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, PageListCacheEntry> _pageListCache = new(StringComparer.Ordinal);

    public DocsApiClient(HttpClient http, IOptions<DocsApiOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _http = http;
        _options = options.Value;
        _clock = clock;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = _options.BaseAddress;
        }

        EnsureUserAgent();
    }

    public async Task<IReadOnlyList<string>> GetPageListAsync(
        string language,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var key = $"{language}/{version}";
        var now = _clock.GetUtcNow();
        if (_pageListCache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Value;
        }

        var path = $"api/pagelist/{Uri.EscapeDataString(language)}/{Uri.EscapeDataString(version)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, path, treatNotFoundAsArticleMissing: false, cancellationToken).ConfigureAwait(false);

        var paths = await response.Content
            .ReadFromJsonAsync<List<string>>(s_jsonOptions, cancellationToken)
            .ConfigureAwait(false);

        var value = (IReadOnlyList<string>)(paths ?? new List<string>(capacity: 0));
        var expiresAt = _clock.GetUtcNow().AddSeconds(_options.PageListCacheSeconds);
        _pageListCache[key] = new PageListCacheEntry(expiresAt, value);
        return value;
    }

    public async Task<string?> ResolveCanonicalAsync(
        string pathname,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathname);

        var path = $"api/article/meta?pathname={Uri.EscapeDataString(pathname)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, path, treatNotFoundAsArticleMissing: false, cancellationToken).ConfigureAwait(false);

        var meta = await response.Content
            .ReadFromJsonAsync<ArticleMetaResponse>(s_jsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (meta is null)
        {
            return null;
        }

        return !string.IsNullOrWhiteSpace(meta.RedirectedFrom)
            ? meta.RedirectedFrom
            : meta.Canonical;
    }

    public async Task<string> GetArticleBodyAsync(
        string pathname,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathname);

        var path = $"api/article/body?pathname={Uri.EscapeDataString(pathname)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, path, treatNotFoundAsArticleMissing: true, cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureUserAgent()
    {
        var version = typeof(DocsApiClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(DocsApiClient).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";

        // Informational versions can carry build metadata ("+git.sha"); strip it because
        // ProductInfoHeaderValue rejects '+'.
        var plus = version.IndexOf('+');
        if (plus >= 0)
        {
            version = version[..plus];
        }

        var header = new ProductInfoHeaderValue(_options.ClientName, version);
        if (!_http.DefaultRequestHeaders.UserAgent.Contains(header))
        {
            _http.DefaultRequestHeaders.UserAgent.Add(header);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string requestPath,
        bool treatNotFoundAsArticleMissing,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        if (treatNotFoundAsArticleMissing && response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new DocsArticleNotFoundException(requestPath, body);
        }

        throw new DocsApiException(response.StatusCode, requestPath, body);
    }

    private static async Task<string?> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private sealed record PageListCacheEntry(DateTimeOffset ExpiresAt, IReadOnlyList<string> Value);

    private sealed record ArticleMetaResponse(
        [property: JsonPropertyName("canonical")] string? Canonical,
        [property: JsonPropertyName("redirectedFrom")] string? RedirectedFrom);
}
