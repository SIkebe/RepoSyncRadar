using System.Xml.Linq;
using RepoSyncRadar.App.Settings;
using Xunit;

namespace RepoSyncRadar.App.Tests.Settings;

public sealed class ThirdPartyNoticesTests
{
    [Fact]
    public void Notices_Cover_All_Runtime_Project_PackageReferences()
    {
        var root = FindRepositoryRoot();
        var versions = XDocument.Load(Path.Combine(root, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .ToDictionary(
                static element => element.Attribute("Include")?.Value ?? string.Empty,
                static element => element.Attribute("Version")?.Value ?? string.Empty,
                StringComparer.Ordinal);
        var packageReferences = EnumeratePackageReferences(
                Path.Combine(root, "src", "RepoSyncRadar.App", "RepoSyncRadar.App.csproj"))
            .Concat(EnumeratePackageReferences(
                Path.Combine(root, "src", "RepoSyncRadar.Core", "RepoSyncRadar.Core.csproj")))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var notices = ThirdPartyNotices.All.ToDictionary(static notice => notice.PackageId, StringComparer.Ordinal);

        foreach (var packageId in packageReferences)
        {
            Assert.True(notices.ContainsKey(packageId), $"Missing third-party notice for {packageId}.");
            Assert.True(versions.TryGetValue(packageId, out var expectedVersion), $"Missing central package version for {packageId}.");
            Assert.Equal(expectedVersion, notices[packageId].Version);
        }
    }

    [Fact]
    public void Notices_Have_License_And_Links()
    {
        foreach (var notice in ThirdPartyNotices.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(notice.PackageId));
            Assert.False(string.IsNullOrWhiteSpace(notice.Version));
            Assert.False(string.IsNullOrWhiteSpace(notice.License));
            Assert.False(string.IsNullOrWhiteSpace(notice.Copyright));
            Assert.False(string.IsNullOrWhiteSpace(notice.LicenseText));
            Assert.Contains(notice.Copyright, notice.LicenseText, StringComparison.Ordinal);
            Assert.True(Uri.TryCreate(notice.ProjectUrl, UriKind.Absolute, out _), notice.ProjectUrl);
            Assert.True(Uri.TryCreate(notice.LicenseUrl, UriKind.Absolute, out _), notice.LicenseUrl);
        }
    }

    [Fact]
    public void Notices_Include_Package_Specific_License_Text()
    {
        var notices = ThirdPartyNotices.All.ToDictionary(static notice => notice.PackageId, StringComparer.Ordinal);

        Assert.Contains("Copyright 2026 MudBlazor", notices["MudBlazor"].LicenseText, StringComparison.Ordinal);
        Assert.Contains("Copyright GitHub 2017", notices["Octokit"].LicenseText, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2021 Daniel Peñalba", notices["TextMateSharp"].LicenseText, StringComparison.Ordinal);
        Assert.Contains("Oniguruma LICENSE", notices["Onigwrap"].LicenseText, StringComparison.Ordinal);
        Assert.Contains("K.Kosako", notices["Onigwrap"].LicenseText, StringComparison.Ordinal);
        Assert.Contains("Redistribution and use in source and binary forms", notices["Microsoft.Web.WebView2"].LicenseText, StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumeratePackageReferences(string projectPath)
        => XDocument.Load(projectPath)
            .Descendants("PackageReference")
            .Select(static element => element.Attribute("Include")?.Value)
            .Where(static packageId => !string.IsNullOrWhiteSpace(packageId))
            .Select(static packageId => packageId!);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}