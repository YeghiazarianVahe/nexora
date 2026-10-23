using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDiagnosisRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "diagnosis_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProfileVersion = table.Column<int>(type: "integer", nullable: false),
                    InitiatedBySid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Complete = table.Column<bool>(type: "boolean", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: true),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_diagnosis_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_diagnosis_runs_endpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "endpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_diagnosis_runs_EndpointId_StartedUtc",
                table: "diagnosis_runs",
                columns: new[] { "EndpointId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_diagnosis_runs_JobId",
                table: "diagnosis_runs",
                column: "JobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "diagnosis_runs");
        }
    }
}
