using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Options;

public class OptionsValidationTests
{
    private const string ValidJson = """
    {
      "GitHub": {
        "Owner": "github",
        "Repo": "docs",
        "PullRequestTitleFilter": "Repo sync",
        "MaxPullRequests": 5
      },
      "DocsApi": {
        "BaseAddress": "https://docs.github.com/",
        "DefaultLanguage": "en",
        "ClientName": "reposyncradar",
        "PageListCacheSeconds": 86400
      },
      "Copilot": {
        "DefaultModel": "gpt-5",
        "Streaming": true,
        "CaptureContent": false,
        "AllowedUrlHosts": [ "docs.github.com", "api.github.com" ]
      }
    }
    """;

    [Fact]
    public void Bind_ValidConfiguration_PassesValidation()
    {
        using var sp = BuildServiceProvider(ValidJson);

        var github = sp.GetRequiredService<IOptions<GitHubOptions>>().Value;
        var docs = sp.GetRequiredService<IOptions<DocsApiOptions>>().Value;
        var copilot = sp.GetRequiredService<IOptions<CopilotOptions>>().Value;

        Assert.Equal("github", github.Owner);
        Assert.Null(github.PullRequestCreatedAtOrAfter);
        Assert.Equal(new Uri("https://docs.github.com/"), docs.BaseAddress);
        Assert.Equal("gpt-5", copilot.DefaultModel);
    }

    [Fact]
    public void Bind_GitHubPullRequestCreatedAtOrAfter_BindsIsoTimestamp()
    {
        var json = ValidJson.Replace(
            "\"MaxPullRequests\": 5",
            "\"MaxPullRequests\": 5,\n        \"PullRequestCreatedAtOrAfter\": \"2026-05-15T00:00:00Z\"",
            StringComparison.Ordinal);
        using var sp = BuildServiceProvider(json);

        var github = sp.GetRequiredService<IOptions<GitHubOptions>>().Value;

        Assert.Equal(
            new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero),
            github.PullRequestCreatedAtOrAfter);
    }

    [Fact]
    public void Bind_GitHubOwnerEmpty_ThrowsOptionsValidationException()
    {
        var json = ValidJson.Replace("\"Owner\": \"github\"", "\"Owner\": \"\"", StringComparison.Ordinal);
        using var sp = BuildServiceProvider(json);

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = sp.GetRequiredService<IOptions<GitHubOptions>>().Value);

        Assert.Contains("Owner", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_DocsApiBaseAddressHttp_ThrowsOptionsValidationException()
    {
        var json = ValidJson.Replace(
            "\"BaseAddress\": \"https://docs.github.com/\"",
            "\"BaseAddress\": \"http://docs.github.com/\"",
            StringComparison.Ordinal);
        using var sp = BuildServiceProvider(json);

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = sp.GetRequiredService<IOptions<DocsApiOptions>>().Value);

        Assert.Contains("BaseAddress", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_AllowedUrlHosts_AreNormalizedLowercaseAndDeduplicated()
    {
        var json = ValidJson.Replace(
            "\"AllowedUrlHosts\": [ \"docs.github.com\", \"api.github.com\" ]",
            "\"AllowedUrlHosts\": [ \"Docs.GitHub.com\", \"API.github.com\", \"docs.github.com\" ]",
            StringComparison.Ordinal);
        using var sp = BuildServiceProvider(json);

        var copilot = sp.GetRequiredService<IOptions<CopilotOptions>>().Value;

        Assert.Equal(2, copilot.AllowedUrlHosts.Count);
        Assert.Contains("docs.github.com", copilot.AllowedUrlHosts);
        Assert.Contains("api.github.com", copilot.AllowedUrlHosts);
        Assert.All(copilot.AllowedUrlHosts, host => Assert.Equal(host, host.ToLowerInvariant()));
    }

    [Fact]
    public void WebView_DefaultAllowedHosts_Include_GitHubCopilotChatApis()
    {
        using var sp = BuildServiceProvider(ValidJson);

        var webView = sp.GetRequiredService<IOptions<WebViewOptions>>().Value;

        Assert.Contains("api.githubcopilot.com", webView.AllowedUrlHosts);
        Assert.Contains("api.business.githubcopilot.com", webView.AllowedUrlHosts);
        Assert.Contains("api.enterprise.githubcopilot.com", webView.AllowedUrlHosts);
    }

    [Fact]
    public void Bind_OAuthScopes_AreNormalizedAndDeduplicated()
    {
        // OAuth scope strings are case-sensitive at the IdP, but GitHub's documentation
        // canonicalizes them as lowercase (e.g. "read:user"). Normalizing keeps the
        // device-flow request body stable regardless of how a contributor types them
        // into appsettings.json.
        var json = ValidJson.Replace(
            "\"AllowedUrlHosts\": [ \"docs.github.com\", \"api.github.com\" ]",
            "\"AllowedUrlHosts\": [ \"docs.github.com\" ],\n    \"OAuthClientId\": \"  Iv1.test  \",\n    \"OAuthScopes\": [ \"Read:User\", \" read:user \", \"\" ]",
            StringComparison.Ordinal);
        using var sp = BuildServiceProvider(json);

        var copilot = sp.GetRequiredService<IOptions<CopilotOptions>>().Value;

        Assert.Equal("Iv1.test", copilot.OAuthClientId);
        Assert.Equal(["read:user"], copilot.OAuthScopes);
    }

    [Fact]
    public void Bind_OAuthClientIdWhitespace_BecomesNull()
    {
        var json = ValidJson.Replace(
            "\"AllowedUrlHosts\": [ \"docs.github.com\", \"api.github.com\" ]",
            "\"AllowedUrlHosts\": [ \"docs.github.com\" ],\n    \"OAuthClientId\": \"   \"",
            StringComparison.Ordinal);
        using var sp = BuildServiceProvider(json);

        var copilot = sp.GetRequiredService<IOptions<CopilotOptions>>().Value;

        Assert.Null(copilot.OAuthClientId);
    }

    [Fact]
    public void Bind_CopilotSdkOptions_AreTrimmedAndNormalized()
    {
        var json = ValidJson.Replace(
            "\"Streaming\": true,",
            "\"Streaming\": true,\n    \"LogLevel\": \" Debug \",\n    \"SessionIdleTimeoutSeconds\": 90,\n    \"CopilotHome\": \" C:/data/copilot \",\n    \"TelemetryFilePath\": \" C:/logs/copilot.jsonl \",",
            StringComparison.Ordinal);
        using var sp = BuildServiceProvider(json);

        var copilot = sp.GetRequiredService<IOptions<CopilotOptions>>().Value;

        Assert.Equal("debug", copilot.LogLevel);
        Assert.Equal(90, copilot.SessionIdleTimeoutSeconds);
        Assert.Equal("C:/data/copilot", copilot.CopilotHome);
        Assert.Equal("C:/logs/copilot.jsonl", copilot.TelemetryFilePath);
    }

    [Fact]
    public void Bind_CopilotDefaultModelEmpty_ThrowsOptionsValidationException()
    {
        var json = ValidJson.Replace("\"DefaultModel\": \"gpt-5\"", "\"DefaultModel\": \"\"", StringComparison.Ordinal);
        using var sp = BuildServiceProvider(json);

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = sp.GetRequiredService<IOptions<CopilotOptions>>().Value);

        Assert.Contains("DefaultModel", ex.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider BuildServiceProvider(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddRepoSyncRadarOptions();
        return services.BuildServiceProvider();
    }
}
