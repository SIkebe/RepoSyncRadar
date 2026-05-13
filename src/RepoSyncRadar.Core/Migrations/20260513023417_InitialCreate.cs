using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepoSyncRadar.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BoostRules",
                columns: table => new
                {
                    Pattern = table.Column<string>(type: "TEXT", nullable: false),
                    Delta = table.Column<double>(type: "REAL", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoostRules", x => x.Pattern);
                });

            migrationBuilder.CreateTable(
                name: "Commits",
                columns: table => new
                {
                    Sha = table.Column<string>(type: "TEXT", nullable: false),
                    PrNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", nullable: false),
                    AuthoredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commits", x => x.Sha);
                });

            migrationBuilder.CreateTable(
                name: "CopilotToolLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", nullable: false),
                    ArgsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CopilotToolLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IgnoreRules",
                columns: table => new
                {
                    Pattern = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IgnoreRules", x => x.Pattern);
                });

            migrationBuilder.CreateTable(
                name: "PathUrlMaps",
                columns: table => new
                {
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PathUrlMaps", x => new { x.Path, x.Version, x.Language });
                });

            migrationBuilder.CreateTable(
                name: "CommitFiles",
                columns: table => new
                {
                    Sha = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Additions = table.Column<int>(type: "INTEGER", nullable: false),
                    Deletions = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommitFiles", x => new { x.Sha, x.Path });
                    table.ForeignKey(
                        name: "FK_CommitFiles_Commits_Sha",
                        column: x => x.Sha,
                        principalTable: "Commits",
                        principalColumn: "Sha",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Drafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Sha = table.Column<string>(type: "TEXT", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Posted = table.Column<bool>(type: "INTEGER", nullable: false),
                    PostedUrl = table.Column<string>(type: "TEXT", nullable: true),
                    GeneratedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Drafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Drafts_Commits_Sha",
                        column: x => x.Sha,
                        principalTable: "Commits",
                        principalColumn: "Sha",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Reviews",
                columns: table => new
                {
                    Sha = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reviews", x => x.Sha);
                    table.ForeignKey(
                        name: "FK_Reviews_Commits_Sha",
                        column: x => x.Sha,
                        principalTable: "Commits",
                        principalColumn: "Sha",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Scorings",
                columns: table => new
                {
                    Sha = table.Column<string>(type: "TEXT", nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    AudienceJson = table.Column<string>(type: "TEXT", nullable: false),
                    SummaryJa = table.Column<string>(type: "TEXT", nullable: false),
                    WhyJa = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    PromptHash = table.Column<string>(type: "TEXT", nullable: false),
                    ScoredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scorings", x => x.Sha);
                    table.ForeignKey(
                        name: "FK_Scorings_Commits_Sha",
                        column: x => x.Sha,
                        principalTable: "Commits",
                        principalColumn: "Sha",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommitFiles_Path",
                table: "CommitFiles",
                column: "Path");

            migrationBuilder.CreateIndex(
                name: "IX_Commits_AuthoredAt",
                table: "Commits",
                column: "AuthoredAt");

            migrationBuilder.CreateIndex(
                name: "IX_Commits_PrNumber",
                table: "Commits",
                column: "PrNumber");

            migrationBuilder.CreateIndex(
                name: "IX_CopilotToolLogs_SessionId",
                table: "CopilotToolLogs",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CopilotToolLogs_ToolName",
                table: "CopilotToolLogs",
                column: "ToolName");

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_Sha_Channel",
                table: "Drafts",
                columns: new[] { "Sha", "Channel" });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_Status",
                table: "Reviews",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Scorings_Score",
                table: "Scorings",
                column: "Score");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BoostRules");

            migrationBuilder.DropTable(
                name: "CommitFiles");

            migrationBuilder.DropTable(
                name: "CopilotToolLogs");

            migrationBuilder.DropTable(
                name: "Drafts");

            migrationBuilder.DropTable(
                name: "IgnoreRules");

            migrationBuilder.DropTable(
                name: "PathUrlMaps");

            migrationBuilder.DropTable(
                name: "Reviews");

            migrationBuilder.DropTable(
                name: "Scorings");

            migrationBuilder.DropTable(
                name: "Commits");
        }
    }
}
