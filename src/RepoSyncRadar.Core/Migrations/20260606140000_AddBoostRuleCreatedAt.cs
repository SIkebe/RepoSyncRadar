using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepoSyncRadar.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddBoostRuleCreatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "BoostRules",
                type: "TEXT",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "BoostRules");
        }
    }
}
