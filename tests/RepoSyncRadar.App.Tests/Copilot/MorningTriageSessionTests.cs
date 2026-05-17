using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.App.Components;
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
        Assert.Contains("radar_save_review", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("DetailsJa", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("詳細分析", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("変更内容", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("根拠", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("確認観点", capturedPrompt, StringComparison.Ordinal);
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

        Assert.Contains(progress.Messages, message => message.Contains("全 2 件", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("1 / 2 件目", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("2 / 2 件目", StringComparison.Ordinal));
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
