using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="CommitList"/>. Asserts that the row count matches the
/// repository response and that the empty state is rendered when no commits are
/// available.
/// </summary>
public class CommitListTests
{
    [Fact]
    public void CommitList_Renders_Rows()
    {
        var commits = new List<Commit>
        {
            MakeCommit("aaaaaaa1", "first"),
            MakeCommit("bbbbbbb2", "second"),
            MakeCommit("ccccccc3", "third"),
        };

        using var cut = RenderListWith(commits);

        var rows = cut.FindAll("[data-testid=\"commit-row\"]");
        Assert.Equal(3, rows.Count);
        Assert.Empty(cut.FindAll("[data-testid=\"commit-list-empty\"]"));
    }

    [Fact]
    public void CommitList_Empty_State()
    {
        using var cut = RenderListWith(new List<Commit>());

        Assert.Empty(cut.FindAll("[data-testid=\"commit-row\"]"));
        var empty = cut.Find("[data-testid=\"commit-list-empty\"]");
        Assert.False(string.IsNullOrWhiteSpace(empty.TextContent));
    }

    [Fact]
    public void CommitList_Requeries_When_RefreshToken_Changes()
    {
        var repo = Substitute.For<IRadarRepository>();
        var responses = new Queue<IReadOnlyList<Commit>>(
        [
            new List<Commit> { MakeCommit("aaaaaaa1", "first") },
            new List<Commit>(),
        ]);
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(responses.Dequeue()));

        var sp = new ServiceCollection()
            .AddSingleton(repo)
            .BuildServiceProvider();

        using var ctx = new Bunit.TestContext();
        var filter = new CommitQueryFilter { Status = ReviewStatus.Unseen };
        var cut = ctx.RenderComponent<CommitList>(parameters => parameters
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Filter, filter)
            .Add(c => c.RefreshToken, 0));

        Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]"));

        cut.SetParametersAndRender(parameters => parameters
            .Add(c => c.Filter, filter)
            .Add(c => c.RefreshToken, 1));

        Assert.Empty(cut.FindAll("[data-testid=\"commit-row\"]"));
        Assert.NotNull(cut.Find("[data-testid=\"commit-list-empty\"]"));
    }

    private static IRenderedComponent<CommitList> RenderListWith(List<Commit> commits)
    {
        var repo = Substitute.For<IRadarRepository>();
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Commit>>(commits));

        var sp = new ServiceCollection()
            .AddSingleton(repo)
            .BuildServiceProvider();

        var ctx = new Bunit.TestContext();
        return ctx.RenderComponent<CommitList>(
            parameters => parameters.AddCascadingValue<IServiceProvider>(sp));
    }

    private static Commit MakeCommit(string sha, string message)
        => new()
        {
            Sha = sha,
            PrNumber = 1,
            Message = message,
            Author = "octocat",
            AuthoredAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
        };
}
