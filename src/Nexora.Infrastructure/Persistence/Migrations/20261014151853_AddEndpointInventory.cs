using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEndpointInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RamFreeBytes",
                table: "endpoints",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "endpoint_inventories",
                columns: table => new
                {
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false),
                    CollectedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoint_inventories", x => x.EndpointId);
                    table.ForeignKey(
                        name: "FK_endpoint_inventories_endpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "endpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "endpoint_inventories");

            migrationBuilder.DropColumn(
                name: "RamFreeBytes",
                table: "endpoints");
        }
    }
}
