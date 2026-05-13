using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.App.Tests.Copilot.Tools;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

/// <summary>
/// Adoption session unit tests (IMPLEMENTATION_PLAN.md §Step 17.3). Verifies prompt
/// composition, persistence, validation, and diff truncation in isolation from the real
/// Copilot SDK.
/// </summary>
public sealed class AdoptionSessionTests
{
    [Fact]
    public async Task Generate_Returns_Three_Drafts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertReviewedCommitAsync("adopt-1", ReviewStatus.Adopted, cancellationToken: ct);

        var github = Substitute.For<IDocsGitHubClient>();
        github.GetUnifiedDiffAsync("adopt-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("diff --git a/x b/x\n+hello\n"));

        var session = Substitute.For<ICopilotSession>();
        session.SessionId.Returns("s1");
        session.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("{\"twitter\":\"tw\",\"slack\":\"sl\",\"customer\":\"cu\"}"));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Adoption, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        var bundle = await sut.GenerateDraftsAsync("adopt-1", ct);

        Assert.Equal("tw", bundle.TwitterJa);
        Assert.Equal("sl", bundle.SlackJa);
        Assert.Equal("cu", bundle.CustomerJa);
    }

    [Fact]
    public async Task Generate_Persists_All_Three_Drafts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertReviewedCommitAsync("adopt-2", ReviewStatus.Adopted, cancellationToken: ct);

        var github = Substitute.For<IDocsGitHubClient>();
        github.GetUnifiedDiffAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("diff"));

        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("{\"twitter\":\"a\",\"slack\":\"b\",\"customer\":\"c\"}"));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Adoption, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        await sut.GenerateDraftsAsync("adopt-2", ct);

        await using var db = harness.CreateDb();
        var drafts = await db.Drafts.AsNoTracking().Where(d => d.Sha == "adopt-2").ToListAsync(ct);
        Assert.Equal(3, drafts.Count);
        Assert.Contains(drafts, d => d.Channel == "twitter" && d.Body == "a");
        Assert.Contains(drafts, d => d.Channel == "slack" && d.Body == "b");
        Assert.Contains(drafts, d => d.Channel == "customer" && d.Body == "c");
    }

    [Fact]
    public async Task Generate_Includes_FewShot_Examples()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        // Seed 7 prior adoptions so we can confirm only 5 are included (most recent first).
        for (var i = 1; i <= 7; i++)
        {
            await harness.InsertReviewedCommitAsync(
                $"past-{i:D2}",
                ReviewStatus.Adopted,
                message: $"past message {i}",
                reviewedAtUtc: new DateTime(2026, 5, i, 12, 0, 0, DateTimeKind.Utc),
                cancellationToken: ct);
        }
        await harness.InsertReviewedCommitAsync("target", ReviewStatus.Adopted, cancellationToken: ct);

        var github = Substitute.For<IDocsGitHubClient>();
        github.GetUnifiedDiffAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("diff"));

        string? capturedPrompt = null;
        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Do<string>(p => capturedPrompt = p), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("{\"twitter\":\"\",\"slack\":\"\",\"customer\":\"\"}"));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Adoption, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        await sut.GenerateDraftsAsync("target", ct);

        Assert.NotNull(capturedPrompt);
        // 5 most recent (past-07 down to past-03) should appear; past-01 and past-02 should not.
        Assert.Contains("past-07", capturedPrompt);
        Assert.Contains("past-06", capturedPrompt);
        Assert.Contains("past-05", capturedPrompt);
        Assert.Contains("past-04", capturedPrompt);
        Assert.Contains("past-03", capturedPrompt);
        Assert.DoesNotContain("past-02", capturedPrompt);
        Assert.DoesNotContain("past-01", capturedPrompt);
    }

    [Fact]
    public async Task Generate_Rejects_Unadopted_Commit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertReviewedCommitAsync("not-yet", ReviewStatus.Unseen, cancellationToken: ct);

        var github = Substitute.For<IDocsGitHubClient>();
        var session = Substitute.For<ICopilotSession>();
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<SessionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GenerateDraftsAsync("not-yet", ct));
        await session.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Generate_Truncates_When_Diff_Too_Large()
    {
        var huge = new string('a', AdoptionSession.MaxDiffBytes + 1024);
        var truncated = AdoptionSession.TruncateDiff(huge);

        Assert.NotEqual(huge, truncated);
        Assert.Contains("truncated", truncated);
        var markerBytes = System.Text.Encoding.UTF8.GetByteCount(AdoptionSession.TruncatedMarker);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(truncated) <=
                    AdoptionSession.MaxDiffBytes + markerBytes);
    }
}
