using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Jobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "job_log_chunks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobTargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Stream = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_log_chunks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "job_targets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Host = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    ResultSummary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SkipReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Progress = table.Column<int>(type: "integer", nullable: true),
                    OutputPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OutputSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LogLines = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_targets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    InitiatorSid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    InitiatorAccount = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProtectedOverride = table.Column<bool>(type: "boolean", nullable: false),
                    ClientIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FinishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TargetCount = table.Column<int>(type: "integer", nullable: false),
                    CancelRequestedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_job_log_chunks_JobTargetId_Seq",
                table: "job_log_chunks",
                columns: new[] { "JobTargetId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_targets_EndpointId_Status",
                table: "job_targets",
                columns: new[] { "EndpointId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_job_targets_JobId",
                table: "job_targets",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_CreatedUtc",
                table: "jobs",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_InitiatorSid_CreatedUtc",
                table: "jobs",
                columns: new[] { "InitiatorSid", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status",
                table: "jobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_log_chunks");

            migrationBuilder.DropTable(
                name: "job_targets");

            migrationBuilder.DropTable(
                name: "jobs");
        }
    }
}
