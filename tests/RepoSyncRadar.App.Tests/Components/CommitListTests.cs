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

        using var ctx = new Bunit.BunitContext();
        var filter = new CommitQueryFilter { Status = ReviewStatus.Unseen };
        var cut = ctx.Render<CommitList>(parameters => parameters
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Filter, filter)
            .Add(c => c.RefreshToken, 0));

        Assert.Single(cut.FindAll("[data-testid=\"commit-row\"]"));

        cut.Render(parameters => parameters
            .Add(c => c.Filter, filter)
            .Add(c => c.RefreshToken, 1));

        Assert.Empty(cut.FindAll("[data-testid=\"commit-row\"]"));
        Assert.NotNull(cut.Find("[data-testid=\"commit-list-empty\"]"));
    }

    [Fact]
    public void CommitList_Checkbox_Emits_Selection_Without_Selecting_Row()
    {
        var commit = MakeCommit("aaaaaaa1", "first");
        Commit? selectedCommit = null;
        CommitSelectionChange? selectionChange = null;

        using var cut = RenderListWith(
            [commit],
            onCommitSelected: selected => selectedCommit = selected,
            onSelectionChanged: change => selectionChange = change);

        cut.Find("[data-testid=\"commit-select\"]").Change(true);

        Assert.Null(selectedCommit);
        Assert.NotNull(selectionChange);
        Assert.Equal([commit.Sha], selectionChange.Shas);
        Assert.True(selectionChange.Selected);
    }

    [Fact]
    public void CommitList_SelectAll_Toggles_Visible_Selection()
    {
        var commits = new List<Commit>
        {
            MakeCommit("aaaaaaa1", "first"),
            MakeCommit("bbbbbbb2", "second"),
        };
        CommitSelectionChange? selectionChange = null;

        using var cut = RenderListWith(commits, onSelectionChanged: change => selectionChange = change);

        cut.Find("[data-testid=\"commit-list-select-all\"]").Click();

        Assert.NotNull(selectionChange);
        Assert.Equal(commits.Select(static commit => commit.Sha), selectionChange.Shas);
        Assert.True(selectionChange.Selected);

        cut.Render(parameters => parameters
            .Add(c => c.SelectedShas, new HashSet<string>(commits.Select(static commit => commit.Sha), StringComparer.Ordinal)));

        cut.Find("[data-testid=\"commit-list-select-all\"]").Click();

        Assert.NotNull(selectionChange);
        Assert.Equal(commits.Select(static commit => commit.Sha), selectionChange.Shas);
        Assert.False(selectionChange.Selected);
    }

    private static IRenderedComponent<CommitList> RenderListWith(
        List<Commit> commits,
        Action<Commit>? onCommitSelected = null,
        Action<CommitSelectionChange>? onSelectionChanged = null)
    {
        var repo = Substitute.For<IRadarRepository>();
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Commit>>(commits));

        var sp = new ServiceCollection()
            .AddSingleton(repo)
            .BuildServiceProvider();

        var ctx = new Bunit.BunitContext();
        return ctx.Render<CommitList>(
            parameters => parameters
                .AddCascadingValue<IServiceProvider>(sp)
                .Add(c => c.OnCommitSelected, selected => onCommitSelected?.Invoke(selected))
                .Add(c => c.SelectionChanged, change => onSelectionChanged?.Invoke(change)));
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
