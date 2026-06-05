using System;
using System.Collections.Generic;
using System.IO;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

/// <summary>
/// Tests for <see cref="SystemProcessRunner"/>'s executable resolution.
/// On Windows, <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
/// with <c>UseShellExecute = false</c> delegates to the Win32 <c>CreateProcess</c> API,
/// which only auto-appends <c>.exe</c> when searching <c>PATH</c>. Some tools
/// ship as <c>.cmd</c> wrappers on Windows, so a bare command name can fail
/// with "the system cannot find the file specified" even when the tool
/// directory is on PATH. <see cref="SystemProcessRunner.ResolveExecutable"/>
/// walks <c>PATH</c> × <c>PATHEXT</c> to recover the full path before handing it
/// to <c>CreateProcess</c>.
/// </summary>
public sealed class SystemProcessRunnerTests
{
    [Fact]
    public void ResolveExecutable_Returns_Input_When_Path_Is_Rooted()
    {
        // Absolute paths must be passed through unchanged so the caller can still
        // see the original Win32Exception ("file not found") if the path is wrong.
        var rooted = OperatingSystem.IsWindows()
            ? @"C:\does\not\exist\foo"
            : "/does/not/exist/foo";

        var resolved = SystemProcessRunner.ResolveExecutable(
            rooted,
            pathEnv: "",
            pathExtEnv: ".CMD",
            fileExists: _ => false);

        Assert.Equal(rooted, resolved);
    }

    [Fact]
    public void ResolveExecutable_Returns_Input_When_FileName_Already_Has_Extension()
    {
        // If the caller explicitly typed "tool.cmd" we should not second-guess them.
        var resolved = SystemProcessRunner.ResolveExecutable(
            "tool.cmd",
            pathEnv: @"C:\tools",
            pathExtEnv: ".COM;.EXE;.BAT;.CMD",
            fileExists: _ => true);

        Assert.Equal("tool.cmd", resolved);
    }

    [Fact]
    public void ResolveExecutable_Finds_Cmd_On_Path_When_Extension_Is_Omitted()
    {
        // The whole point: PATH has C:\tools which contains tool.cmd. CreateProcess
        // would not find this without help because it only auto-appends .exe.
        // The returned path mirrors the PATHEXT entry's casing (Windows treats
        // filenames case-insensitively, so this is purely cosmetic).
        var fakeFs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\tools\tool.cmd",
        };

        var resolved = SystemProcessRunner.ResolveExecutable(
            "tool",
            pathEnv: @"C:\Windows;C:\tools",
            pathExtEnv: ".COM;.EXE;.BAT;.CMD",
            fileExists: fakeFs.Contains);

