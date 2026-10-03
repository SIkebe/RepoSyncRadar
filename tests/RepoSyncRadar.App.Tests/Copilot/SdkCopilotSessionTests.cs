using GitHub.Copilot;
using RepoSyncRadar.App.Copilot;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class SdkCopilotSessionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Assistant_Message_Wire_Event_Preserves_Response_Content(int repetitions)
    {
        var content = string.Concat(Enumerable.Repeat("\u65e5\u672c\u8a9e\U0001f600", repetitions));
        var json = $$"""
            {
                "data": { "messageId": "message-1", "content": "{{content}}" },
                "type": "assistant.message"
            }
            """;

        var assistant = Assert.IsType<AssistantMessageEvent>(SessionEvent.FromJson(json));

        Assert.Equal(content, assistant.Data.Content);
        Assert.Equal("message-1", assistant.Data.MessageId);
    }

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
