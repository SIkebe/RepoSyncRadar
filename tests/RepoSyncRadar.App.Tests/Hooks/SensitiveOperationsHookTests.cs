using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RepoSyncRadar.App.Tests.Hooks;

public sealed class SensitiveOperationsHookTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"toolName":"functions.view","toolArgs":{}}""")]
    public async Task PowerShellHook_AllowsValidPayloadsWithoutPropertiesOrCommands(string payload)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await RunHookAsync(payload, cancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task PowerShellHook_DeniesSensitiveCommand()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var payload = JsonSerializer.Serialize(new
        {
            toolName = "functions.powershell",
            toolArgs = new { command = "gh pr merge 123" },
        });

        var result = await RunHookAsync(payload, cancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("deny", output.RootElement.GetProperty("permissionDecision").GetString());
        Assert.Contains(
            "PR merges must be initiated by a human",
            output.RootElement.GetProperty("permissionDecisionReason").GetString(),
            StringComparison.Ordinal);
    }

    private static async Task<HookResult> RunHookAsync(string payload, CancellationToken cancellationToken)
    {
        var repositoryRoot = FindRepositoryRoot();
        var hookPath = Path.Combine(repositoryRoot, ".github", "hooks", "guard-sensitive-operations.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{hookPath}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        await process.StandardInput.WriteAsync(payload.AsMemory(), cancellationToken);
        process.StandardInput.Close();

        var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new HookResult(process.ExitCode, standardOutput, standardError);
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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed record HookResult(int ExitCode, string StandardOutput, string StandardError);
}
