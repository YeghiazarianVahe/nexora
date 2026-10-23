using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDiagnosisRunningIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_diagnosis_runs_running",
                table: "diagnosis_runs",
                column: "EndpointId",
                unique: true,
                filter: "\"Status\" = 'Running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_diagnosis_runs_running",
                table: "diagnosis_runs");
        }
    }
}
