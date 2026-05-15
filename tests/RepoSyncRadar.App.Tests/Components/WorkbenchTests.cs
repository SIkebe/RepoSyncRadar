using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using NSubstitute;
using RepoSyncRadar.App.Auth;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

/// <summary>
/// bUnit coverage for Workbench-level selection and queue behavior that spans
/// Sidebar, CommitList, CommitDetail, and ReviewActions.
/// </summary>
public sealed class WorkbenchTests
{
    [Fact]
    public void Rejecting_Selected_Commit_Returns_To_Unseen_Queue_And_Clears_Detail()
    {
        var target = new Commit
        {
            Sha = "abc1234abc1234abc1234abc1234abc1234abc1",
            PrNumber = 61071,
            Message = "Update docs changelog",
            Author = "docs-bot",
            AuthoredAt = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
        };
        var currentStatus = ReviewStatus.Unseen;

        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(CountsFor(currentStatus)));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filter = call.Arg<CommitQueryFilter>();
                IReadOnlyList<Commit> commits = filter.Status == ReviewStatus.Unseen && currentStatus == ReviewStatus.Unseen
                    ? [target]
                    : [];
                return Task.FromResult(commits);
            });
        repo.SetReviewAsync(target.Sha, Arg.Any<ReviewStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                currentStatus = call.ArgAt<ReviewStatus>(1);
                return Task.CompletedTask;
            });

        var broadcaster = new ReviewBroadcaster();
        var auth = Substitute.For<IGitHubAuthSession>();
        auth.GetStateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GitHubAuthState.SignedIn));
        auth.GetCurrentLoginAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("octocat"));
        var resolver = Substitute.For<IPathToUrlResolver>();

        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services
            .AddSingleton(repo)
            .AddSingleton<IReviewBroadcaster>(broadcaster)
            .AddSingleton(auth)
            .AddSingleton(Substitute.For<ICopilotAgent>())
            .AddSingleton(resolver)
            .AddSingleton<IOptions<DocsApiOptions>>(Options.Create(new DocsApiOptions
            {
                BaseAddress = new Uri("https://docs.github.com/"),
            }));

        var cut = ctx.RenderComponent<Workbench>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]")));

        cut.Find("[data-testid=\"commit-row\"]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=\"review-reject-reason\"]"));
        cut.Find("[data-testid=\"review-reject-reason\"]").Input("対象外");
        cut.Find("[data-testid=\"review-reject\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=\"commit-row\"]"));
            Assert.Empty(cut.FindAll("[data-testid=\"review-actions\"]"));
            Assert.NotNull(cut.Find("[data-testid=\"commit-detail-empty\"]"));
            Assert.Contains("active", cut.Find("[data-testid=\"sidebar-item-Unseen\"]").ClassList);
        });
        repo.Received(1).SetReviewAsync(target.Sha, ReviewStatus.Rejected, "対象外", Arg.Any<CancellationToken>());
    }

    private static Dictionary<ReviewStatus, int> CountsFor(ReviewStatus currentStatus)
    {
        return new Dictionary<ReviewStatus, int>
        {
            [ReviewStatus.Unseen] = currentStatus == ReviewStatus.Unseen ? 1 : 0,
            [ReviewStatus.Seen] = currentStatus == ReviewStatus.Seen ? 1 : 0,
            [ReviewStatus.Adopted] = currentStatus == ReviewStatus.Adopted ? 1 : 0,
            [ReviewStatus.Rejected] = currentStatus == ReviewStatus.Rejected ? 1 : 0,
            [ReviewStatus.Later] = currentStatus == ReviewStatus.Later ? 1 : 0,
        };
    }
}
