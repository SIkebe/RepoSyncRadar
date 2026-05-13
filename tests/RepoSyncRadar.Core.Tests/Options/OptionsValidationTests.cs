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
        Assert.Equal(new Uri("https://docs.github.com/"), docs.BaseAddress);
        Assert.Equal("gpt-5", copilot.DefaultModel);
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
