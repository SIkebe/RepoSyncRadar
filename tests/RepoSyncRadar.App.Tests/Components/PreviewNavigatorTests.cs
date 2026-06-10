using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

public sealed class PreviewNavigatorTests
{
    [Fact]
    public void Publish_Raises_Event_With_Uri()
    {
        var sut = new PreviewNavigator();
        Uri? captured = null;
        sut.Requested += (_, url) => captured = url;

        sut.Publish(new Uri("http://localhost:4500/en/foo"));

        Assert.Equal(new Uri("http://localhost:4500/en/foo"), captured);
    }

    [Fact]
    public void Publish_Raises_NavigationRequested_With_Uri_Case()
    {
        var sut = new PreviewNavigator();
        PreviewNavigationRequest? captured = null;
        var expected = new Uri("http://localhost:4500/en/foo");
        sut.NavigationRequested += (_, request) => captured = request;

        sut.Publish(expected);

        Assert.NotNull(captured);
        var actual = captured.Value switch
        {
            Uri url => url,
            PreviewComparisonRequest => throw new Xunit.Sdk.XunitException("Expected URI navigation request."),
            null => throw new Xunit.Sdk.XunitException("Expected non-null navigation request."),
        };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Publish_With_Null_Throws()
    {
        var sut = new PreviewNavigator();

        Assert.Throws<ArgumentNullException>(() => sut.Publish(null!));
    }

    [Fact]
    public void PublishComparison_Raises_Event_With_Request()
    {
        var sut = new PreviewNavigator();
        PreviewComparisonRequest? captured = null;
        sut.ComparisonRequested += (_, request) => captured = request;
        var request = new PreviewComparisonRequest(
            new Uri("http://localhost:4501/en/foo"),
            new Uri("http://localhost:4500/en/foo"),
            "変更前",
            "PR HEAD");

        sut.PublishComparison(request);

        Assert.Same(request, captured);
    }

    [Fact]
    public void PublishComparison_Raises_NavigationRequested_With_Comparison_Case()
    {
        var sut = new PreviewNavigator();
        PreviewNavigationRequest? captured = null;
        var request = new PreviewComparisonRequest(
            new Uri("http://localhost:4501/en/foo"),
            new Uri("http://localhost:4500/en/foo"),
            "変更前",
            "PR HEAD");
        sut.NavigationRequested += (_, navigationRequest) => captured = navigationRequest;

        sut.PublishComparison(request);

        Assert.NotNull(captured);
        var actual = captured.Value switch
        {
            Uri => throw new Xunit.Sdk.XunitException("Expected comparison navigation request."),
            PreviewComparisonRequest comparisonRequest => comparisonRequest,
            null => throw new Xunit.Sdk.XunitException("Expected non-null navigation request."),
        };
        Assert.Same(request, actual);
    }

    [Fact]
    public void Public_Methods_Do_Not_Require_Subscribers()
    {
        var sut = new PreviewNavigator();
        var request = new PreviewComparisonRequest(
            new Uri("http://localhost:4501/en/foo"),
            new Uri("http://localhost:4500/en/foo"),
            "変更前",
            "PR HEAD");

        sut.Publish(new Uri("http://localhost:4500/en/foo"));
        sut.PublishComparison(request);
        sut.RequestVersionChange(DocsVersionCatalog.Default);
        sut.RequestFileNavigation(PreviewFileNavigationDirection.Next);
    }

    [Fact]
    public void RequestVersionChange_Raises_Event_With_Version()
    {
        var sut = new PreviewNavigator();
        DocsVersion? captured = null;
        sut.VersionChangeRequested += (_, version) => captured = version;

        sut.RequestVersionChange(DocsVersionCatalog.All.First(version => version.Slug == "ghec"));

        Assert.NotNull(captured);
        Assert.Equal("ghec", captured!.Slug);
    }

    [Fact]
    public void RequestVersionChange_Null_Throws()
    {
        var sut = new PreviewNavigator();

        Assert.Throws<ArgumentNullException>(() => sut.RequestVersionChange(null!));
    }

    [Fact]
    public void RequestFileNavigation_Raises_Event_With_Direction()
    {
        var sut = new PreviewNavigator();
        PreviewFileNavigationDirection? captured = null;
        sut.FileNavigationRequested += (_, direction) => captured = direction;

        sut.RequestFileNavigation(PreviewFileNavigationDirection.Next);

        Assert.Equal(PreviewFileNavigationDirection.Next, captured);
    }

    [Theory]
    [InlineData(PreviewFileNavigationDirection.Previous, -1)]
    [InlineData(PreviewFileNavigationDirection.Next, 1)]
    [InlineData((PreviewFileNavigationDirection)42, 0)]
    public void GetOffset_Maps_Direction_To_Index_Delta(
        PreviewFileNavigationDirection direction,
        int expected)
    {
        Assert.Equal(expected, PreviewFileNavigationDirections.GetOffset(direction));
    }

    [Fact]
    public void PreviewComparisonRequest_Defaults_Version_Metadata_To_Null()
    {
        var sut = new PreviewComparisonRequest(
            new Uri("http://localhost:4501/en/foo"),
            new Uri("http://localhost:4500/en/foo"),
            "変更前",
            "PR HEAD");

        Assert.Null(sut.CurrentVersion);
        Assert.Null(sut.AffectedVersions);
        Assert.Equal(0, sut.SourceChangeCount);
    }
}
