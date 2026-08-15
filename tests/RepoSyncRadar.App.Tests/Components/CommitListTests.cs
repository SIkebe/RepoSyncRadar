using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RepoSyncRadar.App;
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
[Collection("Localization")]
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
        Assert.NotNull(cut.Find("[data-testid=\"commit-rows\"]"));
        Assert.Empty(cut.FindAll("[data-testid=\"commit-list-empty\"]"));
    }

    [Fact]
    public void CommitList_Marks_Selected_Row_As_Active()
    {
        var commits = new List<Commit>
        {
            MakeCommit("aaaaaaa1", "first"),
            MakeCommit("bbbbbbb2", "second"),
        };

        using var cut = RenderListWith(commits, selectedSha: "bbbbbbb2");

        var active = cut.Find("[data-sha=\"bbbbbbb2\"]");
        Assert.Contains("active", active.ClassList);
        Assert.Equal("true", active.QuerySelector("[data-testid=\"commit-row-select-button\"]")?.GetAttribute("aria-current"));
    }

    [Fact]
    public void CommitList_Row_Selection_Uses_Focusable_Button()
    {
        var commit = MakeCommit("aaaaaaa1", "first");
        Commit? selectedCommit = null;

        using var cut = RenderListWith([commit], onCommitSelected: selected => selectedCommit = selected);

        var rowButton = cut.Find("[data-testid=\"commit-row-select-button\"]");
        Assert.Equal("button", rowButton.TagName, ignoreCase: true);

        rowButton.Click();

        Assert.Same(commit, selectedCommit);
    }

    [Fact]
    public void CommitList_Renders_Score_Instead_Of_Author()
    {
        var commit = MakeCommit("aaaaaaa1", "first", score: 0.83);

        using var cut = RenderListWith([commit]);

        var row = cut.Find("[data-testid=\"commit-row\"]");
        var score = cut.Find("[data-testid=\"commit-row-score\"]");
        Assert.Equal("0.83", score.TextContent);
        Assert.DoesNotContain("octocat", row.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitList_Renders_Unscored_Label_When_Score_Is_Missing()
    {
        var commit = MakeCommit("aaaaaaa1", "first");

        using var cut = RenderListWith([commit]);

        Assert.Equal("未採点", cut.Find("[data-testid=\"commit-row-score\"]").TextContent);
    }

    [Fact]
    public void CommitList_Renders_Aggregated_Line_Changes()
    {
        var commit = MakeCommit("aaaaaaa1", "first");
        commit.Files.AddRange(
        [
            new CommitFile { Sha = commit.Sha, Path = "content/one.md", Status = "modified", Additions = 12, Deletions = 3 },
            new CommitFile { Sha = commit.Sha, Path = "content/two.md", Status = "added", Additions = 5, Deletions = 0 },
        ]);

        using var cut = RenderListWith([commit]);

        var stats = cut.Find("[data-testid=\"commit-row-change-stats\"]");
        Assert.Equal("+17", stats.QuerySelector(".additions")?.TextContent);
        Assert.Equal("-3", stats.QuerySelector(".deletions")?.TextContent);
        Assert.Equal("変更行数: 追加 17、削除 3、合計 20", stats.GetAttribute("title"));
        Assert.Equal(stats.GetAttribute("title"), stats.GetAttribute("aria-label"));
    }

    [Fact]
    public void CommitList_Message_Hover_Shows_Full_Message()
    {
        const string message = "docs: update Copilot article\n\nExpand details for enterprise setup.";
        var commit = MakeCommit("aaaaaaa1", message);

        using var cut = RenderListWith([commit]);

        var messageCell = cut.Find(".commit-row .message");
        Assert.Equal("docs: update Copilot article", messageCell.TextContent);
        Assert.Equal(message, messageCell.GetAttribute("title"));
    }

    [Fact]
    public void CommitList_Empty_State()
    {
        using var cut = RenderListWith(new List<Commit>());

        Assert.Empty(cut.FindAll("[data-testid=\"commit-row\"]"));
        var empty = cut.Find("[data-testid=\"commit-list-empty\"]");
        Assert.False(string.IsNullOrWhiteSpace(empty.TextContent));
    }

    [Theory]
    [InlineData(ReviewStatus.Unseen, "未確認キューは空です。", "Triage")]
    [InlineData(ReviewStatus.Adopted, "注目キューは空です。", "注目へ移動")]
    [InlineData(ReviewStatus.Later, "保留キューは空です。", "保留した候補")]
    [InlineData(ReviewStatus.Rejected, "見送り候補は空です。", "見送った候補")]
    [InlineData(ReviewStatus.Archived, "アーカイブは空です。", "アクティブな確認対象")]
    public void CommitList_Empty_State_Follows_Status_Context(
        ReviewStatus status,
        string expectedTitle,
        string expectedHintFragment)
    {
        using var cut = RenderListWith(new List<Commit>(), filter: new CommitQueryFilter { Status = status });

        Assert.Equal(expectedTitle, cut.Find("[data-testid=\"commit-list-empty-title\"]").TextContent);
        Assert.Contains(expectedHintFragment, cut.Find("[data-testid=\"commit-list-empty\"]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitList_Empty_State_Prioritizes_Search_Context()
    {
        using var cut = RenderListWith(
            new List<Commit>(),
            filter: new CommitQueryFilter { Status = ReviewStatus.Adopted, ShaQuery = "abc123" });

        Assert.Equal("検索条件に一致するコミットはありません。", cut.Find("[data-testid=\"commit-list-empty-title\"]").TextContent);
        Assert.Contains("検索語を変える", cut.Find("[data-testid=\"commit-list-empty\"]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitList_Uses_Cascaded_DisplayCulture_When_Process_Culture_Differs()
    {
        try
        {
            AppDisplayCulture.Apply("en");

            using var cut = RenderListWith(
                new List<Commit>(),
                filter: new CommitQueryFilter { Status = ReviewStatus.Unseen },
                displayCulture: AppDisplayCulture.DefaultCultureName);

            Assert.Equal("未確認キューは空です。", cut.Find("[data-testid=\"commit-list-empty-title\"]").TextContent);
            Assert.DoesNotContain("The unseen queue is empty.", cut.Find("[data-testid=\"commit-list-empty\"]").TextContent);
        }
        finally
        {
            AppDisplayCulture.Apply(AppDisplayCulture.DefaultCultureName);
        }
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
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources")
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
        CommitQueryFilter? filter = null,
        string? selectedSha = null,
        string displayCulture = AppDisplayCulture.DefaultCultureName,
        Action<Commit>? onCommitSelected = null,
        Action<CommitSelectionChange>? onSelectionChanged = null)
    {
        var repo = Substitute.For<IRadarRepository>();
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Commit>>(commits));

        var sp = new ServiceCollection()
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources")
            .AddSingleton(repo)
            .BuildServiceProvider();

        var ctx = new Bunit.BunitContext();
        return ctx.Render<CommitList>(
            parameters => parameters
                .AddCascadingValue<IServiceProvider>(sp)
                .AddCascadingValue(LocalizedComponentBase.DisplayCultureCascadeName, displayCulture)
                .Add(c => c.Filter, filter)
                .Add(c => c.SelectedSha, selectedSha)
                .Add(c => c.OnCommitSelected, selected => onCommitSelected?.Invoke(selected))
                .Add(c => c.SelectionChanged, change => onSelectionChanged?.Invoke(change)));
    }

    private static Commit MakeCommit(string sha, string message, double? score = null)
        => new()
        {
            Sha = sha,
            PrNumber = 1,
            Message = message,
            Author = "octocat",
            AuthoredAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
            Scoring = score is null
                ? null
                : new Scoring
                {
                    Sha = sha,
                    Score = score.Value,
                    Category = "feature-update",
                    AudienceJson = "[]",
                    ScoredAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
                },
        };
}
