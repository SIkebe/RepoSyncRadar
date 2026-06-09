using RepoSyncRadar.App.Components;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

public sealed class LocalizationConventionTests
{
    [Fact]
    public void Localized_Child_Components_Inherit_LocalizedComponentBase()
    {
        var componentsDirectory = Path.Combine(FindRepositoryRoot(), "src", "RepoSyncRadar.App", "Components");
        var exceptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "AppHeader.razor",
            "Workbench.razor",
        };

        var localizedComponents = Directory
            .EnumerateFiles(componentsDirectory, "*.razor", SearchOption.TopDirectoryOnly)
            .Where(path => !exceptions.Contains(Path.GetFileName(path)))
            .Where(path => File.ReadAllText(path).Contains("IStringLocalizer<SharedResource>", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(localizedComponents);
        foreach (var component in localizedComponents)
        {
            var source = File.ReadAllText(component);
            Assert.Contains("@inherits LocalizedComponentBase", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ApplyDisplayCultureForRender", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LocalizedComponentBase_Exposes_Named_DisplayCulture_Cascade()
    {
        Assert.Equal("DisplayCulture", LocalizedComponentBase.DisplayCultureCascadeName);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RepoSyncRadar.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
