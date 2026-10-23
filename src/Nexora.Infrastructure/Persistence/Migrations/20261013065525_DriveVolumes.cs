using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DriveVolumes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RamTotalBytes",
                table: "endpoints",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VolumesJson",
                table: "endpoints",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RamTotalBytes",
                table: "endpoints");

            migrationBuilder.DropColumn(
                name: "VolumesJson",
                table: "endpoints");
        }
    }
}
