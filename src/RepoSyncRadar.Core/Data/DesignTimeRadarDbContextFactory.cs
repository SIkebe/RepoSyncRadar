using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RepoSyncRadar.Core.Data;

/// <summary>
/// Lets <c>dotnet ef</c> instantiate <see cref="RadarDbContext"/> without spinning up
/// the App's host. The connection string is intentionally a throwaway path — migrations
/// only read the model, never the actual database.
/// </summary>
internal sealed class DesignTimeRadarDbContextFactory : IDesignTimeDbContextFactory<RadarDbContext>
{
    public RadarDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RadarDbContext>()
            .UseSqlite("Data Source=radar-design-time.db")
            .Options;
        return new RadarDbContext(options);
    }
}
