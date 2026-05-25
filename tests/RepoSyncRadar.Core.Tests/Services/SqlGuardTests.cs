using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services;

/// <summary>
/// Validates <see cref="SqlGuard"/> per IMPLEMENTATION_PLAN.md §Step 18.3.
/// Each case mirrors a row of the spec table.
/// </summary>
public sealed class SqlGuardTests
{
    [Fact]
    public void Accepts_Select_With_Limit()
    {
        var result = SqlGuard.Validate("SELECT * FROM Commits LIMIT 5");
        Assert.True(result.IsValid, result.Reason);
        Assert.Contains("LIMIT 5", result.TransformedSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_Insert()
    {
        var result = SqlGuard.Validate("INSERT INTO Commits (Sha) VALUES ('x')");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Rejects_Multiple_Statements()
    {
        var result = SqlGuard.Validate("SELECT * FROM Commits; DROP TABLE Commits");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Rejects_Disallowed_Table()
    {
        var result = SqlGuard.Validate("SELECT * FROM SecretTable");
        Assert.False(result.IsValid);
        Assert.Contains("SecretTable", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_Current_Radar_Tables()
    {
        var tables = new[]
        {
            "Commits",
            "CommitFiles",
            "Scorings",
            "Reviews",
            "Drafts",
            "PathUrlMaps",
            "IgnoreRules",
            "BoostRules",
            "CopilotToolLogs",
        };

        foreach (var table in tables)
        {
            var result = SqlGuard.Validate($"SELECT * FROM {table} LIMIT 1");
            Assert.True(result.IsValid, result.Reason);
        }
    }

    [Fact]
    public void Rejects_Stale_Display_Table_Names()
    {
        var staleTables = new[] { "Files", "Scores", "Audits", "PathUrlMap" };

        foreach (var table in staleTables)
        {
            var result = SqlGuard.Validate($"SELECT * FROM {table} LIMIT 1");
            Assert.False(result.IsValid);
            Assert.Contains(table, result.Reason!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Appends_Default_Limit_When_Missing()
    {
        var result = SqlGuard.Validate("SELECT Sha FROM Commits");
        Assert.True(result.IsValid, result.Reason);
        Assert.EndsWith("LIMIT 100", result.TransformedSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_Pragma()
    {
        var result = SqlGuard.Validate("pragma table_info('Commits')");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Rejects_Attach()
    {
        var result = SqlGuard.Validate("ATTACH DATABASE 'evil.db' AS evil");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Rejects_Ddl_Hidden_By_Comments_Or_Case()
    {
        // Multi-statement injection masked with mixed case and a comment.
        var result = SqlGuard.Validate("SeLeCt * FROM Commits -- ; DrOp TABLE Commits\n; DrOp TABLE Commits");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Preserves_Positional_Parameters()
    {
        var result = SqlGuard.Validate("SELECT * FROM Commits WHERE Sha = ?", new object?[] { "abc" });
        Assert.True(result.IsValid, result.Reason);
        Assert.Single(result.Parameters);
        Assert.Equal("abc", result.Parameters[0]);
    }
}
