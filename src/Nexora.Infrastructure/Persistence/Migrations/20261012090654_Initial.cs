using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "access_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    SamAccountName = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedBySid = table.Column<string>(type: "text", nullable: true),
                    DecidedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GrantedRole = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    RejectReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "action_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetHost = table.Column<string>(type: "text", nullable: false),
                    OperatorSid = table.Column<string>(type: "text", nullable: false),
                    OperatorAccount = table.Column<string>(type: "text", nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Output = table.Column<string>(type: "text", nullable: true),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_action_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorSid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    ActorAccount = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TargetId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OldValue = table.Column<string>(type: "text", nullable: true),
                    NewValue = table.Column<string>(type: "text", nullable: true),
                    ClientIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "endpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Hostname = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    IpAddress = table.Column<string>(type: "text", nullable: true),
                    LastUser = table.Column<string>(type: "text", nullable: true),
                    OsCaption = table.Column<string>(type: "text", nullable: true),
                    OsVersion = table.Column<string>(type: "text", nullable: true),
                    BootTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastPolledUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Online = table.Column<bool>(type: "boolean", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CpuPercent = table.Column<double>(type: "double precision", nullable: true),
                    RamPercent = table.Column<double>(type: "double precision", nullable: true),
                    DiskPercent = table.Column<double>(type: "double precision", nullable: true),
                    GroupName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "scripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBySid = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scripts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    SamAccountName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAuthScheme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Sid);
                });

            migrationBuilder.CreateIndex(
                name: "IX_access_requests_Sid_Status",
                table: "access_requests",
                columns: new[] { "Sid", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_action_jobs_CreatedUtc",
                table: "action_jobs",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_action_jobs_OperatorSid",
                table: "action_jobs",
                column: "OperatorSid");

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_ActorSid",
                table: "audit_entries",
                column: "ActorSid");

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_TimestampUtc",
                table: "audit_entries",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_endpoints_Hostname",
                table: "endpoints",
                column: "Hostname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scripts_Name",
                table: "scripts",
                column: "Name",
                unique: true);

            // The audit log is append-only: the database itself refuses UPDATE, DELETE and TRUNCATE.
            migrationBuilder.Sql(@"
CREATE FUNCTION audit_entries_append_only() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'audit_entries is append-only';
END;
$$ LANGUAGE plpgsql;");
            migrationBuilder.Sql(@"
CREATE TRIGGER audit_entries_no_update_delete
BEFORE UPDATE OR DELETE ON audit_entries
FOR EACH ROW EXECUTE FUNCTION audit_entries_append_only();");
            migrationBuilder.Sql(@"
CREATE TRIGGER audit_entries_no_truncate
BEFORE TRUNCATE ON audit_entries
FOR EACH STATEMENT EXECUTE FUNCTION audit_entries_append_only();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_entries_no_truncate ON audit_entries;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_entries_no_update_delete ON audit_entries;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_entries_append_only();");

            migrationBuilder.DropTable(
                name: "access_requests");

            migrationBuilder.DropTable(
                name: "action_jobs");

            migrationBuilder.DropTable(
                name: "audit_entries");

            migrationBuilder.DropTable(
                name: "endpoints");

            migrationBuilder.DropTable(
                name: "scripts");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
