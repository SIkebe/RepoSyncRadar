using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class CopilotRuntimePublishTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeCopy_ReplacesEqualSizeAndTimestampAliasesBeforeCopy(bool removePreviousMarker)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repositoryRoot = FindRepositoryRoot();
        var testDirectory = Path.Combine(repositoryRoot, "artifacts", "runtime-copy-tests", Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(testDirectory, "source");
        var outputDirectory = Path.Combine(testDirectory, "build");
        var destinationDirectory = Path.Combine(outputDirectory, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        try
        {
            var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var aliases = new[] { "copilot_runtime.dll", "copilot.exe" };
            foreach (var alias in aliases)
            {
                var source = Path.Combine(sourceDirectory, alias);
                var destination = Path.Combine(destinationDirectory, alias);
                await File.WriteAllTextAsync(source, "new-runtime", cancellationToken);
                await File.WriteAllTextAsync(destination, "old-runtime", cancellationToken);
                File.SetLastWriteTimeUtc(source, timestamp);
                File.SetLastWriteTimeUtc(destination, timestamp);
            }

            var marker = Path.Combine(destinationDirectory, ".reposyncradar-runtime-version");
            if (!removePreviousMarker)
            {
                await File.WriteAllTextAsync(marker, "old-version", cancellationToken);
            }

            var projectPath = Path.Combine(testDirectory, "Copy.proj");
            new XDocument(
                new XElement("Project",
                    new XElement("PropertyGroup",
                        new XElement("OutDir", outputDirectory + Path.DirectorySeparatorChar),
                        new XElement("SourceDir", sourceDirectory),
                        new XElement("_CopilotRid", "win-x64"),
                        new XElement("_CopilotRuntimeLib", aliases[0]),
                        new XElement("_CopilotBinary", aliases[1]),
                        new XElement("CopilotCliVersion", "new-version")),
                    new XElement("Target",
                        new XAttribute("Name", "_CopyCopilotCliToOutput"),
                        new XAttribute("DependsOnTargets", "_CopyCopilotCliToOutputCore")),
                    new XElement("Target",
                        new XAttribute("Name", "_CopyCopilotCliToOutputCore"),
                        new XElement("Copy",
                            new XAttribute("SourceFiles", "$(SourceDir)\\$(_CopilotRuntimeLib);$(SourceDir)\\$(_CopilotBinary)"),
                            new XAttribute("DestinationFolder", "$(OutDir)runtimes\\$(_CopilotRid)\\native"),
                            new XAttribute("SkipUnchangedFiles", "true"))),
                    new XElement("Import", new XAttribute("Project", Path.Combine(repositoryRoot, "Directory.Build.targets")))))
                .Save(projectPath);

            await RunMsBuildAsync(repositoryRoot, projectPath, ["-target:_CopyCopilotCliToOutput"], cancellationToken);

            foreach (var alias in aliases)
            {
                Assert.Equal("new-runtime", await File.ReadAllTextAsync(Path.Combine(destinationDirectory, alias), cancellationToken));
            }

            Assert.Equal("new-version", (await File.ReadAllTextAsync(marker, cancellationToken)).Trim());
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedPublish_ReplacesEqualSizeAndTimestampRuntimeAssets(bool removePreviousMarker)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repositoryRoot = FindRepositoryRoot();
        var testDirectory = Path.Combine(repositoryRoot, "artifacts", "runtime-publish-tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(testDirectory, "build");
        var publishDirectory = Path.Combine(testDirectory, "publish");
        var sourceDirectory = Path.Combine(outputDirectory, "runtimes", "win-x64", "native");
        var destinationDirectory = Path.Combine(publishDirectory, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(sourceDirectory);

        try
        {
            var appProject = XDocument.Load(Path.Combine(repositoryRoot, "src", "RepoSyncRadar.App", "RepoSyncRadar.App.csproj"));
            var publishTarget = appProject.Root!.Elements("Target")
                .Single(target => (string?)target.Attribute("Name") == "CopyCopilotRuntimeAfterPublish");
            var projectPath = Path.Combine(testDirectory, "Publish.proj");
            new XDocument(new XElement("Project", new XElement(publishTarget))).Save(projectPath);

            var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var assets = new[] { "copilot_runtime.dll", "copilot.exe", "copilot-runtime.exe", "runtime.node", Path.Combine("dependencies", "asset.dat") };
            Directory.CreateDirectory(Path.Combine(sourceDirectory, "dependencies"));
            foreach (var asset in assets)
            {
                await File.WriteAllTextAsync(Path.Combine(sourceDirectory, asset), "old-runtime", cancellationToken);
                File.SetLastWriteTimeUtc(Path.Combine(sourceDirectory, asset), timestamp);
            }

            var marker = ".reposyncradar-runtime-version";
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, marker), "1.0.93", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, ".copilot-runtime-assets"), "runtime.node", cancellationToken);

            await PublishAsync(repositoryRoot, projectPath, outputDirectory, publishDirectory, "1.0.93", cancellationToken);
            foreach (var asset in assets)
            {
                Assert.Equal("old-runtime", await File.ReadAllTextAsync(Path.Combine(destinationDirectory, asset), cancellationToken));
            }

            foreach (var asset in assets)
            {
                await File.WriteAllTextAsync(Path.Combine(sourceDirectory, asset), "new-runtime", cancellationToken);
                File.SetLastWriteTimeUtc(Path.Combine(sourceDirectory, asset), timestamp);
                Assert.Equal(new FileInfo(Path.Combine(sourceDirectory, asset)).Length,
                    new FileInfo(Path.Combine(destinationDirectory, asset)).Length);
                Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(sourceDirectory, asset)),
                    File.GetLastWriteTimeUtc(Path.Combine(destinationDirectory, asset)));
            }

            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, marker), "1.0.94-3", cancellationToken);
            if (removePreviousMarker)
            {
                File.Delete(Path.Combine(destinationDirectory, marker));
            }

            await PublishAsync(repositoryRoot, projectPath, outputDirectory, publishDirectory, "1.0.94-3", cancellationToken);
            foreach (var asset in assets)
            {
                Assert.Equal("new-runtime", await File.ReadAllTextAsync(Path.Combine(destinationDirectory, asset), cancellationToken));
            }

            Assert.Equal("1.0.94-3", await File.ReadAllTextAsync(Path.Combine(destinationDirectory, marker), cancellationToken));
            Assert.True(File.Exists(Path.Combine(destinationDirectory, ".copilot-runtime-assets")));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static async Task PublishAsync(
        string repositoryRoot,
        string projectPath,
        string outputDirectory,
        string publishDirectory,
        string runtimeVersion,
        CancellationToken cancellationToken)
    {
        await RunMsBuildAsync(repositoryRoot, projectPath,
        [
            "-target:CopyCopilotRuntimeAfterPublish",
            "-property:RuntimeIdentifier=win-x64",
            $"-property:CopilotCliVersion={runtimeVersion}",
            $"-property:OutDir={outputDirectory}{Path.DirectorySeparatorChar}",
            $"-property:PublishDir={publishDirectory}{Path.DirectorySeparatorChar}",
        ], cancellationToken);
    }

    private static async Task RunMsBuildAsync(
        string repositoryRoot,
        string projectPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "msbuild", projectPath, "-nologo", "-verbosity:quiet" }.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            Assert.True(process.ExitCode == 0, $"{await output}\n{await error}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RepoSyncRadar.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
