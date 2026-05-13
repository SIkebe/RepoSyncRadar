using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services;
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

    private static IRenderedComponent<CommitDetail> RenderDetailWith(
        Commit commit,
        IPathToUrlResolver resolver)
    {
        var sp = new ServiceCollection()
            .AddSingleton(resolver)
            .BuildServiceProvider();

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
