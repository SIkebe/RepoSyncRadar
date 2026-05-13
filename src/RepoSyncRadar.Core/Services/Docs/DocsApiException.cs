using System.Net;

namespace RepoSyncRadar.Core.Services.Docs;

/// <summary>
/// Thrown when <see cref="DocsApiClient"/> receives an unexpected non-success response from
/// <c>docs.github.com</c>. The original status code and response body are preserved on the
/// exception so callers can decide whether to retry or surface to the user.
/// </summary>
public class DocsApiException : Exception
{
    public DocsApiException(HttpStatusCode statusCode, string requestPath, string? responseBody)
        : base($"docs.github.com returned {(int)statusCode} for '{requestPath}'.")
    {
        StatusCode = statusCode;
        RequestPath = requestPath;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string RequestPath { get; }

    public string? ResponseBody { get; }
}

/// <summary>
/// Thrown when a docs article is missing (HTTP 404). A dedicated subtype lets calling code
/// branch on "the path simply does not exist" vs. "the API is unhealthy".
/// </summary>
public sealed class DocsArticleNotFoundException : DocsApiException
{
    public DocsArticleNotFoundException(string requestPath, string? responseBody)
        : base(HttpStatusCode.NotFound, requestPath, responseBody)
    {
    }
}
