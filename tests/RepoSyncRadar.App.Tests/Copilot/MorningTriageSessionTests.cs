using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

/// <summary>
/// Verifies the orchestration contract for <see cref="MorningTriageSession"/>:
/// ingestion → session create → prompt → wait-for-idle → optional abort on cancel.
/// The Copilot SDK is replaced by an <see cref="ICopilotSessionFactory"/> fake so the
/// embedded CLI is never spawned. Manual end-to-end smoke is covered by the §15.4 step
/// of <c>docs/IMPLEMENTATION_PLAN.md</c>.
/// </summary>
public sealed class MorningTriageSessionTests
{
    [Fact]
    public async Task Run_Ingests_Then_Starts_Session()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = new List<string>();

        var ingestion = Substitute.For<ICommitIngestionService>();
        ingestion.IngestAsync(Arg.Any<IProgress<CommitIngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("ingest");
                return Task.FromResult(new IngestionReport(Total: 3, Inserted: 3, Skipped: 0));
            });

        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("ok");

        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<SessionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("session");
                return Task.FromResult(session);
            });

        var triage = new MorningTriageSession(ingestion, factory, NullLogger<MorningTriageSession>.Instance);

        var report = await triage.RunAsync(ct);

        Assert.Equal(["ingest", "session"], calls);
        Assert.Equal(3, report.Inserted);
        await factory.Received(1).CreateSessionAsync(SessionPurpose.Triage, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_Sends_Triage_Prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestion = Substitute.For<ICommitIngestionService>();
        ingestion.IngestAsync(Arg.Any<IProgress<CommitIngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionReport(0, 0, 0)));

        string? capturedPrompt = null;
        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedPrompt = call.Arg<string>();
                return Task.FromResult("done");
            });

        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<SessionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var triage = new MorningTriageSession(ingestion, factory, NullLogger<MorningTriageSession>.Instance);
        await triage.RunAsync(ct);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("Morning Triage", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("github/docs", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("radar_list_commits", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("radar_score_commit", capturedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("radar_save_review", capturedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("スコア上位 5 件", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("DetailsJa", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("詳細分析", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("Goal:", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("Success criteria:", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("Evidence budget:", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("Scoring rubric:", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("Output requirements for `radar_score_commit`:", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("Processing order:", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("最大 10 件", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("1 件ずつ", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("まとめて保存しない", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("Stop rules:", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("変更内容", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("根拠", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("影響", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("確認観点", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("SummaryJa", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("WhyJa", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("最大 90 文字程度", capturedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Skim", capturedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("`Seen`", capturedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_Uses_Long_Triage_Timeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestion = Substitute.For<ICommitIngestionService>();
        ingestion.IngestAsync(Arg.Any<IProgress<CommitIngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionReport(0, 0, 0)));

        TimeSpan? capturedTimeout = null;
        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedTimeout = call.ArgAt<TimeSpan?>(1);
                return Task.FromResult("done");
            });

        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<SessionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var triage = new MorningTriageSession(ingestion, factory, NullLogger<MorningTriageSession>.Instance);
        await triage.RunAsync(ct);

        Assert.NotNull(capturedTimeout);
        Assert.True(capturedTimeout > TimeSpan.FromMinutes(1));
        Assert.Equal(MorningTriageSession.TriageSendTimeout, capturedTimeout);
    }

    [Fact]
    public async Task Run_Uses_Two_Parallel_Sessions_For_Multiple_Unscored_Commits()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestion = Substitute.For<ICommitIngestionService>();
        ingestion.IngestAsync(Arg.Any<IProgress<CommitIngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionReport(3, 3, 0)));

        var repository = Substitute.For<IRadarRepository>();
        repository.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Commit>>([
                MakeCommit("aaa1111111111111111111111111111111111111", 1, "content/a.md"),
                MakeCommit("bbb2222222222222222222222222222222222222", 2, "content/b.md"),
                MakeCommit("ccc3333333333333333333333333333333333333", 3, "content/c.md"),
            ]));

        var prompts = new List<string>();
        var session1 = Substitute.For<ICopilotSession>();
        session1.SendAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                prompts.Add(call.Arg<string>());
                return Task.FromResult("done-1");
            });
        var session2 = Substitute.For<ICopilotSession>();
        session2.SendAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                prompts.Add(call.Arg<string>());
                return Task.FromResult("done-2");
            });

        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<SessionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session1), Task.FromResult(session2));

        var triage = new MorningTriageSession(
            ingestion,
            factory,
            new TriageScoringProgressTracker(),
            repository,
            reviewBroadcaster: null,
            NullLogger<MorningTriageSession>.Instance);

        await triage.RunAsync(ct);

        await factory.Received(2).CreateSessionAsync(SessionPurpose.Triage, Arg.Any<CancellationToken>());
        await session1.Received(1).SendAsync(Arg.Any<string>(), MorningTriageSession.TriageSendTimeout, Arg.Any<CancellationToken>());
        await session2.Received(1).SendAsync(Arg.Any<string>(), MorningTriageSession.TriageSendTimeout, Arg.Any<CancellationToken>());
        Assert.Equal(2, prompts.Count);
        Assert.Contains(prompts, prompt => prompt.Contains("aaa1111111111111111111111111111111111111", StringComparison.Ordinal)
            && prompt.Contains("ccc3333333333333333333333333333333333333", StringComparison.Ordinal));
        Assert.Contains(prompts, prompt => prompt.Contains("bbb2222222222222222222222222222222222222", StringComparison.Ordinal));
        Assert.All(prompts, prompt =>
        {
            Assert.Contains("分割処理", prompt, StringComparison.Ordinal);
            Assert.Contains("radar_score_commit", prompt, StringComparison.Ordinal);
            Assert.Contains("レビュー状態を保存しない", prompt, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Run_Skips_Copilot_Session_When_Repository_Has_No_Unscored_Commits()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestion = Substitute.For<ICommitIngestionService>();
        ingestion.IngestAsync(Arg.Any<IProgress<CommitIngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionReport(0, 0, 0)));
        var repository = Substitute.For<IRadarRepository>();
        repository.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Commit>>([]));
        var factory = Substitute.For<ICopilotSessionFactory>();

        var triage = new MorningTriageSession(
            ingestion,
            factory,
            new TriageScoringProgressTracker(),
            repository,
            reviewBroadcaster: null,
            NullLogger<MorningTriageSession>.Instance);

        await triage.RunAsync(ct);

        await factory.DidNotReceive().CreateSessionAsync(Arg.Any<SessionPurpose>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_Reports_Progress_Through_Stages()
    {
        var ct = TestContext.Current.CancellationToken;
        var progress = new CapturingProgress();
        var ingestion = Substitute.For<ICommitIngestionService>();
        ingestion.IngestAsync(Arg.Any<IProgress<CommitIngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionReport(Total: 5, Inserted: 2, Skipped: 3)));

        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("done"));

        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<SessionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var triage = new MorningTriageSession(ingestion, factory, NullLogger<MorningTriageSession>.Instance);
        await triage.RunAsync(progress, ct);

        Assert.Contains(progress.Messages, message => message.Contains("取得", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("新規 2", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("セッション", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("スコアリング", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("完了", StringComparison.Ordinal));
    }

    private static Commit MakeCommit(string sha, int prNumber, string filePath)
    {
        return new Commit
        {
            Sha = sha,
            PrNumber = prNumber,
            Message = $"commit {sha}",
            Author = "octocat",
            AuthoredAt = new DateTime(2026, 5, 28, 0, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 5, 28, 0, 0, 0, DateTimeKind.Utc),
            Files =
            [
                new CommitFile
                {
                    Sha = sha,
                    Path = filePath,
                    Status = "modified",
                    Additions = 1,
                    Deletions = 0,
                },
            ],
        };
    }

    [Fact]
    public async Task Run_Reports_Realtime_Scoring_Count_From_Tool_Progress()
    {
        var ct = TestContext.Current.CancellationToken;
        var progress = new CapturingProgress();
        var scoringProgress = new TriageScoringProgressTracker();
        var ingestion = Substitute.For<ICommitIngestionService>();
        ingestion.IngestAsync(Arg.Any<IProgress<CommitIngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionReport(Total: 2, Inserted: 2, Skipped: 0)));

        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                scoringProgress.ReportCommitList([
                    "aaa1111111111111111111111111111111111111",
                    "bbb2222222222222222222222222222222222222",
                ]);
                scoringProgress.ReportScoreSaved("aaa1111111111111111111111111111111111111");
                scoringProgress.ReportScoreSaved("bbb2222222222222222222222222222222222222");
                return Task.FromResult("done");
            });

        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<SessionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var triage = new MorningTriageSession(
            ingestion,
            factory,
            scoringProgress,
            NullLogger<MorningTriageSession>.Instance);
        await triage.RunAsync(progress, ct);

        Assert.Contains(progress.Messages, message => message.Contains("対象 2 件", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("スコア保存 1 / 2 件", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("スコア保存 2 / 2 件", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_Waits_For_Idle()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestion = Substitute.For<ICommitIngestionService>();
        ingestion.IngestAsync(Arg.Any<IProgress<CommitIngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionReport(0, 0, 0)));

        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(gate.Task);

        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<SessionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var triage = new MorningTriageSession(ingestion, factory, NullLogger<MorningTriageSession>.Instance);

        var run = triage.RunAsync(ct);

        // The fake session has not produced its idle response yet; RunAsync must be waiting.
        await Task.Delay(50, ct);
        Assert.False(run.IsCompleted);

        gate.SetResult("finally idle");
        var report = await run;
        Assert.NotNull(report);
    }

    [Fact]
    public async Task Run_Cancellation_Aborts_Session()
    {
        var ingestion = Substitute.For<ICommitIngestionService>();
        ingestion.IngestAsync(Arg.Any<IProgress<CommitIngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionReport(0, 0, 0)));

        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var innerCt = callInfo.Arg<CancellationToken>();
                innerCt.Register(() => gate.TrySetCanceled(innerCt));
                return gate.Task;
            });

        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<SessionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var triage = new MorningTriageSession(ingestion, factory, NullLogger<MorningTriageSession>.Instance);

        using var cts = new CancellationTokenSource();
        var run = triage.RunAsync(cts.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
        await session.Received().AbortAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_Error_Propagates_From_Session()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestion = Substitute.For<ICommitIngestionService>();
        ingestion.IngestAsync(Arg.Any<IProgress<CommitIngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionReport(0, 0, 0)));

        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<SessionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var triage = new MorningTriageSession(ingestion, factory, NullLogger<MorningTriageSession>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await triage.RunAsync(ct));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Run_Publishes_Inbox_Refresh_When_Ingestion_Inserts_Commits()
    {
        var ct = TestContext.Current.CancellationToken;
        var progress = new CapturingProgress();
        var ingestion = Substitute.For<ICommitIngestionService>();
        ingestion.IngestAsync(Arg.Any<IProgress<CommitIngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ingestionProgress = call.Arg<IProgress<CommitIngestionProgress>?>();
                ingestionProgress?.Report(new CommitIngestionProgress(
                    Total: 2,
                    Processed: 1,
                    Inserted: 1,
                    Skipped: 0,
                    InsertedSha: "aaa1111111111111111111111111111111111111"));
                ingestionProgress?.Report(new CommitIngestionProgress(
                    Total: 2,
                    Processed: 2,
                    Inserted: 2,
                    Skipped: 0,
                    InsertedSha: "bbb2222222222222222222222222222222222222"));
                return Task.FromResult(new IngestionReport(Total: 2, Inserted: 2, Skipped: 0));
            });

        var session = Substitute.For<ICopilotSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("done"));
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<SessionPurpose>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));
        var broadcaster = Substitute.For<IReviewBroadcaster>();

        var triage = new MorningTriageSession(
            ingestion,
            factory,
            new TriageScoringProgressTracker(),
            broadcaster,
            NullLogger<MorningTriageSession>.Instance);

        await triage.RunAsync(progress, ct);

        broadcaster.Received(2).Publish();
        Assert.Contains(progress.Messages, message => message.Contains("新規 1 / 取得 2", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("新規 2 / 取得 2", StringComparison.Ordinal));
    }

    private sealed class CapturingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }
}
