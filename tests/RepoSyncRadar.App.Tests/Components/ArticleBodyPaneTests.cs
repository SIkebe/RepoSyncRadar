using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Services;
using RepoSyncRadar.Core.Services.Docs;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="ArticleBodyPane"/>. Mode B from DESIGN.md §9.3:
/// the pane fetches rendered HTML via <see cref="IDocsApiClient.GetArticleBodyAsync"/>
/// and embeds it into an <c>&lt;iframe srcdoc&gt;</c>. A 404 (<see cref="DocsArticleNotFoundException"/>)
/// must surface as a visible error message rather than an iframe.
/// </summary>
public class ArticleBodyPaneTests
{
    private const string Pathname = "/en/copilot/about-copilot";

    [Fact]
    public void Renders_Iframe_With_Srcdoc()
    {
        const string articleHtml = "<h1>About Copilot</h1>";
        var apiClient = Substitute.For<IDocsApiClient>();
        apiClient
            .GetArticleBodyAsync(Pathname, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(articleHtml));

        using var cut = RenderPaneWith(apiClient, Pathname);

        cut.WaitForAssertion(() =>
        {
            var iframe = cut.Find("iframe[data-testid=\"article-body-iframe\"]");
            Assert.Equal(articleHtml, iframe.GetAttribute("srcdoc"));
        });

        Assert.Empty(cut.FindAll("[data-testid=\"article-body-error\"]"));
    }

    [Fact]
    public void Shows_Error_On_404()
    {
        var apiClient = Substitute.For<IDocsApiClient>();
        apiClient
            .GetArticleBodyAsync(Pathname, Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => Task.FromException<string>(
                new DocsArticleNotFoundException(Pathname, responseBody: null)));

        using var cut = RenderPaneWith(apiClient, Pathname);

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find("[data-testid=\"article-body-error\"]");
            Assert.Contains("404", error.TextContent);
        });

        Assert.Empty(cut.FindAll("iframe[data-testid=\"article-body-iframe\"]"));
    }

    private static IRenderedComponent<ArticleBodyPane> RenderPaneWith(
        IDocsApiClient apiClient,
        string pathname)
    {
        var sp = new ServiceCollection()
            .AddSingleton(apiClient)
            .BuildServiceProvider();

        var ctx = new Bunit.TestContext();
        return ctx.RenderComponent<ArticleBodyPane>(parameters => parameters
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(p => p.Pathname, pathname));
    }
}
