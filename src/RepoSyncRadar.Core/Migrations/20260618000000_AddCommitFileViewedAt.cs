using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepoSyncRadar.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCommitFileViewedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ViewedAt",
                table: "CommitFiles",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ViewedAt",
                table: "CommitFiles");
        }
    }
}