using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

/// <summary>
/// Tests for <see cref="PreviewSession"/> (IMPLEMENTATION_PLAN.md §Step 19.5). The session
/// tracks the active preview port so that the WebView2 resource filter in
/// <c>MainWindow</c> can let <c>http://localhost:{port}/*</c> pass alongside the regular
/// HTTPS allow-list.
/// </summary>
public sealed class PreviewSessionTests
{
    private static readonly int[] _comparisonPorts = [4500, 4501];

    [Fact]
    public void Inactive_Blocks_All()
    {
        var sut = new PreviewSession();

        Assert.False(sut.IsAllowed(new Uri("http://localhost:4500/en/foo")));
        Assert.False(sut.IsAllowed(new Uri("https://docs.github.com/en")));
        Assert.False(sut.IsActive);
        Assert.Null(sut.ActivePort);
    }

    [Theory]
    [InlineData("http://localhost:4500/en/foo")]
    [InlineData("http://localhost:4500/")]
    [InlineData("http://127.0.0.1:4500/en/foo")]
    [InlineData("http://[::1]:4500/en/foo")]
    public void Active_Allows_Matching_Loopback(string url)
    {
        var sut = new PreviewSession();
        sut.Activate(4500);

        Assert.True(sut.IsAllowed(new Uri(url)));
    }

    [Theory]
    [InlineData("http://localhost:4500/en/foo")]
    [InlineData("http://localhost:4501/en/foo")]
    public void Active_Allows_Multiple_Matching_Loopback_Ports(string url)
    {
        var sut = new PreviewSession();
        sut.Activate(4500, 4501);

        Assert.True(sut.IsAllowed(new Uri(url)));
        Assert.Equal(4500, sut.ActivePort);
        Assert.Equal(_comparisonPorts, sut.ActivePorts);
    }

    [Theory]
    [InlineData("http://localhost:5000/en/foo")] // wrong port
    [InlineData("https://docs.github.com/en")]    // wrong host
    [InlineData("http://example.com:4500/")]       // not loopback
    public void Active_Blocks_Non_Matching(string url)
    {
        var sut = new PreviewSession();
        sut.Activate(4500);

        Assert.False(sut.IsAllowed(new Uri(url)));
    }

    [Fact]
    public void Activate_Then_Deactivate_Restores_Inactive_State()
    {
        var sut = new PreviewSession();
        sut.Activate(4500);
        Assert.True(sut.IsActive);

        sut.Deactivate();

        Assert.False(sut.IsActive);
        Assert.Null(sut.ActivePort);
        Assert.False(sut.IsAllowed(new Uri("http://localhost:4500/")));
    }

    [Fact]
    public void Activate_With_Invalid_Port_Throws()
    {
        var sut = new PreviewSession();

        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Activate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Activate(70000));
        Assert.Throws<ArgumentException>(() => sut.Activate());
    }
}
