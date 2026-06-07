using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepoSyncRadar.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReviewHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Sha = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReviewHistories_Commits_Sha",
                        column: x => x.Sha,
                        principalTable: "Commits",
                        principalColumn: "Sha",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewHistories_ChangedAt",
                table: "ReviewHistories",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReviewHistories_Sha",
                table: "ReviewHistories",
                column: "Sha");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReviewHistories");
        }
    }
}
