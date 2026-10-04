using GitHub.Copilot;
using RepoSyncRadar.App.Copilot;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class CopilotModelFailureTests
{
    [Theory]
    [InlineData("http_400", CopilotModelFailureKind.InvalidRequest)]
    [InlineData("http_413", CopilotModelFailureKind.InputTooLarge)]
    [InlineData("http_429", CopilotModelFailureKind.RateLimited)]
    [InlineData("http_4xx", CopilotModelFailureKind.RequestRejected)]
    [InlineData("http_5xx", CopilotModelFailureKind.ServerError)]
    [InlineData("transport_error", CopilotModelFailureKind.TransportError)]
    public void Final_Result_Classifies_Actual_Session_Error_And_Preserves_Cause(string result, CopilotModelFailureKind kind)
    {
        var collector = new CopilotModelFailureCollector();
        collector.Observe(Final(result));
        Assert.Null(collector.Classify(new InvalidOperationException("JSON parsing failed")));
        collector.Observe(Error());
        var original = new InvalidOperationException("runtime error");

        var classified = Assert.IsType<CopilotModelFailureException>(collector.Classify(original));

        Assert.Equal(kind, classified.Kind);
        Assert.Same(original, classified.InnerException);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("other_error")]
    [InlineData("future_unknown")]
    public void Later_Success_Or_Unknown_Result_Does_Not_Recycle_Earlier_Failure(string result)
    {
        var collector = new CopilotModelFailureCollector();
        collector.Observe(Final("http_429"));
        collector.Observe(Final(result));
        collector.Observe(Error());

        Assert.Null(collector.Classify(new InvalidOperationException()));
    }

    [Fact]
    public void Subagent_Results_And_Errors_Do_Not_Classify_Root_Send()
    {
        var collector = new CopilotModelFailureCollector();
        var result = Final("http_429");
        result.AgentId = "subagent";
        collector.Observe(result);
        collector.Observe(Error());
        Assert.Null(collector.Classify(new InvalidOperationException()));

        collector.Observe(Final("http_413"));
        var error = Error();
        error.AgentId = "subagent";
        var fresh = new CopilotModelFailureCollector();
        fresh.Observe(Final("http_413"));
        fresh.Observe(error);
        Assert.Null(fresh.Classify(new InvalidOperationException()));
    }

    [Fact]
    public void Cancellation_Timeout_And_New_Send_Are_Not_Reclassified()
    {
        var collector = new CopilotModelFailureCollector();
        collector.Observe(Final("http_5xx"));
        collector.Observe(Error());

        Assert.Null(collector.Classify(new OperationCanceledException(TestContext.Current.CancellationToken)));
        Assert.Null(collector.Classify(new TimeoutException()));
        Assert.Null(new CopilotModelFailureCollector().Classify(new InvalidOperationException()));
    }

    [Theory]
    [InlineData("""{"type":"model.call_start","data":{"turnId":"next"}}""")]
    [InlineData("""{"type":"assistant.turn_start","data":{"turnId":"next"}}""")]
    [InlineData("""{"type":"tool.execution_start","data":{"toolName":"radar_read","toolCallId":"next"}}""")]
    public void New_Operation_Does_Not_Mislabel_NonModel_Failure(string json)
    {
        var collector = new CopilotModelFailureCollector();
        collector.Observe(Final("http_429"));
        collector.Observe(SessionEvent.FromJson(json));
        collector.Observe(Error());

        Assert.Null(collector.Classify(new InvalidOperationException("tool failed")));
    }

    private static SessionEvent Final(string result)
        => SessionEvent.FromJson($$$"""{"type":"model.call_final_result","data":{"model":"offline-test","result":"{{{result}}}"}}""");

    private static SessionEvent Error()
        => SessionEvent.FromJson("""{"type":"session.error","data":{"errorType":"model","message":"synthetic"}}""");
}
