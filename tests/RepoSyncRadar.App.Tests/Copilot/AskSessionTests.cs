using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using RepoSyncRadar.App;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

/// <summary>
/// Ask session orchestrator tests (IMPLEMENTATION_PLAN.md §Step 18.3).
/// </summary>
[Collection("Localization")]
public sealed class AskSessionTests : IDisposable
{
    private static readonly string[] ShaMessageColumns = ["Sha", "Message"];
    private static readonly IReadOnlyList<object?>[] ShaMessageRows =
    [
        new object?[] { "abc", "msg" },
    ];

    public AskSessionTests()
    {
        AppDisplayCulture.Apply(AppDisplayCulture.DefaultCultureName);
    }

    public void Dispose()
    {
        AppDisplayCulture.Apply(AppDisplayCulture.DefaultCultureName);
    }
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

    [Fact]
    public async Task AskAsync_Localizes_Rejection_Message_To_English()
    {
        AppDisplayCulture.Apply("en");
        var ct = TestContext.Current.CancellationToken;
        var (sut, _, _) = BuildSut(
            assistantReply: "```sql\nDELETE FROM Commits\n```",
            runnerResult: new RadarQueryResult(false, "禁止キーワード 'DELETE' を含んでいます。", string.Empty, [], []),
            localizer: CreateLocalizer());

        var result = await sut.AskAsync("delete everything", cancellationToken: ct);

        Assert.Contains("The query could not be run", result, StringComparison.Ordinal);
        Assert.Contains("Contains blocked keyword 'DELETE'.", result, StringComparison.Ordinal);
        Assert.DoesNotContain("クエリ", result, StringComparison.Ordinal);
        Assert.DoesNotContain("禁止キーワード", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_Uses_Actual_Sqlite_Table_Names()
    {
        var prompt = AskSession.BuildPrompt("Enterprise Team を API で扱う変更は?");

        Assert.Contains("CommitFiles", prompt, StringComparison.Ordinal);
        Assert.Contains("Scorings", prompt, StringComparison.Ordinal);
        Assert.Contains("CopilotToolLogs", prompt, StringComparison.Ordinal);
        Assert.Contains("PathUrlMaps", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Commits, Files", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Scores", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Audits", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("PathUrlMap\n", prompt, StringComparison.Ordinal);
    }

    private static (AskSession Sut, IRadarQueryRunner Runner, ICopilotSession Session) BuildSut(
        string assistantReply,
        RadarQueryResult runnerResult,
        IStringLocalizer<SharedResource>? localizer = null)
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

        return (new AskSession(runner, factory, NullLogger<AskSession>.Instance, localizer), runner, session);
    }

    private static IStringLocalizer<SharedResource> CreateLocalizer()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources")
            .BuildServiceProvider();

        return services.GetRequiredService<IStringLocalizer<SharedResource>>();
    }
}
