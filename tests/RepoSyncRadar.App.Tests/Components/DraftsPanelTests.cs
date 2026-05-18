using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.App.Tests.Copilot.Tools;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

/// <summary>
/// bUnit tests for the <see cref="DraftsPanel"/> component (IMPLEMENTATION_PLAN.md §Step 17.3).
/// </summary>
public sealed class DraftsPanelTests
{
    [Fact]
    public async Task Renders_Three_Sections()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("sha1", ct);
        await using (var seed = harness.CreateDb())
        {
            var nowUtc = DateTime.UtcNow;
            seed.Drafts.AddRange(
                new Draft { Sha = "sha1", Channel = "twitter", Body = "TW", GeneratedAt = nowUtc },
                new Draft { Sha = "sha1", Channel = "teams", Body = "TM", GeneratedAt = nowUtc },
                new Draft { Sha = "sha1", Channel = "customer", Body = "CU", GeneratedAt = nowUtc },
                new Draft { Sha = "sha1", Channel = "explanation", Body = "EX", GeneratedAt = nowUtc });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = new Bunit.BunitContext();
        var clipboard = Substitute.For<IClipboard>();
        var agent = Substitute.For<ICopilotAgent>();
        ctx.Services
            .AddSingleton(harness.DbFactory)
            .AddSingleton(clipboard)
            .AddSingleton(agent);

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid=\"drafts-section-twitter\"]"));
            Assert.NotNull(cut.Find("[data-testid=\"drafts-section-teams\"]"));
            Assert.NotNull(cut.Find("[data-testid=\"drafts-section-customer\"]"));
            Assert.NotNull(cut.Find("[data-testid=\"drafts-section-explanation\"]"));
            Assert.Contains("TW", cut.Find("[data-testid=\"drafts-body-twitter\"]").TextContent);
            Assert.Contains("TM", cut.Find("[data-testid=\"drafts-body-teams\"]").TextContent);
            Assert.Contains("CU", cut.Find("[data-testid=\"drafts-body-customer\"]").TextContent);
            Assert.Contains("EX", cut.Find("[data-testid=\"drafts-body-explanation\"]").TextContent);
        });
    }

    [Fact]
    public async Task Copy_Button_Invokes_Clipboard()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("sha1", ct);
        await using (var seed = harness.CreateDb())
        {
            seed.Drafts.Add(new Draft { Sha = "sha1", Channel = "twitter", Body = "tweet-body", GeneratedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = new Bunit.BunitContext();
        var clipboard = Substitute.For<IClipboard>();
        clipboard.SetTextAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        var agent = Substitute.For<ICopilotAgent>();
        ctx.Services
            .AddSingleton(harness.DbFactory)
            .AddSingleton(clipboard)
            .AddSingleton(agent);

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=\"drafts-copy-twitter\"]"));
        cut.Find("[data-testid=\"drafts-copy-twitter\"]").Click();
        await clipboard.Received(1).SetTextAsync("tweet-body");
    }

    [Fact]
    public async Task Regenerate_Calls_AdoptionSession_Again()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("sha1", ct);
        await using (var seed = harness.CreateDb())
        {
            seed.Drafts.Add(new Draft { Sha = "sha1", Channel = "twitter", Body = "old", GeneratedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = new Bunit.BunitContext();
        var clipboard = Substitute.For<IClipboard>();
        var agent = Substitute.For<ICopilotAgent>();
        agent.GenerateDraftsAsync("sha1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DraftBundle("new-tw", "new-sl", "new-cu", "new-ex")));

        ctx.Services
            .AddSingleton(harness.DbFactory)
            .AddSingleton(clipboard)
            .AddSingleton(agent);

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=\"drafts-regenerate\"]"));
        cut.Find("[data-testid=\"drafts-regenerate\"]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("new-tw", cut.Find("[data-testid=\"drafts-body-twitter\"]").TextContent);
            Assert.Contains("new-ex", cut.Find("[data-testid=\"drafts-body-explanation\"]").TextContent);
        });
        await agent.Received(1).GenerateDraftsAsync("sha1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Regenerate_Shows_Friendly_Progress_While_Running()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("sha1", ct);

        var pending = new TaskCompletionSource<DraftBundle>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ctx = new Bunit.BunitContext();
        var clipboard = Substitute.For<IClipboard>();
        var agent = Substitute.For<ICopilotAgent>();
        agent.GenerateDraftsAsync("sha1", Arg.Any<CancellationToken>())
            .Returns(pending.Task);

        ctx.Services
            .AddSingleton(harness.DbFactory)
            .AddSingleton(clipboard)
            .AddSingleton(agent);

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=\"drafts-regenerate\"]"));
        cut.Find("[data-testid=\"drafts-regenerate\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Copilot が差分解説と共有文案を作成中", cut.Find("[data-testid=\"drafts-progress-text\"]").TextContent, StringComparison.Ordinal);
            Assert.Contains("経過", cut.Find("[data-testid=\"drafts-progress-elapsed\"]").TextContent, StringComparison.Ordinal);
            Assert.Contains("完了すると", cut.Find("[data-testid=\"drafts-busy\"]").TextContent, StringComparison.Ordinal);
            Assert.False(cut.Find("[data-testid=\"drafts-cancel\"]").HasAttribute("disabled"));
        });

        pending.SetResult(new DraftBundle("new-tw", "new-tm", "new-cu", "new-ex"));
        cut.WaitForAssertion(() =>
            Assert.Contains("new-ex", cut.Find("[data-testid=\"drafts-body-explanation\"]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_Stops_Regeneration_And_Shows_Status()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("sha1", ct);

        CancellationToken capturedToken = default;
        using var ctx = new Bunit.BunitContext();
        var clipboard = Substitute.For<IClipboard>();
        var agent = Substitute.For<ICopilotAgent>();
        agent.GenerateDraftsAsync("sha1", Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedToken = call.Arg<CancellationToken>();
                var pending = new TaskCompletionSource<DraftBundle>(TaskCreationOptions.RunContinuationsAsynchronously);
                capturedToken.Register(() => pending.TrySetCanceled(capturedToken));
                return pending.Task;
            });

        ctx.Services
            .AddSingleton(harness.DbFactory)
            .AddSingleton(clipboard)
            .AddSingleton(agent);

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=\"drafts-regenerate\"]"));
        cut.Find("[data-testid=\"drafts-regenerate\"]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=\"drafts-cancel\"]"));

        cut.Find("[data-testid=\"drafts-cancel\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(capturedToken.IsCancellationRequested);
            Assert.Contains("再生成を中止しました", cut.Find("[data-testid=\"drafts-status\"]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Regenerate_Shows_Friendly_Message_For_Json_Parse_Failures()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("sha1", ct);

        using var ctx = new Bunit.BunitContext();
        var clipboard = Substitute.For<IClipboard>();
        var agent = Substitute.For<ICopilotAgent>();
        agent.GenerateDraftsAsync("sha1", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DraftBundle>(new InvalidOperationException("Adoption session returned non-JSON output.")));

        ctx.Services
            .AddSingleton(harness.DbFactory)
            .AddSingleton(clipboard)
            .AddSingleton(agent);

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=\"drafts-regenerate\"]"));
        cut.Find("[data-testid=\"drafts-regenerate\"]").Click();

        cut.WaitForAssertion(() =>
        {
            var status = cut.Find("[data-testid=\"drafts-status\"]").TextContent;
            Assert.Contains("Copilot の応答を文案として読み取れませんでした", status, StringComparison.Ordinal);
            Assert.DoesNotContain("non-JSON", status, StringComparison.OrdinalIgnoreCase);
        });
    }
}
