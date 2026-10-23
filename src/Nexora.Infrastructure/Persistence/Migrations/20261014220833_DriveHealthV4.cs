using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DriveHealthV4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InterfaceWarning",
                table: "disk_health",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReasonsJson",
                table: "disk_health",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "TempCritC",
                table: "disk_health",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TempMaxLifetimeC",
                table: "disk_health",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TempThresholdSource",
                table: "disk_health",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TempWarnC",
                table: "disk_health",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InterfaceWarning",
                table: "disk_health");

            migrationBuilder.DropColumn(
                name: "ReasonsJson",
                table: "disk_health");

            migrationBuilder.DropColumn(
                name: "TempCritC",
                table: "disk_health");

            migrationBuilder.DropColumn(
                name: "TempMaxLifetimeC",
                table: "disk_health");

            migrationBuilder.DropColumn(
                name: "TempThresholdSource",
                table: "disk_health");

            migrationBuilder.DropColumn(
                name: "TempWarnC",
                table: "disk_health");
        }
    }
}
