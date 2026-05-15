using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoSyncRadar.App.Copilot;
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
        ingestion.IngestAsync(Arg.Any<CancellationToken>())
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
        ingestion.IngestAsync(Arg.Any<CancellationToken>())
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
    }

    [Fact]
    public async Task Run_Uses_Long_Triage_Timeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestion = Substitute.For<ICommitIngestionService>();
        ingestion.IngestAsync(Arg.Any<CancellationToken>())
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
        ingestion.IngestAsync(Arg.Any<CancellationToken>())
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
    public async Task Run_Waits_For_Idle()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestion = Substitute.For<ICommitIngestionService>();
        ingestion.IngestAsync(Arg.Any<CancellationToken>())
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
        ingestion.IngestAsync(Arg.Any<CancellationToken>())
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
        ingestion.IngestAsync(Arg.Any<CancellationToken>())
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

    private sealed class CapturingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }
}
