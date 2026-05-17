using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RepoSyncRadar.Core.Data;

#nullable disable

namespace RepoSyncRadar.Core.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RadarDbContext))]
    [Migration("20260517000000_AddScoringDetailsJa")]
    public partial class AddScoringDetailsJa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DetailsJa",
                table: "Scorings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetailsJa",
                table: "Scorings");
        }
    }
}