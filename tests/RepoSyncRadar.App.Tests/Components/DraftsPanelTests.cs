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
                new Draft { Sha = "sha1", Channel = "customer", Body = "CU", GeneratedAt = nowUtc });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = new Bunit.TestContext();
        var clipboard = Substitute.For<IClipboard>();
        var agent = Substitute.For<ICopilotAgent>();
        ctx.Services
            .AddSingleton(harness.DbFactory)
            .AddSingleton(clipboard)
            .AddSingleton(agent);

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.RenderComponent<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid=\"drafts-section-twitter\"]"));
            Assert.NotNull(cut.Find("[data-testid=\"drafts-section-teams\"]"));
            Assert.NotNull(cut.Find("[data-testid=\"drafts-section-customer\"]"));
            Assert.Contains("TW", cut.Find("[data-testid=\"drafts-body-twitter\"]").TextContent);
            Assert.Contains("TM", cut.Find("[data-testid=\"drafts-body-teams\"]").TextContent);
            Assert.Contains("CU", cut.Find("[data-testid=\"drafts-body-customer\"]").TextContent);
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

        using var ctx = new Bunit.TestContext();
        var clipboard = Substitute.For<IClipboard>();
        clipboard.SetTextAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        var agent = Substitute.For<ICopilotAgent>();
        ctx.Services
            .AddSingleton(harness.DbFactory)
            .AddSingleton(clipboard)
            .AddSingleton(agent);

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.RenderComponent<DraftsPanel>(p => p
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

        using var ctx = new Bunit.TestContext();
        var clipboard = Substitute.For<IClipboard>();
        var agent = Substitute.For<ICopilotAgent>();
        agent.GenerateDraftsAsync("sha1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DraftBundle("new-tw", "new-sl", "new-cu")));

        ctx.Services
            .AddSingleton(harness.DbFactory)
            .AddSingleton(clipboard)
            .AddSingleton(agent);

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.RenderComponent<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=\"drafts-regenerate\"]"));
        cut.Find("[data-testid=\"drafts-regenerate\"]").Click();
        cut.WaitForAssertion(() =>
            Assert.Contains("new-tw", cut.Find("[data-testid=\"drafts-body-twitter\"]").TextContent));
        await agent.Received(1).GenerateDraftsAsync("sha1", Arg.Any<CancellationToken>());
    }
}
