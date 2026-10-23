using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileSizeCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "profile_sizes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    MeasuredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeasuredBySid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_sizes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_profile_sizes_endpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "endpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_BatchId",
                table: "jobs",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_profile_sizes_EndpointId_Sid",
                table: "profile_sizes",
                columns: new[] { "EndpointId", "Sid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "profile_sizes");

            migrationBuilder.DropIndex(
                name: "IX_jobs_BatchId",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "jobs");
        }
    }
}