        Assert.Equal(@"C:\tools\tool.CMD", resolved);
    }

    [Fact]
    public void ResolveExecutable_Prefers_Earlier_Path_Entry()
    {
        // Mimics a user who has two tool installs on PATH; the first one wins,
        // matching CreateProcess search semantics.
        var fakeFs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\tools-old\tool.cmd",
            @"C:\tools-new\tool.cmd",
        };

        var resolved = SystemProcessRunner.ResolveExecutable(
            "tool",
            pathEnv: @"C:\tools-old;C:\tools-new",
            pathExtEnv: ".CMD",
            fileExists: fakeFs.Contains);

        Assert.Equal(@"C:\tools-old\tool.CMD", resolved);
    }

    [Fact]
    public void ResolveExecutable_Prefers_Earlier_PathExt_Entry()
    {
        // PATHEXT order matters too: a folder with both foo.exe and foo.cmd should
        // resolve to foo.exe when ".EXE" comes first in PATHEXT.
        var fakeFs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\tools\foo.exe",
            @"C:\tools\foo.cmd",
        };

        var resolved = SystemProcessRunner.ResolveExecutable(
            "foo",
            pathEnv: @"C:\tools",
            pathExtEnv: ".COM;.EXE;.BAT;.CMD",
            fileExists: fakeFs.Contains);

        Assert.Equal(@"C:\tools\foo.EXE", resolved);
    }

    [Fact]
    public void ResolveExecutable_Returns_Input_Unchanged_When_Nothing_Found()
    {
        // Falling through to the original name preserves the existing Win32Exception
        // wrapping path in RunAsync/Start so the UX message stays consistent.
        var resolved = SystemProcessRunner.ResolveExecutable(
            "definitely-not-on-path-xyz",
            pathEnv: @"C:\Windows;C:\tools",
            pathExtEnv: ".COM;.EXE;.BAT;.CMD",
            fileExists: _ => false);

        Assert.Equal("definitely-not-on-path-xyz", resolved);
    }

    [Fact]
    public void ResolveExecutable_Ignores_Empty_And_Quoted_Path_Entries()
    {
        // Windows PATH frequently contains stray semicolons and quoted entries
        // ("C:\Program Files\Tools"). Both should resolve cleanly.
        var fakeFs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files\Tools\tool.cmd",
        };

        var resolved = SystemProcessRunner.ResolveExecutable(
            "tool",
            pathEnv: ";;\"C:\\Program Files\\Tools\";",
            pathExtEnv: ".CMD",
            fileExists: fakeFs.Contains);

        Assert.Equal(@"C:\Program Files\Tools\tool.CMD", resolved);
    }

    [Fact]
    public void AppendBuffered_Appends_Distinct_Lines()
    {
        var buffer = new List<string>();
        var gate = new object();

        SystemProcessRunner.AppendBuffered(buffer, gate, "foo", 64);
        SystemProcessRunner.AppendBuffered(buffer, gate, "bar", 64);
        SystemProcessRunner.AppendBuffered(buffer, gate, "baz", 64);

        var expected = new List<string> { "foo", "bar", "baz" };
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void AppendBuffered_Collapses_Consecutive_Identical_Lines()
    {
        // Repeated warnings would otherwise flood the 64-line ring buffer and
        // push the real root-cause line out before the UI can sample it.
        var buffer = new List<string>();
        var gate = new object();
        const string warning = "⚠ repeated warning from child process.";

        for (var i = 0; i < 5; i++)
        {
            SystemProcessRunner.AppendBuffered(buffer, gate, warning, 64);
        }

        Assert.Single(buffer);
        Assert.Equal(warning + " (x 5)", buffer[0]);
    }

    [Fact]
    public void AppendBuffered_Bumps_Existing_Repeat_Counter()
    {
        // A pre-collapsed "(x 3)" tail must be recognised and bumped, not
        // appended verbatim — otherwise the buffer fills with stair-step
        // duplicates ("foo", "foo (x 2)", "foo (x 2) (x 2)", ...).
        var buffer = new List<string> { "foo (x 3)" };
        var gate = new object();

        SystemProcessRunner.AppendBuffered(buffer, gate, "foo", 64);

        Assert.Single(buffer);
        Assert.Equal("foo (x 4)", buffer[0]);
    }

    [Fact]
    public void AppendBuffered_Treats_Different_Lines_As_Separate_Runs()
    {
        // Collapse must only fire for *consecutive* identical lines. A
        // genuinely new line in between has to break the run so we never
        // hide information that happens to repeat earlier.
        var buffer = new List<string>();
        var gate = new object();

        SystemProcessRunner.AppendBuffered(buffer, gate, "warning A", 64);
        SystemProcessRunner.AppendBuffered(buffer, gate, "warning A", 64);
        SystemProcessRunner.AppendBuffered(buffer, gate, "error B", 64);
        SystemProcessRunner.AppendBuffered(buffer, gate, "warning A", 64);

        var expected = new List<string> { "warning A (x 2)", "error B", "warning A" };
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void AppendBuffered_Drops_Oldest_When_Capacity_Exceeded()
    {
        var buffer = new List<string>();
        var gate = new object();

        for (var i = 0; i < 5; i++)
        {
            SystemProcessRunner.AppendBuffered(buffer, gate, $"line-{i}", maxBufferedLines: 3);
        }

        var expected = new List<string> { "line-2", "line-3", "line-4" };
        Assert.Equal(expected, buffer);
    }

    [Theory]
    [InlineData("foo (x 2)", true, "foo", 2)]
    [InlineData("warning ABC (x 17)", true, "warning ABC", 17)]
    [InlineData("foo", false, "", 0)]
    [InlineData("foo (x 1)", false, "", 0)]      // count < 2 is the un-collapsed form
    [InlineData("foo (x 0)", false, "", 0)]
    [InlineData("foo (x -3)", false, "", 0)]
    [InlineData("foo (x abc)", false, "", 0)]
    [InlineData("foo (x )", false, "", 0)]
    [InlineData("foo (x 2", false, "", 0)]       // missing closing paren
    public void TryParseRepeat_Matches_Only_Exact_Suffix(string input, bool expected, string expectedBase, int expectedCount)
    {
        var matched = SystemProcessRunner.TryParseRepeat(input, out var baseLine, out var count);

        Assert.Equal(expected, matched);
        if (expected)
        {
            Assert.Equal(expectedBase, baseLine);
            Assert.Equal(expectedCount, count);
        }
    }
}
