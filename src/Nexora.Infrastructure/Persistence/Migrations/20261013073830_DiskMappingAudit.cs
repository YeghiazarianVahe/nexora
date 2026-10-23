using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DiskMappingAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BusTypeCode",
                table: "disk_health",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassSource",
                table: "disk_health",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthSource",
                table: "disk_health",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HostDriveCount",
                table: "disk_health",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HostSmartRecords",
                table: "disk_health",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MediaTypeCode",
                table: "disk_health",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhysicalMatch",
                table: "disk_health",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SmartMatched",
                table: "disk_health",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SpindleSpeed",
                table: "disk_health",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BusTypeCode",
                table: "disk_health");

            migrationBuilder.DropColumn(
                name: "ClassSource",
                table: "disk_health");

            migrationBuilder.DropColumn(
                name: "HealthSource",
                table: "disk_health");

            migrationBuilder.DropColumn(
                name: "HostDriveCount",
                table: "disk_health");

            migrationBuilder.DropColumn(
                name: "HostSmartRecords",
                table: "disk_health");

            migrationBuilder.DropColumn(
                name: "MediaTypeCode",
                table: "disk_health");

            migrationBuilder.DropColumn(
                name: "PhysicalMatch",
                table: "disk_health");

            migrationBuilder.DropColumn(
                name: "SmartMatched",
                table: "disk_health");

            migrationBuilder.DropColumn(
                name: "SpindleSpeed",
                table: "disk_health");
        }
    }
}
