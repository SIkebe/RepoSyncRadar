using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

/// <summary>
/// Ask session orchestrator tests (IMPLEMENTATION_PLAN.md §Step 18.3).
/// </summary>
public sealed class AskSessionTests
{
    private static readonly string[] ShaMessageColumns = ["Sha", "Message"];
    private static readonly IReadOnlyList<object?>[] ShaMessageRows =
    [
        new object?[] { "abc", "msg" },
    ];
    private static readonly string[] ShaOnlyColumns = ["Sha"];
    private static readonly IReadOnlyList<object?>[] ShaOnlyRows =
    [
        new object?[] { "abc" },
    ];

    [Fact]
    public async Task AskAsync_Returns_Formatted_Rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sut, runner, _) = BuildSut(
            assistantReply: "```sql\nSELECT Sha, Message FROM Commits\n```",
            runnerResult: new RadarQueryResult(
                true,
                null,
                "SELECT Sha, Message FROM Commits\nLIMIT 100",
                ShaMessageColumns,
                ShaMessageRows));

        var result = await sut.AskAsync("最近のコミットを見せて", cancellationToken: ct);

        Assert.Contains("Sha", result);
        Assert.Contains("Message", result);
        Assert.Contains("abc", result);
        await runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_Hides_Sql_From_User_By_Default()
    {
        var ct = TestContext.Current.CancellationToken;
        var transformed = "SELECT Sha FROM Commits\nLIMIT 100";

        var (sutDefault, _, _) = BuildSut(
            assistantReply: "```sql\nSELECT Sha FROM Commits\n```",
            runnerResult: new RadarQueryResult(true, null, transformed, ShaOnlyColumns, ShaOnlyRows));
        var defaultText = await sutDefault.AskAsync("一覧", cancellationToken: ct);
        Assert.DoesNotContain("```sql", defaultText, StringComparison.Ordinal);

        var (sutDebug, _, _) = BuildSut(
            assistantReply: "```sql\nSELECT Sha FROM Commits\n```",
            runnerResult: new RadarQueryResult(true, null, transformed, ShaOnlyColumns, ShaOnlyRows));
        var debugText = await sutDebug.AskAsync("一覧", debug: true, cancellationToken: ct);
        Assert.Contains("```sql", debugText, StringComparison.Ordinal);
        Assert.Contains(transformed, debugText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskAsync_Rejects_Write_Like_Prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sut, runner, _) = BuildSut(
            assistantReply: "```sql\nDELETE FROM Commits\n```",
            runnerResult: new RadarQueryResult(false, "禁止キーワード 'DELETE' を含んでいます。", string.Empty, [], []));

        var result = await sut.AskAsync("すべて削除して", cancellationToken: ct);

        Assert.Contains("実行できませんでした", result);
        await runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<object?>?>(),
            Arg.Any<CancellationToken>());
    }

    private static (AskSession Sut, IRadarQueryRunner Runner, ICopilotSession Session) BuildSut(
        string assistantReply,
        RadarQueryResult runnerResult)
    {
        var session = Substitute.For<ICopilotSession>();
        session.SessionId.Returns("ask-1");
        session.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(assistantReply));

        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(SessionPurpose.Ask, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(session));

        var runner = Substitute.For<IRadarQueryRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(runnerResult));

        return (new AskSession(runner, factory, NullLogger<AskSession>.Instance), runner, session);
    }
}
