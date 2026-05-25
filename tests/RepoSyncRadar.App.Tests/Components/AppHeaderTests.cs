using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoSyncRadar.App.Auth;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.App.Settings;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

/// <summary>
/// bUnit tests for <see cref="AppHeader"/>. Verifies the three auth states render the
/// right affordances and that the Triage button drives
/// <see cref="ICopilotAgent.RunMorningTriageAsync"/> + republishes through
/// <see cref="IReviewBroadcaster"/> so Sidebar / CommitList refresh themselves.
/// </summary>
[Collection("Localization")]
public sealed class AppHeaderTests
{
    [Fact]
    public void Renders_SignedIn_State_With_SignOut_And_Enabled_Triage()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var sp = BuildServices(session, out _, out _);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        Assert.Contains("app-header", cut.Find("[data-testid=\"app-header\"]").ClassList);
        Assert.Equal(
            "SignedIn",
            cut.Find("[data-testid=\"app-header-state\"]").GetAttribute("data-state"));
        Assert.NotNull(cut.Find("[data-testid=\"app-header-signout\"]"));
        Assert.Equal(
            "@octocat",
            cut.Find("[data-testid=\"app-header-login\"]").TextContent);
        Assert.False(cut.Find("[data-testid=\"app-header-sync\"]").HasAttribute("disabled"));
        Assert.Equal("Triage", cut.Find("[data-testid=\"app-header-sync\"]").TextContent.Trim());
    }

    [Fact]
    public void Renders_NotSignedIn_State_With_SignIn_And_Disabled_Triage()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.NotSignedIn));

        var sp = BuildServices(session, out _, out _);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        Assert.Equal(
            "NotSignedIn",
            cut.Find("[data-testid=\"app-header-state\"]").GetAttribute("data-state"));
        Assert.NotNull(cut.Find("[data-testid=\"app-header-signin\"]"));
        Assert.True(cut.Find("[data-testid=\"app-header-sync\"]").HasAttribute("disabled"));
    }

    [Fact]
    public void Renders_NotConfigured_State_With_Hint_And_Disabled_Triage()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.NotConfigured));

        var sp = BuildServices(session, out _, out _);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        Assert.Equal(
            "NotConfigured",
            cut.Find("[data-testid=\"app-header-state\"]").GetAttribute("data-state"));
        Assert.NotNull(cut.Find("[data-testid=\"app-header-config-hint\"]"));
        Assert.True(cut.Find("[data-testid=\"app-header-sync\"]").HasAttribute("disabled"));
    }

    [Fact]
    public void Triage_Button_Calls_Agent_And_Publishes_Broadcaster()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var sp = BuildServices(session, out var agent, out var broadcaster);
        agent
            .RunMorningTriageAsync(Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IngestionReport(Total: 3, Inserted: 2, Skipped: 1)));

        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-sync\"]").Click();

        agent.Received(1).RunMorningTriageAsync(Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>());
        broadcaster.Received(1).Publish();
        Assert.Contains("新規 2", cut.Find("[data-testid=\"app-header-last-sync\"]").TextContent);
    }

    [Fact]
    public void Triage_Progress_Is_Rendered_While_Agent_Is_Running()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var gate = new TaskCompletionSource<IngestionReport>();
        var sp = BuildServices(session, out var agent, out _);
        agent
            .RunMorningTriageAsync(Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<IProgress<string>?>()?.Report("未確認コミットをスコアリング中: 対象 5 件 / 分析 3 / 5 件 / スコア保存 2 / 5 件 (abc12345)");
                return gate.Task;
            });

        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-sync\"]").Click();

        cut.WaitForAssertion(() =>
        {
            var status = cut.Find("[data-testid=\"app-header-triage-status\"]").TextContent;
            Assert.DoesNotContain("未確認コミットをスコアリング中", status, StringComparison.Ordinal);
            Assert.Equal("作業 Copilot の判定結果を保存中", NormalizeText(cut.Find("[data-testid=\"app-header-triage-current\"]").TextContent));
            Assert.Equal("対象 5 件", NormalizeText(cut.Find("[data-testid=\"app-header-triage-target\"]").TextContent));
            Assert.Equal("分析済み 3 / 5 件", NormalizeText(cut.Find("[data-testid=\"app-header-triage-analyzed\"]").TextContent));
            Assert.Equal("保存済み 2 / 5 件", NormalizeText(cut.Find("[data-testid=\"app-header-triage-saved\"]").TextContent));
            Assert.Equal("コミット abc12345", NormalizeText(cut.Find("[data-testid=\"app-header-triage-reference\"]").TextContent));
            Assert.DoesNotContain("Copilot 分析中", status, StringComparison.Ordinal);
            Assert.Contains("経過", cut.Find("[data-testid=\"app-header-triage-elapsed\"]").TextContent, StringComparison.Ordinal);
        });

        gate.SetResult(new IngestionReport(Total: 1, Inserted: 1, Skipped: 0));
        cut.WaitForAssertion(() =>
            Assert.Contains("Triage 完了", cut.Find("[data-testid=\"app-header-triage-status\"]").TextContent));
    }

    [Fact]
    public void Triage_Progress_Is_Structured_When_Agent_Reports_Analysis_Phase()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var gate = new TaskCompletionSource<IngestionReport>();
        var sp = BuildServices(session, out var agent, out _);
        agent
            .RunMorningTriageAsync(Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<IProgress<string>?>()?.Report("今回の未スコア未確認コミットを分析中: 対象 15 件 / 分析 1 / 15 件 / スコア保存 0 / 15 件 (e4361356)");
                return gate.Task;
            });

        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-sync\"]").Click();

        cut.WaitForAssertion(() =>
        {
            var status = NormalizeText(cut.Find("[data-testid=\"app-header-triage-status\"]").TextContent);
            Assert.StartsWith("作業 Copilot が 1 / 15 件目を分析中 経過", status, StringComparison.Ordinal);
            Assert.DoesNotContain("進行中 経過", status, StringComparison.Ordinal);
            Assert.DoesNotContain("今回の未スコア未確認コミットを分析中", status, StringComparison.Ordinal);
            Assert.Equal("対象 15 件", NormalizeText(cut.Find("[data-testid=\"app-header-triage-target\"]").TextContent));
            Assert.Equal("分析済み 1 / 15 件", NormalizeText(cut.Find("[data-testid=\"app-header-triage-analyzed\"]").TextContent));
            Assert.Equal("保存済み 0 / 15 件", NormalizeText(cut.Find("[data-testid=\"app-header-triage-saved\"]").TextContent));
            Assert.Equal("コミット e4361356", NormalizeText(cut.Find("[data-testid=\"app-header-triage-reference\"]").TextContent));
        });

        gate.SetResult(new IngestionReport(Total: 1, Inserted: 1, Skipped: 0));
    }

    [Fact]
    public void Triage_Progress_Is_Structured_When_Agent_Reports_Ingestion_Phase()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var gate = new TaskCompletionSource<IngestionReport>();
        var sp = BuildServices(session, out var agent, out _);
        agent
            .RunMorningTriageAsync(Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<IProgress<string>?>()?.Report("未確認コミットを取り込み中: 新規 10 / 取得 12 件 (e760b391)");
                return gate.Task;
            });

        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-sync\"]").Click();

        cut.WaitForAssertion(() =>
        {
            var status = NormalizeText(cut.Find("[data-testid=\"app-header-triage-status\"]").TextContent);
            Assert.StartsWith("作業 未確認コミットを取り込み中 経過", status, StringComparison.Ordinal);
            Assert.DoesNotContain("未確認コミットを取り込み中:", status, StringComparison.Ordinal);
            Assert.Equal("新規 10 件", NormalizeText(cut.Find("[data-testid=\"app-header-triage-inserted\"]").TextContent));
            Assert.Equal("取得 12 件", NormalizeText(cut.Find("[data-testid=\"app-header-triage-fetched\"]").TextContent));
            Assert.Equal("コミット e760b391", NormalizeText(cut.Find("[data-testid=\"app-header-triage-reference\"]").TextContent));
        });

        gate.SetResult(new IngestionReport(Total: 12, Inserted: 10, Skipped: 0));
    }

    [Fact]
    public void Triage_Progress_Uses_Label_Chips_For_Generic_Busy_Status()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var gate = new TaskCompletionSource<IngestionReport>();
        var sp = BuildServices(session, out var agent, out _);
        agent
            .RunMorningTriageAsync(Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<IProgress<string>?>()?.Report("取り込み完了: 取得 12 / 新規 10 / スキップ 2");
                return gate.Task;
            });

        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-sync\"]").Click();

        cut.WaitForAssertion(() =>
        {
            var status = NormalizeText(cut.Find("[data-testid=\"app-header-triage-status\"]").TextContent);
            Assert.StartsWith("作業 未確認コミットを取り込み中 経過", status, StringComparison.Ordinal);
            Assert.Equal("取得 12 件", NormalizeText(cut.Find("[data-testid=\"app-header-triage-fetched\"]").TextContent));
            Assert.Equal("新規 10 件", NormalizeText(cut.Find("[data-testid=\"app-header-triage-inserted\"]").TextContent));
            Assert.Equal("スキップ 2 件", NormalizeText(cut.Find("[data-testid=\"app-header-triage-skipped\"]").TextContent));
        });

        gate.SetResult(new IngestionReport(Total: 12, Inserted: 10, Skipped: 2));
    }

    [Theory]
    [InlineData("Repo sync PR を取得しています…", "作業 Repo sync PR を取得中 経過")]
    [InlineData("Copilot セッションを準備しています…", "作業 Copilot セッションを準備中 経過")]
    public void Triage_Progress_Uses_Current_Work_Label_For_Busy_Status_Without_Counts(
        string progressMessage,
        string expectedPrefix)
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var gate = new TaskCompletionSource<IngestionReport>();
        var sp = BuildServices(session, out var agent, out _);
        agent
            .RunMorningTriageAsync(Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<IProgress<string>?>()?.Report(progressMessage);
                return gate.Task;
            });

        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-sync\"]").Click();

        cut.WaitForAssertion(() =>
        {
            var status = NormalizeText(cut.Find("[data-testid=\"app-header-triage-status\"]").TextContent);
            Assert.StartsWith(expectedPrefix, status, StringComparison.Ordinal);
            Assert.DoesNotContain(progressMessage, status, StringComparison.Ordinal);
        });

        gate.SetResult(new IngestionReport(Total: 0, Inserted: 0, Skipped: 0));
    }

    [Fact]
    public void Triage_Failure_Shows_Error_Without_Publishing_Broadcaster()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var sp = BuildServices(session, out var agent, out var broadcaster);
        agent
            .RunMorningTriageAsync(Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("network down"));

        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-sync\"]").Click();

        var err = cut.Find("[data-testid=\"app-header-error\"]");
        Assert.Contains("Triage 失敗", err.TextContent);
        Assert.Contains("network down", err.TextContent);
        broadcaster.DidNotReceive().Publish();
    }

    [Fact]
    public void SignIn_Button_Calls_SignInAsync_And_Refreshes_State()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        // First call (initial render) — NotSignedIn. After SignInAsync succeeds the
        // component re-queries state and should observe SignedIn so the UI flips.
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(GitHubAuthState.NotSignedIn),
                Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));

        var sp = BuildServices(session, out _, out _);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-signin\"]").Click();

        session.Received(1).SignInAsync(Arg.Any<CancellationToken>());
        Assert.Equal(
            "SignedIn",
            cut.Find("[data-testid=\"app-header-state\"]").GetAttribute("data-state"));
        Assert.Equal(
            "@octocat",
            cut.Find("[data-testid=\"app-header-login\"]").TextContent);
    }

    [Fact]
    public void Renders_SignedIn_State_Without_Login_When_UserApi_Returns_Null()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var sp = BuildServices(session, out _, out _);
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        // Still rendered as signed-in (Triage enabled), but no @login chip — the
        // header must not crash just because /user 5xx'd or rate-limited.
        Assert.Equal(
            "SignedIn",
            cut.Find("[data-testid=\"app-header-state\"]").GetAttribute("data-state"));
        Assert.Empty(cut.FindAll("[data-testid=\"app-header-login\"]"));
        Assert.False(cut.Find("[data-testid=\"app-header-sync\"]").HasAttribute("disabled"));
    }

    [Fact]
    public void Settings_Button_Loads_And_Renders_Ignore_Rules()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));
        var repo = Substitute.For<IRadarRepository>();
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IgnoreRule>>([
                new IgnoreRule
                {
                    Pattern = "content/copilot/**",
                    Reason = "ignore-directory",
                    CreatedAt = new DateTime(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc),
                },
                new IgnoreRule
                {
                    Pattern = "data/release-notes/**",
                    Reason = "noisy",
                    CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0, DateTimeKind.Utc),
                },
            ]));

        var sp = BuildServices(session, out _, out _, repo);
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-settings\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid=\"settings-panel\"]"));
            Assert.NotNull(cut.Find("[data-testid=\"settings-third-party-notices\"]"));
            Assert.DoesNotContain("無視リストを更新", cut.Find(".app-settings-header").TextContent, StringComparison.Ordinal);
            Assert.Equal(
                "無視リストを更新",
                cut.Find("[data-testid=\"settings-ignore-rules\"] [data-testid=\"settings-refresh-ignore-rules\"]").TextContent.Trim());
            var patterns = cut.FindAll("[data-testid=\"settings-ignore-rule-pattern\"]")
                .Select(static node => node.TextContent)
                .ToArray();
            Assert.Equal(["content/copilot/**", "data/release-notes/**"], patterns);
            Assert.Contains("ignore-directory", cut.Find("[data-testid=\"settings-ignore-rule-reason\"]").TextContent);
        });
        repo.Received(1).GetIgnoreRulesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Settings_Renders_Copilot_Token_Usage_And_Reset()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));
        var usageTracker = new CopilotUsageTracker();
        usageTracker.Record(new CopilotUsageRecord(
            new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            SessionPurpose.Triage.ToString(),
            "gpt-5",
            "api-1",
            1200,
            340,
            60,
            20,
            10,
            0.0042,
            123_000_000,
            []));

        var sp = BuildServices(session, out _, out _, usageTracker: usageTracker);
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        Assert.Contains("0.1230", cut.Find("[data-testid=\"app-header-copilot-usage\"]").TextContent);

        cut.Find("[data-testid=\"app-header-settings\"]").Click();

        var summary = cut.Find("[data-testid=\"settings-copilot-usage-summary\"]").TextContent;
        Assert.Contains("AI Credits0.1230 credits", summary, StringComparison.Ordinal);
        Assert.Contains("Premium Request コスト0.004200 PR", summary, StringComparison.Ordinal);
        Assert.Contains("合計1,600 tokens", summary, StringComparison.Ordinal);
        Assert.Contains("入力1,200 tokens", summary, StringComparison.Ordinal);
        Assert.Contains("出力340 tokens", summary, StringComparison.Ordinal);
        Assert.Contains("推論60 tokens", summary, StringComparison.Ordinal);
        var lastUsage = cut.Find("[data-testid=\"settings-copilot-usage-last\"]").TextContent;
        Assert.Contains("Triage", lastUsage, StringComparison.Ordinal);
        Assert.Contains("0.1230 credits", lastUsage, StringComparison.Ordinal);
        Assert.Contains("1,600 tokens", lastUsage, StringComparison.Ordinal);
        Assert.Contains("123,000,000 nano AIU", cut.Find("[data-testid=\"settings-copilot-usage-aiu\"]").TextContent);

        cut.Find("[data-testid=\"settings-copilot-usage-reset\"]").Click();

        Assert.Empty(cut.FindAll("[data-testid=\"app-header-copilot-usage\"]"));
        Assert.NotNull(cut.Find("[data-testid=\"settings-copilot-usage-empty\"]"));
    }

    [Fact]
    public void Header_Makes_Missing_Ai_Credits_Explicit()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));
        var usageTracker = new CopilotUsageTracker();
        usageTracker.Record(new CopilotUsageRecord(
            new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            SessionPurpose.Triage.ToString(),
            "gpt-5",
            "api-1",
            1000,
            200,
            0,
            0,
            0,
            null,
            null,
            []));

        var sp = BuildServices(session, out _, out _, usageTracker: usageTracker);
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        var usage = cut.Find("[data-testid=\"app-header-copilot-usage\"]").TextContent;
        Assert.Contains("AI Credits 未報告", usage, StringComparison.Ordinal);
        Assert.Contains("1,200 tokens", usage, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_Labels_Official_Pricing_Estimates_When_Sdk_Ai_Credits_Are_Missing()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));
        var usageTracker = new CopilotUsageTracker();
        usageTracker.Record(new CopilotUsageRecord(
            new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            SessionPurpose.Adoption.ToString(),
            "gpt-5.5",
            "api-1",
            100,
            10,
            5,
            20,
            0,
            null,
            null,
            []));

        var sp = BuildServices(session, out _, out _, usageTracker: usageTracker);
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        var headerUsage = cut.Find("[data-testid=\"app-header-copilot-usage\"]").TextContent;
        Assert.Contains("AI Credits", headerUsage, StringComparison.Ordinal);
        Assert.Contains("(推定)", headerUsage, StringComparison.Ordinal);

        cut.Find("[data-testid=\"app-header-settings\"]").Click();

        Assert.Contains("算出元: GitHub Docs 価格表から推定", cut.Find("[data-testid=\"settings-copilot-usage-aiu\"]").TextContent, StringComparison.Ordinal);
        Assert.Contains("GitHub Docs のモデル別価格表から概算", cut.Find("[data-testid=\"settings-copilot-usage-privacy\"]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_Renders_Beta4_Session_Usage_Metrics()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));
        var usageTracker = new CopilotUsageTracker();
        usageTracker.RecordSessionMetrics(new CopilotSessionUsageMetrics(
            new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero),
            "session-1",
            SessionPurpose.Triage.ToString(),
            "gpt-5",
            1000,
            220,
            30,
            40,
            10,
            250_000_000,
            2.5,
            3,
            700,
            180,
            []));

        var sp = BuildServices(session, out _, out _, usageTracker: usageTracker);
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-settings\"]").Click();

        var summary = cut.Find("[data-testid=\"settings-copilot-usage-summary\"]").TextContent;
        Assert.Contains("AI Credits0.2500 credits", summary, StringComparison.Ordinal);
        Assert.Contains("Premium Request コスト2.50 PR", summary, StringComparison.Ordinal);
        Assert.Contains("Premium Requests3 requests", summary, StringComparison.Ordinal);
        var metrics = cut.Find("[data-testid=\"settings-copilot-usage-session-metrics\"]").TextContent;
        Assert.Contains("gpt-5", metrics, StringComparison.Ordinal);
        Assert.Contains("3 requests", metrics, StringComparison.Ordinal);
        Assert.Contains("last call 700 tokens in, 180 tokens out", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_Delete_Ignore_Rule_Removes_Single_Row_And_Refreshes_List()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));
        var rules = new List<IgnoreRule>
        {
            new()
            {
                Pattern = "content/copilot/**",
                Reason = "ignore-directory",
                CreatedAt = new DateTime(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc),
            },
            new()
            {
                Pattern = "data/release-notes/**",
                Reason = "noisy",
                CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0, DateTimeKind.Utc),
            },
        };
        var repo = Substitute.For<IRadarRepository>();
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<IgnoreRule>>(rules.ToArray()));
        repo.DeleteIgnoreRulesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var patterns = call.Arg<IEnumerable<string>>().ToHashSet(StringComparer.Ordinal);
                return rules.RemoveAll(rule => patterns.Contains(rule.Pattern));
            });

        var sp = BuildServices(session, out _, out _, repo);
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-settings\"]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=\"settings-ignore-rule\"]").Count));

        cut.FindAll("[data-testid=\"settings-delete-ignore-rule\"]")[0].Click();

        cut.WaitForAssertion(() =>
        {
            var pattern = Assert.Single(cut.FindAll("[data-testid=\"settings-ignore-rule-pattern\"]"));
            Assert.Equal("data/release-notes/**", pattern.TextContent);
            Assert.Contains("1 件の無視リストを削除", cut.Find("[data-testid=\"settings-ignore-rules-delete-status\"]").TextContent, StringComparison.Ordinal);
        });
        var expectedPatterns = new[] { "content/copilot/**" };
        repo.Received(1).DeleteIgnoreRulesAsync(
            Arg.Is<IEnumerable<string>>(patterns => patterns.SequenceEqual(expectedPatterns)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Settings_Delete_Selected_Ignore_Rules_Removes_Multiple_Rows()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));
        var rules = new List<IgnoreRule>
        {
            new()
            {
                Pattern = "content/copilot/**",
                Reason = "ignore-directory",
                CreatedAt = new DateTime(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc),
            },
            new()
            {
                Pattern = "data/release-notes/**",
                Reason = "noisy",
                CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0, DateTimeKind.Utc),
            },
            new()
            {
                Pattern = "content/actions/**",
                Reason = "keep",
                CreatedAt = new DateTime(2026, 5, 12, 9, 0, 0, DateTimeKind.Utc),
            },
        };
        var repo = Substitute.For<IRadarRepository>();
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<IgnoreRule>>(rules.ToArray()));
        repo.DeleteIgnoreRulesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var patterns = call.Arg<IEnumerable<string>>().ToHashSet(StringComparer.Ordinal);
                return rules.RemoveAll(rule => patterns.Contains(rule.Pattern));
            });

        var sp = BuildServices(session, out _, out _, repo);
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-settings\"]").Click();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=\"settings-ignore-rule\"]").Count));
        cut.FindAll("[data-testid=\"settings-ignore-rule-select\"]")[0].Change(true);
        cut.FindAll("[data-testid=\"settings-ignore-rule-select\"]")[2].Change(true);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("2 件選択中", cut.Find("[data-testid=\"settings-ignore-rules-selected-count\"]").TextContent, StringComparison.Ordinal);
            Assert.False(cut.Find("[data-testid=\"settings-delete-selected-ignore-rules\"]").HasAttribute("disabled"));
        });

        cut.Find("[data-testid=\"settings-delete-selected-ignore-rules\"]").Click();

        cut.WaitForAssertion(() =>
        {
            var pattern = Assert.Single(cut.FindAll("[data-testid=\"settings-ignore-rule-pattern\"]"));
            Assert.Equal("data/release-notes/**", pattern.TextContent);
            Assert.Contains("2 件の無視リストを削除", cut.Find("[data-testid=\"settings-ignore-rules-delete-status\"]").TextContent, StringComparison.Ordinal);
            Assert.Contains("0 件選択中", cut.Find("[data-testid=\"settings-ignore-rules-selected-count\"]").TextContent, StringComparison.Ordinal);
        });
        var expectedPatterns = new[]
        {
            "content/copilot/**",
            "content/actions/**",
        };
        repo.Received(1).DeleteIgnoreRulesAsync(
            Arg.Is<IEnumerable<string>>(patterns => patterns.SequenceEqual(expectedPatterns)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Settings_Shows_Empty_State_When_No_Ignore_Rules_Exist()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));
        var repo = Substitute.For<IRadarRepository>();
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IgnoreRule>>([]));

        var sp = BuildServices(session, out _, out _, repo);
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-settings\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(
                "まだ設定されていません",
                cut.Find("[data-testid=\"settings-ignore-rules-empty\"]").TextContent,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Settings_DefaultTheme_Selection_Is_Rendered_And_Saved()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));
        var repo = Substitute.For<IRadarRepository>();
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IgnoreRule>>([]));
        var settingsStore = Substitute.For<IAppUserSettingsStore>();
        settingsStore.Current.Returns(new AppUserSettings { DefaultDocsTheme = DocsThemeMode.Dark });
        settingsStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AppUserSettings { DefaultDocsTheme = DocsThemeMode.Dark }));
        settingsStore.SaveDefaultDocsThemeAsync(DocsThemeMode.Light, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sp = BuildServices(session, out _, out _, repo, settingsStore);
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-settings\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", cut.Find("[data-testid=\"settings-default-theme-dark\"]").GetAttribute("aria-pressed"));
            Assert.Equal("false", cut.Find("[data-testid=\"settings-default-theme-light\"]").GetAttribute("aria-pressed"));
        });

        cut.Find("[data-testid=\"settings-default-theme-light\"]").Click();

        cut.WaitForAssertion(() =>
        {
            settingsStore.Received(1).SaveDefaultDocsThemeAsync(DocsThemeMode.Light, Arg.Any<CancellationToken>());
            Assert.Equal("false", cut.Find("[data-testid=\"settings-default-theme-dark\"]").GetAttribute("aria-pressed"));
            Assert.Equal("true", cut.Find("[data-testid=\"settings-default-theme-light\"]").GetAttribute("aria-pressed"));
            Assert.Contains("保存", cut.Find("[data-testid=\"settings-default-theme-status\"]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Settings_DisplayLanguage_Selection_Is_Rendered_And_Saved()
    {
        var session = Substitute.For<IGitHubAuthSession>();
        session
            .GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GitHubAuthState.SignedIn));
        session
            .GetCurrentLoginAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("octocat"));
        var repo = Substitute.For<IRadarRepository>();
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IgnoreRule>>([]));
        var settingsStore = Substitute.For<IAppUserSettingsStore>();
        settingsStore.Current.Returns(new AppUserSettings { DisplayCulture = "en" });
        settingsStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AppUserSettings { DisplayCulture = "en" }));
        settingsStore.SaveDisplayCultureAsync("ja", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sp = BuildServices(session, out _, out _, repo, settingsStore);
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<AppHeader>(
            p => p.AddCascadingValue<IServiceProvider>(sp));

        cut.Find("[data-testid=\"app-header-settings\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Settings", cut.Find("[data-testid=\"settings-panel\"]").TextContent, StringComparison.Ordinal);
            Assert.Equal("false", cut.Find("[data-testid=\"settings-display-language-ja\"]").GetAttribute("aria-pressed"));
            Assert.Equal("true", cut.Find("[data-testid=\"settings-display-language-en\"]").GetAttribute("aria-pressed"));
        });

        cut.Find("[data-testid=\"settings-display-language-ja\"]").Click();

        cut.WaitForAssertion(() =>
        {
            settingsStore.Received(1).SaveDisplayCultureAsync("ja", Arg.Any<CancellationToken>());
            Assert.Equal("true", cut.Find("[data-testid=\"settings-display-language-ja\"]").GetAttribute("aria-pressed"));
            Assert.Equal("false", cut.Find("[data-testid=\"settings-display-language-en\"]").GetAttribute("aria-pressed"));
            var status = cut.Find("[data-testid=\"settings-display-language-status\"]").TextContent;
            Assert.Contains("表示言語", status, StringComparison.Ordinal);
            Assert.DoesNotContain("すぐ反映", status, StringComparison.Ordinal);
        });
    }

    private static ServiceProvider BuildServices(
        IGitHubAuthSession session,
        out ICopilotAgent agent,
        out IReviewBroadcaster broadcaster,
        IRadarRepository? repo = null,
        IAppUserSettingsStore? settingsStore = null,
        ICopilotUsageTracker? usageTracker = null)
    {
        agent = Substitute.For<ICopilotAgent>();
        broadcaster = Substitute.For<IReviewBroadcaster>();
        var resolvedSettingsStore = settingsStore ?? Substitute.For<IAppUserSettingsStore>();
        var localSettingsStore = Substitute.For<ILocalAppSettingsStore>();
        localSettingsStore.SettingsPath.Returns(Path.Combine(Path.GetTempPath(), "appsettings.local.json"));
        localSettingsStore.Current.Returns(LocalAppSettings.Default.Clone());
        localSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(LocalAppSettings.Default.Clone()));
        localSettingsStore.SaveAsync(Arg.Any<LocalAppSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        if (settingsStore is null)
        {
            resolvedSettingsStore.Current.Returns(AppUserSettings.Default);
            resolvedSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(AppUserSettings.Default));
            resolvedSettingsStore.SaveDefaultDocsThemeAsync(Arg.Any<DocsThemeMode>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            resolvedSettingsStore.SaveDisplayCultureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
        }
        return new ServiceCollection()
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources")
            .AddSingleton(session)
            .AddSingleton(agent)
            .AddSingleton(broadcaster)
            .AddSingleton(repo ?? Substitute.For<IRadarRepository>())
            .AddSingleton(resolvedSettingsStore)
            .AddSingleton(localSettingsStore)
            .AddSingleton(usageTracker ?? new CopilotUsageTracker())
            .BuildServiceProvider();
    }

    private static string NormalizeText(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
