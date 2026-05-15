using RepoSyncRadar.App.Copilot;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class CopilotSessionFactoryTests
{
    [Fact]
    public void ResolveFallbackModel_When_Configured_Model_Fails_Prefers_Gpt5()
    {
        var fallback = CopilotSessionFactory.ResolveFallbackModel(
            "gpt-5.5",
            ["gpt-4.1", "gpt-5", "claude-sonnet-4.5"]);

        Assert.Equal("gpt-5", fallback);
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

        Assert.Equal("gpt-5", fallback);
    }

    [Fact]
    public void ResolveFallbackModel_When_No_Alternative_Remains_Returns_Null()
    {
        var fallback = CopilotSessionFactory.ResolveFallbackModel("gpt-5", ["gpt-5"]);

        Assert.Null(fallback);
    }
}
