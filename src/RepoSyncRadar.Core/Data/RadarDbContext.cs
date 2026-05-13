using Microsoft.EntityFrameworkCore;
using RepoSyncRadar.Core.Models;

namespace RepoSyncRadar.Core.Data;

/// <summary>
/// Single-file SQLite store for commits, reviews, drafts, learned rules, and Copilot audit logs.
/// </summary>
public sealed class RadarDbContext(DbContextOptions<RadarDbContext> options) : DbContext(options)
{
    public DbSet<Commit> Commits => Set<Commit>();
    public DbSet<CommitFile> CommitFiles => Set<CommitFile>();
    public DbSet<Scoring> Scorings => Set<Scoring>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Draft> Drafts => Set<Draft>();
    public DbSet<PathUrlMap> PathUrlMaps => Set<PathUrlMap>();
    public DbSet<IgnoreRule> IgnoreRules => Set<IgnoreRule>();
    public DbSet<BoostRule> BoostRules => Set<BoostRule>();
    public DbSet<CopilotToolLog> CopilotToolLogs => Set<CopilotToolLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Commit>(e =>
        {
            e.HasKey(c => c.Sha);
            e.HasIndex(c => c.PrNumber);
            e.HasIndex(c => c.AuthoredAt);
            e.HasOne(c => c.Scoring).WithOne()
                .HasForeignKey<Scoring>(s => s.Sha)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Review).WithOne()
                .HasForeignKey<Review>(r => r.Sha)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Files).WithOne()
                .HasForeignKey(f => f.Sha)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Drafts).WithOne()
                .HasForeignKey(d => d.Sha)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommitFile>(e =>
        {
            e.HasKey(f => new { f.Sha, f.Path });
            e.HasIndex(f => f.Path);
        });

        modelBuilder.Entity<Scoring>(e =>
        {
            e.HasKey(s => s.Sha);
            e.HasIndex(s => s.Score);
        });

        modelBuilder.Entity<Review>(e =>
        {
            e.HasKey(r => r.Sha);
            e.HasIndex(r => r.Status);
            e.Property(r => r.Status).HasConversion<string>();
        });

        modelBuilder.Entity<Draft>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => new { d.Sha, d.Channel });
        });

        modelBuilder.Entity<PathUrlMap>(e =>
        {
            e.HasKey(p => new { p.Path, p.Version, p.Language });
        });

        modelBuilder.Entity<IgnoreRule>(e => e.HasKey(r => r.Pattern));
        modelBuilder.Entity<BoostRule>(e => e.HasKey(r => r.Pattern));

        modelBuilder.Entity<CopilotToolLog>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.SessionId);
            e.HasIndex(l => l.ToolName);
        });
    }
}
