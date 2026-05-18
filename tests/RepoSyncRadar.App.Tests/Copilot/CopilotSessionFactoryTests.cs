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
    public void BuildClientOptions_Wires_Sdk_Diagnostics_And_Telemetry()
    {
        var copilot = new CopilotOptions
        {
            CliPath = " C:/tools/copilot.exe ",
            CopilotHome = " C:/data/copilot ",
            LogLevel = " debug ",
            SessionIdleTimeoutSeconds = 120,
            TelemetryFilePath = " C:/logs/copilot-otel.jsonl ",
            CaptureContent = true,
        };

        var logger = NullLogger<CopilotSessionFactory>.Instance;
        var options = CopilotSessionFactory.BuildClientOptions(copilot, logger, "token-123");

        Assert.True(options.AutoStart);
        Assert.False(options.UseLoggedInUser);
        Assert.Same(logger, options.Logger);
        Assert.Equal("token-123", options.GitHubToken);
        Assert.Equal("debug", options.LogLevel);
        Assert.Equal("C:/tools/copilot.exe", options.CliPath);
        Assert.Equal("C:/data/copilot", options.CopilotHome);
        Assert.Equal(120, options.SessionIdleTimeoutSeconds);
        Assert.NotNull(options.Telemetry);
        Assert.Equal("file", options.Telemetry!.ExporterType);
        Assert.Equal("C:/logs/copilot-otel.jsonl", options.Telemetry.FilePath);
        Assert.Equal("RepoSyncRadar", options.Telemetry.SourceName);
        Assert.True(options.Telemetry.CaptureContent);
    }

    [Fact]
    public void BuildClientOptions_Disables_File_Telemetry_When_Path_Is_Missing()
    {
        var options = CopilotSessionFactory.BuildClientOptions(
            new CopilotOptions { CaptureContent = true },
            NullLogger<CopilotSessionFactory>.Instance,
            "token-123");

        Assert.Null(options.Telemetry);
        Assert.Null(options.SessionIdleTimeoutSeconds);
    }
}
