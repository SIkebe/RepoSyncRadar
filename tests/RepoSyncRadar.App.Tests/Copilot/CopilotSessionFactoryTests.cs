using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class CopilotSessionFactoryTests
{
    [Fact]
    public void ResolveFallbackModel_When_Configured_Model_Fails_Prefers_Current_Base_Model()
    {
        var fallback = CopilotSessionFactory.ResolveFallbackModel(
            "gpt-4.1",
            ["gpt-4.1", "gpt-5-mini", "gpt-5.3-codex", "claude-sonnet-4.6"]);

        Assert.Equal("gpt-5.3-codex", fallback);
    }

    [Fact]
    public void ResolveFallbackModel_When_Base_Model_Is_Missing_Prefers_Broadly_Available_Gpt5Mini()
    {
        var fallback = CopilotSessionFactory.ResolveFallbackModel(
            "gpt-4.1",
            ["gpt-4.1", "gpt-5-mini", "claude-haiku-4.5"]);

        Assert.Equal("gpt-5-mini", fallback);
    }

    [Fact]
    public void ResolveFallbackModel_Avoids_Retiring_Models_When_Alternatives_Exist()
    {
        var fallback = CopilotSessionFactory.ResolveFallbackModel(
            "gpt-5.5",
            ["gpt-4.1", "gpt-5.2", "custom-current"]);

        Assert.Equal("custom-current", fallback);
    }

    [Fact]
    public void ResolveFallbackModel_When_Preferred_Models_Are_Missing_Uses_First_Alternative()
    {
        var fallback = CopilotSessionFactory.ResolveFallbackModel(
            "gpt-5.5",
            ["custom-a", "custom-b"]);

        Assert.Equal("custom-a", fallback);
    }

    [Fact]
    public void ResolveFallbackModel_When_Model_Catalog_Is_Unavailable_Uses_Default_Fallback()
    {
        var fallback = CopilotSessionFactory.ResolveFallbackModel("gpt-5.5", []);

        Assert.Equal("gpt-5-mini", fallback);
    }

    [Fact]
    public void ResolveFallbackModel_When_No_Alternative_Remains_Returns_Null()
    {
        var fallback = CopilotSessionFactory.ResolveFallbackModel("gpt-5-mini", ["gpt-5-mini"]);

        Assert.Null(fallback);
    }

    [Fact]
    public void ConfigureFallbackModel_Clears_ModelSpecific_ReasoningEffort()
    {
        var config = new SessionConfig
        {
            Model = "gpt-5.6-luna",
            ReasoningEffort = "max",
        };

        CopilotSessionFactory.ConfigureFallbackModel(config, "gpt-5-mini");

        Assert.Equal("gpt-5-mini", config.Model);
        Assert.Null(config.ReasoningEffort);
    }

    [Fact]
    public void SelectToolsForPurpose_Adoption_Uses_Only_ReadOnly_Radar_Tools()
    {
        var readOnlyTool = CreateTool("radar_fetch_rendered");
        var writeTool = CreateTool("radar_save_review");

        var tools = CopilotSessionFactory.SelectToolsForPurpose(
            SessionPurpose.Adoption,
            [readOnlyTool],
            [writeTool]);

        Assert.Equal(["radar_fetch_rendered"], tools.Select(static tool => tool.Name));
    }

    [Fact]
    public void BuildClientOptions_Wires_Sdk_Diagnostics_And_Telemetry()
    {
        var copilot = new CopilotOptions
        {
            CliPath = " C:/tools/copilot.exe ",
            CopilotHome = " C:/data/copilot ",
            LogLevel = " debug ",
            SessionIdleTimeoutSeconds = 120,
            TelemetryFilePath = " C:/logs/copilot-otel.jsonl ",
            TelemetryOtlpProtocol = "http/json",
            CaptureContent = true,
            EnableRemoteSessions = true,
        };

        var logger = NullLogger<CopilotSessionFactory>.Instance;
        var options = CopilotSessionFactory.BuildClientOptions(copilot, logger, "token-123", "0.1.30");

        Assert.False(options.UseLoggedInUser);
        Assert.Same(logger, options.Logger);
        Assert.Equal("token-123", options.GitHubToken);
        Assert.NotNull(options.ClientInfo);
        Assert.Equal("RepoSyncRadar", options.ClientInfo!.ApplicationName);
        Assert.Equal("0.1.30", options.ClientInfo.ApplicationVersion);
        Assert.Null(options.ClientInfo.IntegrationName);
        Assert.Null(options.ClientInfo.IntegrationVersion);
        Assert.Equal("debug", options.LogLevel?.Value);
        Assert.True(options.EnableRemoteSessions);
        var stdio = Assert.IsType<StdioRuntimeConnection>(options.Connection);
        Assert.Equal("C:/tools/copilot.exe", stdio.Path);
        Assert.Equal("C:/data/copilot", options.BaseDirectory);
        Assert.Equal(120, options.SessionIdleTimeoutSeconds);
        Assert.NotNull(options.Telemetry);
        Assert.Equal("file", options.Telemetry!.ExporterType);
        Assert.Equal("C:/logs/copilot-otel.jsonl", options.Telemetry.FilePath);
        Assert.Equal("http/json", options.Telemetry.OtlpProtocol);
        Assert.Equal("RepoSyncRadar", options.Telemetry.SourceName);
        Assert.True(options.Telemetry.CaptureContent);
    }

    [Fact]
    public void BuildClientOptions_Disables_File_Telemetry_When_Path_Is_Missing()
    {
        var options = CopilotSessionFactory.BuildClientOptions(
            new CopilotOptions { CaptureContent = true },
            NullLogger<CopilotSessionFactory>.Instance,
            "token-123",
            "0.1.30");

        Assert.Null(options.Telemetry);
        Assert.Null(options.SessionIdleTimeoutSeconds);
    }

    [Fact]
    public void BuildClientOptions_Uses_Bundled_Stdio_When_CliPath_Is_Missing()
    {
        var options = CopilotSessionFactory.BuildClientOptions(
            new CopilotOptions(),
            NullLogger<CopilotSessionFactory>.Instance,
            "token-123",
            "0.1.30");

        var stdio = Assert.IsType<StdioRuntimeConnection>(options.Connection);
        Assert.Null(stdio.Path);
    }

    private static AIFunction CreateTool(string name)
        => AIFunctionFactory.Create(
            () => "ok",
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = "test tool",
            });
}
