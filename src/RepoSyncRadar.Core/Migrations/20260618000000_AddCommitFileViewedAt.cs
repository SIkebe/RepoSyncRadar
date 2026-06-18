using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RepoSyncRadar.Core.Data;

#nullable disable

namespace RepoSyncRadar.Core.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RadarDbContext))]
    [Migration("20260618000000_AddCommitFileViewedAt")]
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