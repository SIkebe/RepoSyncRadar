using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="CommitDetail"/>. Verifies that the +/- stats appear next
/// to each file and that resolver-returned URLs are rendered as anchors.
/// </summary>
public class CommitDetailTests
{
    private static readonly string[] CopilotAboutUrls =
    [
        "/en/copilot/about-copilot",
        "/en/enterprise-cloud@latest/copilot/about-copilot",
    ];

    [Fact]
    public void CommitDetail_Shows_Resolved_Urls()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));

        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync("content/copilot/about-copilot.md", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(CopilotAboutUrls));

        using var cut = RenderDetailWith(commit, resolver);

        var anchors = cut.FindAll("[data-testid=\"commit-detail-url\"]");
        Assert.Equal(2, anchors.Count);
        Assert.Equal("/en/copilot/about-copilot", anchors[0].GetAttribute("href"));
        Assert.Equal(
            "/en/enterprise-cloud@latest/copilot/about-copilot",
            anchors[1].GetAttribute("href"));
    }

    [Fact]
    public void CommitDetail_Shows_File_Stats()
    {
        var commit = MakeCommit(
            ("content/copilot/about-copilot.md", 42, 5),
            ("content/copilot/other.md", 0, 3));

        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        using var cut = RenderDetailWith(commit, resolver);

        Assert.Equal(
            "+42 -5",
            cut.Find("[data-testid=\"commit-detail-stats-content/copilot/about-copilot.md\"]").TextContent);
        Assert.Equal(
            "+0 -3",
            cut.Find("[data-testid=\"commit-detail-stats-content/copilot/other.md\"]").TextContent);
    }

    [Fact]
    public void CommitDetail_Shows_Scoring_When_Present()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));
        commit.Scoring = new Scoring
        {
            Sha = commit.Sha,
            Score = 0.72,
            Category = "feature-update",
            AudienceJson = "[\"devrel\",\"customer\"]",
            SummaryJa = "Copilot Workspace の挙動を明確化する変更。",
            WhyJa = "公式 docs の更新で、顧客向け説明にも影響するため重要。",
            Model = "gpt-5",
            ScoredAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
        };

        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        using var cut = RenderDetailWith(commit, resolver);

        var score = cut.Find("[data-testid=\"commit-detail-score\"]");
        Assert.Contains("0.72", score.TextContent);
        Assert.Equal("feature-update", cut.Find("[data-testid=\"commit-detail-category\"]").TextContent);
        var audience = cut.Find("[data-testid=\"commit-detail-audience\"]").TextContent;
        Assert.Contains("devrel", audience);
        Assert.Contains("customer", audience);
        Assert.Equal(
            "Copilot Workspace の挙動を明確化する変更。",
            cut.Find("[data-testid=\"commit-detail-summary\"]").TextContent);
        Assert.Equal(
            "公式 docs の更新で、顧客向け説明にも影響するため重要。",
            cut.Find("[data-testid=\"commit-detail-why\"]").TextContent);
    }

    [Fact]
    public void CommitDetail_Shows_Unscored_Hint_When_Scoring_Missing()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));
        Assert.Null(commit.Scoring);

        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        using var cut = RenderDetailWith(commit, resolver);

        var hint = cut.Find("[data-testid=\"commit-detail-unscored\"]");
        Assert.Contains("未スコアリング", hint.TextContent);
        Assert.Empty(cut.FindAll("[data-testid=\"commit-detail-score\"]"));
    }

    [Fact]
    public void CommitDetail_Handles_Malformed_Audience_Json()
    {
        var commit = MakeCommit(("content/copilot/x.md", 1, 0));
        commit.Scoring = new Scoring
        {
            Sha = commit.Sha,
            Score = 0.5,
            Category = "doc-fix",
            AudienceJson = "not-json",
            SummaryJa = "summary",
            WhyJa = "why",
            Model = "gpt-5",
            ScoredAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
        };

        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        using var cut = RenderDetailWith(commit, resolver);

        // The audience element is omitted entirely when AudienceJson cannot be parsed.
        Assert.Empty(cut.FindAll("[data-testid=\"commit-detail-audience\"]"));
        Assert.Equal("doc-fix", cut.Find("[data-testid=\"commit-detail-category\"]").TextContent);
    }

    [Fact]
    public void OpenInWebView_Button_Hidden_When_No_Mapping_And_No_Resolved_Url()
    {
        // GraphQL schema file → not a content/*.md page, resolver also returns nothing.
        var commit = MakeCommit(("src/graphql/data/fpt/schema.docs.graphql", 3, 3));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        using var cut = RenderDetailWith(commit, resolver);

        Assert.Empty(cut.FindAll("[data-testid=\"commit-detail-open-in-webview\"]"));
    }

    [Fact]
    public void OpenInWebView_Falls_Back_To_Official_Url_When_Preview_Inactive()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync("content/copilot/about-copilot.md", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(CopilotAboutUrls));

        var navigator = new PreviewNavigator();
        Uri? captured = null;
        navigator.Requested += (_, url) => captured = url;
        var session = new PreviewSession(); // inactive (no Activate call)

        using var cut = RenderDetailWith(commit, resolver, navigator, session);
        cut.Find("[data-testid=\"commit-detail-open-in-webview\"]").Click();

        Assert.NotNull(captured);
        // Resolver returns a relative path; CommitDetail absolutises it against
        // DocsApiOptions.BaseAddress so the WebView2 receives a fully-qualified URL.
        Assert.Equal(
            "https://docs.github.com/en/copilot/about-copilot",
            captured!.AbsoluteUri);
    }

    [Fact]
    public void OpenInWebView_Rewrites_To_Localhost_When_Preview_Active()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var navigator = new PreviewNavigator();
        Uri? captured = null;
        navigator.Requested += (_, url) => captured = url;
        var session = new PreviewSession();
        session.Activate(4500);

        using var cut = RenderDetailWith(commit, resolver, navigator, session);
        cut.Find("[data-testid=\"commit-detail-open-in-webview\"]").Click();

        Assert.NotNull(captured);
        Assert.Equal("http://localhost:4500/en/copilot/about-copilot", captured!.AbsoluteUri);
    }

    private static IRenderedComponent<CommitDetail> RenderDetailWith(
        Commit commit,
        IPathToUrlResolver resolver)
        => RenderDetailWith(commit, resolver, navigator: null, session: null);

    private static IRenderedComponent<CommitDetail> RenderDetailWith(
        Commit commit,
        IPathToUrlResolver resolver,
        IPreviewNavigator? navigator,
        PreviewSession? session)
    {
        var services = new ServiceCollection().AddSingleton(resolver);
        if (navigator is not null)
        {
            services.AddSingleton<IPreviewNavigator>(navigator);
        }
        if (session is not null)
        {
            services.AddSingleton(session);
        }
        // Wire the docs base address so CommitDetail can absolutise the relative
        // paths returned by IPathToUrlResolver — matches the production DI setup.
        services.AddSingleton<IOptions<DocsApiOptions>>(
            Options.Create(new DocsApiOptions
            {
                BaseAddress = new Uri("https://docs.github.com/"),
            }));
        var sp = services.BuildServiceProvider();

        var ctx = new Bunit.TestContext();
        return ctx.RenderComponent<CommitDetail>(parameters => parameters
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(p => p.Commit, commit));
    }

    private static Commit MakeCommit(params (string Path, int Additions, int Deletions)[] files)
    {
        var commit = new Commit
        {
            Sha = "feedfacefeedfacefeedfacefeedfacefeedface",
            PrNumber = 1,
            Message = "Repo sync",
            Author = "octocat",
            AuthoredAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
        };
        foreach (var (path, additions, deletions) in files)
        {
            commit.Files.Add(new CommitFile
            {
                Sha = commit.Sha,
                Path = path,
                Status = "modified",
                Additions = additions,
                Deletions = deletions,
            });
        }
        return commit;
    }
}
