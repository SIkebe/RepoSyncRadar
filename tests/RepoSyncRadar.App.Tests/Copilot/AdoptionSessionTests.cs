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
/// Focused draft session unit tests (IMPLEMENTATION_PLAN.md §Step 17.3). Verifies prompt
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
            .Returns(Task.FromResult("{\"explanation\":\"ex\",\"twitter\":\"tw\",\"teams\":\"tm\",\"customer\":\"cu\"}"));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Adoption, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        var bundle = await sut.GenerateDraftsAsync("adopt-1", ct);

        Assert.Equal("tw", bundle.TwitterJa);
        Assert.Equal("tm", bundle.TeamsJa);
        Assert.Equal("cu", bundle.CustomerJa);
        Assert.Equal("ex", bundle.ExplanationJa);
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
            .Returns(Task.FromResult("{\"explanation\":\"diff explanation\",\"twitter\":\"a\",\"teams\":\"b\",\"customer\":\"c\"}"));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Adoption, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        await sut.GenerateDraftsAsync("adopt-2", ct);

        await using var db = harness.CreateDb();
        var drafts = await db.Drafts.AsNoTracking().Where(d => d.Sha == "adopt-2").ToListAsync(ct);
        Assert.Equal(4, drafts.Count);
        Assert.Contains(drafts, d => d.Channel == "explanation" && d.Body == "diff explanation");
        Assert.Contains(drafts, d => d.Channel == "twitter" && d.Body == "a");
        Assert.Contains(drafts, d => d.Channel == "teams" && d.Body == "b");
        Assert.Contains(drafts, d => d.Channel == "customer" && d.Body == "c");
    }

    [Fact]
    public void ParseBundle_Accepts_Json_Wrapped_In_Assistant_Text()
    {
        var bundle = AdoptionSession.ParseBundle(
            "以下の内容で生成しました。\n```json\n" +
            "{\"explanation\":\"ex {braced}\",\"twitter\":\"tw\",\"teams\":\"tm\",\"customer\":\"cu\"}" +
            "\n```\n必要に応じて調整してください。");

        Assert.Equal("tw", bundle.TwitterJa);
        Assert.Equal("tm", bundle.TeamsJa);
        Assert.Equal("cu", bundle.CustomerJa);
        Assert.Equal("ex {braced}", bundle.ExplanationJa);
    }

    [Fact]
    public async Task Generate_Persists_Drafts_When_Copilot_Wraps_Json()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertReviewedCommitAsync("wrapped", ReviewStatus.Adopted, cancellationToken: ct);

        var github = Substitute.For<IDocsGitHubClient>();
        github.GetUnifiedDiffAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("diff"));

        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("Here is the JSON:\n{\"explanation\":\"wrapped-ex\",\"twitter\":\"wrapped-tw\",\"teams\":\"wrapped-tm\",\"customer\":\"wrapped-cu\"}"));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Adoption, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        await sut.GenerateDraftsAsync("wrapped", ct);

        await using var db = harness.CreateDb();
        var drafts = await db.Drafts.AsNoTracking().Where(d => d.Sha == "wrapped").ToListAsync(ct);
        Assert.Contains(drafts, d => d.Channel == "explanation" && d.Body == "wrapped-ex");
        Assert.Contains(drafts, d => d.Channel == "twitter" && d.Body == "wrapped-tw");
    }

    [Fact]
    public async Task Generate_Retries_Once_When_Response_Is_Not_Json()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertReviewedCommitAsync("repair", ReviewStatus.Adopted, cancellationToken: ct);

        var github = Substitute.For<IDocsGitHubClient>();
        github.GetUnifiedDiffAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("diff"));

        var calls = new List<string>();
        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Do<string>(calls.Add), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult("差分解説: repair-ex\nTwitter: repair-tw\nTeams: repair-tm\n顧客向け: repair-cu"),
                Task.FromResult("{\"explanation\":\"repair-ex\",\"twitter\":\"repair-tw\",\"teams\":\"repair-tm\",\"customer\":\"repair-cu\"}"));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Adoption, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        var bundle = await sut.GenerateDraftsAsync("repair", ct);

        Assert.Equal("repair-tw", bundle.TwitterJa);
        Assert.Equal("repair-ex", bundle.ExplanationJa);
        Assert.Equal(2, calls.Count);
        Assert.Contains("前回の応答はアプリで処理できる JSON ではありませんでした", calls[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_Parses_Labeled_Text_When_Repair_Is_Not_Json()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertReviewedCommitAsync("labeled", ReviewStatus.Adopted, cancellationToken: ct);

        var github = Substitute.For<IDocsGitHubClient>();
        github.GetUnifiedDiffAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("diff"));

        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult("JSON ではなく通常文で返します。"),
                Task.FromResult("""
                ## 差分解説
                labeled-ex

                ## Twitter
                labeled-tw

                ## Teams
                labeled-tm

                ## 顧客向け
                labeled-cu
                """));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Adoption, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        var bundle = await sut.GenerateDraftsAsync("labeled", ct);

        Assert.Equal("labeled-ex", bundle.ExplanationJa);
        Assert.Equal("labeled-tw", bundle.TwitterJa);
        Assert.Equal("labeled-tm", bundle.TeamsJa);
        Assert.Equal("labeled-cu", bundle.CustomerJa);
    }

    [Fact]
    public void ParsePlainTextBundle_Uses_Response_As_Explanation_When_No_Labels_Exist()
    {
        var parsed = AdoptionSession.TryParsePlainTextBundle("文案として読める本文です。", out var bundle);

        Assert.True(parsed);
        Assert.Equal("文案として読める本文です。", bundle.ExplanationJa);
        Assert.Empty(bundle.TwitterJa);
        Assert.Empty(bundle.TeamsJa);
        Assert.Empty(bundle.CustomerJa);
    }

    [Fact]
    public async Task Generate_Includes_FewShot_Examples()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        // Seed 7 prior focused commits so we can confirm only 5 are included (most recent first).
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
            .Returns(Task.FromResult("{\"explanation\":\"\",\"twitter\":\"\",\"teams\":\"\",\"customer\":\"\"}"));
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
        Assert.Contains("差分解説", capturedPrompt);
        Assert.Contains("差分の見方", capturedPrompt);
        Assert.Contains("重要なポイント", capturedPrompt);
        Assert.Contains("細部を読まなくても変更点を理解", capturedPrompt);
    }

    [Fact]
    public async Task Generate_Includes_Official_Doc_Urls_In_Prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertReviewedCommitAsync("url-prompt", ReviewStatus.Adopted, cancellationToken: ct);
        await using (var db = harness.CreateDb())
        {
            db.CommitFiles.Add(new CommitFile
            {
                Sha = "url-prompt",
                Path = "content/copilot/about-copilot.md",
                Status = "modified",
                Additions = 1,
                Deletions = 0,
            });
            db.PathUrlMaps.Add(new PathUrlMap
            {
                Path = "content/copilot/about-copilot.md",
                Version = "fpt",
                Language = "en",
                Url = "/en/copilot/about-copilot",
                ResolvedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var github = Substitute.For<IDocsGitHubClient>();
        github.GetUnifiedDiffAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("diff"));

        string? capturedPrompt = null;
        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Do<string>(p => capturedPrompt = p), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("{\"explanation\":\"\",\"twitter\":\"https://docs.github.com/en/copilot/about-copilot\",\"teams\":\"https://docs.github.com/en/copilot/about-copilot\",\"customer\":\"https://docs.github.com/en/copilot/about-copilot\"}"));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Adoption, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        await sut.GenerateDraftsAsync("url-prompt", ct);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("## 公式ドキュメント URL", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("https://docs.github.com/en/copilot/about-copilot", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("twitter / teams / customer には", capturedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_Includes_OpenApi_Detail_Requirements_For_Api_Data_Files()
    {
        var commit = new Commit
        {
            Sha = "openapi-data",
            Message = "Refresh generated API data",
            Author = "docs-bot",
            Files =
            [
                new CommitFile { Path = "content/rest/copilot/index.md" },
                new CommitFile { Path = "src/rest/data/fpt-2026-03-10/copilot.json" },
                new CommitFile { Path = "src/github-apps/data/ghec-2026-03-10/server-to-server-rest.json" },
                new CommitFile { Path = "src/webhooks/lib/config.json" },
            ],
        };

        var prompt = AdoptionSession.BuildPrompt(commit, [], "diff");

        Assert.Contains("## OpenAPI / API reference 差分の追加要件", prompt, StringComparison.Ordinal);
        Assert.Contains("Markdown 差分だけで判断しない", prompt, StringComparison.Ordinal);
        Assert.Contains("当該 API の差分に関する詳細な解説を必ず含める", prompt, StringComparison.Ordinal);
        Assert.Contains("エンドポイント、HTTP メソッド、権限/permission、認証方式", prompt, StringComparison.Ordinal);
        Assert.Contains("src/rest/data/fpt-2026-03-10/copilot.json", prompt, StringComparison.Ordinal);
        Assert.Contains("src/github-apps/data/ghec-2026-03-10/server-to-server-rest.json", prompt, StringComparison.Ordinal);
        Assert.Contains("src/webhooks/lib/config.json", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_Does_Not_Include_OpenApi_Requirements_For_Rest_Markdown_Only()
    {
        var commit = new Commit
        {
            Sha = "rest-markdown",
            Message = "Update REST docs page",
            Author = "octo",
            Files =
            [
                new CommitFile { Path = "content/rest/copilot/index.md" },
            ],
        };

        var prompt = AdoptionSession.BuildPrompt(commit, [], "diff");

        Assert.DoesNotContain("## OpenAPI / API reference 差分の追加要件", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_Appends_Official_Doc_Url_When_Drafts_Omit_It()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertReviewedCommitAsync("url-append", ReviewStatus.Adopted, cancellationToken: ct);
        await using (var db = harness.CreateDb())
        {
            db.CommitFiles.Add(new CommitFile
            {
                Sha = "url-append",
                Path = "content/actions/learn-github-actions.md",
                Status = "modified",
                Additions = 1,
                Deletions = 0,
            });
            await db.SaveChangesAsync(ct);
        }

        var github = Substitute.For<IDocsGitHubClient>();
        github.GetUnifiedDiffAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("diff"));

        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("{\"explanation\":\"ex\",\"twitter\":\"tw\",\"teams\":\"tm\",\"customer\":\"cu\"}"));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Adoption, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        var bundle = await sut.GenerateDraftsAsync("url-append", ct);

        const string expectedUrl = "https://docs.github.com/en/actions/learn-github-actions";
        Assert.Contains(expectedUrl, bundle.TwitterJa, StringComparison.Ordinal);
        Assert.Contains(expectedUrl, bundle.TeamsJa, StringComparison.Ordinal);
        Assert.Contains(expectedUrl, bundle.CustomerJa, StringComparison.Ordinal);

        await using var verifyDb = harness.CreateDb();
        var drafts = await verifyDb.Drafts.AsNoTracking().Where(d => d.Sha == "url-append").ToListAsync(ct);
        Assert.Contains(drafts, d => d.Channel == "twitter" && d.Body.Contains(expectedUrl, StringComparison.Ordinal));
        Assert.Contains(drafts, d => d.Channel == "teams" && d.Body.Contains(expectedUrl, StringComparison.Ordinal));
        Assert.Contains(drafts, d => d.Channel == "customer" && d.Body.Contains(expectedUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generate_Fallback_Official_Doc_Url_Drops_Index_Segment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertReviewedCommitAsync("url-index", ReviewStatus.Adopted, cancellationToken: ct);
        await using (var db = harness.CreateDb())
        {
            db.CommitFiles.Add(new CommitFile
            {
                Sha = "url-index",
                Path = "content/copilot/concepts/models/index.md",
                Status = "modified",
                Additions = 1,
                Deletions = 0,
            });
            await db.SaveChangesAsync(ct);
        }

        var github = Substitute.For<IDocsGitHubClient>();
        github.GetUnifiedDiffAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("diff"));

        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("{\"explanation\":\"ex\",\"twitter\":\"tw\",\"teams\":\"tm\",\"customer\":\"cu\"}"));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Adoption, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        var bundle = await sut.GenerateDraftsAsync("url-index", ct);

        const string expectedUrl = "https://docs.github.com/en/copilot/concepts/models";
        Assert.Contains(expectedUrl, bundle.TwitterJa, StringComparison.Ordinal);
        Assert.DoesNotContain("/models/index", bundle.TwitterJa, StringComparison.Ordinal);
        Assert.Contains(expectedUrl, bundle.TeamsJa, StringComparison.Ordinal);
        Assert.DoesNotContain("/models/index", bundle.TeamsJa, StringComparison.Ordinal);
        Assert.Contains(expectedUrl, bundle.CustomerJa, StringComparison.Ordinal);
        Assert.DoesNotContain("/models/index", bundle.CustomerJa, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_Normalizes_Mapped_Official_Doc_Url_Index_Segment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertReviewedCommitAsync("url-mapped-index", ReviewStatus.Adopted, cancellationToken: ct);
        await using (var db = harness.CreateDb())
        {
            db.CommitFiles.Add(new CommitFile
            {
                Sha = "url-mapped-index",
                Path = "content/copilot/concepts/models/index.md",
                Status = "modified",
                Additions = 1,
                Deletions = 0,
            });
            db.PathUrlMaps.Add(new PathUrlMap
            {
                Path = "content/copilot/concepts/models/index.md",
                Version = "fpt",
                Language = "en",
                Url = "/en/copilot/concepts/models/index",
                ResolvedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var github = Substitute.For<IDocsGitHubClient>();
        github.GetUnifiedDiffAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("diff"));

        string? capturedPrompt = null;
        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Do<string>(prompt => capturedPrompt = prompt), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("{\"explanation\":\"ex\",\"twitter\":\"tw\",\"teams\":\"tm\",\"customer\":\"cu\"}"));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Adoption, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        var bundle = await sut.GenerateDraftsAsync("url-mapped-index", ct);

        const string expectedUrl = "https://docs.github.com/en/copilot/concepts/models";
        Assert.Contains(expectedUrl, capturedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("/models/index", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains(expectedUrl, bundle.TwitterJa, StringComparison.Ordinal);
        Assert.DoesNotContain("/models/index", bundle.TwitterJa, StringComparison.Ordinal);
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

    [Fact]
    public async Task GenerateBatchExplanation_Generates_Drafts_For_Each_Selected_Adopted_Commit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertReviewedCommitAsync("batch-1", ReviewStatus.Adopted, message: "first focused change", cancellationToken: ct);
        await harness.InsertReviewedCommitAsync("batch-2", ReviewStatus.Adopted, message: "second focused change", cancellationToken: ct);

        var github = Substitute.For<IDocsGitHubClient>();
        github.GetUnifiedDiffAsync("batch-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("diff --git a/a b/a\n+one\n"));
        github.GetUnifiedDiffAsync("batch-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("diff --git a/b b/b\n+two\n"));

        var capturedPrompts = new List<string>();
        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Do<string>(capturedPrompts.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("{\"explanation\":\"ex\",\"twitter\":\"tw\",\"teams\":\"tm\",\"customer\":\"cu\"}"));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Adoption, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        var generated = await sut.GenerateBatchExplanationAsync(["batch-1", "batch-2"], ct);

        Assert.Equal(2, generated);
        Assert.Equal(2, capturedPrompts.Count);
        Assert.Contains("batch-1", capturedPrompts[0], StringComparison.Ordinal);
        Assert.Contains("first focused change", capturedPrompts[0], StringComparison.Ordinal);
        Assert.Contains("+one", capturedPrompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("+two", capturedPrompts[0], StringComparison.Ordinal);
        Assert.Contains("batch-2", capturedPrompts[1], StringComparison.Ordinal);
        Assert.Contains("second focused change", capturedPrompts[1], StringComparison.Ordinal);
        Assert.Contains("+two", capturedPrompts[1], StringComparison.Ordinal);

        await using var verifyDb = harness.CreateDb();
        var drafts = await verifyDb.Drafts.AsNoTracking()
            .Where(d => d.Sha == "batch-1" || d.Sha == "batch-2")
            .ToListAsync(ct);
        Assert.Equal(8, drafts.Count);
        Assert.Contains(drafts, d => d.Sha == "batch-1" && d.Channel == "explanation" && d.Body == "ex");
        Assert.Contains(drafts, d => d.Sha == "batch-2" && d.Channel == "explanation" && d.Body == "ex");
    }

    [Fact]
    public async Task GenerateBatchExplanation_Rejects_Unadopted_Commits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertReviewedCommitAsync("batch-adopted", ReviewStatus.Adopted, cancellationToken: ct);
        await harness.InsertReviewedCommitAsync("batch-later", ReviewStatus.Later, cancellationToken: ct);

        var github = Substitute.For<IDocsGitHubClient>();
        var session = Substitute.For<ICopilotSession>();
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<SessionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var sut = new AdoptionSession(harness.DbFactory, github, factory, NullLogger<AdoptionSession>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.GenerateBatchExplanationAsync(["batch-adopted", "batch-later"], ct));
        await session.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
