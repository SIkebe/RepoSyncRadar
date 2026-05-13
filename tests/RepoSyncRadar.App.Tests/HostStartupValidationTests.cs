using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core;
using Xunit;

namespace RepoSyncRadar.App.Tests;

public class HostStartupValidationTests
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
        "AllowedUrlHosts": [ "docs.github.com" ]
      }
    }
    """;

    [Fact]
    public async Task StartAsync_ValidConfiguration_StartsHostSuccessfully()
    {
        using var host = BuildHost(ValidJson);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_InvalidGitHubOwner_ThrowsOptionsValidationException()
    {
        var json = ValidJson.Replace("\"Owner\": \"github\"", "\"Owner\": \"\"", StringComparison.Ordinal);
        using var host = BuildHost(json);

        await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));
    }

    private static IHost BuildHost(string json)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        builder.Configuration.AddJsonStream(stream);
        builder.Services.AddRepoSyncRadarOptions();

        return builder.Build();
    }
}
