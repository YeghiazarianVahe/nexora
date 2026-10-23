using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GroupsMetricsDisks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The old free-text GroupName is replaced by real groups. It is dropped only at the END of this migration, after its
            // values have been copied into endpoint_groups / endpoint_group_members (QA-15).
            migrationBuilder.AddColumn<string>(name: "ReportedHostname", table: "endpoints", type: "character varying(100)", maxLength: 100, nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "endpoints",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastDiskPollUtc",
                table: "endpoints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "disk_health",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    DiskKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Serial = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MediaType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BusType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    HealthPercent = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TemperatureC = table.Column<int>(type: "integer", nullable: true),
                    MaxTemperatureC = table.Column<int>(type: "integer", nullable: true),
                    PowerOnHours = table.Column<long>(type: "bigint", nullable: true),
                    WearPercent = table.Column<int>(type: "integer", nullable: true),
                    ReadErrors = table.Column<long>(type: "bigint", nullable: true),
                    WriteErrors = table.Column<long>(type: "bigint", nullable: true),
                    ReallocatedSectors = table.Column<long>(type: "bigint", nullable: true),
                    PendingSectors = table.Column<long>(type: "bigint", nullable: true),
                    PredictFailure = table.Column<bool>(type: "boolean", nullable: false),
                    EstimatedDaysLeft = table.Column<int>(type: "integer", nullable: true),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AttributesJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_disk_health", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "disk_temp_samples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    DiskKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TemperatureC = table.Column<int>(type: "integer", nullable: true),
                    HealthPercent = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_disk_temp_samples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "endpoint_group_members",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoint_group_members", x => new { x.GroupId, x.EndpointId });
                });

            migrationBuilder.CreateTable(
                name: "endpoint_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoint_groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "fleet_samples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Online = table.Column<int>(type: "integer", nullable: false),
                    AvgCpu = table.Column<double>(type: "double precision", nullable: true),
                    AvgRam = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fleet_samples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "metric_samples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Cpu = table.Column<double>(type: "double precision", nullable: true),
                    Ram = table.Column<double>(type: "double precision", nullable: true),
                    Disk = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metric_samples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_disk_health_EndpointId_DiskKey",
                table: "disk_health",
                columns: new[] { "EndpointId", "DiskKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_disk_temp_samples_EndpointId_DiskKey_TimestampUtc",
                table: "disk_temp_samples",
                columns: new[] { "EndpointId", "DiskKey", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_group_members_EndpointId",
                table: "endpoint_group_members",
                column: "EndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_groups_Name",
                table: "endpoint_groups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fleet_samples_TimestampUtc",
                table: "fleet_samples",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_metric_samples_EndpointId_TimestampUtc",
                table: "metric_samples",
                columns: new[] { "EndpointId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_metric_samples_TimestampUtc",
                table: "metric_samples",
                column: "TimestampUtc");

            // Preserve legacy groups: one group per distinct (trimmed, case-insensitive) name, truncated to the 60-char limit.
            // Membership follows. The untruncated original is kept in the description when it had to be shortened.
            migrationBuilder.Sql(@"
INSERT INTO endpoint_groups (""Id"", ""Name"", ""Description"", ""CreatedUtc"")
SELECT gen_random_uuid(), left(n.display, 60),
       CASE WHEN char_length(n.display) > 60 THEN left('Migrated from legacy group name: ' || n.display, 300) ELSE 'Migrated from legacy group name' END,
       now()
FROM (SELECT min(btrim(""GroupName"")) AS display
      FROM endpoints WHERE ""GroupName"" IS NOT NULL AND btrim(""GroupName"") <> ''
      GROUP BY lower(left(btrim(""GroupName""), 60))) n
ON CONFLICT DO NOTHING;");
            migrationBuilder.Sql(@"
INSERT INTO endpoint_group_members (""GroupId"", ""EndpointId"")
SELECT DISTINCT g.""Id"", e.""Id""
FROM endpoints e
JOIN endpoint_groups g ON lower(g.""Name"") = lower(left(btrim(e.""GroupName""), 60))
WHERE e.""GroupName"" IS NOT NULL AND btrim(e.""GroupName"") <> ''
ON CONFLICT DO NOTHING;");

            migrationBuilder.DropColumn(name: "GroupName", table: "endpoints");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "disk_health");

            migrationBuilder.DropTable(
                name: "disk_temp_samples");

            migrationBuilder.DropTable(
                name: "endpoint_group_members");

            migrationBuilder.DropTable(
                name: "endpoint_groups");

            migrationBuilder.DropTable(
                name: "fleet_samples");

            migrationBuilder.DropTable(
                name: "metric_samples");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "endpoints");

            migrationBuilder.DropColumn(
                name: "LastDiskPollUtc",
                table: "endpoints");

            migrationBuilder.DropColumn(name: "ReportedHostname", table: "endpoints");
            migrationBuilder.AddColumn<string>(name: "GroupName", table: "endpoints", type: "character varying(100)", maxLength: 100, nullable: true);
        }
    }
}
