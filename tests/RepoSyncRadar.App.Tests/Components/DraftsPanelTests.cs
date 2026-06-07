using AngleSharp.Html.Dom;
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
    public async Task Renders_Draft_Sections_Without_Legacy_Teams()
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
            .AddSingleton(agent)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid=\"drafts-section-twitter\"]"));
            Assert.NotNull(cut.Find("[data-testid=\"drafts-section-customer\"]"));
            Assert.NotNull(cut.Find("[data-testid=\"drafts-section-explanation\"]"));
            Assert.Equal("TW", TextAreaValue(cut, "twitter"));
            Assert.Equal("CU", TextAreaValue(cut, "customer"));
            Assert.Equal("EX", TextAreaValue(cut, "explanation"));
            Assert.Empty(cut.FindAll("[data-testid=\"drafts-section-teams\"]"));
            Assert.DoesNotContain("TM", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Renders_Placeholders_When_Drafts_Have_Not_Been_Generated()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("sha1", ct);

        using var ctx = new Bunit.BunitContext();
        var clipboard = Substitute.For<IClipboard>();
        var agent = Substitute.For<ICopilotAgent>();
        ctx.Services
            .AddSingleton(harness.DbFactory)
            .AddSingleton(clipboard)
            .AddSingleton(agent)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("まだ生成されていません", cut.Find("[data-testid=\"drafts-empty-explanation\"]").TextContent, StringComparison.Ordinal);
            Assert.Contains("再生成ボタン", cut.Find("[data-testid=\"drafts-empty-twitter\"]").TextContent, StringComparison.Ordinal);
            Assert.True(cut.Find("[data-testid=\"drafts-copy-explanation\"]").HasAttribute("disabled"));
            Assert.Empty(cut.FindAll("[data-testid=\"drafts-body-explanation\"]"));
        });
    }

    [Fact]
    public async Task Copy_Button_Invokes_Clipboard_With_Current_Edit()
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
            .AddSingleton(agent)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=\"drafts-copy-twitter\"]"));
        cut.Find("[data-testid=\"drafts-body-twitter\"]").Input("edited tweet");
        cut.Find("[data-testid=\"drafts-copy-twitter\"]").Click();
        await clipboard.Received(1).SetTextAsync("edited tweet");
    }

    [Fact]
    public async Task Save_Persists_Edited_Draft_And_Reloads_It()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("sha1", ct);
        await using (var seed = harness.CreateDb())
        {
            var nowUtc = DateTime.UtcNow.AddMinutes(-1);
            seed.Drafts.AddRange(
                new Draft { Sha = "sha1", Channel = "twitter", Body = "old tweet", GeneratedAt = nowUtc },
                new Draft { Sha = "sha1", Channel = "twitter", Body = "older duplicate", GeneratedAt = nowUtc.AddMinutes(-1) });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = new Bunit.BunitContext();
        var clipboard = Substitute.For<IClipboard>();
        var agent = Substitute.For<ICopilotAgent>();
        ctx.Services
            .AddSingleton(harness.DbFactory)
            .AddSingleton(clipboard)
            .AddSingleton(agent)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() => Assert.Equal("old tweet", TextAreaValue(cut, "twitter")));
        cut.Find("[data-testid=\"drafts-body-twitter\"]").Input("edited tweet");
        cut.Find("[data-testid=\"drafts-save-twitter\"]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("保存しました", cut.Find("[data-testid=\"drafts-status\"]").TextContent, StringComparison.Ordinal));
        await using (var verify = harness.CreateDb())
        {
            var drafts = await verify.Drafts.AsNoTracking().Where(d => d.Sha == "sha1" && d.Channel == "twitter").ToListAsync(ct);
            Assert.Single(drafts);
            Assert.Equal("edited tweet", drafts[0].Body);
        }

        await harness.InsertCommitAsync("sha2", ct);
        cut.Render(parameters => parameters.Add(c => c.Sha, "sha2"));
        cut.Render(parameters => parameters.Add(c => c.Sha, "sha1"));
        cut.WaitForAssertion(() => Assert.Equal("edited tweet", TextAreaValue(cut, "twitter")));
    }

    [Fact]
    public async Task Save_Shows_Friendly_Status_When_Database_Save_Fails()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("sha1", ct);
        await using (var seed = harness.CreateDb())
        {
            seed.Drafts.Add(new Draft { Sha = "sha1", Channel = "twitter", Body = "old tweet", GeneratedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = new Bunit.BunitContext();
        var clipboard = Substitute.For<IClipboard>();
        var agent = Substitute.For<ICopilotAgent>();
        ctx.Services
            .AddSingleton<IDbContextFactory<RadarDbContext>>(new FailingAfterFirstContextFactory(harness.DbFactory))
            .AddSingleton(clipboard)
            .AddSingleton(agent)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() => Assert.Equal("old tweet", TextAreaValue(cut, "twitter")));
        cut.Find("[data-testid=\"drafts-body-twitter\"]").Input("edited tweet");
        cut.Find("[data-testid=\"drafts-save-twitter\"]").Click();

        cut.WaitForAssertion(() =>
        {
            var status = cut.Find("[data-testid=\"drafts-status\"]").TextContent;
            Assert.Contains("保存に失敗しました", status, StringComparison.Ordinal);
            Assert.Contains("database locked", status, StringComparison.Ordinal);
            Assert.Equal("edited tweet", TextAreaValue(cut, "twitter"));
            Assert.Contains("未保存", cut.Find("[data-testid=\"drafts-dirty-twitter\"]").TextContent, StringComparison.Ordinal);
        });

        await using var verify = harness.CreateDb();
        var draft = await verify.Drafts.AsNoTracking().SingleAsync(d => d.Sha == "sha1" && d.Channel == "twitter", ct);
        Assert.Equal("old tweet", draft.Body);
    }

    [Fact]
    public async Task Revert_Restores_Saved_Draft_Text()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("sha1", ct);
        await using (var seed = harness.CreateDb())
        {
            seed.Drafts.Add(new Draft { Sha = "sha1", Channel = "customer", Body = "saved customer", GeneratedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = new Bunit.BunitContext();
        var clipboard = Substitute.For<IClipboard>();
        var agent = Substitute.For<ICopilotAgent>();
        ctx.Services
            .AddSingleton(harness.DbFactory)
            .AddSingleton(clipboard)
            .AddSingleton(agent)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() => Assert.Equal("saved customer", TextAreaValue(cut, "customer")));
        cut.Find("[data-testid=\"drafts-body-customer\"]").Input("edited customer");
        cut.WaitForAssertion(() => Assert.Contains("未保存", cut.Find("[data-testid=\"drafts-dirty-customer\"]").TextContent, StringComparison.Ordinal));

        cut.Find("[data-testid=\"drafts-revert-customer\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("saved customer", TextAreaValue(cut, "customer"));
            Assert.Empty(cut.FindAll("[data-testid=\"drafts-dirty-customer\"]"));
        });
    }

    [Fact]
    public async Task Changing_Sha_Clears_Previous_Status()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("sha1", ct);
        await harness.InsertCommitAsync("sha2", ct);
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
            .AddSingleton(agent)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=\"drafts-copy-twitter\"]"));
        cut.Find("[data-testid=\"drafts-copy-twitter\"]").Click();
        cut.WaitForAssertion(() =>
            Assert.Contains("コピーしました", cut.Find("[data-testid=\"drafts-status\"]").TextContent, StringComparison.Ordinal));

        cut.Render(parameters => parameters.Add(c => c.Sha, "sha2"));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=\"drafts-status\"]"));
            Assert.NotNull(cut.Find("[data-testid=\"drafts-empty-twitter\"]"));
        });
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
            .AddSingleton(agent)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=\"drafts-regenerate\"]"));
        cut.Find("[data-testid=\"drafts-regenerate\"]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("new-tw", TextAreaValue(cut, "twitter"));
            Assert.Equal("new-ex", TextAreaValue(cut, "explanation"));
        });
        await agent.Received(1).GenerateDraftsAsync("sha1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Regenerate_With_Unsaved_Edit_Requires_Confirmation()
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
            .AddSingleton(agent)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=\"drafts-body-twitter\"]"));
        cut.Find("[data-testid=\"drafts-body-twitter\"]").Input("dirty edit");
        cut.Find("[data-testid=\"drafts-regenerate\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("未保存の編集", cut.Find("[data-testid=\"drafts-status\"]").TextContent, StringComparison.Ordinal);
            Assert.NotNull(cut.Find("[data-testid=\"drafts-regenerate-confirm\"]"));
        });
        await agent.DidNotReceive().GenerateDraftsAsync("sha1", Arg.Any<CancellationToken>());

        cut.Find("[data-testid=\"drafts-regenerate-confirm\"]").Click();
        cut.WaitForAssertion(() => Assert.Equal("new-tw", TextAreaValue(cut, "twitter")));
        await agent.Received(1).GenerateDraftsAsync("sha1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Validation_Shows_Empty_Url_Warning_And_Twitter_Count()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var harness = await WriteHarness.CreateAsync(ct);
        await harness.InsertCommitAsync("sha1", ct);
        await using (var seed = harness.CreateDb())
        {
            seed.CommitFiles.Add(new CommitFile
            {
                Sha = "sha1",
                Path = "content/actions/example.md",
                Status = "modified",
            });
            seed.PathUrlMaps.Add(new PathUrlMap
            {
                Path = "content/actions/example.md",
                Version = "fpt",
                Language = "ja",
                Url = "/ja/actions/example",
                ResolvedAt = DateTime.UtcNow,
            });
            seed.Drafts.AddRange(
                new Draft { Sha = "sha1", Channel = "twitter", Body = "short https://example.com/path", GeneratedAt = DateTime.UtcNow },
                new Draft { Sha = "sha1", Channel = "customer", Body = "customer body", GeneratedAt = DateTime.UtcNow },
                new Draft { Sha = "sha1", Channel = "explanation", Body = "explanation body", GeneratedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = new Bunit.BunitContext();
        var clipboard = Substitute.For<IClipboard>();
        var agent = Substitute.For<ICopilotAgent>();
        ctx.Services
            .AddSingleton(harness.DbFactory)
            .AddSingleton(clipboard)
            .AddSingleton(agent)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");

        var sp = ctx.Services.BuildServiceProvider();
        var cut = ctx.Render<DraftsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(sp)
            .Add(c => c.Sha, "sha1"));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("X 文字数: 29", cut.Find("[data-testid=\"drafts-count-twitter\"]").TextContent, StringComparison.Ordinal);
            Assert.Contains("公式 URL", cut.Find("[data-testid=\"drafts-warning-twitter\"]").TextContent, StringComparison.Ordinal);
            Assert.Contains("公式 URL", cut.Find("[data-testid=\"drafts-warning-customer\"]").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=\"drafts-warning-explanation\"]"));
        });

        cut.Find("[data-testid=\"drafts-body-explanation\"]").Input(string.Empty);
        cut.WaitForAssertion(() =>
            Assert.Contains("本文が空", cut.Find("[data-testid=\"drafts-warning-explanation\"]").TextContent, StringComparison.Ordinal));
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
            .AddSingleton(agent)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");

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
            Assert.Equal("new-ex", TextAreaValue(cut, "explanation")));
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
            .AddSingleton(agent)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");

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
            .AddSingleton(agent)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources");

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

    private static string TextAreaValue(IRenderedComponent<DraftsPanel> cut, string channel)
        => Assert.IsAssignableFrom<IHtmlTextAreaElement>(cut.Find($"[data-testid=\"drafts-body-{channel}\"]")).Value;

    private sealed class FailingAfterFirstContextFactory(IDbContextFactory<RadarDbContext> inner) : IDbContextFactory<RadarDbContext>
    {
        private int _createCount;

        public RadarDbContext CreateDbContext()
        {
            _createCount++;
            if (_createCount > 1)
            {
                throw new InvalidOperationException("database locked");
            }

            return inner.CreateDbContext();
        }
    }
}
