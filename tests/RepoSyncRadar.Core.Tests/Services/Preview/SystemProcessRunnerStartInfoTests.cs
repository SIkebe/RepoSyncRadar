using System;
using System.Collections.Generic;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

/// <summary>
/// Tests covering <see cref="SystemProcessRunner.BuildStartInfo"/>, the bit of
/// <see cref="SystemProcessRunner.Start(string, string, string, IReadOnlyDictionary{string, string?}?)"/>
/// that can be exercised without spawning a real child. The merge semantics
/// are the contract <see cref="PreviewServerHost"/> relies on to thread
/// <c>PORT</c> through to the docs server.
/// </summary>
public sealed class SystemProcessRunnerStartInfoTests
{
    [Fact]
    public void Sets_Required_Redirect_Flags()
    {
        var psi = SystemProcessRunner.BuildStartInfo(
            "where",
            "PATH",
            workingDirectory: AppContext.BaseDirectory,
            environment: null);

        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.True(psi.CreateNoWindow);
        Assert.Equal(AppContext.BaseDirectory, psi.WorkingDirectory);
    }

    [Fact]
    public void Forces_Utf8_For_Redirected_Streams()
    {
        // Required so child diagnostics with non-ASCII glyphs (e.g. Next.js'
        // "⚠ i18n configuration ... unsupported" App Router warning) survive
        // the pipe intact on locales where Console.OutputEncoding defaults
        // to a Windows code page (CP932 on Japanese Windows).
        var psi = SystemProcessRunner.BuildStartInfo(
            "where",
            "PATH",
            workingDirectory: AppContext.BaseDirectory,
            environment: null);

        Assert.Equal(System.Text.Encoding.UTF8, psi.StandardOutputEncoding);
        Assert.Equal(System.Text.Encoding.UTF8, psi.StandardErrorEncoding);
    }

    [Fact]
    public void Merges_Environment_Overrides()
    {
        var psi = SystemProcessRunner.BuildStartInfo(
            "where",
            "PATH",
            workingDirectory: AppContext.BaseDirectory,
            environment: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["PORT"] = "4500",
                ["RADAR_E2E_FLAG"] = "1",
            });

        Assert.Equal("4500", psi.Environment["PORT"]);
        Assert.Equal("1", psi.Environment["RADAR_E2E_FLAG"]);
    }

    [Fact]
    public void Removes_Variables_When_Value_Is_Null()
    {
        var psi = SystemProcessRunner.BuildStartInfo(
            "where",
            "PATH",
            workingDirectory: AppContext.BaseDirectory,
            environment: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["RADAR_TEST_VAR"] = "first",
            });
        Assert.True(psi.Environment.ContainsKey("RADAR_TEST_VAR"));

        var cleared = SystemProcessRunner.BuildStartInfo(
            "where",
            "PATH",
            workingDirectory: AppContext.BaseDirectory,
            environment: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["RADAR_TEST_VAR"] = null,
            });

        Assert.False(cleared.Environment.ContainsKey("RADAR_TEST_VAR"));
    }
}
