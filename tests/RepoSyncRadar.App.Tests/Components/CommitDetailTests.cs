using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.IO;
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
    private static readonly string[] _copilotAboutUrls =
    [
        "/en/copilot/about-copilot",
        "/en/enterprise-cloud@latest/copilot/about-copilot",
    ];

    [Theory]
    [InlineData(17, 17, 17)]
    [InlineData(17, 18, 18)]
    [InlineData(17, 24, 18)]
    [InlineData(0, 9, 1)]
    public void SmoothElapsedSeconds_Advances_At_Most_One_Second_Per_Render(
        int displayed,
        int actual,
        int expected)
    {
        Assert.Equal(expected, ProgressElapsedDisplay.SmoothElapsedSeconds(displayed, actual));
    }

    [Fact]
    public void CommitDetail_Shows_Resolved_Urls()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));

        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync("content/copilot/about-copilot.md", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(_copilotAboutUrls));

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
    public void CommitDetail_Formats_Header_For_Scannability()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        using var cut = RenderDetailWith(commit, resolver);

        var sha = cut.Find("[data-testid=\"commit-detail-sha\"]");
        Assert.Equal("feedfac", sha.TextContent);
        Assert.Equal(commit.Sha, sha.GetAttribute("title"));
        Assert.Equal("Repo sync", cut.Find("[data-testid=\"commit-detail-message\"]").TextContent);
    }

    [Fact]
    public void CommitDetail_Shows_Only_Useful_Commit_Message_Line()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));
        commit.Message = "Update authentication documentation for token formats\n\nCo-authored-by: Copilot <copilot@users.noreply.github.com>\nSigned-off-by: octocat <octocat@example.com>";
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        using var cut = RenderDetailWith(commit, resolver);

        var message = cut.Find("[data-testid=\"commit-detail-message\"]");
        Assert.Equal("p", message.TagName.ToLowerInvariant());
        Assert.Equal("Update authentication documentation for token formats", message.TextContent);
        Assert.DoesNotContain("Co-authored-by", message.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Signed-off-by", message.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommitDetail_Renders_Pull_Request_Actions_To_Commit_Diff()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));
        commit.PrNumber = 12345;
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));
        var navigator = new PreviewNavigator();
        Uri? captured = null;
        navigator.Requested += (_, url) => captured = url;

        using var cut = RenderDetailWith(commit, resolver, navigator, new PreviewSession());

        var button = cut.Find("[data-testid=\"commit-detail-open-pr\"]");
        Assert.Equal("PR #12345", button.TextContent);
        button.Click();
        Assert.NotNull(captured);
        Assert.Equal("https://github.com/github/docs/commit/feedfacefeedfacefeedfacefeedfacefeedface", captured!.AbsoluteUri);

        var external = cut.Find("[data-testid=\"commit-detail-open-pr-external\"]");
        Assert.Equal("https://github.com/github/docs/commit/feedfacefeedfacefeedfacefeedfacefeedface", external.GetAttribute("href"));
        Assert.Equal("_blank", external.GetAttribute("target"));
        Assert.Equal("noopener", external.GetAttribute("rel"));
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
            DetailsJa = "変更内容: Copilot Workspace の説明を具体化。\n根拠: about-copilot.md の本文差分。\n影響: 顧客説明と DevRel 共有に利用可能。\n確認観点: 既存 GA 表現と矛盾しないか確認。",
            Model = "gpt-5",
            ScoredAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
        };

        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        using var cut = RenderDetailWith(commit, resolver);

        Assert.NotNull(cut.Find(".score-tile"));
        Assert.Equal("スコア", cut.Find(".score-label").TextContent);
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
        var details = cut.Find("[data-testid=\"commit-detail-details\"]").TextContent;
        Assert.Contains("変更内容", details, StringComparison.Ordinal);
        Assert.Contains("根拠", details, StringComparison.Ordinal);
        Assert.Contains("確認観点", details, StringComparison.Ordinal);
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
            DetailsJa = string.Empty,
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
        Assert.Empty(cut.FindAll("[data-testid=\"commit-detail-details\"]"));
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
            .Returns(Task.FromResult<IReadOnlyList<string>>(_copilotAboutUrls));

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
    public void OpenInWebView_Starts_Local_Preview_For_Mappable_File_When_Inactive()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var navigator = new PreviewNavigator();
        PreviewComparisonRequest? captured = null;
        navigator.ComparisonRequested += (_, request) => captured = request;
        var session = new PreviewSession();
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareMarkdownComparisonPreviewAsync(
                commit.PrNumber,
                commit.Sha,
                "content/copilot/about-copilot.md",
                Arg.Any<IProgress<string>?>(),
                Arg.Any<DocsVersion?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PreviewComparisonLink?>(new PreviewComparisonLink(
                new Uri("http://localhost:4501/en/copilot/about-copilot"),
                new Uri("http://localhost:4500/en/copilot/about-copilot"),
                4501,
                4500,
                "parent1234567890",
                commit.Sha)));

        using var cut = RenderDetailWith(commit, resolver, navigator, session, coordinator);
        cut.Find("[data-testid=\"commit-detail-open-in-webview\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("http://localhost:4501/en/copilot/about-copilot", captured?.BeforeUrl.AbsoluteUri);
            Assert.Equal("http://localhost:4500/en/copilot/about-copilot", captured?.AfterUrl.AbsoluteUri);
            Assert.Equal("content/copilot/about-copilot.md", captured?.FilePath);
            Assert.Equal(1, captured?.FileOrdinal);
            Assert.Equal(1, captured?.FileCount);
            Assert.Contains(
                "content/copilot/about-copilot.md",
                cut.Find("[data-testid=\"commit-detail-preview-status\"]").TextContent,
                StringComparison.Ordinal);
            Assert.Contains(
                "content/copilot/about-copilot.md",
                cut.Find("[data-testid=\"commit-detail-file\"][data-path=\"content/copilot/about-copilot.md\"] [data-testid=\"commit-detail-preview-status\"]").TextContent,
                StringComparison.Ordinal);
        });
        _ = coordinator.Received(1).PrepareMarkdownComparisonPreviewAsync(
            commit.PrNumber,
            commit.Sha,
            "content/copilot/about-copilot.md",
            Arg.Any<IProgress<string>?>(),
            Arg.Any<DocsVersion?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OpenInWebView_Starts_Markdown_Comparison_For_Non_Content_Markdown_File()
    {
        var commit = MakeCommit(("CHANGELOG.md", 4, 1));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var navigator = new PreviewNavigator();
        PreviewComparisonRequest? captured = null;
        navigator.ComparisonRequested += (_, request) => captured = request;
        var session = new PreviewSession();
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareMarkdownComparisonPreviewAsync(
                commit.PrNumber,
                commit.Sha,
                "CHANGELOG.md",
                Arg.Any<IProgress<string>?>(),
                Arg.Any<DocsVersion?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PreviewComparisonLink?>(new PreviewComparisonLink(
                new Uri("http://127.0.0.1:4500/markdown/before"),
                new Uri("http://127.0.0.1:4500/markdown/after"),
                4500,
                4500,
                "parent1234567890",
                commit.Sha)));

        using var cut = RenderDetailWith(commit, resolver, navigator, session, coordinator);
        cut.Find("[data-testid=\"commit-detail-open-in-webview\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("http://127.0.0.1:4500/markdown/before", captured?.BeforeUrl.AbsoluteUri);
            Assert.Equal("http://127.0.0.1:4500/markdown/after", captured?.AfterUrl.AbsoluteUri);
            Assert.Equal("CHANGELOG.md", captured?.FilePath);
            Assert.Equal(1, captured?.FileOrdinal);
            Assert.Equal(1, captured?.FileCount);
            Assert.Contains("Markdown", captured?.BeforeLabel, StringComparison.Ordinal);
            Assert.Contains("Markdown", cut.Find("[data-testid=\"commit-detail-preview-status\"]").TextContent, StringComparison.Ordinal);
        });
        _ = coordinator.Received(1).PrepareMarkdownComparisonPreviewAsync(
            commit.PrNumber,
            commit.Sha,
            "CHANGELOG.md",
            Arg.Any<IProgress<string>?>(),
            Arg.Any<DocsVersion?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OpenInWebView_Shows_Progress_And_Cancel_While_Local_Preview_Starts()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var tcs = new TaskCompletionSource<PreviewComparisonLink?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken receivedToken = default;
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareMarkdownComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<DocsVersion?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                receivedToken = call.ArgAt<CancellationToken>(5);
                receivedToken.Register(() =>
                    tcs.TrySetException(new OperationCanceledException(receivedToken)));
                return tcs.Task;
            });

        using var cut = RenderDetailWith(
            commit,
            resolver,
            new PreviewNavigator(),
            new PreviewSession(),
            coordinator);
        cut.Find("[data-testid=\"commit-detail-open-in-webview\"]").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=\"commit-detail-file\"][data-path=\"content/copilot/about-copilot.md\"] [data-testid=\"commit-detail-preview-progress\"]");
            cut.Find("[data-testid=\"commit-detail-preview-cancel-button\"]");
            Assert.Contains("経過", cut.Find("[data-testid=\"commit-detail-preview-progress-elapsed\"]").TextContent, StringComparison.Ordinal);
        });

        cut.Find("[data-testid=\"commit-detail-preview-cancel-button\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(receivedToken.IsCancellationRequested);
            Assert.Contains("中止", cut.Find("[data-testid=\"commit-detail-preview-status\"]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task OpenInWebView_Does_Not_Publish_Stale_Preview_When_Commit_Changes()
    {
        var first = MakeCommitWithSha("1111111111111111111111111111111111111111", ("content/copilot/about-copilot.md", 1, 0));
        var second = MakeCommitWithSha("2222222222222222222222222222222222222222", ("content/copilot/other.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var pending = new TaskCompletionSource<PreviewComparisonLink?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinatorReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareMarkdownComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<DocsVersion?>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var link = await pending.Task.ConfigureAwait(false);
                coordinatorReturned.TrySetResult();
                return link;
            });
        var navigator = new PreviewNavigator();
        var publishCount = 0;
        navigator.ComparisonRequested += (_, _) => publishCount++;

        using var cut = RenderDetailWith(first, resolver, navigator, new PreviewSession(), coordinator);
        cut.Find("[data-testid=\"commit-detail-open-in-webview\"]").Click();
        cut.WaitForAssertion(() =>
            cut.Find("[data-testid=\"commit-detail-preview-progress\"]"));

        cut.Render(parameters => parameters.Add(p => p.Commit, second));
        pending.SetResult(MakeComparisonLinkFor(first, "content/copilot/about-copilot.md"));

        cut.WaitForAssertion(() => Assert.True(coordinatorReturned.Task.IsCompleted));
        await Task.Delay(50, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(0, publishCount);
    }

    [Fact]
    public void OpenInWebView_Cancels_Previous_Commit_Preview_When_New_Preview_Starts()
    {
        var first = MakeCommitWithSha("1111111111111111111111111111111111111111", ("content/copilot/about-copilot.md", 1, 0));
        var second = MakeCommitWithSha("2222222222222222222222222222222222222222", ("content/copilot/other.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var firstPending = new TaskCompletionSource<PreviewComparisonLink?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareMarkdownComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<DocsVersion?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var sha = call.ArgAt<string>(1);
                if (string.Equals(sha, first.Sha, StringComparison.Ordinal))
                {
                    firstToken = call.ArgAt<CancellationToken>(5);
                    firstToken.Register(() =>
                        firstPending.TrySetException(new OperationCanceledException(firstToken)));
                    return firstPending.Task;
                }

                return Task.FromResult<PreviewComparisonLink?>(MakeComparisonLinkFor(second, "content/copilot/other.md"));
            });
        var navigator = new PreviewNavigator();
        PreviewComparisonRequest? captured = null;
        navigator.ComparisonRequested += (_, request) => captured = request;

        using var cut = RenderDetailWith(first, resolver, navigator, new PreviewSession(), coordinator);
        cut.Find("[data-testid=\"commit-detail-open-in-webview\"]").Click();
        cut.WaitForAssertion(() => Assert.True(firstToken.CanBeCanceled));

        cut.Render(parameters => parameters.Add(p => p.Commit, second));
        var secondButton = cut.Find("[data-testid=\"commit-detail-open-in-webview\"]");
        Assert.False(secondButton.HasAttribute("disabled"));
        secondButton.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(firstToken.IsCancellationRequested);
            Assert.Equal("content/copilot/other.md", captured?.FilePath);
            Assert.Contains("2222222", captured?.AfterLabel, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void OpenInWebView_When_Comparison_Disabled_Shows_Status()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var navigator = new PreviewNavigator();
        var session = new PreviewSession();
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareMarkdownComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<DocsVersion?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PreviewComparisonLink?>(null));

        using var cut = RenderDetailWith(commit, resolver, navigator, session, coordinator);
        cut.Find("[data-testid=\"commit-detail-open-in-webview\"]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains(
                "プレビュー機能は無効",
                cut.Find("[data-testid=\"commit-detail-file\"][data-path=\"content/copilot/about-copilot.md\"] [data-testid=\"commit-detail-preview-status\"]").TextContent,
                StringComparison.Ordinal));
    }

    [Fact]
    public void CleanupPreviewCache_Calls_Coordinator_And_Shows_Count()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.CleanupCacheAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(3));

        using var cut = RenderDetailWith(
            commit,
            resolver,
            new PreviewNavigator(),
            new PreviewSession(),
            coordinator);
        cut.Find("[data-testid=\"commit-detail-preview-cleanup-button\"]").Click();

        cut.WaitForAssertion(() =>
        {
            coordinator.Received(1).CleanupCacheAsync(Arg.Any<CancellationToken>());
            Assert.Contains(
                "3 件",
                cut.Find("[data-testid=\"commit-detail-preview-cleanup-status\"]").TextContent,
                StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=\"commit-detail-preview-status\"]"));
        });
    }

    [Fact]
    public void CleanupPreviewCache_Shows_Progress_And_Can_Cancel()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken receivedToken = default;
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.CleanupCacheAsync(Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                receivedToken = call.ArgAt<CancellationToken>(0);
                receivedToken.Register(() =>
                    tcs.TrySetException(new OperationCanceledException(receivedToken)));
                return tcs.Task;
            });

        using var cut = RenderDetailWith(
            commit,
            resolver,
            new PreviewNavigator(),
            new PreviewSession(),
            coordinator);
        cut.Find("[data-testid=\"commit-detail-preview-cleanup-button\"]").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=\"commit-detail-preview-cleanup-progress\"]");
            Assert.Contains(
                "キャッシュをクリーンアップ中",
                cut.Find("[data-testid=\"commit-detail-preview-cleanup-progress-text\"]").TextContent,
                StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=\"commit-detail-preview-progress\"]"));
        });

        cut.Find("[data-testid=\"commit-detail-preview-cleanup-cancel-button\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(receivedToken.IsCancellationRequested);
            Assert.Contains(
                "キャッシュ削除を中止",
                cut.Find("[data-testid=\"commit-detail-preview-cleanup-status\"]").TextContent,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void VersionChangeRequested_Reruns_Active_File_With_New_Version()
    {
        var commit = MakeCommit(("content/foo/bar.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var fpt = DocsVersionCatalog.All.First(version => version.Slug == "fpt");
        var ghec = DocsVersionCatalog.All.First(version => version.Slug == "ghec");
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareMarkdownComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<DocsVersion?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var requestedVersion = call.ArgAt<DocsVersion?>(4) ?? fpt;
                return Task.FromResult<PreviewComparisonLink?>(MakeComparisonLinkFor(commit, "content/foo/bar.md") with
                {
                    CurrentVersion = requestedVersion,
                    AffectedVersions = [fpt, ghec],
                });
            });
        var navigator = new PreviewNavigator();

        using var cut = RenderDetailWith(commit, resolver, navigator, new PreviewSession(), coordinator);
        cut.Find("[data-testid=\"commit-detail-open-in-webview\"]").Click();
        cut.WaitForAssertion(() =>
        {
            coordinator.Received(1).PrepareMarkdownComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Is<DocsVersion?>(version => version == null),
                Arg.Any<CancellationToken>());
        });
        cut.WaitForAssertion(() =>
            Assert.Contains("bar.md", cut.Find("[data-testid=\"commit-detail-preview-status\"]").TextContent, StringComparison.Ordinal));

        navigator.RequestVersionChange(ghec);

        cut.WaitForAssertion(() =>
        {
            coordinator.Received(1).PrepareMarkdownComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Is<DocsVersion?>(version => version != null && version.Slug == "ghec"),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public void VersionChangeRequested_Same_Version_Does_Not_Rerun()
    {
        var commit = MakeCommit(("content/foo/bar.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var fpt = DocsVersionCatalog.All.First(version => version.Slug == "fpt");
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareMarkdownComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<DocsVersion?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PreviewComparisonLink?>(MakeComparisonLinkFor(commit, "content/foo/bar.md") with
            {
                CurrentVersion = fpt,
                AffectedVersions = [fpt],
            }));
        var navigator = new PreviewNavigator();

        using var cut = RenderDetailWith(commit, resolver, navigator, new PreviewSession(), coordinator);
        cut.Find("[data-testid=\"commit-detail-open-in-webview\"]").Click();
        cut.WaitForAssertion(() =>
            coordinator.Received(1).PrepareMarkdownComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<DocsVersion?>(),
                Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() =>
            Assert.Contains("bar.md", cut.Find("[data-testid=\"commit-detail-preview-status\"]").TextContent, StringComparison.Ordinal));

        navigator.RequestVersionChange(fpt);

        coordinator.Received(1).PrepareMarkdownComparisonPreviewAsync(
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IProgress<string>?>(),
            Arg.Any<DocsVersion?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void FileNavigationRequested_Opens_Adjacent_Commit_File_With_Current_Version()
    {
        var commit = MakeCommit(
            ("content/copilot/first.md", 1, 0),
            ("content/copilot/second.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var fpt = DocsVersionCatalog.All.First(v => v.Slug == "fpt");
        var requested = new List<(string Path, DocsVersion? Version)>();
        var navigator = new PreviewNavigator();
        var session = new PreviewSession();
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareMarkdownComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<DocsVersion?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var path = call.ArgAt<string>(2);
                var version = call.ArgAt<DocsVersion?>(4);
                requested.Add((path, version));
                return Task.FromResult<PreviewComparisonLink?>(new PreviewComparisonLink(
                    new Uri($"http://localhost:4501/en/{Path.GetFileNameWithoutExtension(path)}"),
                    new Uri($"http://localhost:4500/en/{Path.GetFileNameWithoutExtension(path)}"),
                    4501,
                    4500,
                    "parent1234567890",
                    commit.Sha)
                {
                    CurrentVersion = version ?? fpt,
                    AffectedVersions = [fpt],
                });
            });

        using var cut = RenderDetailWith(commit, resolver, navigator, session, coordinator);
        cut.FindAll("[data-testid=\"commit-detail-open-in-webview\"]")[0].Click();
        cut.WaitForAssertion(() => Assert.Single(requested));
        cut.WaitForAssertion(() =>
            Assert.Contains("content/copilot/first.md", cut.Find("[data-testid=\"commit-detail-preview-status\"]").TextContent, StringComparison.Ordinal));

        navigator.RequestFileNavigation(PreviewFileNavigationDirection.Next);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, requested.Count);
            Assert.Equal("content/copilot/second.md", requested[1].Path);
            Assert.Equal(fpt, requested[1].Version);
        });
        cut.WaitForAssertion(() =>
            Assert.Contains("content/copilot/second.md", cut.Find("[data-testid=\"commit-detail-preview-status\"]").TextContent, StringComparison.Ordinal));

        navigator.RequestFileNavigation(PreviewFileNavigationDirection.Previous);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(3, requested.Count);
            Assert.Equal("content/copilot/first.md", requested[2].Path);
            Assert.Equal(fpt, requested[2].Version);
        });

        navigator.RequestFileNavigation((PreviewFileNavigationDirection)42);

        Assert.Equal(3, requested.Count);
    }

    [Fact]
    public void FileNavigationRequested_Without_Active_Preview_Does_Not_Run_Coordinator()
    {
        var commit = MakeCommit(
            ("content/copilot/first.md", 1, 0),
            ("content/copilot/second.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var navigator = new PreviewNavigator();
        var session = new PreviewSession();
        var coordinator = Substitute.For<IPreviewCoordinator>();

        using var cut = RenderDetailWith(commit, resolver, navigator, session, coordinator);

        navigator.RequestFileNavigation(PreviewFileNavigationDirection.Next);

        coordinator.DidNotReceive().PrepareMarkdownComparisonPreviewAsync(
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IProgress<string>?>(),
            Arg.Any<DocsVersion?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OpenInWebView_Times_Out_When_Local_Preview_Exceeds_Limit()
    {
        var commit = MakeCommit(("content/copilot/about-copilot.md", 1, 0));
        var resolver = Substitute.For<IPathToUrlResolver>();
        resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var tcs = new TaskCompletionSource<PreviewComparisonLink?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken receivedToken = default;
        var coordinator = Substitute.For<IPreviewCoordinator>();
        coordinator.PrepareMarkdownComparisonPreviewAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<DocsVersion?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                receivedToken = call.ArgAt<CancellationToken>(5);
                receivedToken.Register(() =>
                    tcs.TrySetException(new OperationCanceledException(receivedToken)));
                return tcs.Task;
            });

        using var cut = RenderDetailWith(
            commit,
            resolver,
            new PreviewNavigator(),
            new PreviewSession(),
            coordinator,
            previewReadyTimeoutSeconds: 1);
        cut.Find("[data-testid=\"commit-detail-open-in-webview\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(receivedToken.IsCancellationRequested);
            var status = cut.Find("[data-testid=\"commit-detail-preview-status\"]").TextContent;
            Assert.Contains("上限 1 秒", status, StringComparison.Ordinal);
        }, timeout: TimeSpan.FromSeconds(3));
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
        => RenderDetailWith(commit, resolver, navigator, session, coordinator: null);

    private static IRenderedComponent<CommitDetail> RenderDetailWith(
        Commit commit,
        IPathToUrlResolver resolver,
        IPreviewNavigator? navigator,
        PreviewSession? session,
        IPreviewCoordinator? coordinator,
        int previewReadyTimeoutSeconds = 600)
    {
        var services = new ServiceCollection()
            .AddSingleton(resolver)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");
        if (navigator is not null)
        {
            services.AddSingleton<IPreviewNavigator>(navigator);
        }
        if (session is not null)
        {
            services.AddSingleton(session);
        }
        if (coordinator is not null)
        {
            services.AddSingleton(coordinator);
        }
        // Wire the docs base address so CommitDetail can absolutise the relative
        // paths returned by IPathToUrlResolver — matches the production DI setup.
        services.AddSingleton<IOptions<DocsApiOptions>>(
            Options.Create(new DocsApiOptions
            {
                BaseAddress = new Uri("https://docs.github.com/"),
            }));
        services.AddSingleton<IOptions<DocsRepositoryOptions>>(
            Options.Create(new DocsRepositoryOptions
            {
                PreviewReadyTimeoutSeconds = previewReadyTimeoutSeconds,
            }));
        services.AddSingleton<IOptions<GitHubOptions>>(
            Options.Create(new GitHubOptions
            {
                Owner = "github",
                Repo = "docs",
            }));
        var sp = services.BuildServiceProvider();

        var ctx = new Bunit.BunitContext();
        return ctx.Render<CommitDetail>(parameters => parameters
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(p => p.Commit, commit));
    }

    private static PreviewComparisonLink MakeComparisonLinkFor(Commit commit, string path)
        => new(
            new Uri($"http://localhost:4501/en/{Path.GetFileNameWithoutExtension(path)}"),
            new Uri($"http://localhost:4500/en/{Path.GetFileNameWithoutExtension(path)}"),
            4501,
            4500,
            "parent1234567890",
            commit.Sha);

    private static Commit MakeCommit(params (string Path, int Additions, int Deletions)[] files)
        => MakeCommitWithSha("feedfacefeedfacefeedfacefeedfacefeedface", files);

    private static Commit MakeCommitWithSha(string sha, params (string Path, int Additions, int Deletions)[] files)
    {
        var commit = new Commit
        {
            Sha = sha,
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
