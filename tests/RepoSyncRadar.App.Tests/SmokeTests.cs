using Xunit;

namespace RepoSyncRadar.App.Tests;

public class SmokeTests
{
    [Fact]
    public void Smoke_BUnit_TestContext_Can_Be_Constructed()
    {
        using var ctx = new Bunit.TestContext();

        Assert.NotNull(ctx);
        Assert.NotNull(ctx.Services);
    }
}
