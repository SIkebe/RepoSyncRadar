using RepoSyncRadar.App.Copilot;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class SdkCopilotSessionTests
{
    [Theory]
    [InlineData(null, "tw", "cu", "explanation")]
    [InlineData("ex", null, "cu", "twitter")]
    [InlineData("ex", "tw", null, "customer")]
    public void CreateDraftBundle_Rejects_Missing_Fields(
        string? explanation,
        string? twitter,
        string? customer,
        string missingField)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SdkCopilotSession.CreateDraftBundle(explanation, twitter, customer));

        Assert.Contains(missingField, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDraftBundle_Maps_All_Fields()
    {
        var bundle = SdkCopilotSession.CreateDraftBundle("ex", "tw", "cu");

        Assert.Equal("ex", bundle.ExplanationJa);
        Assert.Equal("tw", bundle.TwitterJa);
        Assert.Equal("cu", bundle.CustomerJa);
        Assert.Empty(bundle.TeamsJa);
    }
}
